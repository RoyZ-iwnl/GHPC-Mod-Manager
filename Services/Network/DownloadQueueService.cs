using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using Newtonsoft.Json;
using GHPC_Mod_Manager.Models;
using GHPC_Mod_Manager.Resources;

namespace GHPC_Mod_Manager.Services.Network;

public interface IDownloadQueueService : IDisposable
{
    ObservableCollection<DownloadTaskItem> Tasks { get; }
    int MaxConcurrentDownloads { get; set; }
    double CurrentSpeedBytesPerSecond { get; }
    IReadOnlyList<double> SpeedHistory { get; }
    event EventHandler? QueueChanged;
    event EventHandler<string>? TaskEnqueued;

    Task<byte[]> EnqueueAsync(
        DownloadRequest request,
        Func<IProgress<DownloadProgress>, CancellationToken, Task<byte[]>> downloader,
        IProgress<DownloadProgress>? callerProgress = null,
        CancellationToken cancellationToken = default);

    void Pause(Guid id);
    void Resume(Guid id);
    void Cancel(Guid id);
    void Retry(Guid id);
    void MoveUp(Guid id);
    void MoveDown(Guid id);
    void Remove(Guid id);
    void PauseAll();
    void ResumeAll();
    void CancelAll();

    void RegisterDefaultDownloader(
        Func<DownloadRequest, IProgress<DownloadProgress>, CancellationToken, Task<byte[]>> downloader);
}

public sealed class DownloadQueueService : IDownloadQueueService
{
    private static string Text(string key) =>
        Strings.ResourceManager.GetString(key, Strings.Culture) ?? key;

    private sealed class QueueEntry
    {
        public required DownloadTaskItem Item { get; init; }
        public required DownloadRequest Request { get; init; }
        public Func<IProgress<DownloadProgress>, CancellationToken, Task<byte[]>>? Downloader { get; set; }
        public IProgress<DownloadProgress>? CallerProgress { get; set; }
        public TaskCompletionSource<byte[]> Completion { get; set; } = NewCompletionSource();
        public CancellationTokenSource Cancellation { get; set; } = new();
        public CancellationTokenRegistration CallerCancellationRegistration { get; set; }
        public bool IsRunning { get; set; }
        public bool PauseRequested { get; set; }
        public bool CancelRequested { get; set; }

        private static TaskCompletionSource<byte[]> NewCompletionSource() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ResetForRetry()
        {
            Cancellation.Dispose();
            Cancellation = new CancellationTokenSource();
            Completion = NewCompletionSource();
            PauseRequested = false;
            CancelRequested = false;
            IsRunning = false;
        }
    }

    private sealed class PersistedDownload
    {
        public Guid Id { get; set; }
        public string Url { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
        public long? ExpectedSize { get; set; }
        public string? ExpectedDigest { get; set; }
        public string? AssetName { get; set; }
        public DateTime EnqueuedAt { get; set; }
        public DownloadTaskStatus Status { get; set; }
        public double Progress { get; set; }
        public long BytesReceived { get; set; }
        public long TotalBytes { get; set; }
        public int Priority { get; set; }
        public string ErrorMessage { get; set; } = string.Empty;
    }

    private readonly ISettingsService _settingsService;
    private readonly ILoggingService _loggingService;
    private readonly object _gate = new();
    private readonly List<QueueEntry> _entries = new();
    private readonly List<DownloadTaskItem> _taskList = new();
    private readonly Timer _speedTimer;
    private bool _disposed;
    private int _activeDownloads;
    private int _maxConcurrentDownloads;
    private Func<DownloadRequest, IProgress<DownloadProgress>, CancellationToken, Task<byte[]>>? _defaultDownloader;
    private double _currentSpeedBytesPerSecond;
    private IReadOnlyList<double> _speedHistory = Array.Empty<double>();

    public ObservableCollection<DownloadTaskItem> Tasks { get; } = new();

    public int MaxConcurrentDownloads
    {
        get => _maxConcurrentDownloads;
        set
        {
            var normalized = Math.Clamp(value, 1, 3);
            if (_maxConcurrentDownloads == normalized)
                return;

            _maxConcurrentDownloads = normalized;
            _settingsService.Settings.MaxConcurrentDownloads = normalized;
            _ = _settingsService.SaveSettingsAsync();
            ScheduleDownloads();
            RaiseQueueChanged();
        }
    }

    public double CurrentSpeedBytesPerSecond => _currentSpeedBytesPerSecond;
    public IReadOnlyList<double> SpeedHistory => _speedHistory;
    public event EventHandler? QueueChanged;
    public event EventHandler<string>? TaskEnqueued;

    public DownloadQueueService(ISettingsService settingsService, ILoggingService loggingService)
    {
        _settingsService = settingsService;
        _loggingService = loggingService;
        _maxConcurrentDownloads = Math.Clamp(settingsService.Settings.MaxConcurrentDownloads, 1, 3);
        _speedTimer = new Timer(SampleSpeed, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        _ = LoadPersistedQueueAsync();
    }

    public void RegisterDefaultDownloader(
        Func<DownloadRequest, IProgress<DownloadProgress>, CancellationToken, Task<byte[]>> downloader)
    {
        _defaultDownloader = downloader;
    }

    public Task<byte[]> EnqueueAsync(
        DownloadRequest request,
        Func<IProgress<DownloadProgress>, CancellationToken, Task<byte[]>> downloader,
        IProgress<DownloadProgress>? callerProgress = null,
        CancellationToken cancellationToken = default)
    {
        if (_disposed)
            return Task.FromException<byte[]>(new ObjectDisposedException(nameof(DownloadQueueService)));

        var item = new DownloadTaskItem
        {
            Id = Guid.NewGuid(),
            Url = request.Url,
            FileName = string.IsNullOrWhiteSpace(request.FileName) ? request.Url : request.FileName,
            Source = request.Source,
            DisplayName = request.DisplayName,
            SourceTextOverride = request.SourceText,
            ExpectedSize = request.ExpectedSize,
            ExpectedDigest = request.ExpectedDigest,
            AssetName = request.AssetName,
            EnqueuedAt = DateTime.UtcNow,
            Status = DownloadTaskStatus.Pending,
            Priority = 0,
            TotalBytes = request.ExpectedSize ?? 0
        };

        var entry = new QueueEntry
        {
            Item = item,
            Request = request,
            Downloader = downloader,
            CallerProgress = callerProgress
        };

        lock (_gate)
        {
            _entries.Add(entry);
            _taskList.Add(item);
        }

        OnUi(() => Tasks.Add(item));
        PersistQueue();
        RaiseQueueChanged();
        OnUi(() => TaskEnqueued?.Invoke(this, item.DisplayName ?? item.FileName));

        if (cancellationToken.CanBeCanceled)
            entry.CallerCancellationRegistration = cancellationToken.Register(() => Cancel(item.Id));

        ScheduleDownloads();
        return entry.Completion.Task;
    }

    public void Pause(Guid id)
    {
        QueueEntry? entry;
        lock (_gate)
        {
            entry = FindEntry(id);
            if (entry == null || !entry.Item.CanPause)
                return;

            entry.PauseRequested = true;
            entry.Item.Status = DownloadTaskStatus.Paused;
            entry.Cancellation.Cancel();
        }

        PersistQueue();
        RaiseQueueChanged();
    }

    public void Resume(Guid id)
    {
        QueueEntry? entry;
        lock (_gate)
        {
            entry = FindEntry(id);
            if (entry == null || entry.Item.Status != DownloadTaskStatus.Paused)
                return;

            entry.ResetForRetry();
            entry.Item.Status = DownloadTaskStatus.Pending;
        }

        PersistQueue();
        RaiseQueueChanged();
        ScheduleDownloads();
    }

    public void Cancel(Guid id)
    {
        QueueEntry? entry;
        lock (_gate)
        {
            entry = FindEntry(id);
            if (entry == null || entry.Item.Status is DownloadTaskStatus.Completed or DownloadTaskStatus.Cancelled)
                return;

            entry.CancelRequested = true;
            entry.Cancellation.Cancel();
            entry.Item.Status = DownloadTaskStatus.Cancelled;
            entry.Completion.TrySetCanceled();
        }

        PersistQueue();
        RaiseQueueChanged();
        ScheduleDownloads();
    }

    public void Retry(Guid id)
    {
        QueueEntry? entry;
        lock (_gate)
        {
            entry = FindEntry(id);
            if (entry == null || !entry.Item.CanRetry)
                return;

            if (entry.Downloader == null && _defaultDownloader != null)
            {
                var defaultDownloader = _defaultDownloader;
                entry.Downloader = (progress, token) => defaultDownloader(entry.Request, progress, token);
            }

            if (entry.Downloader == null)
            {
                entry.Item.ErrorMessage = Text("DownloadRetryUnavailable");
                return;
            }

            entry.ResetForRetry();
            entry.Item.ErrorMessage = string.Empty;
            entry.Item.Progress = 0;
            entry.Item.BytesReceived = 0;
            entry.Item.SpeedBytesPerSecond = 0;
            entry.Item.Status = DownloadTaskStatus.Pending;
        }

        PersistQueue();
        RaiseQueueChanged();
        ScheduleDownloads();
    }

    public void MoveUp(Guid id) => ChangePriority(id, 1);
    public void MoveDown(Guid id) => ChangePriority(id, -1);

    public void Remove(Guid id)
    {
        QueueEntry? entry;
        lock (_gate)
        {
            entry = FindEntry(id);
            if (entry == null || !entry.Item.CanRemove)
                return;

            _entries.Remove(entry);
            _taskList.Remove(entry.Item);
        }

        OnUi(() => Tasks.Remove(entry.Item));
        PersistQueue();
        RaiseQueueChanged();
    }

    public void PauseAll()
    {
        foreach (var entry in SnapshotEntries())
            Pause(entry.Item.Id);
    }

    public void ResumeAll()
    {
        foreach (var entry in SnapshotEntries())
            Resume(entry.Item.Id);
    }

    public void CancelAll()
    {
        foreach (var entry in SnapshotEntries())
            Cancel(entry.Item.Id);
    }

    private void ChangePriority(Guid id, int direction)
    {
        lock (_gate)
        {
            var entry = FindEntry(id);
            if (entry == null)
                return;

            var ordered = _taskList
                .Where(t => t.Status is DownloadTaskStatus.Pending or DownloadTaskStatus.Paused or DownloadTaskStatus.Downloading)
                .OrderByDescending(t => t.Priority)
                .ThenBy(t => t.EnqueuedAt)
                .ToList();
            var index = ordered.IndexOf(entry.Item);
            var targetIndex = index + (direction > 0 ? -1 : 1);
            if (index < 0 || targetIndex < 0 || targetIndex >= ordered.Count)
                return;

            var target = ordered[targetIndex];
            var oldPriority = entry.Item.Priority;
            entry.Item.Priority = target.Priority;
            target.Priority = oldPriority;
        }

        ReorderVisibleTasks();
        PersistQueue();
        RaiseQueueChanged();
        ScheduleDownloads();
    }

    private void ScheduleDownloads()
    {
        List<QueueEntry> toStart;
        lock (_gate)
        {
            if (_disposed)
                return;

            var capacity = Math.Max(0, _maxConcurrentDownloads - _activeDownloads);
            toStart = _entries
                .Where(e => !e.IsRunning && e.Downloader != null && e.Item.Status == DownloadTaskStatus.Pending)
                .OrderByDescending(e => e.Item.Priority)
                .ThenBy(e => e.Item.EnqueuedAt)
                .Take(capacity)
                .ToList();

            foreach (var entry in toStart)
            {
                entry.IsRunning = true;
                _activeDownloads++;
                entry.Item.Status = DownloadTaskStatus.Downloading;
            }
        }

        foreach (var entry in toStart)
            _ = RunEntryAsync(entry);

        if (toStart.Count > 0)
        {
            PersistQueue();
            RaiseQueueChanged();
        }
    }

    private async Task RunEntryAsync(QueueEntry entry)
    {
        try
        {
            var downloader = entry.Downloader;
            if (downloader == null)
                throw new InvalidOperationException(Text("DownloadRetryUnavailable"));

            var progress = new Progress<DownloadProgress>(value => UpdateProgress(entry, value));
            var data = await downloader(progress, entry.Cancellation.Token);

            lock (_gate)
            {
                if (!entry.CancelRequested && !entry.PauseRequested)
                {
                    entry.Item.Status = DownloadTaskStatus.Completed;
                    entry.Item.Progress = 100;
                    entry.Item.SpeedBytesPerSecond = 0;
                    // 把所有分块标为已完成，确保IDM进度条完整闭合
                    if (entry.Item.ChunkSegments?.Count > 0)
                    {
                        entry.Item.ChunkSegments = entry.Item.ChunkSegments
                            .Select(s => new ChunkSegmentInfo
                            {
                                StartByte = s.StartByte,
                                EndByte = s.EndByte,
                                BytesDownloaded = s.TotalSize,
                                SegmentStatus = ChunkSegmentStatus.Completed
                            })
                            .ToList();
                    }
                    entry.Completion.TrySetResult(data);
                }
            }
        }
        catch (OperationCanceledException)
        {
            lock (_gate)
            {
                if (entry.CancelRequested)
                    entry.Completion.TrySetCanceled();
                else if (entry.PauseRequested)
                    entry.Item.Status = DownloadTaskStatus.Paused;
                else
                    MarkFailed(entry, Text("DownloadCancelled"));
            }
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                MarkFailed(entry, ex.Message);
            }
            _loggingService.LogError(ex, "Download queue task failed: {0}", entry.Item.FileName);
        }
        finally
        {
            lock (_gate)
            {
                entry.IsRunning = false;
                _activeDownloads = Math.Max(0, _activeDownloads - 1);
            }

            PersistQueue();
            RaiseQueueChanged();
            ScheduleDownloads();
        }
    }

    private void UpdateProgress(QueueEntry entry, DownloadProgress progress)
    {
        lock (_gate)
        {
            if (entry.Item.Status == DownloadTaskStatus.Cancelled)
                return;

            entry.Item.BytesReceived = progress.BytesReceived;
            entry.Item.TotalBytes = progress.TotalBytes > 0 ? progress.TotalBytes : entry.Item.TotalBytes;
            entry.Item.Progress = progress.ProgressPercentage;
            entry.Item.SpeedBytesPerSecond = progress.SpeedBytesPerSecond;
            // 传递分块快照，触发IDM风格进度条更新
            if (progress.ChunkSegments != null)
                entry.Item.ChunkSegments = progress.ChunkSegments;
            _currentSpeedBytesPerSecond = _taskList
                .Where(t => t.Status == DownloadTaskStatus.Downloading)
                .Sum(t => t.SpeedBytesPerSecond);
        }

        entry.CallerProgress?.Report(progress);

        RaiseQueueChanged();
    }

    private void MarkFailed(QueueEntry entry, string message)
    {
        if (entry.CancelRequested || entry.Item.Status == DownloadTaskStatus.Cancelled)
            return;

        entry.Item.Status = DownloadTaskStatus.Failed;
        entry.Item.ErrorMessage = string.IsNullOrWhiteSpace(message)
            ? Text("DownloadFailedWithoutException")
            : message;
        entry.Completion.TrySetException(new InvalidOperationException(entry.Item.ErrorMessage));
    }

    private void SampleSpeed(object? _)
    {
        if (_disposed)
            return;

        double speed;
        lock (_gate)
        {
            speed = _taskList
                .Where(t => t.Status == DownloadTaskStatus.Downloading)
                .Sum(t => t.SpeedBytesPerSecond);
            _currentSpeedBytesPerSecond = speed;
            var history = _speedHistory.ToList();
            history.Add(speed);
            if (history.Count > 60)
                history.RemoveRange(0, history.Count - 60);
            _speedHistory = history;
        }

        RaiseQueueChanged();
    }

    private async Task LoadPersistedQueueAsync()
    {
        try
        {
            var path = QueuePath;
            if (!File.Exists(path))
                return;

            var json = await File.ReadAllTextAsync(path);
            var records = JsonConvert.DeserializeObject<List<PersistedDownload>>(json) ?? new();
            foreach (var record in records)
            {
                // 重启时跳过已完成/已取消项目，自动清理
                if (record.Status is DownloadTaskStatus.Completed or DownloadTaskStatus.Cancelled)
                    continue;

                var item = new DownloadTaskItem
                {
                    Id = record.Id == Guid.Empty ? Guid.NewGuid() : record.Id,
                    Url = record.Url,
                    FileName = record.FileName,
                    Source = record.Source,
                    ExpectedSize = record.ExpectedSize,
                    ExpectedDigest = record.ExpectedDigest,
                    AssetName = record.AssetName,
                    EnqueuedAt = record.EnqueuedAt == default ? DateTime.UtcNow : record.EnqueuedAt,
                    Status = record.Status,
                    Progress = record.Progress,
                    BytesReceived = record.BytesReceived,
                    TotalBytes = record.TotalBytes,
                    Priority = record.Priority,
                    ErrorMessage = record.ErrorMessage
                };

                QueueEntry entry;
                lock (_gate)
                {
                    if (item.Status is DownloadTaskStatus.Pending or DownloadTaskStatus.Downloading or DownloadTaskStatus.Paused)
                    {
                        item.Status = DownloadTaskStatus.Failed;
                        item.ErrorMessage = Text("ApplicationRestartInterrupted");
                    }

                    entry = new QueueEntry
                    {
                        Item = item,
                        Request = new DownloadRequest
                        {
                            Url = item.Url,
                            FileName = item.FileName,
                            Source = item.Source,
                            ExpectedSize = item.ExpectedSize,
                            ExpectedDigest = item.ExpectedDigest,
                            AssetName = item.AssetName
                        }
                    };
                    _entries.Add(entry);
                    _taskList.Add(item);
                }

                OnUi(() => Tasks.Add(item));
            }

            PersistQueue();
            RaiseQueueChanged();
        }
        catch (Exception ex)
        {
            _loggingService.LogWarning("Failed to restore download queue: {0}", ex.Message);
        }
    }

    private void PersistQueue()
    {
        if (_disposed)
            return;

        List<PersistedDownload> records;
        lock (_gate)
        {
            records = _taskList.Select(item => new PersistedDownload
            {
                Id = item.Id,
                Url = item.Url,
                FileName = item.FileName,
                Source = item.Source,
                ExpectedSize = item.ExpectedSize,
                ExpectedDigest = item.ExpectedDigest,
                AssetName = item.AssetName,
                EnqueuedAt = item.EnqueuedAt,
                Status = item.Status,
                Progress = item.Progress,
                BytesReceived = item.BytesReceived,
                TotalBytes = item.TotalBytes,
                Priority = item.Priority,
                ErrorMessage = item.ErrorMessage
            }).ToList();
        }

        _ = WriteQueueAsync(records);
    }

    private async Task WriteQueueAsync(List<PersistedDownload> records)
    {
        try
        {
            var json = JsonConvert.SerializeObject(records, Formatting.Indented);
            await File.WriteAllTextAsync(QueuePath, json);
        }
        catch (Exception ex)
        {
            _loggingService.LogWarning("Failed to save download queue: {0}", ex.Message);
        }
    }

    private string QueuePath => Path.Combine(_settingsService.AppDataPath, "download_queue.json");

    private QueueEntry? FindEntry(Guid id) => _entries.FirstOrDefault(entry => entry.Item.Id == id);

    private List<QueueEntry> SnapshotEntries()
    {
        lock (_gate)
            return _entries.ToList();
    }

    private void ReorderVisibleTasks()
    {
        var ordered = _taskList
            .OrderByDescending(t => t.Priority)
            .ThenBy(t => t.EnqueuedAt)
            .ToList();
        OnUi(() =>
        {
            for (var index = 0; index < ordered.Count; index++)
            {
                var currentIndex = Tasks.IndexOf(ordered[index]);
                if (currentIndex >= 0 && currentIndex != index)
                    Tasks.Move(currentIndex, index);
            }
        });
    }

    private void RaiseQueueChanged()
    {
        OnUi(() => QueueChanged?.Invoke(this, EventArgs.Empty));
    }

    private static void OnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
            action();
        else
            dispatcher.BeginInvoke(action);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _speedTimer.Dispose();
        lock (_gate)
        {
            foreach (var entry in _entries)
            {
                entry.CancelRequested = true;
                entry.Cancellation.Cancel();
                entry.Completion.TrySetCanceled();
                entry.CallerCancellationRegistration.Dispose();
                entry.Cancellation.Dispose();
            }
        }
    }
}

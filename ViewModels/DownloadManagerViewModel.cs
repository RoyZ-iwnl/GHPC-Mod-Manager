using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GHPC_Mod_Manager.Models;
using GHPC_Mod_Manager.Resources;
using GHPC_Mod_Manager.Services;
using GHPC_Mod_Manager.Services.Network;
using System.Collections.Specialized;
using System.Collections.ObjectModel;
using System.Windows;

namespace GHPC_Mod_Manager.ViewModels;

public partial class DownloadManagerViewModel : ObservableObject
{
    private readonly IDownloadQueueService _queue;
    private readonly ISettingsService _settings;
    private readonly IMainConfigService _mainConfig;
    private readonly INavigationService _navigation;

    public ObservableCollection<DownloadTaskItem> Tasks => _queue.Tasks;
    public ObservableCollection<double> SpeedSamples { get; } = new();
    public ObservableCollection<ProxyServerItem> AvailableProxyServers { get; } = new();
    public string DownloadsLabel => Text("NavDownloads");
    public string QueueTitle => Text("DownloadQueue");
    public string SpeedLabel => Text("DownloadSpeed");
    public string EmptyLabel => Text("DownloadEmpty");
    public string ProxyTitle => Text("ProxyQuickActions");
    public string PauseAllLabel => Text("PauseAll");
    public string ResumeAllLabel => Text("ResumeAll");
    public string CancelAllLabel => Text("CancelAll");
    public string ConcurrencyLabel => Text("ConcurrentDownloads");
    public string EnableProxyLabel => Text("EnableGitHubProxy");
    public string SelectProxyLabel => Text("SelectProxy");
    public string TokenSettingsLabel => Text("OpenTokenSettings");
    public string ProxySettingsLabel => Text("OpenProxySettings");
    public string MoveUpLabel => Text("MoveUp");
    public string MoveDownLabel => Text("MoveDown");
    public string RemoveLabel => Text("Remove");
    public string PauseLabel => Text("Pause");
    public string ResumeLabel => Text("Resume");
    public string RetryLabel => Text("Retry");
    public string CancelLabel => Text("Cancel");

    [ObservableProperty]
    private string _currentSpeed = "0 B/s";

    [ObservableProperty]
    private int _maxConcurrentDownloads;

    [ObservableProperty]
    private bool _isChineseLanguage;

    [ObservableProperty]
    private bool _isEmpty;

    [ObservableProperty]
    private bool _useGitHubProxy;

    [ObservableProperty]
    private ProxyServerItem? _selectedProxyServer;

    public DownloadManagerViewModel(
        IDownloadQueueService queue,
        ISettingsService settings,
        IMainConfigService mainConfig,
        INavigationService navigation)
    {
        _queue = queue;
        _settings = settings;
        _mainConfig = mainConfig;
        _navigation = navigation;
        MaxConcurrentDownloads = queue.MaxConcurrentDownloads;
        IsChineseLanguage = string.Equals(settings.Settings.Language, "zh-CN", StringComparison.OrdinalIgnoreCase);
        UseGitHubProxy = settings.Settings.UseGitHubProxy;
        LoadProxyServers();
        _queue.QueueChanged += OnQueueChanged;
        _queue.Tasks.CollectionChanged += OnTasksChanged;
        IsEmpty = Tasks.Count == 0;
        RefreshSpeed();
    }

    partial void OnMaxConcurrentDownloadsChanged(int value) => _queue.MaxConcurrentDownloads = value;

    partial void OnUseGitHubProxyChanged(bool value)
    {
        _settings.Settings.UseGitHubProxy = value;
        _ = _settings.SaveSettingsAsync();
    }

    partial void OnSelectedProxyServerChanged(ProxyServerItem? value)
    {
        if (value == null)
            return;
        _settings.Settings.GitHubProxyServer = value.EnumValue;
        _settings.Settings.GitHubProxyServerId = value.ServerId;
        _ = _settings.SaveSettingsAsync();
    }

    [RelayCommand]
    private void Pause(DownloadTaskItem? task) { if (task != null) _queue.Pause(task.Id); }
    [RelayCommand]
    private void Resume(DownloadTaskItem? task) { if (task != null) _queue.Resume(task.Id); }
    [RelayCommand]
    private void Cancel(DownloadTaskItem? task) { if (task != null) _queue.Cancel(task.Id); }
    [RelayCommand]
    private void Retry(DownloadTaskItem? task) { if (task != null) _queue.Retry(task.Id); }
    [RelayCommand]
    private void MoveUp(DownloadTaskItem? task) { if (task != null) _queue.MoveUp(task.Id); }
    [RelayCommand]
    private void MoveDown(DownloadTaskItem? task) { if (task != null) _queue.MoveDown(task.Id); }
    [RelayCommand]
    private void Remove(DownloadTaskItem? task) { if (task != null) _queue.Remove(task.Id); }
    [RelayCommand] private void PauseAll() => _queue.PauseAll();
    [RelayCommand] private void ResumeAll() => _queue.ResumeAll();
    [RelayCommand] private void CancelAll() => _queue.CancelAll();
    [RelayCommand] private void OpenTokenSettings() => _navigation.NavigateToSettings();
    [RelayCommand] private void OpenProxySettings() => _navigation.NavigateToSettings();

    private void LoadProxyServers()
    {
        var remote = _mainConfig.GetRemoteProxyServers();
        var items = remote == null
            ? ProxyServerItem.BuildFallback()
            : ProxyServerItem.BuildFromRemote(remote, _settings.Settings.Language);
        foreach (var item in items)
            AvailableProxyServers.Add(item);

        var selectedId = _settings.Settings.GitHubProxyServerId;
        SelectedProxyServer = AvailableProxyServers.FirstOrDefault(item =>
            string.Equals(item.ServerId, selectedId, StringComparison.OrdinalIgnoreCase))
            ?? AvailableProxyServers.FirstOrDefault(item => item.EnumValue == _settings.Settings.GitHubProxyServer)
            ?? AvailableProxyServers.FirstOrDefault();
    }

    private void OnQueueChanged(object? sender, EventArgs e)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
            RefreshSpeed();
        else
            dispatcher.BeginInvoke(RefreshSpeed);
    }

    private void OnTasksChanged(object? sender, NotifyCollectionChangedEventArgs e) => IsEmpty = Tasks.Count == 0;

    private void RefreshSpeed()
    {
        var speed = _queue.CurrentSpeedBytesPerSecond;
        CurrentSpeed = FormatSpeed(speed);
        SpeedSamples.Clear();
        foreach (var sample in _queue.SpeedHistory.TakeLast(60))
            SpeedSamples.Add(sample);
    }

    private static string FormatSpeed(double bytes)
    {
        if (bytes >= 1024 * 1024) return $"{bytes / 1024 / 1024:0.0} MB/s";
        if (bytes >= 1024) return $"{bytes / 1024:0} KB/s";
        return $"{bytes:0} B/s";
    }

    private static string Text(string key) => Strings.ResourceManager.GetString(key, Strings.Culture) ?? key;
}

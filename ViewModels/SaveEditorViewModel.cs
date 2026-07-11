using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GHPC_Mod_Manager.Models;
using GHPC_Mod_Manager.Resources;
using GHPC_Mod_Manager.Services;
using GHPC_Mod_Manager.Services.Game;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;

namespace GHPC_Mod_Manager.ViewModels;

public partial class SaveEditorViewModel : ObservableObject
{
    private readonly ISaveEditorService _saveEditorService;
    private readonly ISettingsService _settingsService;
    private readonly IDialogService _dialogService;
    private readonly ILoggingService _loggingService;
    private readonly IProcessService _processService;

    private SaveFileData? _currentSaveData;

    [ObservableProperty]
    private string _saveFilePath = string.Empty;

    [ObservableProperty]
    private bool _isSaveFileExists = false;

    [ObservableProperty]
    private bool _isLoaded = false;

    [ObservableProperty]
    private bool _isGameRunning = false;

    [ObservableProperty]
    private ObservableCollection<TheaterTreeNode> _theaterTree = new();

    [ObservableProperty]
    private object? _selectedTreeNode;

    [ObservableProperty]
    private MissionTreeNode? _selectedMissionNode;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _hasChanges = false;

    [ObservableProperty]
    private ObservableCollection<BackupRecord> _backups = new();

    public SaveEditorViewModel(
        ISaveEditorService saveEditorService,
        ISettingsService settingsService,
        IDialogService dialogService,
        ILoggingService loggingService,
        IProcessService processService)
    {
        _saveEditorService = saveEditorService;
        _settingsService = settingsService;
        _dialogService = dialogService;
        _loggingService = loggingService;
        _processService = processService;

        // 订阅游戏运行状态变化，运行时禁用所有写操作
        IsGameRunning = _processService.IsGameRunning;
        _processService.GameRunningStateChanged += (s, e) =>
        {
            Application.Current?.Dispatcher.Invoke(() => IsGameRunning = e);
        };

        SaveFilePath = _saveEditorService.GetDefaultSaveFilePath();
        IsSaveFileExists = _saveEditorService.SaveFileExists(SaveFilePath);
        RefreshBackupList();

        if (IsSaveFileExists)
        {
            _ = LoadInitialSaveFileAsync();
        }
    }

    // 构造期间的初始加载，独立方法以便捕获异常避免未处理异常
    private async Task LoadInitialSaveFileAsync()
    {
        try
        {
            await LoadSaveFile();
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, "SaveEditor_LoadFailed", SaveFilePath);
        }
    }

    // 游戏运行状态变化时刷新写操作命令的可用性
    partial void OnIsGameRunningChanged(bool value)
    {
        SaveFileCommand.NotifyCanExecuteChanged();
        ManualBackupCommand.NotifyCanExecuteChanged();
        RestoreFromBackupCommand.NotifyCanExecuteChanged();
        ResetProgressCommand.NotifyCanExecuteChanged();
        CompleteAllMissionsCommand.NotifyCanExecuteChanged();
        ToggleMissionStatusCommand.NotifyCanExecuteChanged();

        if (value)
        {
            StatusMessage = Strings.SaveEditor_GameRunning;
        }
        else
        {
            // 游戏停止后恢复状态栏提示，避免停留在"运行中"文案
            StatusMessage = IsLoaded ? Strings.SaveEditor_Loaded : Strings.SaveEditor_NoData;
        }
    }

    // 写操作可用性：游戏未运行时才允许
    private bool CanWriteOperation() => !IsGameRunning;

    [RelayCommand]
    private async Task LoadSaveFile()
    {
        if (string.IsNullOrEmpty(SaveFilePath))
        {
            _dialogService.ShowWarning(Strings.SaveEditor_PathEmpty, Strings.SaveEditor_LoadFailed);
            return;
        }

        // 先判断文件是否存在，区分"文件不存在"与"格式错误"两种失败原因
        if (!_saveEditorService.SaveFileExists(SaveFilePath))
        {
            IsLoaded = false;
            IsSaveFileExists = false;
            _dialogService.ShowError(Strings.SaveEditor_NoFile, Strings.SaveEditor_LoadFailed);
            return;
        }

        var data = await _saveEditorService.LoadSaveFileAsync(SaveFilePath);
        if (data == null)
        {
            // 文件存在但加载失败：JSON格式错误或IO异常
            _dialogService.ShowError(Strings.SaveEditor_LoadFailed, Strings.SaveEditor_LoadFailed);
            return;
        }

        _currentSaveData = data;
        LoadTheatersFromData(data);
        IsLoaded = true;
        HasChanges = false;
        StatusMessage = Strings.SaveEditor_Loaded;
    }

    [RelayCommand(CanExecute = nameof(CanWriteOperation))]
    private async Task SaveFile()
    {
        if (_currentSaveData == null)
        {
            _dialogService.ShowWarning(Strings.SaveEditor_NoData, Strings.SaveEditor_SaveFailed);
            return;
        }

        try
        {
            if (_saveEditorService.SaveFileExists(SaveFilePath))
            {
                await _saveEditorService.AutoBackupBeforeSaveAsync(SaveFilePath);
                RefreshBackupList();
            }

            await _saveEditorService.SaveSaveFileAsync(SaveFilePath, _currentSaveData);
            HasChanges = false;
            StatusMessage = Strings.SaveEditor_SaveSuccess;
            _dialogService.ShowSuccess(Strings.SaveEditor_SaveSuccess, Strings.SaveEditor_Save);
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, "SaveEditor_SaveFailed");
            _dialogService.ShowError($"{Strings.SaveEditor_SaveFailed}: {ex.Message}", Strings.SaveEditor_SaveFailed);
        }
    }

    [RelayCommand]
    private async Task BrowseSaveFile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = $"{Strings.SaveEditor_GHPCSaveFile}|GHPC_data.sav|{Strings.SaveEditor_AllFiles}|*.*",
            Title = Strings.SaveEditor_SelectFile,
            InitialDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games", "GHPC", "Data")
        };

        if (dialog.ShowDialog() == true)
        {
            SaveFilePath = dialog.FileName;
            IsSaveFileExists = _saveEditorService.SaveFileExists(SaveFilePath);
            await LoadSaveFile();
        }
    }

    [RelayCommand(CanExecute = nameof(CanWriteOperation))]
    private async Task ManualBackup()
    {
        if (!_saveEditorService.SaveFileExists(SaveFilePath))
        {
            _dialogService.ShowWarning(Strings.SaveEditor_BackupNotExist, Strings.SaveEditor_BackupFailed);
            return;
        }

        try
        {
            await _saveEditorService.BackupSaveFileAsync(SaveFilePath);
            RefreshBackupList();
            StatusMessage = Strings.SaveEditor_ManualBackupSuccess;
            _dialogService.ShowSuccess(Strings.SaveEditor_BackupSuccess, Strings.SaveEditor_BackupSuccess);
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, "SaveEditor_ManualBackupFailed");
            _dialogService.ShowError($"{Strings.SaveEditor_BackupFailed}: {ex.Message}", Strings.SaveEditor_BackupFailed);
        }
    }

    [RelayCommand(CanExecute = nameof(CanWriteOperation))]
    private async Task RestoreFromBackup(BackupRecord? backup)
    {
        if (backup == null) return;

        var message = string.Format(Strings.SaveEditor_RestoreTime, backup.DisplayTime);
        var result = _dialogService.Confirm(Strings.SaveEditor_RestoreConfirm + "\n\n" + message, Strings.SaveEditor_Confirm);

        if (result)
        {
            // 恢复前先备份当前存档（若存在），保证可撤销，避免当前进度永久丢失
            if (_saveEditorService.SaveFileExists(SaveFilePath))
            {
                try
                {
                    await _saveEditorService.BackupSaveFileAsync(SaveFilePath);
                    RefreshBackupList();
                }
                catch (Exception ex)
                {
                    _loggingService.LogError(ex, "SaveEditor_RestoreBackupCurrentFailed", SaveFilePath);
                }
            }

            var success = await _saveEditorService.RestoreFromBackupAsync(backup.FilePath, SaveFilePath);
            if (success)
            {
                await LoadSaveFile();
                StatusMessage = Strings.SaveEditor_RestoredFromBackup;
                _dialogService.ShowSuccess(Strings.SaveEditor_RestoreSuccess, Strings.SaveEditor_RestoreSuccess);
            }
            else
            {
                _dialogService.ShowError(Strings.SaveEditor_RestoreFailed, Strings.SaveEditor_RestoreFailed);
            }
        }
    }

    [RelayCommand]
    private void DeleteBackup(BackupRecord? backup)
    {
        if (backup == null) return;

        var result = _dialogService.Confirm(Strings.SaveEditor_DeleteConfirm + "\n\n" + backup.FileName, Strings.SaveEditor_Confirm);

        if (result)
        {
            if (_saveEditorService.DeleteBackup(backup.FilePath))
            {
                RefreshBackupList();
                StatusMessage = Strings.SaveEditor_DeleteSuccess;
            }
            else
            {
                _dialogService.ShowError(Strings.SaveEditor_DeleteFailed, Strings.SaveEditor_DeleteFailed);
            }
        }
    }

    private void RefreshBackupList()
    {
        Backups.Clear();
        foreach (var backup in _saveEditorService.GetBackupList())
        {
            Backups.Add(backup);
        }
    }

    [RelayCommand(CanExecute = nameof(CanWriteOperation))]
    private void ResetProgress()
    {
        if (_currentSaveData == null) return;

        var result = _dialogService.Confirm(Strings.SaveEditor_ResetConfirm, Strings.SaveEditor_Confirm);

        if (result)
        {
            // 结构不符时返回 false，此时不应提示"成功"
            var changed = _saveEditorService.ResetAllProgress(_currentSaveData);
            if (changed)
            {
                LoadTheatersFromData(_currentSaveData);
                HasChanges = true;
                StatusMessage = Strings.SaveEditor_ResetSuccess;
            }
            else
            {
                _dialogService.ShowWarning(Strings.SaveEditor_NoData, Strings.SaveEditor_Confirm);
            }
        }
    }

    [RelayCommand(CanExecute = nameof(CanWriteOperation))]
    private void CompleteAllMissions()
    {
        if (_currentSaveData == null) return;

        var result = _dialogService.Confirm(Strings.SaveEditor_CompleteConfirm, Strings.SaveEditor_Confirm);

        if (result)
        {
            var changed = _saveEditorService.CompleteAllMissions(_currentSaveData);
            if (changed)
            {
                LoadTheatersFromData(_currentSaveData);
                HasChanges = true;
                StatusMessage = Strings.SaveEditor_CompleteSuccess;
            }
            else
            {
                _dialogService.ShowWarning(Strings.SaveEditor_NoData, Strings.SaveEditor_Confirm);
            }
        }
    }

    [RelayCommand(CanExecute = nameof(CanWriteOperation))]
    private void ToggleMissionStatus(string faction)
    {
        if (_currentSaveData == null || SelectedMissionNode == null) return;

        var toggled = _saveEditorService.ToggleMissionCompletion(
            _currentSaveData,
            SelectedMissionNode.TheaterId,
            SelectedMissionNode.Name,
            faction);

        // 无论是否切换成功都刷新UI，保持与数据一致；仅在成功时标记更改
        RefreshMissionStatus(SelectedMissionNode);
        if (toggled)
        {
            HasChanges = true;
            StatusMessage = string.Format(Strings.SaveEditor_ToggleStatus, SelectedMissionNode.Name, faction);
        }
    }

    private void LoadTheatersFromData(SaveFileData data)
    {
        TheaterTree.Clear();

        if (data?.PlayerSave?.Value?.TheaterMissionPlayStates == null)
        {
            StatusMessage = Strings.SaveEditor_NoData;
            return;
        }

        foreach (var theater in data.PlayerSave.Value.TheaterMissionPlayStates)
        {
            var theaterNode = new TheaterTreeNode
            {
                Id = theater.Key,
                Name = theater.Key
            };

            foreach (var mission in theater.Value)
            {
                var missionNode = new MissionTreeNode
                {
                    Name = mission.Key,
                    TheaterId = theater.Key
                };
                missionNode.SetStatuses(mission.Value);
                theaterNode.Missions.Add(missionNode);
            }

            TheaterTree.Add(theaterNode);
        }

        StatusMessage = string.Format(Strings.SaveEditor_TheatersLoaded, TheaterTree.Count);
    }

    partial void OnSelectedTreeNodeChanged(object? value)
    {
        if (value is MissionTreeNode missionNode)
        {
            SelectedMissionNode = missionNode;
        }
        else
        {
            SelectedMissionNode = null;
        }
    }

    private void RefreshMissionStatus(MissionTreeNode node)
    {
        if (_currentSaveData?.PlayerSave?.Value?.TheaterMissionPlayStates == null) return;

        if (_currentSaveData.PlayerSave.Value.TheaterMissionPlayStates.TryGetValue(node.TheaterId, out var theater)
            && theater.TryGetValue(node.Name, out var status))
        {
            node.SetStatuses(status);
        }
    }
}
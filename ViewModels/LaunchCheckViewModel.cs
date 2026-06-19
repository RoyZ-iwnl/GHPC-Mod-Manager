using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GHPC_Mod_Manager.Models;
using GHPC_Mod_Manager.Resources;
using GHPC_Mod_Manager.Services;
using System.Collections.ObjectModel;

namespace GHPC_Mod_Manager.ViewModels;

/// <summary>
/// 启动检查步骤状态
/// </summary>
public enum LaunchCheckStepStatus
{
    Pending,
    InProgress,
    Passed,
    Warning,
    Failed,
    Skipped
}

/// <summary>
/// 启动检查步骤数据模型
/// </summary>
public partial class LaunchCheckStep : ObservableObject
{
    public string Id { get; }
    public string TitleKey { get; }
    public string? DescriptionKey { get; }

    [ObservableProperty]
    private LaunchCheckStepStatus _status = LaunchCheckStepStatus.Pending;

    [ObservableProperty]
    private ObservableCollection<string> _details = new();

    public bool RequiresOnline { get; }

    public LaunchCheckStep(string id, string titleKey, string? descriptionKey = null, bool requiresOnline = false)
    {
        Id = id;
        TitleKey = titleKey;
        DescriptionKey = descriptionKey;
        RequiresOnline = requiresOnline;
    }

    public string LocalizedTitle => Strings.ResourceManager.GetString(TitleKey, Strings.Culture) ?? TitleKey;
}

/// <summary>
/// 启动检查完成事件参数
/// </summary>
public class LaunchCheckCompletedEventArgs : EventArgs
{
    public bool CanLaunch { get; set; }
    public bool UserConfirmed { get; set; }
    public List<LaunchCheckStep> Results { get; set; } = new();
}

/// <summary>
/// 启动检查窗口 ViewModel
/// </summary>
public partial class LaunchCheckViewModel : ObservableObject
{
    private readonly ISettingsService _settingsService;
    private readonly ILaunchCheckService _launchCheckService;
    private readonly ILoggingService _loggingService;

    private List<ModViewModel> _allMods = new();

    [ObservableProperty]
    private ObservableCollection<LaunchCheckStep> _steps = new();

    [ObservableProperty]
    private int _currentStepIndex = -1;

    [ObservableProperty]
    private bool _isOnlineMode = true;

    [ObservableProperty]
    private bool _isChecking;

    [ObservableProperty]
    private string _currentStatusMessage = string.Empty;

    [ObservableProperty]
    private bool _canContinue;

    [ObservableProperty]
    private bool _showContinueButton;

    [ObservableProperty]
    private bool _showCancelButton = true;

    public LaunchCheckStep? CurrentStep
        => CurrentStepIndex >= 0 && CurrentStepIndex < Steps.Count ? Steps[CurrentStepIndex] : null;

    public string CurrentStepTitle
        => CurrentStep?.LocalizedTitle ?? Strings.LaunchCheckTitle;

    public string CurrentStepStatusText
        => CurrentStep?.Status switch
        {
            LaunchCheckStepStatus.InProgress => Strings.LaunchCheckInProgress,
            LaunchCheckStepStatus.Passed => Strings.LaunchCheckPassed,
            LaunchCheckStepStatus.Warning => Strings.Warning,
            LaunchCheckStepStatus.Failed => Strings.LaunchCheckFailed,
            LaunchCheckStepStatus.Skipped => Strings.LaunchCheckSkipped,
            _ => string.Empty
        };

    public ObservableCollection<string> CurrentStepDetails
        => CurrentStep?.Details ?? new ObservableCollection<string>();

    public ObservableCollection<string> IssueSummary
        => new(
            Steps.Where(step => step.Status is LaunchCheckStepStatus.Warning or LaunchCheckStepStatus.Failed)
                 .Select(step => $"{step.LocalizedTitle}: {step.Details.FirstOrDefault() ?? GetStatusText(step.Status)}"));

    public bool HasIssueSummary
        => Steps.Any(step => step.Status is LaunchCheckStepStatus.Warning or LaunchCheckStepStatus.Failed);

    public event EventHandler<LaunchCheckCompletedEventArgs>? CheckCompleted;

    public LaunchCheckViewModel(
        ISettingsService settingsService,
        ILaunchCheckService launchCheckService,
        ILoggingService loggingService)
    {
        _settingsService = settingsService;
        _launchCheckService = launchCheckService;
        _loggingService = loggingService;
    }

    /// <summary>
    /// 初始化检查步骤
    /// </summary>
    public void Initialize(List<ModViewModel> allMods)
    {
        _allMods = allMods;
        IsOnlineMode = _settingsService.Settings.OnlineCheckOnLaunch;

        Steps.Clear();
        Steps.Add(new LaunchCheckStep("gamePath", "LaunchCheckGamePath"));

        if (!_settingsService.Settings.SkipModUpdateCheck)
            Steps.Add(new LaunchCheckStep("modUpdate", "LaunchCheckModUpdate", "ModUpdatesAvailableOnLaunch", requiresOnline: true));

        if (!_settingsService.Settings.SkipConflictCheck)
            Steps.Add(new LaunchCheckStep("conflict", "LaunchCheckConflict"));

        Steps.Add(new LaunchCheckStep("dependency", "LaunchCheckDependency"));
        Steps.Add(new LaunchCheckStep("delisted", "LaunchCheckDelisted"));

        if (!_settingsService.Settings.SkipIntegrityCheck)
            Steps.Add(new LaunchCheckStep("integrity", "LaunchCheckIntegrity"));

        if (!_settingsService.Settings.SkipGameVersionCheck)
            Steps.Add(new LaunchCheckStep("compatibility", "LaunchCheckCompatibility"));

        if (!_settingsService.Settings.SkipManagerVersionCheck)
            Steps.Add(new LaunchCheckStep("managerVersion", "LaunchCheckManagerVersion", "ManagerUpdateAvailable", requiresOnline: true));

        CurrentStepIndex = Steps.Count > 0 ? 0 : -1;
        CanContinue = false;
        ShowContinueButton = false;
        ShowCancelButton = true;
        RefreshCurrentStepState();
    }

    [RelayCommand]
    public async Task StartCheckingAsync()
    {
        IsChecking = true;
        ShowCancelButton = true;
        ShowContinueButton = false;
        CanContinue = false;
        CurrentStatusMessage = string.Empty;

        for (int i = 0; i < Steps.Count; i++)
        {
            if (!IsChecking)
                break;

            CurrentStepIndex = i;
            var step = Steps[i];
            step.Details.Clear();
            step.Status = LaunchCheckStepStatus.InProgress;
            CurrentStatusMessage = Strings.LaunchCheckInProgress;
            RefreshCurrentStepState();

            await ExecuteStepAsync(step);
            RefreshCurrentStepState();
        }

        IsChecking = false;

        var hasIssues = Steps.Any(step => step.Status is LaunchCheckStepStatus.Warning or LaunchCheckStepStatus.Failed);
        if (!hasIssues)
        {
            CheckCompleted?.Invoke(this, new LaunchCheckCompletedEventArgs
            {
                CanLaunch = true,
                UserConfirmed = true,
                Results = Steps.ToList()
            });
            return;
        }

        FocusMostRelevantStep();
        CurrentStatusMessage = string.Empty;
        ShowContinueButton = true;
        RefreshCurrentStepState();
    }

    private async Task ExecuteStepAsync(LaunchCheckStep step)
    {
        try
        {
            var evaluation = step.Id switch
            {
                "gamePath" => await _launchCheckService.CheckGamePathAsync(_settingsService.Settings.GameRootPath),
                "modUpdate" => await _launchCheckService.CheckModUpdatesAsync(_allMods, IsOnlineMode),
                "conflict" => await _launchCheckService.CheckConflictsAsync(_allMods),
                "dependency" => await _launchCheckService.CheckDependenciesAsync(_allMods),
                "delisted" => await _launchCheckService.CheckDelistedModsAsync(_allMods),
                "integrity" => await _launchCheckService.CheckIntegrityAsync(),
                "compatibility" => await _launchCheckService.CheckGameVersionCompatibilityAsync(_settingsService.Settings.GameRootPath, _allMods),
                "managerVersion" => await _launchCheckService.CheckManagerVersionAsync(IsOnlineMode),
                _ => new LaunchCheckEvaluation { Status = LaunchCheckStepStatus.Passed }
            };

            step.Status = evaluation.Status;
            foreach (var detail in evaluation.Details)
                step.Details.Add(detail);
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, "Launch check step failed: {0}", step.Id);
            step.Status = LaunchCheckStepStatus.Failed;
            step.Details.Add(ex.Message);
        }
    }

    [RelayCommand]
    public void Continue()
    {
        CheckCompleted?.Invoke(this, new LaunchCheckCompletedEventArgs
        {
            CanLaunch = true,
            UserConfirmed = true,
            Results = Steps.ToList()
        });
    }

    [RelayCommand]
    public void SelectStep(LaunchCheckStep? step)
    {
        if (step == null)
            return;

        var index = Steps.IndexOf(step);
        if (index < 0)
            return;

        CurrentStepIndex = index;
        RefreshCurrentStepState();
    }

    [RelayCommand]
    public void Cancel()
    {
        IsChecking = false;
        CheckCompleted?.Invoke(this, new LaunchCheckCompletedEventArgs
        {
            CanLaunch = false,
            UserConfirmed = false,
            Results = Steps.ToList()
        });
    }

    private void FocusMostRelevantStep()
    {
        if (Steps.Count == 0)
        {
            CurrentStepIndex = -1;
            return;
        }

        var failedIndex = Steps.ToList().FindIndex(step => step.Status == LaunchCheckStepStatus.Failed);
        if (failedIndex >= 0)
        {
            CurrentStepIndex = failedIndex;
            return;
        }

        var warningIndex = Steps.ToList().FindIndex(step => step.Status == LaunchCheckStepStatus.Warning);
        if (warningIndex >= 0)
        {
            CurrentStepIndex = warningIndex;
            return;
        }

        CurrentStepIndex = Math.Clamp(CurrentStepIndex, 0, Steps.Count - 1);
    }

    private void RefreshCurrentStepState()
    {
        OnPropertyChanged(nameof(CurrentStep));
        OnPropertyChanged(nameof(CurrentStepTitle));
        OnPropertyChanged(nameof(CurrentStepStatusText));
        OnPropertyChanged(nameof(CurrentStepDetails));
        OnPropertyChanged(nameof(IssueSummary));
        OnPropertyChanged(nameof(HasIssueSummary));
    }

    private static string GetStatusText(LaunchCheckStepStatus status)
        => status switch
        {
            LaunchCheckStepStatus.InProgress => Strings.LaunchCheckInProgress,
            LaunchCheckStepStatus.Passed => Strings.LaunchCheckPassed,
            LaunchCheckStepStatus.Warning => Strings.Warning,
            LaunchCheckStepStatus.Failed => Strings.LaunchCheckFailed,
            LaunchCheckStepStatus.Skipped => Strings.LaunchCheckSkipped,
            _ => string.Empty
        };
}

using GHPC_Mod_Manager.Models;
using GHPC_Mod_Manager.Services;
using GHPC_Mod_Manager.ViewModels;
using System.Windows;
using System.Collections.Generic;

namespace GHPC_Mod_Manager.Views;

public partial class LaunchCheckWindow : Window
{
    private readonly LaunchCheckViewModel _viewModel;

    public LaunchCheckWindow(
        ISettingsService settingsService,
        ILaunchCheckService launchCheckService,
        ILoggingService loggingService,
        List<ModViewModel> allMods)
    {
        InitializeComponent();

        _viewModel = new LaunchCheckViewModel(
            settingsService,
            launchCheckService,
            loggingService);

        _viewModel.Initialize(allMods);
        _viewModel.CheckCompleted += OnCheckCompleted;

        DataContext = _viewModel;
    }

    private void OnCheckCompleted(object? sender, LaunchCheckCompletedEventArgs e)
    {
        if (e.UserConfirmed)
        {
            DialogResult = e.CanLaunch;
        }
        else
        {
            DialogResult = false;
        }
        Close();
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);

        // 自动开始检查
        _viewModel.StartCheckingCommand.Execute(null);
    }
}
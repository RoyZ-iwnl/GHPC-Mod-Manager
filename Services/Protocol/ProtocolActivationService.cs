using GHPC_Mod_Manager.Models;
using GHPC_Mod_Manager.Helpers;
using GHPC_Mod_Manager.Resources;
using GHPC_Mod_Manager.ViewModels;
using System.Text.RegularExpressions;
using System.Windows;

namespace GHPC_Mod_Manager.Services.Protocol
{
    /// <summary>
    /// 协议激活服务实现
    /// </summary>
    public sealed class ProtocolActivationService : IProtocolActivationService
    {
        private readonly ISettingsService _settingsService;
        private readonly IThemeService _themeService;
        private readonly ILoggingService _loggingService;
        private readonly INavigationService _navigationService;
        private readonly Lazy<MainViewModel> _mainViewModel;

        // Mod ID 只允许常见字符，防止外部链接带入奇怪内容
        private static readonly Regex ModIdPattern = new(@"^[A-Za-z0-9_.\-]{1,64}$", RegexOptions.Compiled);

        // 同一时间只处理一个Mod链接，避免弹出多个确认框（只在UI线程读写）
        private bool _isHandlingModRoute;

        public ProtocolActivationService(
            ISettingsService settingsService,
            IThemeService themeService,
            ILoggingService loggingService,
            INavigationService navigationService,
            Lazy<MainViewModel> mainViewModel)
        {
            _settingsService = settingsService;
            _themeService = themeService;
            _loggingService = loggingService;
            _navigationService = navigationService;
            _mainViewModel = mainViewModel;
        }

        public async Task HandleAsync(string protocolUri)
        {
            if (!Uri.TryCreate(protocolUri, UriKind.Absolute, out var uri))
            {
                _loggingService.LogWarning($"Invalid protocol URI: {protocolUri}");
                return;
            }

            if (!string.Equals(uri.Scheme, "ghpcmm", StringComparison.OrdinalIgnoreCase))
            {
                _loggingService.LogWarning($"Unsupported protocol scheme: {uri.Scheme}");
                return;
            }

            // 检查是否为解锁终末地主题路由
            if (IsUnlockEndfieldRoute(uri))
            {
                await UnlockAndApplyEndfieldThemeAsync();
                return;
            }

            // Mod 路由: ghpcmm://mod/{install|update|open}/{ModId}
            if (uri.Host.Equals("mod", StringComparison.OrdinalIgnoreCase))
            {
                HandleModRoute(uri, protocolUri);
                return;
            }

            _loggingService.LogWarning($"Unhandled protocol route: {protocolUri}");
        }

        private static bool IsUnlockEndfieldRoute(Uri uri)
        {
            // 标准格式: ghpcmm://theme/unlock-endfield
            return uri.Host.Equals("theme", StringComparison.OrdinalIgnoreCase) &&
                   uri.AbsolutePath.Equals("/unlock-endfield", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 解析Mod路由，例如 ghpcmm://mod/install/GHPCCO
        /// </summary>
        /// <returns>格式不对时返回null</returns>
        public static (ProtocolModAction Action, string ModId)? ParseModRoute(Uri uri)
        {
            // 浏览器有时会在末尾补 "/"，统一去掉
            // segments = ["install", "GHPCCO"]
            var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length != 2)
                return null;

            ProtocolModAction? action = segments[0].ToLowerInvariant() switch
            {
                "install" => ProtocolModAction.Install,
                "update" => ProtocolModAction.Update,
                "open" => ProtocolModAction.Open,
                _ => null
            };

            var modId = Uri.UnescapeDataString(segments[1]);
            if (action == null || !ModIdPattern.IsMatch(modId))
                return null;

            return (action.Value, modId);
        }

        private void HandleModRoute(Uri uri, string protocolUri)
        {
            // 安装可能持续数分钟，这里不等待，否则命名管道和启动流程都会被卡住
            _ = Application.Current.Dispatcher.InvokeAsync(async () =>
            {
                var started = false;
                try
                {
                    ActivateMainWindow();

                    var route = ParseModRoute(uri);
                    if (route == null)
                    {
                        _loggingService.LogWarning($"Unhandled protocol route: {protocolUri}");
                        MessageDialogHelper.ShowWarning(string.Format(Strings.ProtocolInvalidLink, protocolUri), Strings.ProtocolDialogTitle);
                        return;
                    }

                    if (_isHandlingModRoute)
                    {
                        MessageDialogHelper.ShowInformation(Strings.ProtocolBusy, Strings.ProtocolDialogTitle);
                        return;
                    }

                    // 首次运行还没有游戏目录和Mod列表
                    if (_settingsService.Settings.IsFirstRun)
                    {
                        MessageDialogHelper.ShowInformation(Strings.ProtocolFinishSetupFirst, Strings.ProtocolDialogTitle);
                        return;
                    }

                    _isHandlingModRoute = true;
                    started = true;
                    _loggingService.LogInfo(Strings.ProtocolModRouteReceived, route.Value.Action, route.Value.ModId);

                    // 设置页打开时先回到主界面
                    _navigationService.NavigateToMainView();
                    await _mainViewModel.Value.HandleModProtocolAsync(route.Value.Action, route.Value.ModId);
                }
                catch (Exception ex)
                {
                    _loggingService.LogError(ex, "Protocol mod route failed: {0}", protocolUri);
                }
                finally
                {
                    if (started)
                        _isHandlingModRoute = false;
                }
            });
        }

        // 激活并前置主窗口
        private static void ActivateMainWindow()
        {
            var window = Application.Current.MainWindow;
            if (window == null)
                return;

            if (window.WindowState == WindowState.Minimized)
                window.WindowState = WindowState.Normal;
            window.Activate();

            // 从全屏游戏里点链接时 Activate 不一定能抢到前台，短暂置顶一次
            window.Topmost = true;
            window.Topmost = false;
        }

        private async Task UnlockAndApplyEndfieldThemeAsync()
        {
            _settingsService.Settings.IsEndfieldThemeUnlocked = true;
            _settingsService.Settings.Theme = AppTheme.Endfield;
            await _settingsService.SaveSettingsAsync();

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                _themeService.SetTheme(AppTheme.Endfield);

                _loggingService.LogInfo("Protocol activation: Endfield theme unlocked and applied");

                // 激活并前置主窗口
                var window = Application.Current.MainWindow;
                if (window != null)
                {
                    if (window.WindowState == WindowState.Minimized)
                        window.WindowState = WindowState.Normal;
                    window.Activate();
                }
            });
        }
    }
}

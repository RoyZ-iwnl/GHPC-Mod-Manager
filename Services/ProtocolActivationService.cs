using GHPC_Mod_Manager.Models;
using System.Windows;

namespace GHPC_Mod_Manager.Services
{
    /// <summary>
    /// 协议激活服务实现
    /// </summary>
    public sealed class ProtocolActivationService : IProtocolActivationService
    {
        private readonly ISettingsService _settingsService;
        private readonly IThemeService _themeService;
        private readonly ILoggingService _loggingService;

        public ProtocolActivationService(
            ISettingsService settingsService,
            IThemeService themeService,
            ILoggingService loggingService)
        {
            _settingsService = settingsService;
            _themeService = themeService;
            _loggingService = loggingService;
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

            _loggingService.LogWarning($"Unhandled protocol route: {protocolUri}");
        }

        private static bool IsUnlockEndfieldRoute(Uri uri)
        {
            // 标准格式: ghpcmm://theme/unlock-endfield
            return uri.Host.Equals("theme", StringComparison.OrdinalIgnoreCase) &&
                   uri.AbsolutePath.Equals("/unlock-endfield", StringComparison.OrdinalIgnoreCase);
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

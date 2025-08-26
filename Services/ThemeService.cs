using GHPC_Mod_Manager.Models;
using System.Windows;
using GHPC_Mod_Manager.Resources;

namespace GHPC_Mod_Manager.Services;

public interface IThemeService
{
    AppTheme CurrentTheme { get; }
    event EventHandler<AppTheme>? ThemeChanged;
    void SetTheme(AppTheme theme);
    void ApplyTheme(AppTheme theme);
    void InitializeWithCurrentState(AppTheme actualTheme); // 新增方法来同步状态
}

public class ThemeService : IThemeService
{
    private readonly ILoggingService _loggingService;
    private AppTheme _currentTheme = AppTheme.Light;

    public AppTheme CurrentTheme => _currentTheme;
    public event EventHandler<AppTheme>? ThemeChanged;

    public ThemeService(ILoggingService loggingService)
    {
        _loggingService = loggingService;
        _loggingService.LogInfo(Strings.ThemeServiceConstructor, _currentTheme);
    }

    public void InitializeWithCurrentState(AppTheme actualTheme)
    {
        _loggingService.LogInfo(Strings.ThemeServiceInitializeState, actualTheme);
        _currentTheme = actualTheme;
    }

    public void SetTheme(AppTheme theme)
    {
        _loggingService.LogInfo(Strings.SetThemeCalled, theme, _currentTheme);
        
        // 总是应用主题，确保同步
        _currentTheme = theme;
        ApplyTheme(theme);
        ThemeChanged?.Invoke(this, theme);
        _loggingService.LogInfo(Strings.ThemeSwitched, theme);
    }

    public void ApplyTheme(AppTheme theme)
    {
        try
        {
            _loggingService.LogInfo(Strings.ApplyThemeStarted, theme);
            
            // 清除所有现有主题资源字典
            var toRemove = Application.Current.Resources.MergedDictionaries
                .Where(d => d.Source?.ToString().Contains("/Themes/") == true)
                .ToList();
            
            _loggingService.LogInfo(Strings.PreparingToRemoveResourceDictionaries, toRemove.Count);
            foreach (var dict in toRemove)
            {
                _loggingService.LogInfo(Strings.RemovingResourceDictionary, dict.Source);
                Application.Current.Resources.MergedDictionaries.Remove(dict);
            }

            _loggingService.LogInfo(Strings.RemovedResourceDictionaries, toRemove.Count);

            // 加载新主题资源
            var themeResourcePath = theme switch
            {
                AppTheme.Light => "/Themes/LightTheme.xaml",
                AppTheme.Dark => "/Themes/DarkTheme.xaml",
                _ => "/Themes/LightTheme.xaml"
            };

            _loggingService.LogInfo(Strings.LoadingThemeResource, themeResourcePath);

            var themeResource = new ResourceDictionary
            {
                Source = new Uri(themeResourcePath, UriKind.Relative)
            };

            // 加载通用控件样式
            var controlStyles = new ResourceDictionary
            {
                Source = new Uri("/Themes/ControlStyles.xaml", UriKind.Relative)
            };

            // 重新构建资源字典 - 确保主题颜色在前，控件样式在后
            Application.Current.Resources.MergedDictionaries.Add(themeResource);
            Application.Current.Resources.MergedDictionaries.Add(controlStyles);

            _loggingService.LogInfo(Strings.ThemeResourceApplied, theme, Application.Current.Resources.MergedDictionaries.Count);
            
            // 强制刷新UI
            Application.Current.Dispatcher.BeginInvoke(() =>
            {
                _loggingService.LogInfo(Strings.StartingForcedUIRefresh);
                // 触发UI刷新
                foreach (Window window in Application.Current.Windows)
                {
                    window.InvalidateVisual();
                    window.UpdateLayout();
                }
                _loggingService.LogInfo(Strings.ForcedUIRefreshComplete);
            }, System.Windows.Threading.DispatcherPriority.Background);
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.ThemeApplicationError);
        }
    }
}
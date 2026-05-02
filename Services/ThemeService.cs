using GHPC_Mod_Manager.Models;
using System.Windows;
using GHPC_Mod_Manager.Resources;
using GHPC_Mod_Manager.Helpers;

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
        ThemeTracker.Instance.CurrentTheme = actualTheme;
    }

    public void SetTheme(AppTheme theme)
    {
        _loggingService.LogInfo(Strings.SetThemeCalled, theme, _currentTheme);
        if (_currentTheme == theme)
        {
            return;
        }

        _currentTheme = theme;
        ApplyTheme(theme);
        ThemeTracker.Instance.CurrentTheme = theme;

        ThemeChanged?.Invoke(this, theme);
        _loggingService.LogInfo(Strings.ThemeSwitched, theme);
    }

    public void ApplyTheme(AppTheme theme)
    {
        try
        {
            _loggingService.LogInfo(Strings.ApplyThemeStarted, theme);

            // 只移除主题相关的资源字典（LightTheme/DarkTheme），保留通用资源
            var toRemove = Application.Current.Resources.MergedDictionaries
                .Where(d => d.Source?.ToString().Contains("LightTheme.xaml") == true ||
                           d.Source?.ToString().Contains("DarkTheme.xaml") == true ||
                           d.Source?.ToString().Contains("EndfieldTheme.xaml") == true)
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
                AppTheme.Endfield => "/Themes/EndfieldTheme.xaml",
                _ => "/Themes/LightTheme.xaml"
            };

            _loggingService.LogInfo(Strings.LoadingThemeResource, themeResourcePath);

            var themeResource = new ResourceDictionary
            {
                Source = new Uri(themeResourcePath, UriKind.Relative)
            };

            // 将新主题资源添加到最后，确保它能覆盖所有其他样式
            Application.Current.Resources.MergedDictionaries.Add(themeResource);

            _loggingService.LogInfo(Strings.ThemeResourceApplied, theme, Application.Current.Resources.MergedDictionaries.Count);
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.ThemeApplicationError);
        }
    }
}

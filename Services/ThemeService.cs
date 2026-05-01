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

            // 只移除主题相关的资源字典（LightTheme/DarkTheme），保留通用资源
            var toRemove = Application.Current.Resources.MergedDictionaries
                .Where(d => d.Source?.ToString().Contains("LightTheme.xaml") == true ||
                           d.Source?.ToString().Contains("DarkTheme.xaml") == true)
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

            // 插入新主题资源到Foundation之后（保持正确的加载顺序）
            var foundationIndex = Application.Current.Resources.MergedDictionaries
                .Select((d, i) => new { Dict = d, Index = i })
                .FirstOrDefault(x => x.Dict.Source?.ToString().Contains("Foundation.xaml") == true)?.Index ?? 0;

            Application.Current.Resources.MergedDictionaries.Insert(foundationIndex + 1, themeResource);

            _loggingService.LogInfo(Strings.ThemeResourceApplied, theme, Application.Current.Resources.MergedDictionaries.Count);
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.ThemeApplicationError);
        }
    }
}

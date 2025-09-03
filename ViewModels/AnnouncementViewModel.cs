using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GHPC_Mod_Manager.Resources;
using GHPC_Mod_Manager.Services;
using GHPC_Mod_Manager.Models;
using Markdig;

namespace GHPC_Mod_Manager.ViewModels;

public partial class AnnouncementViewModel : ObservableObject
{
    private readonly IAnnouncementService _announcementService;
    private readonly ILoggingService _loggingService;
    private readonly IThemeService _themeService;

    [ObservableProperty]
    private string _markdownContent = string.Empty;

    [ObservableProperty]
    private string _htmlContent = string.Empty;

    [ObservableProperty]
    private bool _isLoading = true;

    [ObservableProperty]
    private bool _hasContent = false;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    public IRelayCommand? CloseCommand { get; set; }

    public AnnouncementViewModel(IAnnouncementService announcementService, ILoggingService loggingService, IThemeService themeService)
    {
        _announcementService = announcementService;
        _loggingService = loggingService;
        _themeService = themeService;
    }

    public async Task LoadAnnouncementAsync(string language)
    {
        try
        {
            IsLoading = true;
            HasContent = false;
            ErrorMessage = string.Empty;

            var markdownContent = await _announcementService.GetAnnouncementAsync(language);
            
            if (!string.IsNullOrWhiteSpace(markdownContent))
            {
                MarkdownContent = markdownContent;
                
                // Convert Markdown to HTML for display
                var pipeline = new MarkdownPipelineBuilder()
                    .UseAdvancedExtensions()
                    .Build();
                    
                HtmlContent = Markdown.ToHtml(markdownContent, pipeline);
                HasContent = true;
                
                _loggingService.LogInfo(Strings.AnnouncementLoadedSuccessfully, language);
            }
            else
            {
                HasContent = false;
                _loggingService.LogInfo(Strings.NoAnnouncementAvailable, language);
            }
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.AnnouncementLoadFailed);
            ErrorMessage = Strings.AnnouncementLoadFailed;
            HasContent = false;
        }
        finally
        {
            IsLoading = false;
        }
    }

    public (string backgroundColor, string textColor, string linkColor, string codeBackgroundColor, string borderColor) GetThemeColors()
    {
        var currentTheme = _themeService.CurrentTheme;
        var isDarkTheme = currentTheme == AppTheme.Dark;
        
        Console.WriteLine($"[DEBUG] AnnouncementViewModel.GetThemeColors() - CurrentTheme: {currentTheme}, IsDark: {isDarkTheme}");
        _loggingService.LogInfo("Getting theme colors for current theme: {0}, isDark: {1}", currentTheme, isDarkTheme);
        
        if (isDarkTheme)
        {
            var colors = (
                backgroundColor: "#10101A",     // Dark Surface
                textColor: "#E6E1E5",         // Dark OnSurface
                linkColor: "#D0BCFF",         // Dark Primary
                codeBackgroundColor: "#211F26", // Dark SurfaceContainer
                borderColor: "#938F99"        // Dark Outline
            );
            Console.WriteLine($"[DEBUG] Using dark theme colors: Background={colors.backgroundColor}, Text={colors.textColor}");
            _loggingService.LogInfo("Using dark theme colors: {0}", colors.backgroundColor);
            return colors;
        }
        else
        {
            var colors = (
                backgroundColor: "#FFFBFE",     // Light Surface
                textColor: "#1C1B1F",         // Light OnSurface
                linkColor: "#6750A4",         // Light Primary
                codeBackgroundColor: "#F3EDF7", // Light SurfaceContainer
                borderColor: "#79747E"        // Light Outline
            );
            Console.WriteLine($"[DEBUG] Using light theme colors: Background={colors.backgroundColor}, Text={colors.textColor}");
            _loggingService.LogInfo("Using light theme colors: {0}", colors.backgroundColor);
            return colors;
        }
    }
}
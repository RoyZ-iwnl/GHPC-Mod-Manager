using GHPC_Mod_Manager.ViewModels;
using GHPC_Mod_Manager.Services;
using System.Windows;
using System.Diagnostics;
using Microsoft.Web.WebView2.Core;
using System.Windows.Media;

namespace GHPC_Mod_Manager.Views;

public partial class AnnouncementWindow : Window
{
    private bool _isWebViewInitialized = false;

    public AnnouncementWindow()
    {
        InitializeComponent();
        Loaded += Window_Loaded;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            // Initialize WebView2 asynchronously
            await AnnouncementWebView.EnsureCoreWebView2Async(null);
            _isWebViewInitialized = true;

            // Set up navigation event handler for external links
            AnnouncementWebView.CoreWebView2.NewWindowRequested += CoreWebView2_NewWindowRequested;

            // Set up WebMessageReceived to handle external links from JavaScript
            AnnouncementWebView.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;

            if (DataContext is AnnouncementViewModel viewModel)
            {
                // Load HTML content into WebView2 when HasContent changes
                viewModel.PropertyChanged += (s, args) =>
                {
                    if (args.PropertyName == nameof(AnnouncementViewModel.HtmlContent) && !string.IsNullOrEmpty(viewModel.HtmlContent))
                    {
                        Dispatcher.Invoke(() =>
                        {
                            LoadContentIntoWebView(viewModel.HtmlContent);
                        });
                    }
                };

                // Listen for theme changes and refresh content
                if (App.GetService<IThemeService>() is IThemeService themeService)
                {
                    themeService.ThemeChanged += (s, theme) =>
                    {
                        if (!string.IsNullOrEmpty(viewModel.HtmlContent))
                        {
                            Dispatcher.Invoke(() =>
                            {
                                LoadContentIntoWebView(viewModel.HtmlContent);
                            });
                        }
                    };
                }

                // Check if content is already loaded
                if (!string.IsNullOrEmpty(viewModel.HtmlContent))
                {
                    LoadContentIntoWebView(viewModel.HtmlContent);
                }

                // Handle close command - use a simple action
                viewModel.CloseCommand = new CommunityToolkit.Mvvm.Input.RelayCommand(() => Close());
            }
        }
        catch
        {
            // WebView2 初始化失败，降级显示错误信息
        }
    }

    private void LoadContentIntoWebView(string htmlContent)
    {
        if (!_isWebViewInitialized)
            return;

        try
        {
            AnnouncementWebView.NavigateToString(ApplyThemeToHtml(htmlContent));
        }
        catch
        {
            // 加载内容失败，静默处理
        }
    }

    private void CoreWebView2_WebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var message = e.TryGetWebMessageAsString();
            if (!string.IsNullOrEmpty(message) && (message.StartsWith("http://") || message.StartsWith("https://")))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = message,
                    UseShellExecute = true
                });
            }
        }
        catch
        {
            // 打开外部链接失败，静默处理
        }
    }

    private void CoreWebView2_NewWindowRequested(object sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = e.Uri,
                UseShellExecute = true
            });
            e.Handled = true;
        }
        catch
        {
            // 打开外部链接失败，静默处理
        }
    }

    private void AnnouncementWebView_NavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        // WebView2 导航完成，无需额外处理
    }

    private async void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is AnnouncementViewModel viewModel)
        {
            await viewModel.SaveSettingsAsync();
        }
        Close();
    }

    private string ApplyThemeToHtml(string htmlContent)
    {
        var pageBackground = GetBrushHex("SurfaceBrush", "#151C24");
        var surfaceBackground = GetBrushHex("SurfaceBrush", "#151C24");
        var surfaceAltBackground = GetBrushHex("SurfaceAltBrush", "#1D2732");
        var surfaceContainer = GetBrushHex("SurfaceContainerBrush", "#202B37");
        var textColor = GetBrushHex("OnSurfaceBrush", "#F3F5F7");
        var mutedTextColor = GetBrushHex("OnSurfaceVariantBrush", "#B2BCC7");
        var linkColor = GetBrushHex("AccentBrush", "#4B7BE5");
        var borderColor = GetBrushHex("OutlineVariantBrush", "#344252");
        var codeBorderColor = GetBrushHex("OutlineBrush", borderColor);
        var isDarkTheme = App.GetService<IThemeService>()?.CurrentTheme == Models.AppTheme.Dark;

        var themeCss = $@"
<style id=""ghpc-theme-overrides"">
    :root {{
        color-scheme: {(isDarkTheme ? "dark" : "light")};
    }}

    html {{
        background: {pageBackground} !important;
    }}

    body {{
        margin: 18px !important;
        background: {surfaceBackground} !important;
        color: {textColor} !important;
    }}

    h1, h2, h3, h4, h5, h6,
    p, li, span, div, strong, em {{
        color: {textColor} !important;
    }}

    h1, h2 {{
        border-color: {borderColor} !important;
    }}

    a {{
        color: {linkColor} !important;
    }}

    a:hover {{
        color: {linkColor} !important;
        opacity: 0.86 !important;
    }}

    blockquote {{
        background: {surfaceAltBackground} !important;
        border-left-color: {linkColor} !important;
        color: {textColor} !important;
    }}

    pre {{
        background: {surfaceAltBackground} !important;
        color: {textColor} !important;
        border: 1px solid {codeBorderColor} !important;
    }}

    code {{
        background: {surfaceContainer} !important;
        color: {textColor} !important;
    }}

    table {{
        background: transparent !important;
    }}

    th {{
        background: {surfaceAltBackground} !important;
        color: {textColor} !important;
        border-color: {borderColor} !important;
    }}

    td {{
        background: {surfaceBackground} !important;
        color: {textColor} !important;
        border-color: {borderColor} !important;
    }}

    tr:nth-child(even) {{
        background: transparent !important;
    }}

    hr {{
        border-top-color: {borderColor} !important;
    }}

    img {{
        background: transparent !important;
    }}

    ::-webkit-scrollbar-track {{
        background: {surfaceAltBackground} !important;
    }}

    ::-webkit-scrollbar-thumb {{
        background: {borderColor} !important;
    }}

    ::-webkit-scrollbar-thumb:hover {{
        background: {linkColor} !important;
    }}

    * {{
        scrollbar-color: {borderColor} {surfaceAltBackground} !important;
    }}

    .muted, small {{
        color: {mutedTextColor} !important;
    }}
</style>";

        var headCloseTagIndex = htmlContent.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
        if (headCloseTagIndex >= 0)
        {
            return htmlContent.Insert(headCloseTagIndex, themeCss);
        }

        return $"{themeCss}{htmlContent}";
    }

    private static string GetBrushHex(string resourceKey, string fallback)
    {
        if (Application.Current.TryFindResource(resourceKey) is SolidColorBrush brush)
        {
            return ToHex(brush.Color);
        }

        if (Application.Current.TryFindResource(resourceKey) is Color color)
        {
            return ToHex(color);
        }

        return fallback;
    }

    private static string ToHex(Color color)
    {
        return color.A == byte.MaxValue
            ? $"#{color.R:X2}{color.G:X2}{color.B:X2}"
            : $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";
    }
}

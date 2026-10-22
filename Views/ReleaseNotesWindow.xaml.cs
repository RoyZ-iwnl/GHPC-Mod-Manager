using GHPC_Mod_Manager.Services;
using GHPC_Mod_Manager.Resources;
using Ganss.Xss;
using Markdig;
using Microsoft.Web.WebView2.Core;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;

namespace GHPC_Mod_Manager.Views;

public partial class ReleaseNotesWindow : Window
{
    private readonly string _releaseTag;
    private readonly string _markdown;
    private readonly string _baseUrl;
    private bool _webViewReady;
    private IThemeService? _themeService;

    public ReleaseNotesWindow(string releaseTag, string markdown, string? baseUrl = null)
    {
        _releaseTag = releaseTag;
        _markdown = markdown;
        _baseUrl = baseUrl ?? "https://github.com/";
        InitializeComponent();
        Title = $"{Strings.ReleaseNotesTitle} - {releaseTag}";
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await ReleaseNotesWebView.EnsureCoreWebView2Async();
            _webViewReady = true;
            ReleaseNotesWebView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
            ReleaseNotesWebView.CoreWebView2.NewWindowRequested += OnNewWindowRequested;

            _themeService = App.GetService<IThemeService>();
            if (_themeService != null)
                _themeService.ThemeChanged += OnThemeChanged;

            RenderMarkdown();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Strings.ReleaseNotesTitle, MessageBoxButton.OK, MessageBoxImage.Error);
            Close();
        }
    }

    private void OnThemeChanged(object? sender, Models.AppTheme theme)
    {
        Dispatcher.Invoke(RenderMarkdown);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (_themeService != null)
            _themeService.ThemeChanged -= OnThemeChanged;
    }

    private void RenderMarkdown()
    {
        if (!_webViewReady)
            return;

        var pipeline = new MarkdownPipelineBuilder()
            .UsePipeTables()
            .UseTaskLists()
            .Build();
        var renderedMarkdown = Markdown.ToHtml(_markdown, pipeline);
        var sanitizer = new HtmlSanitizer();
        sanitizer.AllowedTags.Add("thead");
        sanitizer.AllowedTags.Add("tbody");
        sanitizer.AllowedTags.Add("tfoot");
        sanitizer.AllowedTags.Add("caption");
        sanitizer.AllowedTags.Add("colgroup");
        sanitizer.AllowedTags.Add("col");
        sanitizer.AllowedTags.Add("del");
        sanitizer.AllowedTags.Add("input");
        sanitizer.AllowedAttributes.Add("clear");
        sanitizer.AllowedAttributes.Add("align");
        sanitizer.AllowedAttributes.Add("width");
        sanitizer.AllowedAttributes.Add("height");
        sanitizer.AllowedAttributes.Add("checked");
        sanitizer.AllowedAttributes.Add("disabled");
        sanitizer.AllowedSchemes.Add("https");
        sanitizer.AllowedSchemes.Add("http");
        renderedMarkdown = sanitizer.Sanitize(renderedMarkdown);

        var background = GetBrushHex("SurfaceBrush", "#FFFFFF");
        var foreground = GetBrushHex("OnSurfaceBrush", "#202124");
        var muted = GetBrushHex("OnSurfaceVariantBrush", "#5F6368");
        var accent = GetBrushHex("AccentBrush", "#1769AA");
        var border = GetBrushHex("OutlineVariantBrush", "#D5D9DE");
        var codeBackground = GetBrushHex("SurfaceAltBrush", "#F2F4F6");

        var html = $$"""
            <!DOCTYPE html>
            <html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <base href="{{System.Net.WebUtility.HtmlEncode(_baseUrl)}}">
            <style>
            :root { color-scheme: light dark; }
            body { margin: 20px; font-family: 'Segoe UI', 'Microsoft YaHei UI', sans-serif; line-height: 1.65; background: {{background}}; color: {{foreground}}; overflow-wrap: anywhere; }
            h1,h2,h3,h4,h5,h6 { line-height: 1.3; margin: 1.3em 0 .55em; }
            h1,h2 { border-bottom: 1px solid {{border}}; padding-bottom: .35em; }
            p,ul,ol,blockquote,table,pre { margin: .9em 0; }
            a { color: {{accent}}; }
            img { max-width: 100%; height: auto; }
            pre { overflow-x: auto; padding: 14px; border: 1px solid {{border}}; background: {{codeBackground}}; border-radius: 4px; }
            code { font-family: Consolas, monospace; background: {{codeBackground}}; padding: 2px 4px; border-radius: 3px; }
            pre code { padding: 0; }
            blockquote { margin-left: 0; padding: 2px 14px; border-left: 3px solid {{accent}}; color: {{muted}}; }
            table { border-collapse: collapse; max-width: 100%; display: block; overflow-x: auto; }
            th,td { border: 1px solid {{border}}; padding: 7px 10px; text-align: left; }
            th { background: {{codeBackground}}; }
            hr { border: 0; border-top: 1px solid {{border}}; }
            </style></head><body><h1>{{System.Net.WebUtility.HtmlEncode(_releaseTag)}}</h1>{{renderedMarkdown}}
            <script>
            document.addEventListener('click', function (event) {
                const link = event.target.closest('a');
                if (!link || !link.href || !/^https?:$/i.test(link.protocol)) return;
                event.preventDefault();
                window.chrome.webview.postMessage(link.href);
            });
            </script></body></html>
            """;

        ReleaseNotesWebView.NavigateToString(html);
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        var url = e.TryGetWebMessageAsString();
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            OpenExternalUrl(uri);
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            OpenExternalUrl(uri);
    }

    private static void OpenExternalUrl(Uri uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch
        {
            // 外链打开失败时保持弹窗不受影响。
        }
    }

    private static string GetBrushHex(string resourceKey, string fallback)
    {
        if (Application.Current.TryFindResource(resourceKey) is SolidColorBrush brush)
            return $"#{brush.Color.R:X2}{brush.Color.G:X2}{brush.Color.B:X2}";
        if (Application.Current.TryFindResource(resourceKey) is Color color)
            return $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        return fallback;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}

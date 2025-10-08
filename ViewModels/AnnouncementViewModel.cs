using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GHPC_Mod_Manager.Resources;
using GHPC_Mod_Manager.Services;
using GHPC_Mod_Manager.Models;

namespace GHPC_Mod_Manager.ViewModels;

public partial class AnnouncementViewModel : ObservableObject
{
    private readonly IAnnouncementService _announcementService;
    private readonly ILoggingService _loggingService;
    private readonly IThemeService _themeService;

    [ObservableProperty]
    private string _markdownContent = string.Empty;

    [ObservableProperty]
    private string _htmlContent = string.Empty; // Will contain HTML with embedded markdown parser

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

                // Create HTML with embedded marked.js for client-side markdown parsing
                HtmlContent = CreateMarkdownHtml(markdownContent);
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
        _loggingService.LogInfo(Strings.GettingThemeColorsForCurrentTheme, currentTheme, isDarkTheme);
        
        if (isDarkTheme)
        {
            var colors = (
                backgroundColor: "#10101A",     // Dark Surface
                textColor: "#E6E1E5",         // Dark OnSurface
                linkColor: "#BB86FC",         // Dark Primary (better contrast)
                codeBackgroundColor: "#211F26", // Dark SurfaceContainer
                borderColor: "#938F99"        // Dark Outline
            );
            Console.WriteLine($"[DEBUG] Using dark theme colors: Background={colors.backgroundColor}, Text={colors.textColor}");
            _loggingService.LogInfo(Strings.UsingDarkThemeColors, colors.backgroundColor);
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
            _loggingService.LogInfo(Strings.UsingLightThemeColors, colors.backgroundColor);
            return colors;
        }
    }

    private string CreateMarkdownHtml(string markdown)
    {
        // 使用服务器端markdown解析，避免JavaScript复杂性
        var htmlContent = ConvertMarkdownToHtml(markdown);

        return $@"
<!DOCTYPE html>
<html>
<head>
    <meta charset='utf-8'>
    <meta name='viewport' content='width=device-width, initial-scale=1'>
    <base href='about:blank'>
    <style>
        body {{
            font-family: 'Microsoft YaHei UI', 'Segoe UI', Arial, sans-serif;
            margin: 20px;
            line-height: 1.6;
            color: {GetThemeColors().textColor};
            background-color: {GetThemeColors().backgroundColor};
        }}

        h1, h2, h3, h4, h5, h6 {{
            color: {GetThemeColors().textColor};
            opacity: 0.9;
            margin-top: 1.5em;
            margin-bottom: 0.5em;
            scroll-margin-top: 20px;
        }}

        h1 {{
            font-size: 2em;
            border-bottom: 2px solid {GetThemeColors().borderColor};
            padding-bottom: 0.3em;
        }}

        h2 {{
            font-size: 1.5em;
            border-bottom: 1px solid {GetThemeColors().borderColor};
            padding-bottom: 0.2em;
        }}

        h3 {{ font-size: 1.25em; }}

        p {{ margin: 1em 0; }}

        blockquote {{
            border-left: 4px solid {GetThemeColors().linkColor};
            margin: 1em 0;
            padding: 10px 20px;
            background-color: {GetThemeColors().codeBackgroundColor};
            border-radius: 4px;
        }}

        code {{
            background-color: {GetThemeColors().codeBackgroundColor};
            color: {GetThemeColors().textColor};
            padding: 2px 6px;
            border-radius: 3px;
            font-family: 'Consolas', 'Courier New', monospace;
            font-size: 0.9em;
        }}

        pre {{
            background-color: {GetThemeColors().codeBackgroundColor};
            color: {GetThemeColors().textColor};
            padding: 15px;
            border-radius: 5px;
            overflow-x: auto;
            border: 1px solid {GetThemeColors().borderColor};
            font-family: 'Consolas', 'Courier New', monospace;
            font-size: 0.9em;
            line-height: 1.4;
        }}

        a {{
            color: {GetThemeColors().linkColor};
            text-decoration: none;
            transition: color 0.2s ease;
        }}

        a:hover {{
            color: {GetThemeColors().linkColor};
            text-decoration: underline;
            opacity: 0.8;
        }}

        table {{
            border-collapse: collapse;
            width: 100%;
            margin: 1em 0;
            font-size: 0.9em;
        }}

        th, td {{
            border: 1px solid {GetThemeColors().borderColor};
            padding: 12px;
            text-align: left;
        }}

        th {{
            background-color: {GetThemeColors().codeBackgroundColor};
            font-weight: 600;
        }}

        tr:nth-child(even) {{
            background-color: {GetThemeColors().codeBackgroundColor};
        }}

        ul, ol {{ margin: 1em 0; padding-left: 2em; }}

        li {{ margin: 0.5em 0; }}

        hr {{
            border: none;
            border-top: 1px solid {GetThemeColors().borderColor};
            margin: 2em 0;
        }}

        img {{
            max-width: 100%;
            height: auto;
            border-radius: 4px;
            margin: 1em 0;
        }}

        /* Smooth scrolling */
        html {{ scroll-behavior: smooth; }}

        /* Custom scrollbar styling to match app theme */
        ::-webkit-scrollbar {{
            width: 8px;
            height: 8px;
        }}

        ::-webkit-scrollbar-track {{
            background: {GetThemeColors().codeBackgroundColor};
            border-radius: 4px;
        }}

        ::-webkit-scrollbar-thumb {{
            background: {GetThemeColors().borderColor};
            border-radius: 4px;
            transition: background 0.2s ease;
        }}

        ::-webkit-scrollbar-thumb:hover {{
            background: {GetThemeColors().linkColor};
            opacity: 0.8;
        }}

        /* Firefox scrollbar styling */
        * {{
            scrollbar-width: thin;
            scrollbar-color: {GetThemeColors().borderColor} {GetThemeColors().codeBackgroundColor};
        }}

        /* Responsive design */
        @media (max-width: 768px) {{
            body {{ margin: 10px; }}
            table {{ font-size: 0.8em; }}
            th, td {{ padding: 8px; }}
        }}
    </style>
</head>
<body>
    {htmlContent}

    <script>
        // 简单的锚点处理，只处理外部链接
        document.addEventListener('click', function(e) {{
            const target = e.target.closest('a');
            if (target && target.href) {{
                const href = target.getAttribute('href');

                // 只处理外部链接，内部锚点让浏览器自然处理
                if (href.startsWith('http://') || href.startsWith('https://')) {{
                    e.preventDefault();
                    window.external.notify(href);
                }}
                // 内部锚点链接不阻止默认行为，让浏览器处理
            }}
        }});

        // 页面加载时处理hash
        window.addEventListener('load', function() {{
            if (window.location.hash) {{
                setTimeout(function() {{
                    const element = document.querySelector(window.location.hash);
                    if (element) {{
                        element.scrollIntoView({{ behavior: 'smooth' }});
                    }}
                }}, 100);
            }}
        }});
    </script>
</body>
</html>";
    }

    private string ConvertMarkdownToHtml(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return string.Empty;

        var lines = markdown.Split('\n');
        var html = new System.Text.StringBuilder();
        var inCodeBlock = false;
        var codeBlockLang = "";

        foreach (var line in lines)
        {
            var trimmedLine = line.Trim();

            // 代码块处理
            if (trimmedLine.StartsWith("```"))
            {
                if (!inCodeBlock)
                {
                    inCodeBlock = true;
                    codeBlockLang = trimmedLine.Substring(3).Trim();
                    html.AppendLine($"<pre><code class=\"language-{codeBlockLang}\">");
                }
                else
                {
                    inCodeBlock = false;
                    html.AppendLine("</code></pre>");
                }
                continue;
            }

            if (inCodeBlock)
            {
                html.AppendLine(HtmlEncode(line));
                continue;
            }

            // 标题处理并生成ID
            if (trimmedLine.StartsWith("# "))
            {
                var text = trimmedLine.Substring(2).Trim();
                var id = GenerateHeaderId(text);
                html.AppendLine($"<h1 id=\"{id}\">{HtmlEncode(text)}</h1>");
            }
            else if (trimmedLine.StartsWith("## "))
            {
                var text = trimmedLine.Substring(3).Trim();
                var id = GenerateHeaderId(text);
                html.AppendLine($"<h2 id=\"{id}\">{HtmlEncode(text)}</h2>");
            }
            else if (trimmedLine.StartsWith("### "))
            {
                var text = trimmedLine.Substring(4).Trim();
                var id = GenerateHeaderId(text);
                html.AppendLine($"<h3 id=\"{id}\">{HtmlEncode(text)}</h3>");
            }
            else if (trimmedLine.StartsWith("#### "))
            {
                var text = trimmedLine.Substring(5).Trim();
                var id = GenerateHeaderId(text);
                html.AppendLine($"<h4 id=\"{id}\">{HtmlEncode(text)}</h4>");
            }
            else if (trimmedLine.StartsWith("##### "))
            {
                var text = trimmedLine.Substring(6).Trim();
                var id = GenerateHeaderId(text);
                html.AppendLine($"<h5 id=\"{id}\">{HtmlEncode(text)}</h5>");
            }
            else if (trimmedLine.StartsWith("###### "))
            {
                var text = trimmedLine.Substring(7).Trim();
                var id = GenerateHeaderId(text);
                html.AppendLine($"<h6 id=\"{id}\">{HtmlEncode(text)}</h6>");
            }
            // 链接处理 - 支持行内链接
            else if (trimmedLine.Contains("[") && trimmedLine.Contains("]("))
            {
                // 处理包含链接的行
                var processedLine = ProcessLinksInLine(trimmedLine);
                html.AppendLine($"<p>{processedLine}</p>");
            }
            // 空行
            else if (string.IsNullOrEmpty(trimmedLine))
            {
                html.AppendLine("<br>");
            }
            // 普通段落
            else if (!string.IsNullOrEmpty(trimmedLine))
            {
                html.AppendLine($"<p>{HtmlEncode(trimmedLine)}</p>");
            }
        }

        return html.ToString();
    }

    private string GenerateHeaderId(string headerText)
    {
        if (string.IsNullOrEmpty(headerText))
            return string.Empty;

        // 生成标准化的header ID
        var id = headerText
            .ToLower()
            .Replace(" ", "-")
            .Replace("_", "-")
            .Replace(" ", "-")
            .Replace("/", "-")
            .Replace("?", "")
            .Replace("!", "")
            .Replace(".", "")
            .Replace(",", "")
            .Replace(":", "")
            .Replace(";", "")
            .Replace("(", "")
            .Replace(")", "")
            .Replace("[", "")
            .Replace("]", "")
            .Replace("{", "")
            .Replace("}", "");

        // 移除多余的连字符
        while (id.Contains("--"))
            id = id.Replace("--", "-");

        // 移除开头和结尾的连字符
        id = id.Trim('-');

        // 确保ID以字母开头
        if (id.Length > 0 && char.IsDigit(id[0]))
            id = "h-" + id;

        return id;
    }

    private string ProcessLinksInLine(string line)
    {
        // 处理内部锚点链接 [文本](#ID)
        var internalLinkPattern = @"\[([^\]]+)\]\(#([^)]+)\)";
        var result = System.Text.RegularExpressions.Regex.Replace(line, internalLinkPattern, match =>
        {
            var linkText = match.Groups[1].Value;
            var anchorId = match.Groups[2].Value;
            return $"<a href=\"#{anchorId}\">{HtmlEncode(linkText)}</a>";
        });

        // 处理外部链接 [文本](URL)
        var externalLinkPattern = @"\[([^\]]+)\]\(([^#][^)]*)\)";
        result = System.Text.RegularExpressions.Regex.Replace(result, externalLinkPattern, match =>
        {
            var linkText = match.Groups[1].Value;
            var url = match.Groups[2].Value;
            return $"<a href=\"{HtmlEncode(url)}\" target=\"_blank\">{HtmlEncode(linkText)}</a>";
        });

        return HtmlEncodeNormalText(result);
    }

    private string HtmlEncodeNormalText(string text)
    {
        // 对HTML标签内容进行编码，但保留已经生成的HTML标签
        var result = text;
        var tags = new List<string>();

        // 临时替换已有的HTML标签
        var tagPattern = @"<(\w+)[^>]*>.*?</\1>|<(\w+)[^>]]*/?>";
        result = System.Text.RegularExpressions.Regex.Replace(result, tagPattern, match =>
        {
            var placeholder = $"__HTML_TAG_{tags.Count}__";
            tags.Add(match.Value);
            return placeholder;
        });

        // 编码剩余文本
        result = HtmlEncode(result);

        // 恢复HTML标签
        for (int i = 0; i < tags.Count; i++)
        {
            result = result.Replace($"__HTML_TAG_{i}__", tags[i]);
        }

        return result;
    }

    private string HtmlEncode(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        return text.Replace("&", "&amp;")
                   .Replace("<", "&lt;")
                   .Replace(">", "&gt;")
                   .Replace("\"", "&quot;")
                   .Replace("'", "&#39;");
    }
}
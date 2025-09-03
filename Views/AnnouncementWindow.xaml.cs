using GHPC_Mod_Manager.ViewModels;
using GHPC_Mod_Manager.Services;
using System.Windows;

namespace GHPC_Mod_Manager.Views;

public partial class AnnouncementWindow : Window
{
    public AnnouncementWindow()
    {
        InitializeComponent();
        Loaded += Window_Loaded;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is AnnouncementViewModel viewModel)
        {
            // Load HTML content into WebBrowser when HasContent changes
            viewModel.PropertyChanged += (s, args) =>
            {
                if (args.PropertyName == nameof(AnnouncementViewModel.HtmlContent) && !string.IsNullOrEmpty(viewModel.HtmlContent))
                {
                    Dispatcher.Invoke(() =>
                    {
                        AnnouncementBrowser.NavigateToString(CreateHtmlPage(viewModel.HtmlContent));
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
                            AnnouncementBrowser.NavigateToString(CreateHtmlPage(viewModel.HtmlContent));
                        });
                    }
                };
            }

            // Check if content is already loaded
            if (!string.IsNullOrEmpty(viewModel.HtmlContent))
            {
                Dispatcher.Invoke(() =>
                {
                    AnnouncementBrowser.NavigateToString(CreateHtmlPage(viewModel.HtmlContent));
                });
            }

            // Handle close command - use a simple action
            viewModel.CloseCommand = new CommunityToolkit.Mvvm.Input.RelayCommand(() => Close());
        }
    }

    private string CreateHtmlPage(string htmlContent)
    {
        // Get theme colors from ViewModel
        var themeColors = (backgroundColor: "#FFFBFE", textColor: "#1C1B1F", linkColor: "#6750A4", 
                          codeBackgroundColor: "#F3EDF7", borderColor: "#79747E");
        
        if (DataContext is AnnouncementViewModel viewModel)
        {
            themeColors = viewModel.GetThemeColors();
            System.Diagnostics.Debug.WriteLine($"Using theme colors - Background: {themeColors.backgroundColor}, Text: {themeColors.textColor}");
        }
        else
        {
            System.Diagnostics.Debug.WriteLine("DataContext is not AnnouncementViewModel, using default light theme colors");
        }
        
        return $@"
<!DOCTYPE html>
<html>
<head>
    <meta charset='utf-8'>
    <style>
        body {{ 
            font-family: 'Microsoft YaHei UI', 'Segoe UI', Arial, sans-serif; 
            margin: 20px; 
            line-height: 1.6;
            color: {themeColors.textColor};
            background-color: {themeColors.backgroundColor};
        }}
        h1, h2, h3 {{ 
            color: {themeColors.textColor}; 
            opacity: 0.9;
        }}
        blockquote {{ 
            border-left: 4px solid {themeColors.linkColor}; 
            margin: 0; 
            padding-left: 16px; 
            background-color: {themeColors.codeBackgroundColor};
            padding: 10px 16px;
            border-radius: 4px;
        }}
        code {{ 
            background-color: {themeColors.codeBackgroundColor}; 
            color: {themeColors.textColor};
            padding: 2px 4px; 
            border-radius: 3px; 
            font-family: 'Consolas', 'Courier New', monospace;
        }}
        pre {{ 
            background-color: {themeColors.codeBackgroundColor}; 
            color: {themeColors.textColor};
            padding: 12px; 
            border-radius: 5px; 
            overflow-x: auto;
            border: 1px solid {themeColors.borderColor};
        }}
        a {{ 
            color: {themeColors.linkColor}; 
            text-decoration: none; 
        }}
        a:hover {{ 
            text-decoration: underline; 
            opacity: 0.8;
        }}
        table {{
            border-collapse: collapse;
            width: 100%;
            margin: 10px 0;
        }}
        th, td {{
            border: 1px solid {themeColors.borderColor};
            padding: 8px 12px;
            text-align: left;
        }}
        th {{
            background-color: {themeColors.codeBackgroundColor};
            font-weight: 600;
        }}
    </style>
</head>
<body>
{htmlContent}
</body>
</html>";
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
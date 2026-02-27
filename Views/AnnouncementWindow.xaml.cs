using GHPC_Mod_Manager.ViewModels;
using GHPC_Mod_Manager.Services;
using System.Windows;
using System.Diagnostics;
using Microsoft.Web.WebView2.Core;

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
            AnnouncementWebView.NavigateToString(htmlContent);
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

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
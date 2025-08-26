using GHPC_Mod_Manager.Services;
using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using GHPC_Mod_Manager.Resources;

namespace GHPC_Mod_Manager.Views;

public partial class LogWindow : Window
{
    private readonly ObservableCollection<LogEntry> _logEntries = new();
    private readonly ILoggingService _loggingService;

    public LogWindow()
    {
        InitializeComponent();
        _loggingService = App.GetService<ILoggingService>();
        LogListView.ItemsSource = _logEntries;
        
        LoadRecentLogs();
        _loggingService.LogReceived += OnLogReceived;
        
        Closed += (s, e) => _loggingService.LogReceived -= OnLogReceived;
    }

    private void LoadRecentLogs()
    {
        var recentLogs = _loggingService.GetRecentLogs();
        foreach (var log in recentLogs)
        {
            _logEntries.Add(log);
        }
    }

    private void OnLogReceived(object? sender, LogEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _logEntries.Insert(0, e.LogEntry);
            
            // Keep only last 500 entries
            while (_logEntries.Count > 500)
            {
                _logEntries.RemoveAt(_logEntries.Count - 1);
            }
        });
    }

    private void ClearLogsButton_Click(object sender, RoutedEventArgs e)
    {
        _logEntries.Clear();
    }

    private void CopySelectedButton_Click(object sender, RoutedEventArgs e)
    {
        if (LogListView.SelectedItems.Count == 0)
        {
            MessageBox.Show(Strings.SelectLogsToCompany, Strings.Tip, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var sb = new StringBuilder();
        foreach (LogEntry entry in LogListView.SelectedItems)
        {
            sb.AppendLine($"[{entry.Timestamp:yyyy-MM-dd HH:mm:ss}] [{entry.Level}] {entry.Message}");
        }

        try
        {
            Clipboard.SetText(sb.ToString());
            MessageBox.Show(string.Format(Strings.LogsCopiedToClipboard, LogListView.SelectedItems.Count), Strings.Success, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(string.Format(Strings.CopyToClipboardError, ex.Message), Strings.Error, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void CopyAllButton_Click(object sender, RoutedEventArgs e)
    {
        if (_logEntries.Count == 0)
        {
            MessageBox.Show(Strings.NoLogsToCopy, Strings.Tip, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var sb = new StringBuilder();
        foreach (var entry in _logEntries)
        {
            sb.AppendLine($"[{entry.Timestamp:yyyy-MM-dd HH:mm:ss}] [{entry.Level}] {entry.Message}");
        }

        try
        {
            Clipboard.SetText(sb.ToString());
            MessageBox.Show(string.Format(Strings.AllLogsCopiedToClipboard, _logEntries.Count), Strings.Success, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(string.Format(Strings.CopyToClipboardError, ex.Message), Strings.Error, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
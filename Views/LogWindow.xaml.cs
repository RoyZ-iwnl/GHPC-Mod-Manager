using GHPC_Mod_Manager.Services;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Data;
using GHPC_Mod_Manager.Resources;

namespace GHPC_Mod_Manager.Views;

public partial class LogWindow : Window
{
    private readonly ObservableCollection<LogEntry> _allLogEntries = new();
    private readonly ObservableCollection<LogEntry> _filteredLogEntries = new();
    private readonly ILoggingService _loggingService;

    public LogWindow()
    {
        InitializeComponent();
        _loggingService = App.GetService<ILoggingService>();
        LogListView.ItemsSource = _filteredLogEntries;

        LoadRecentLogs();
        _loggingService.LogReceived += OnLogReceived;

        Closed += (s, e) => _loggingService.LogReceived -= OnLogReceived;
    }

    private void LoadRecentLogs()
    {
        var recentLogs = _loggingService.GetRecentLogs();
        foreach (var log in recentLogs)
        {
            _allLogEntries.Add(log);
        }
        ApplyFilter();
    }

    private void OnLogReceived(object? sender, LogEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _allLogEntries.Insert(0, e.LogEntry);

            // Keep only last 1000 entries in all logs
            while (_allLogEntries.Count > 1000)
            {
                _allLogEntries.RemoveAt(_allLogEntries.Count - 1);
            }

            ApplyFilter();
        });
    }

    private void ApplyFilter()
    {
        // Add null checks for checkboxes (they may not be initialized yet)
        var showInfo = ShowInfoCheckBox?.IsChecked ?? true;
        var showWarning = ShowWarningCheckBox?.IsChecked ?? true;
        var showError = ShowErrorCheckBox?.IsChecked ?? true;

        _filteredLogEntries.Clear();
        foreach (var entry in _allLogEntries)
        {
            var shouldShow = entry.Level switch
            {
                Microsoft.Extensions.Logging.LogLevel.Information => showInfo,
                Microsoft.Extensions.Logging.LogLevel.Warning => showWarning,
                Microsoft.Extensions.Logging.LogLevel.Error => showError,
                _ => true
            };

            if (shouldShow)
            {
                _filteredLogEntries.Add(entry);
            }
        }
    }

    private void FilterCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        ApplyFilter();
    }

    private void ClearLogsButton_Click(object sender, RoutedEventArgs e)
    {
        _allLogEntries.Clear();
        _filteredLogEntries.Clear();
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
        if (_filteredLogEntries.Count == 0)
        {
            MessageBox.Show(Strings.NoLogsToCopy, Strings.Tip, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var sb = new StringBuilder();
        foreach (var entry in _filteredLogEntries)
        {
            sb.AppendLine($"[{entry.Timestamp:yyyy-MM-dd HH:mm:ss}] [{entry.Level}] {entry.Message}");
        }

        try
        {
            Clipboard.SetText(sb.ToString());
            MessageBox.Show(string.Format(Strings.AllLogsCopiedToClipboard, _filteredLogEntries.Count), Strings.Success, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(string.Format(Strings.CopyToClipboardError, ex.Message), Strings.Error, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ExportLogsButton_Click(object sender, RoutedEventArgs e)
    {
        var saveDialog = new SaveFileDialog
        {
            Filter = "Text Files (*.txt)|*.txt|Log Files (*.log)|*.log|All Files (*.*)|*.*",
            DefaultExt = "txt",
            FileName = $"GHPC_Mod_Manager_Logs_{DateTime.Now:yyyyMMdd_HHmmss}.txt"
        };

        if (saveDialog.ShowDialog() == true)
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("=".PadRight(80, '='));
                sb.AppendLine($"GHPC Mod Manager Logs");
                sb.AppendLine($"Exported: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                sb.AppendLine($"Total entries: {_filteredLogEntries.Count}");
                sb.AppendLine("=".PadRight(80, '='));
                sb.AppendLine();

                foreach (var entry in _filteredLogEntries)
                {
                    sb.AppendLine($"[{entry.Timestamp:yyyy-MM-dd HH:mm:ss}] [{entry.Level}] {entry.Message}");
                }

                File.WriteAllText(saveDialog.FileName, sb.ToString());
                MessageBox.Show($"{Strings.Success}: {saveDialog.FileName}", Strings.Success, MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"{Strings.Error}: {ex.Message}", Strings.Error, MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
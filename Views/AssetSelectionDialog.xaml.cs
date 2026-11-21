using GHPC_Mod_Manager.Models;
using GHPC_Mod_Manager.Resources;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Input;

namespace GHPC_Mod_Manager.Views;

public partial class AssetSelectionDialog : Window
{
    private readonly List<AssetSelectionItem> _items;

    public GitHubAsset? SelectedAsset { get; private set; }

    public AssetSelectionDialog(IReadOnlyList<GitHubAsset> assets, string modName, string version)
    {
        InitializeComponent();

        _items = assets
            .Select(asset => new AssetSelectionItem(asset))
            .OrderByDescending(item => item.IsNotSource)
            .ThenByDescending(item => item.Asset.Size)
            .ThenBy(item => item.Asset.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (_items.Count > 0)
            _items[0].IsRecommended = true;

        MessageText.Text = string.Format(
            CultureInfo.CurrentCulture,
            Strings.AssetSelectionMessage,
            modName,
            version);
        AssetList.ItemsSource = _items;
        if (_items.Count > 0)
            AssetList.SelectedIndex = 0;
    }

    private void AssetList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        ConfirmButton.IsEnabled = AssetList.SelectedItem is AssetSelectionItem;
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (AssetList.SelectedItem is AssetSelectionItem item)
        {
            SelectedAsset = item.Asset;
            DialogResult = true;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 1)
            DragMove();
    }

    private sealed class AssetSelectionItem
    {
        public GitHubAsset Asset { get; }
        public string Name => Asset.Name;
        public bool IsNotSource { get; }
        public string SizeDisplay { get; }
        public bool IsRecommended { get; set; }
        public string RecommendationDisplay => IsRecommended
            ? Strings.AssetSelectionRecommended
            : IsNotSource
                ? Strings.AssetSelectionAlternative
                : Strings.AssetSelectionSource;

        public AssetSelectionItem(GitHubAsset asset)
        {
            Asset = asset;
            IsNotSource = !ContainsSourceMarker(asset.Name);
            SizeDisplay = asset.Size > 0 ? FormatSize(asset.Size) : Strings.AssetSelectionUnknownSize;
        }

        private static bool ContainsSourceMarker(string fileName)
        {
            var name = Path.GetFileNameWithoutExtension(fileName);
            return name.Contains("source", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("src", StringComparison.OrdinalIgnoreCase);
        }

        private static string FormatSize(long bytes)
        {
            const double unit = 1024;
            if (bytes >= unit * unit * unit)
                return $"{bytes / (unit * unit * unit):0.##} GB";
            if (bytes >= unit * unit)
                return $"{bytes / (unit * unit):0.##} MB";
            if (bytes >= unit)
                return $"{bytes / unit:0.##} KB";
            return $"{bytes} B";
        }
    }
}

using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;

namespace GHPC_Mod_Manager.ViewModels;

/// <summary>
/// 更新选择对话框中的单行条目
/// </summary>
public partial class ModUpdateSelectItem : ObservableObject
{
    [ObservableProperty]
    private bool _isSelected = true;

    public string ModId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string InstalledVersion { get; set; } = string.Empty;
    public string LatestVersion { get; set; } = string.Empty;

    public string VersionLabel => $"{InstalledVersion}  →  {LatestVersion}";
}

/// <summary>
/// 更新选择对话框的 ViewModel（非 DI，直接 new）
/// </summary>
public partial class ModUpdateSelectViewModel : ObservableObject
{
    public ObservableCollection<ModUpdateSelectItem> Items { get; } = new();

    private bool _isAllSelected = true;
    public bool IsAllSelected
    {
        get => _isAllSelected;
        set
        {
            if (SetProperty(ref _isAllSelected, value))
                ApplySelectAll(value);
        }
    }

    private void ApplySelectAll(bool value)
    {
        foreach (var item in Items)
            item.IsSelected = value;
    }

    public string Header =>
        string.Format(Resources.Strings.ModUpdateDialogHeader, Items.Count);

    public string ConfirmButtonText =>
        string.Format(Resources.Strings.UpdateSelectedMods, SelectedCount);

    public int SelectedCount => Items.Count(i => i.IsSelected);
    public bool CanConfirm => SelectedCount > 0;

    public ModUpdateSelectViewModel(IEnumerable<ModUpdateSelectItem> items)
    {
        foreach (var item in items)
        {
            item.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ModUpdateSelectItem.IsSelected))
                {
                    OnPropertyChanged(nameof(SelectedCount));
                    OnPropertyChanged(nameof(CanConfirm));
                    OnPropertyChanged(nameof(ConfirmButtonText));
                    SyncAllSelectedState();
                }
            };
            Items.Add(item);
        }
    }

    private void SyncAllSelectedState()
    {
        var allChecked = Items.All(i => i.IsSelected);
        // 绕过 setter 避免循环修改所有子项
        if (_isAllSelected != allChecked)
            SetProperty(ref _isAllSelected, allChecked, nameof(IsAllSelected));
    }
}

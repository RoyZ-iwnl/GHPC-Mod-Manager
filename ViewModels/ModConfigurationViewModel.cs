using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GHPC_Mod_Manager.Models;
using GHPC_Mod_Manager.Services;
using Newtonsoft.Json;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.ComponentModel;
using System.Windows.Data;
using GHPC_Mod_Manager.Resources;

namespace GHPC_Mod_Manager.ViewModels;

// 配置项详细信息
public partial class ConfigurationItemViewModel : ObservableObject
{
    [ObservableProperty]
    private string _key = string.Empty;
    
    [ObservableProperty]
    private string _originalKey = string.Empty; // 存储原始键值用于配置文件
    
    [ObservableProperty]
    private string _displayName = string.Empty; // 存储本地化显示名称
    
    [ObservableProperty]
    private object _originalValue = string.Empty;
    
    [ObservableProperty]
    private string _description = string.Empty;
    
    [ObservableProperty]
    private string _comment = string.Empty;  // Comment from # in config line
    
    [ObservableProperty]
    private bool _isBooleanType;
    
    [ObservableProperty]
    private bool _isStringType = true;
    
    [ObservableProperty]
    private bool _isChoiceType;
    
    [ObservableProperty]
    private List<string> _choiceOptions = new();
    
    [ObservableProperty]
    private bool _boolValue;
    
    [ObservableProperty]
    private string _stringValue = string.Empty;
    
    [ObservableProperty]
    private bool _isStandaloneComment; // 标识是否为单独一行的注释
    
    public bool HasDescription => !string.IsNullOrEmpty(Description);
    public bool HasComment => !string.IsNullOrEmpty(Comment);
    public bool IsConfigurationItem => !IsStandaloneComment; // 是否为配置项（非单独注释）
    
    public ConfigurationItemViewModel(string originalKey, string displayName, object value, string description = "", string comment = "")
    {
        OriginalKey = originalKey; // 原始键值
        Key = displayName; // 显示名称
        DisplayName = displayName;
        OriginalValue = value;
        Description = description;
        Comment = comment;
        IsStandaloneComment = false;
        
        // 确定类型并设置初始值
        if (value is bool boolVal)
        {
            IsBooleanType = true;
            IsStringType = false;
            BoolValue = boolVal;
        }
        else
        {
            StringValue = value?.ToString() ?? string.Empty;
        }
    }
    
    // 创建单独注释行的构造函数
    public ConfigurationItemViewModel(string comment)
    {
        IsStandaloneComment = true;
        Comment = comment;
        Key = comment;
        DisplayName = comment;
        OriginalKey = comment;
        OriginalValue = string.Empty;
        Description = string.Empty;
        StringValue = string.Empty;
        IsBooleanType = false;
        IsStringType = false;
        IsChoiceType = false;
    }
    
    public object GetCurrentValue()
    {
        // 单独注释行不参与配置保存
        if (IsStandaloneComment)
            return string.Empty;
            
        if (IsBooleanType)
            return BoolValue;
        if (IsChoiceType)
            return StringValue;
        
        // 尝试转换为数字
        if (double.TryParse(StringValue, out var doubleVal))
            return doubleVal;
        if (int.TryParse(StringValue, out var intVal))
            return intVal;
            
        return StringValue;
    }
}

// 配置预设
public partial class ConfigurationPreset : ObservableObject
{
    [ObservableProperty]
    private string _name = string.Empty;
    
    [ObservableProperty]
    private Dictionary<string, object> _configuration = new();
    
    [ObservableProperty]
    private DateTime _createdDate = DateTime.Now;
    
    public ConfigurationPreset()
    {
        // 默认构造函数用于JSON反序列化
    }
    
    public ConfigurationPreset(string name)
    {
        Name = name;
    }
    
    public override string ToString()
    {
        return Name;
    }
}

// MOD配置窗口ViewModel
public partial class ModConfigurationViewModel : ObservableObject
{
    private readonly IModManagerService _modManagerService;
    private readonly ILoggingService _loggingService;
    private readonly ISettingsService _settingsService;
    
    [ObservableProperty]
    private string _modId = string.Empty;
    
    [ObservableProperty]
    private string _modDisplayName = string.Empty;
    
    [ObservableProperty]
    private string _windowTitle = string.Empty;
    
    [ObservableProperty]
    private ObservableCollection<ConfigurationItemViewModel> _configuration = new();
    
    private readonly ICollectionView _filteredConfiguration;
    public ICollectionView FilteredConfiguration => _filteredConfiguration;
    
    [ObservableProperty]
    private string _searchText = string.Empty;
    
    [ObservableProperty]
    private string _filteredConfigurationCount = string.Empty;
    
    [ObservableProperty]
    private ObservableCollection<ConfigurationPreset> _presets = new();
    
    [ObservableProperty]
    private ConfigurationPreset? _selectedPreset;
    
    [ObservableProperty]
    private string _newPresetName = string.Empty;
    
    [ObservableProperty]
    private string _statusMessage = string.Empty;
    
    [ObservableProperty]
    private bool _hasConfiguration;
    
    public Window? OwnerWindow { get; set; }
    
    public ModConfigurationViewModel(
        IModManagerService modManagerService,
        ILoggingService loggingService,
        ISettingsService settingsService)
    {
        _modManagerService = modManagerService;
        _loggingService = loggingService;
        _settingsService = settingsService;
        
        // Initialize filtered collection view
        _filteredConfiguration = CollectionViewSource.GetDefaultView(Configuration);
        _filteredConfiguration.Filter = FilterConfiguration;
        
        // Set up search text change handler
        PropertyChanged += OnPropertyChanged;
        
        UpdateFilteredConfigurationCount();
    }
    
    private void OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SearchText))
        {
            _filteredConfiguration.Refresh();
            UpdateFilteredConfigurationCount();
        }
    }
    
    private bool FilterConfiguration(object obj)
    {
        if (obj is not ConfigurationItemViewModel configItem)
            return false;
            
        if (string.IsNullOrEmpty(SearchText))
            return true;
            
        var searchTerms = SearchText.ToLower();
        
        // Search in key, description, and comment
        return configItem.Key.ToLower().Contains(searchTerms) ||
               configItem.Description.ToLower().Contains(searchTerms) ||
               configItem.Comment.ToLower().Contains(searchTerms);
    }
    
    private void UpdateFilteredConfigurationCount()
    {
        var filteredCount = 0;
        foreach (var item in FilteredConfiguration)
        {
            filteredCount++;
        }
        
        if (Configuration.Count == 0)
        {
            FilteredConfigurationCount = "";
        }
        else if (string.IsNullOrEmpty(SearchText))
        {
            FilteredConfigurationCount = string.Format(GHPC_Mod_Manager.Resources.Strings.ItemsCount, Configuration.Count);
        }
        else
        {
            FilteredConfigurationCount = string.Format(GHPC_Mod_Manager.Resources.Strings.ItemsCountOfTotal, filteredCount, Configuration.Count);
        }
    }
    
    public async Task InitializeAsync(string modId, string displayName)
    {
        ModId = modId;
        ModDisplayName = displayName;
        WindowTitle = string.Format(Strings.ConfigurationFor, displayName);
        
        await LoadConfigurationAsync();
        await LoadPresetsAsync();
    }
    
    private async Task LoadConfigurationAsync()
    {
        try
        {
            // 使用新的有序方法获取配置项
            var orderedConfigItems = await _modManagerService.GetModConfigurationOrderedAsync(ModId);
            
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                Configuration.Clear();
                
                // 按顺序添加所有配置项和注释行
                foreach (var item in orderedConfigItems)
                {
                    Configuration.Add(item);
                }
                
                HasConfiguration = Configuration.Count > 0;
                UpdateFilteredConfigurationCount();
            });
            
            StatusMessage = string.Format(Strings.ConfigurationItemsLoaded, Configuration.Count);
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.ConfigurationLoadError, ModId);
            StatusMessage = Strings.ConfigurationLoadFailed;
        }
    }
    
    private async Task LoadPresetsAsync()
    {
        try
        {
            var presetsPath = Path.Combine(_settingsService.AppDataPath, "presets", $"{ModId}.json");
            
            if (File.Exists(presetsPath))
            {
                var json = await File.ReadAllTextAsync(presetsPath);
                var presetList = JsonConvert.DeserializeObject<List<ConfigurationPreset>>(json) ?? new();
                
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    Presets.Clear();
                    foreach (var preset in presetList)
                    {
                        Presets.Add(preset);
                    }
                });
            }
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.PresetLoadError, ModId);
        }
    }
    
    private async Task SavePresetsAsync()
    {
        try
        {
            var presetsDir = Path.Combine(_settingsService.AppDataPath, "presets");
            Directory.CreateDirectory(presetsDir);
            
            var presetsPath = Path.Combine(presetsDir, $"{ModId}.json");
            var json = JsonConvert.SerializeObject(Presets, Formatting.Indented);
            await File.WriteAllTextAsync(presetsPath, json);
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.PresetSaveError, ModId);
        }
    }
    
    [RelayCommand]
    private async Task LoadPresetAsync()
    {
        if (SelectedPreset == null) return;
        
        try
        {
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                foreach (var configItem in Configuration)
                {
                    // 使用原始键值查找预设配置
                    if (SelectedPreset.Configuration.TryGetValue(configItem.OriginalKey, out var value))
                    {
                        if (configItem.IsBooleanType && value is bool boolVal)
                        {
                            configItem.BoolValue = boolVal;
                        }
                        else
                        {
                            configItem.StringValue = value?.ToString() ?? string.Empty;
                        }
                    }
                }
            });
            
            StatusMessage = string.Format(Strings.PresetLoaded_, SelectedPreset.Name);
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.PresetLoadError, SelectedPreset.Name);
            StatusMessage = Strings.PresetLoadFailed;
        }
    }
    
    [RelayCommand]
    private async Task SavePresetAsync()
    {
        if (SelectedPreset == null) return;
        
        try
        {
            var config = new Dictionary<string, object>();
            foreach (var item in Configuration)
            {
                // 跳过单独注释行，只处理配置项
                if (!item.IsStandaloneComment)
                {
                    // 使用原始键值而不是本地化名称
                    config[item.OriginalKey] = item.GetCurrentValue();
                }
            }
            
            SelectedPreset.Configuration = config;
            await SavePresetsAsync();
            
            StatusMessage = string.Format(Strings.PresetSaved_, SelectedPreset.Name);
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.PresetSaveError, SelectedPreset?.Name ?? "Unknown");
            StatusMessage = Strings.PresetSaveFailed;
        }
    }
    
    [RelayCommand]
    private async Task SaveAsPresetAsync()
    {
        if (string.IsNullOrWhiteSpace(NewPresetName)) 
        {
            StatusMessage = Strings.PresetNameRequired;
            return;
        }
        
        try
        {
            var config = new Dictionary<string, object>();
            foreach (var item in Configuration)
            {
                // 跳过单独注释行，只处理配置项
                if (!item.IsStandaloneComment)
                {
                    // 使用原始键值而不是本地化名称
                    config[item.OriginalKey] = item.GetCurrentValue();
                }
            }
            
            var newPreset = new ConfigurationPreset(NewPresetName.Trim())
            {
                Configuration = config
            };
            
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                Presets.Add(newPreset);
                SelectedPreset = newPreset;
            });
            
            await SavePresetsAsync();
            
            NewPresetName = string.Empty;
            StatusMessage = string.Format(Strings.PresetCreated, newPreset.Name);
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.PresetCreateError, NewPresetName);
            StatusMessage = Strings.PresetCreationFailed;
        }
    }
    
    [RelayCommand]
    private async Task DeletePresetAsync()
    {
        if (SelectedPreset == null) return;
        
        var result = MessageBox.Show(
            string.Format(Strings.ConfirmDeletePreset, SelectedPreset.Name),
            Strings.ConfirmDelete,
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
            
        if (result != MessageBoxResult.Yes) return;
        
        try
        {
            var presetName = SelectedPreset.Name;
            
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                Presets.Remove(SelectedPreset);
                SelectedPreset = null;
            });
            
            await SavePresetsAsync();
            StatusMessage = string.Format(Strings.PresetDeleted, presetName);
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.PresetDeleteError, SelectedPreset?.Name ?? "Unknown");
            StatusMessage = Strings.PresetDeletionFailed;
        }
    }
    
    [RelayCommand]
    private async Task ApplyAsync()
    {
        await SaveConfigurationAsync();
    }
    
    [RelayCommand]
    private async Task OkAsync()
    {
        await SaveConfigurationAsync();
        OwnerWindow?.Close();
    }
    
    [RelayCommand]
    private void Cancel()
    {
        OwnerWindow?.Close();
    }
    
    private async Task SaveConfigurationAsync()
    {
        try
        {
            var config = new Dictionary<string, object>();
            foreach (var item in Configuration)
            {
                // 跳过单独注释行，只保存配置项
                if (!item.IsStandaloneComment)
                {
                    // 使用原始键值而不是本地化名称
                    config[item.OriginalKey] = item.GetCurrentValue();
                }
            }
            
            var success = await _modManagerService.SaveModConfigurationAsync(ModId, config);
            StatusMessage = success ? Strings.ConfigurationSaved : Strings.ConfigurationSaveError;
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.ConfigurationSaveError, ModId);
            StatusMessage = Strings.ConfigurationSaveError;
        }
    }
}
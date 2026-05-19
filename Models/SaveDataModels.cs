using System.Collections.Generic;
using GHPC_Mod_Manager.Resources;
using Newtonsoft.Json;

namespace GHPC_Mod_Manager.Models;

/// <summary>
/// GHPC存档文件根结构
/// </summary>
public class SaveFileData
{
    [JsonProperty("GHPC_PLAYER_SAVE")]
    public PlayerSaveData? PlayerSave { get; set; }
}

/// <summary>
/// Unity序列化的玩家存档数据
/// </summary>
public class PlayerSaveData
{
    [JsonProperty("__type")]
    public string Type { get; set; } = string.Empty;

    [JsonProperty("value")]
    public SaveDataValue? Value { get; set; }
}

/// <summary>
/// 存档数据内容
/// </summary>
public class SaveDataValue
{
    [JsonProperty("TheaterMissionPlayStates")]
    public Dictionary<string, Dictionary<string, Dictionary<string, int>>> TheaterMissionPlayStates { get; set; } = new();
}

/// <summary>
/// 任务完成状态
/// </summary>
public class MissionStatus
{
    public bool Blue { get; set; }
    public bool Red { get; set; }
    public bool Neutral { get; set; }
}

/// <summary>
/// 战役信息（用于UI显示）
/// </summary>
public class TheaterInfo
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public List<MissionInfo> Missions { get; set; } = new();
}

/// <summary>
/// 任务信息（用于UI显示）
/// </summary>
public class MissionInfo
{
    public string Name { get; set; } = string.Empty;
    public Dictionary<string, int> CompletionStatus { get; set; } = new();

    public bool Blue => CompletionStatus.TryGetValue("Blue", out var v) && v == 1;
    public bool Red => CompletionStatus.TryGetValue("Red", out var v) && v == 1;
    public bool Neutral => CompletionStatus.TryGetValue("Neutral", out var v) && v == 1;

    public bool HasBlue => CompletionStatus.ContainsKey("Blue");
    public bool HasRed => CompletionStatus.ContainsKey("Red");
    public bool HasNeutral => CompletionStatus.ContainsKey("Neutral");
}

/// <summary>
/// 备份记录
/// </summary>
public class BackupRecord
{
    public string FilePath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public DateTime BackupTime { get; set; }
    public long FileSize { get; set; }

    public string DisplayTime => BackupTime.ToString("yyyy-MM-dd HH:mm:ss");
    public string DisplaySize => FileSize < 1024 ? $"{FileSize} B" : (FileSize < 1024 * 1024 ? $"{FileSize / 1024.0:F1} KB" : $"{FileSize / 1024.0 / 1024.0:F1} MB");
}

/// <summary>
/// 备份配置
/// </summary>
public class BackupConfig
{
    public List<BackupRecord> Backups { get; set; } = new();
    public int MaxBackupCount { get; set; } = 20;
}

/// <summary>
/// 阵营完成状态
/// </summary>
public class FactionState : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    private int _value;

    public string Name { get; set; } = string.Empty;

    public int Value
    {
        get => _value;
        set
        {
            if (SetProperty(ref _value, value))
            {
                OnPropertyChanged(nameof(IsCompleted));
                OnPropertyChanged(nameof(DisplayName));
            }
        }
    }

    public bool IsCompleted => Value == 1;
    public string DisplayName => Name switch
    {
        "Blue" => Strings.FactionBlue,
        "Red" => Strings.FactionRed,
        "Neutral" => Strings.FactionNeutral,
        _ => Name
    };
}

/// <summary>
/// 树形任务节点（叶子节点）
/// </summary>
public class MissionTreeNode : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    private string _name = string.Empty;
    private string _theaterId = string.Empty;

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public string TheaterId
    {
        get => _theaterId;
        set => SetProperty(ref _theaterId, value);
    }

    public List<FactionState> Factions { get; set; } = new();

    public void SetStatuses(Dictionary<string, int> statuses)
    {
        Factions = statuses
            .Select(status => new FactionState { Name = status.Key, Value = status.Value })
            .ToList();
        OnPropertyChanged(nameof(Factions));
    }
}

/// <summary>
/// 树形战役节点（父节点）
/// </summary>
public class TheaterTreeNode
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public List<MissionTreeNode> Missions { get; set; } = new();
}
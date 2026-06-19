using GHPC_Mod_Manager.Models;
using System.Collections.Generic;

namespace GHPC_Mod_Manager.Services.Game;

/// <summary>
/// 存档编辑器服务接口
/// </summary>
public interface ISaveEditorService
{
    /// <summary>
    /// 加载存档文件
    /// </summary>
    Task<SaveFileData?> LoadSaveFileAsync(string saveFilePath);

    /// <summary>
    /// 保存存档文件
    /// </summary>
    Task SaveSaveFileAsync(string saveFilePath, SaveFileData data);

    /// <summary>
    /// 获取默认存档路径
    /// </summary>
    string GetDefaultSaveFilePath(string gameRootPath);

    /// <summary>
    /// 手动备份存档文件
    /// </summary>
    Task<string> BackupSaveFileAsync(string saveFilePath);

    /// <summary>
    /// 获取备份列表
    /// </summary>
    List<BackupRecord> GetBackupList();

    /// <summary>
    /// 从备份恢复存档
    /// </summary>
    Task<bool> RestoreFromBackupAsync(string backupPath, string targetPath);

    /// <summary>
    /// 删除备份
    /// </summary>
    bool DeleteBackup(string backupPath);

    /// <summary>
    /// 自动保存前备份
    /// </summary>
    Task AutoBackupBeforeSaveAsync(string saveFilePath);

    /// <summary>
    /// 重置所有任务进度
    /// </summary>
    void ResetAllProgress(SaveFileData data);

    /// <summary>
    /// 完成所有任务（将所有已存在的阵营状态设为1）
    /// </summary>
    void CompleteAllMissions(SaveFileData data, string faction = "All");

    /// <summary>
    /// 切换任务完成状态
    /// </summary>
    void ToggleMissionCompletion(SaveFileData data, string theaterId, string missionName, string faction);

    /// <summary>
    /// 存档文件是否存在
    /// </summary>
    bool SaveFileExists(string saveFilePath);
}
using GHPC_Mod_Manager.Models;
using GHPC_Mod_Manager.Resources;
using Newtonsoft.Json;
using System.Collections.Generic;
using System.IO;

namespace GHPC_Mod_Manager.Services.Game;

public class SaveEditorService : ISaveEditorService
{
    private readonly ILoggingService _loggingService;
    private readonly string _backupDir;

    public SaveEditorService(ILoggingService loggingService)
    {
        _loggingService = loggingService;
        _backupDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app_data", "save_backups");
        Directory.CreateDirectory(_backupDir);
    }

    public async Task<SaveFileData?> LoadSaveFileAsync(string saveFilePath)
    {
        try
        {
            if (!File.Exists(saveFilePath))
            {
                _loggingService.LogWarning("SaveEditor_FileNotFound", saveFilePath);
                return null;
            }

            var json = await File.ReadAllTextAsync(saveFilePath);
            var data = JsonConvert.DeserializeObject<SaveFileData>(json);

            _loggingService.LogInfo("SaveEditor_Loaded", saveFilePath);
            return data;
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, "SaveEditor_LoadFailed", saveFilePath);
            return null;
        }
    }

    public async Task SaveSaveFileAsync(string saveFilePath, SaveFileData data)
    {
        // 原子写入：先写临时文件，成功后替换目标，避免写入中断损坏存档
        var json = JsonConvert.SerializeObject(data, Formatting.Indented);
        var tempPath = saveFilePath + ".tmp";

        try
        {
            var directory = Path.GetDirectoryName(saveFilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllTextAsync(tempPath, json);

            // 同卷 Move/Replace 是原子操作，覆盖目标文件
            if (File.Exists(saveFilePath))
                File.Replace(tempPath, saveFilePath, null);
            else
                File.Move(tempPath, saveFilePath);

            _loggingService.LogInfo("SaveEditor_Saved", saveFilePath);
        }
        catch
        {
            // 失败时清理残留的临时文件，不影响原存档
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { /* 忽略清理失败 */ }
            }
            throw;
        }
    }

    public string GetDefaultSaveFilePath()
    {
        var documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return Path.Combine(documentsPath, "My Games", "GHPC", "Data", "GHPC_data.sav");
    }

    public async Task<string> BackupSaveFileAsync(string saveFilePath)
    {
        if (!File.Exists(saveFilePath))
        {
            throw new FileNotFoundException("存档文件不存在", saveFilePath);
        }

        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var backupPath = Path.Combine(_backupDir, $"GHPC_data_{timestamp}.sav");

        await Task.Run(() => File.Copy(saveFilePath, backupPath, true));

        _loggingService.LogInfo("SaveEditor_BackupCreated", backupPath);

        CleanupOldBackups();

        return backupPath;
    }

    public List<BackupRecord> GetBackupList()
    {
        var backups = new List<BackupRecord>();

        if (!Directory.Exists(_backupDir))
            return backups;

        var files = Directory.GetFiles(_backupDir, "GHPC_data_*.sav");
        foreach (var file in files)
        {
            var info = new FileInfo(file);
            backups.Add(new BackupRecord
            {
                FilePath = file,
                FileName = info.Name,
                BackupTime = info.CreationTime,
                FileSize = info.Length
            });
        }

        return backups.OrderByDescending(b => b.BackupTime).ToList();
    }

    public async Task<bool> RestoreFromBackupAsync(string backupPath, string targetPath)
    {
        try
        {
            if (!File.Exists(backupPath))
            {
                _loggingService.LogWarning("SaveEditor_BackupNotFound", backupPath);
                return false;
            }

            await Task.Run(() => File.Copy(backupPath, targetPath, true));

            _loggingService.LogInfo("SaveEditor_RestoredFromBackup", targetPath);
            return true;
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, "SaveEditor_RestoreFailed", backupPath);
            return false;
        }
    }

    public bool DeleteBackup(string backupPath)
    {
        try
        {
            if (File.Exists(backupPath))
            {
                File.Delete(backupPath);
                _loggingService.LogInfo("SaveEditor_BackupDeleted", backupPath);
                return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, "SaveEditor_DeleteBackupFailed", backupPath);
            return false;
        }
    }

    public async Task AutoBackupBeforeSaveAsync(string saveFilePath)
    {
        if (File.Exists(saveFilePath))
        {
            await BackupSaveFileAsync(saveFilePath);
        }
    }

    private void CleanupOldBackups()
    {
        const int maxBackups = 20;

        var backups = GetBackupList();
        if (backups.Count > maxBackups)
        {
            foreach (var old in backups.Skip(maxBackups))
            {
                DeleteBackup(old.FilePath);
            }
        }
    }

    public bool ResetAllProgress(SaveFileData data)
    {
        if (data?.PlayerSave?.Value?.TheaterMissionPlayStates == null) return false;

        foreach (var theater in data.PlayerSave.Value.TheaterMissionPlayStates.Values)
        {
            foreach (var mission in theater.Values)
            {
                foreach (var faction in mission.Keys.ToList())
                {
                    mission[faction] = 0;
                }
            }
        }

        _loggingService.LogInfo("SaveEditor_ProgressReset");
        return true;
    }

    public bool CompleteAllMissions(SaveFileData data)
    {
        if (data?.PlayerSave?.Value?.TheaterMissionPlayStates == null) return false;

        foreach (var theater in data.PlayerSave.Value.TheaterMissionPlayStates.Values)
        {
            foreach (var mission in theater.Values)
            {
                foreach (var existingFaction in mission.Keys.ToList())
                {
                    mission[existingFaction] = 1;
                }
            }
        }

        _loggingService.LogInfo("SaveEditor_AllMissionsCompleted");
        return true;
    }

    public bool ToggleMissionCompletion(SaveFileData data, string theaterId, string missionName, string faction)
    {
        if (data?.PlayerSave?.Value?.TheaterMissionPlayStates == null) return false;

        if (data.PlayerSave.Value.TheaterMissionPlayStates.TryGetValue(theaterId, out var theater)
            && theater.TryGetValue(missionName, out var mission)
            && mission.TryGetValue(faction, out var status))
        {
            mission[faction] = status == 1 ? 0 : 1;
            return true;
        }

        // 找不到任务时记录日志，便于排查存档结构差异
        _loggingService.LogWarning(Strings.SaveEditor_ToggleNotFound, theaterId, missionName, faction);
        return false;
    }

    public bool SaveFileExists(string saveFilePath)
    {
        return File.Exists(saveFilePath);
    }
}
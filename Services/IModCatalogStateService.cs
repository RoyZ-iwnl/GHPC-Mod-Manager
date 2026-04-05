using System.IO;
using Newtonsoft.Json;

namespace GHPC_Mod_Manager.Services;

/// <summary>
/// MOD 目录状态快照 DTO，持久化到 app_data/mod_catalog_state.json
/// </summary>
public class ModCatalogState
{
    public int SchemaVersion { get; set; } = 1;
    public List<string> ModIds { get; set; } = new();
    public DateTime SnapshotTimeUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// MOD 目录状态服务：读写 mod_catalog_state.json 快照文件
/// </summary>
public interface IModCatalogStateService
{
    /// <summary>
    /// 当前会话的目录状态（LoadAsync 后有效）
    /// </summary>
    ModCatalogState? State { get; }

    /// <summary>
    /// 加载持久化状态（不存在/损坏/空时返回 null）
    /// </summary>
    Task<ModCatalogState?> LoadAsync();

    /// <summary>
    /// 用当前有效 MOD ID 集合覆写快照
    /// </summary>
    Task SaveAsync(IEnumerable<string> currentModIds);
}

public class ModCatalogStateService : IModCatalogStateService
{
    private readonly ISettingsService _settingsService;
    private readonly ILoggingService _loggingService;
    private string _filePath = string.Empty;

    public ModCatalogState? State { get; private set; }

    public ModCatalogStateService(ISettingsService settingsService, ILoggingService loggingService)
    {
        _settingsService = settingsService;
        _loggingService = loggingService;
        _filePath = Path.Combine(_settingsService.AppDataPath, "mod_catalog_state.json");
    }

    public async Task<ModCatalogState?> LoadAsync()
    {
        try
        {
            if (!File.Exists(_filePath))
                return null;

            var json = await File.ReadAllTextAsync(_filePath);
            var state = JsonConvert.DeserializeObject<ModCatalogState>(json);

            if (state == null || state.ModIds.Count == 0)
            {
                _loggingService.LogInfo("ModCatalogStateService: State file is empty or invalid, treating as first run");
                return null;
            }

            State = state;
            return State;
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, "ModCatalogStateService: Failed to load state file, will rebuild");
            return null;
        }
    }

    public async Task SaveAsync(IEnumerable<string> currentModIds)
    {
        try
        {
            var ids = currentModIds.OrderBy(x => x).Distinct().ToList();
            State = new ModCatalogState
            {
                SchemaVersion = 1,
                ModIds = ids,
                SnapshotTimeUtc = DateTime.UtcNow
            };

            var json = JsonConvert.SerializeObject(State, Formatting.Indented);
            await File.WriteAllTextAsync(_filePath, json);
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, "ModCatalogStateService: Failed to save state");
        }
    }
}

# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

This is a WPF-based GHPC (Gunner, HEAT, PC!) Mod Manager built with .NET 8.0. The application manages game modifications, translations, and MelonLoader integration for the GHPC game.

## Common Development Commands

### Build and Run
- **Build**: `dotnet build` or use Visual Studio Build Solution (Ctrl+Shift+B)
- **Run Debug**: `dotnet run` or F5 in Visual Studio
- **Clean**: `dotnet clean`
- **Rebuild**: `dotnet clean && dotnet build`

### Testing
- Run tests: `dotnet test` (if test projects are added)

### Package Management
- Restore packages: `dotnet restore`
- Add package: `dotnet add package <PackageName>`

### Resource File Generation
When modifying .resx files, regenerate using:

```bash
# 生成英文资源
powershell.exe -Command "& 'C:\Program Files (x86)\Microsoft SDKs\Windows\v10.0A\bin\NETFX 4.8 Tools\ResGen.exe' 'Resources\Strings.resx' /str:cs,GHPC_Mod_Manager.Resources,Strings /publicclass"
mv "Resources/Strings.cs" "Resources/Strings.Designer.cs"

# 生成中文资源
powershell.exe -Command "& 'C:\Program Files (x86)\Microsoft SDKs\Windows\v10.0A\bin\NETFX 4.8 Tools\ResGen.exe' 'Resources\Strings.zh-CN.resx' /str:cs,GHPC_Mod_Manager.Resources,Strings.zh-CN /publicclass"
mv "Resources/Strings.zh-CN.cs" "Resources/Strings.zh-CN.Designer.cs"
```

---

## Architecture Overview

### MVVM Pattern Implementation

```
Views (XAML)
   ↓ DataBinding
ViewModels (CommunityToolkit.Mvvm)
   ↓ Dependency Injection
Services (Business Logic)
   ↓
Models & Data Layer
```

**关键原则：**
- View 只负责UI展示，不包含业务逻辑
- ViewModel 处理UI状态和命令，通过 ObservableProperty 实现数据绑定
- Service 封装所有业务逻辑，通过DI注入到ViewModel
- 所有跨层调用必须通过接口 (IService pattern)

---

## Core Services Architecture

### 1. 数据与配置服务

#### **SettingsService** (`ISettingsService`)
- **职责**: 全局配置管理
- **数据源**: `app_data/settings.json`
- **关键字段**:
  - `GameRootPath`: 游戏根目录
  - `Language`: UI语言 (zh-CN/en-US)
  - `UseGitHubProxy`: GitHub代理开关
  - `GitHubProxyServer`: 代理服务器选择
  - `ModListUrl`: Mod列表JSON URL
  - `UpdateChannel`: 更新通道 (Stable/Preview)

#### **ModI18nService** (`IModI18nService`)
- **职责**: Mod配置项的多语言标签管理
- **数据源**: 远程 `mod_i18n.json`
- **用途**: 将Mod配置文件的英文key转换为本地化显示文本

---

### 2. 网络与缓存服务

#### **NetworkService** (`INetworkService`)
- **职责**: 所有HTTP请求和GitHub API交互
- **核心功能**:
  - GitHub Releases获取（带7天持久化缓存）
  - 文件下载（支持Work Stealing多线程下载）
  - GitHub代理加速支持
  - API rate limit检测和处理
- **缓存策略**:
  - GitHub Releases: 7天持久化缓存 (`app_data/cache/github_releases/{owner}_{repo}_{YYYYMMDD}.json`)
  - GHPC.DMR.gg (ModConfig/ModI18n): **不缓存**，每次直接请求
  - 其他 URL: 24小时缓存 (`app_data/cache/*.json`)
  - `forceRefresh=true` 时跳过缓存直接请求，请求成功后覆盖写入缓存
- **重要方法**:
  - `GetGitHubReleasesAsync(owner, repo, forceRefresh)`: 获取GitHub Releases
  - `GetModConfigAsync(url, forceRefresh)`: 获取Mod列表配置
  - `GetModI18nConfigAsync(url, forceRefresh)`: 获取Mod多语言配置
  - `DownloadFileAsync(url, progress)`: 多线程下载文件
  - `ClearCache()`: 删除整个 cache/ 目录并清空 rate limit 锁

---

### 3. Mod管理服务

#### **ModManagerService** (`IModManagerService`)
- **职责**: Mod的完整生命周期管理
- **数据源**:
  - `mod_install_manifest.json`: 已安装Mod清单
  - 远程 `mod_list.json`: 可用Mod配置
- **核心流程**:
  1. **GetModListAsync**:
     - 加载远程mod_list.json
     - 扫描本地Mods目录
     - 合并手动安装的Mod
     - 查询每个Mod的最新版本（通过NetworkService）
     - 检测新增Mod（比较已安装清单，用于UI横幅提示）
  2. **InstallModAsync**:
     - 优先从备份快速恢复
     - 下载GitHub Release
     - 解压到Mods目录（支持ZIP、RAR、7z等格式）
     - 更新manifest
  3. **UpdateModAsync/UninstallModAsync**: 版本管理和卸载
  4. **EnableModAsync/DisableModAsync**: .dll ↔ .dll.disabled 切换

- **重要**:
  - `ModConfig.ReleaseUrl` 已经是完整的GitHub API URL
  - 格式: `https://api.github.com/repos/owner/repo/releases/latest`
  - 无需手动拼接，直接使用 `NetworkService.GetGitHubReleasesAsync` 解析
  - **新增Mod检测**: 通过比对本地清单与远程列表，检测新增Mod并触发UI横幅提示

#### **ModBackupService** (`IModBackupService`)
- **职责**: Mod版本备份，支持快速重装
- **备份位置**: `GHPCMM/modbackup/{modId}/{version}/`
- **用途**: 避免重复下载，提升重装速度

---

### 4. 翻译系统服务

#### **TranslationManagerService** (`ITranslationManagerService`)
- **职责**: 管理 XUnityAutoTranslator 核心插件、翻译资源包以及语言/开关状态
- **关键依赖**: 通过 `INetworkService` 获取 `translationconfig.json` 与 GitHub 发布信息，使用 `IFileOperationTracker` 记录文件操作
- **工作流程**:
  1. **配置解析**：读取设置中的 `TranslationConfigUrl`，解析 Owner/RepoName（或从 RepoUrl 推断），并缓存配置到 `app_data/translationconfig.json`
  2. **安装**：定位 XUnity AutoTranslator Zip 与最新 `ghpc-translation-*` 压缩包，下载后调用 `TrackedFileOperations.ExtractZipAsync` 解压到游戏目录，然后将处理过的文件区分为插件与翻译资源写入 `translation_install_manifest.json`
  3. **更新**：
     - **插件更新** (`UpdateXUnityPluginAsync`): 支持Progress回调显示下载速度，更新完成后刷新插件和总更新状态
     - **资源更新** (`UpdateTranslationFilesAsync`): 支持Progress回调显示下载速度，依据清单删除旧资源，下载/解压最新资源包，更新完成后刷新资源和总更新状态
  4. **卸载**：按清单删除插件与翻译资源，清理空目录并重置清单
  5. **运行期**：读取/写入 `AutoTranslator/Config.ini` 的语言项，并通过 `.dll`⇆`.GHPCMMBAK` 重命名控制插件启用状态，同时判定手动安装及拉取最新发布时间
- **关键存储**:
  - `app_data/translation_install_manifest.json`：记录版本、文件列表、启用状态与安装时间
  - `AutoTranslator/Translation/`：按语言划分的翻译资源目录
  - `AppSettings.TranslationConfigUrl`：翻译配置源地址，可在设置页调整
- **更新状态管理**:
  - `IsTranslationPluginUpdateAvailable`: 插件更新可用标志
  - `IsTranslationResourceUpdateAvailable`: 资源更新可用标志
  - `IsTranslationUpdateAvailable`: 总更新标志（插件 OR 资源）
  - 更新操作完成后必须刷新所有相关状态，确保UI横幅和按钮正确显示/隐藏

---

### 5. 系统集成服务

#### **ProcessService** (`IProcessService`)
- **职责**: GHPC.exe进程监控
- **事件**: `GameRunningStateChanged` - 游戏运行时禁用Mod操作
- **用途**: 防止运行时修改Mod导致崩溃

#### **MelonLoaderService** (`IMelonLoaderService`)
- **职责**: MelonLoader检测、安装和验证
- **流程**:
  - 检测游戏目录下MelonLoader文件夹
  - 从GitHub下载最新Release
  - 验证version.dll等关键文件

#### **SteamGameFinderService** (`ISteamGameFinderService`)
- **职责**: 自动检测GHPC游戏安装路径
- **触发时机**: SetupWizard进入游戏目录选择步骤（步骤3）时自动触发
- **搜索策略**:
  1. **读取注册表**: 查询 `HKEY_CURRENT_USER\Software\Valve\Steam` 获取Steam安装路径
  2. **解析游戏库配置**: 读取 `steamapps\libraryfolders.vdf` 获取所有游戏库路径
  3. **多重搜索方法**:
     - 直接路径搜索: `steamapps/common/Gunner, HEAT, PC!/Bin/GHPC.exe`
     - AppID Manifest搜索: 使用 `appmanifest_665650.acf` 定位游戏
     - 通用模糊搜索: 在common目录查找包含"Gunner"的文件夹
- **自动填充**: 找到路径后自动填充到UI并保存到settings
- **Steam版本验证**:
  - 用户手动选择路径后验证是否为Steam版本
  - 检测标志: `steamapps`目录结构、manifest文件、路径特征
  - 非Steam版本显示兼容性警告（仅支持Steam正版）
- **完整i18n**: 所有日志和提示信息支持多语言

#### **UpdateService** (`IUpdateService`)
- **职责**: 程序自更新检查
- **流程**:
  - 查询RoyZ-iwnl/GHPC-Mod-Manager releases
  - 下载新版本exe
  - 启动安装进程，退出当前程序

#### **ProtocolActivationService** (`IProtocolActivationService`)
- **职责**: 解析和处理 `ghpcmm://` 协议URI
- **路由**:
  - `ghpcmm://theme/unlock-endfield`: 解锁并应用终末地主题
- **流程**:
  - 验证URI格式和协议scheme
  - 匹配路由并执行对应操作
  - 操作完成后激活并前置主窗口
- **用途**: 支持URL协议激活，实现外部链接触发应用内功能

#### **NamedPipeProtocolIpcServer** (`IProtocolIpcServer`)
- **职责**: 单实例模式下主实例的IPC服务端，监听第二实例发来的协议URI
- **实现**: Named Pipes，管道名 `GHPC_Mod_Manager_ProtocolPipe`
- **流程**:
  - `Start()`: 在后台线程启动监听循环
  - 收到URI后转发给 `IProtocolActivationService.HandleAsync()`
  - `Dispose()`: 取消监听并释放资源
- **生命周期**: App.OnStartup中启动，App.OnExit中Dispose

#### **ProtocolIpcClient** (静态类)
- **职责**: 第二实例向主实例发送协议URI
- **实现**: Named Pipes客户端，连接同一管道名
- **超时**: 2秒连接超时
- **用途**: 第二实例启动时，将协议参数发送给主实例后退出

#### **单实例检测与IPC协作流程**
- **Mutex**: `GHPC_Mod_Manager_SingleInstance_Mutex`
- **流程**:
  1. 启动时创建Mutex，`createdNew=false` 表示已有实例
  2. 第二实例：通过 `ProtocolIpcClient.SendAsync()` 发送协议URI
  3. 第二实例：通过Win32 API (`SetForegroundWindow`/`ShowWindow`) 激活主实例窗口
  4. 第二实例：释放Mutex并Shutdown
- **URL协议注册**: `HKCU\Software\Classes\ghpcmm`，首次运行时注册
- **Mutex释放**: 在 `OnExit` 中 `ReleaseMutex()` + `Dispose()`，必须在 `Environment.Exit` 之前

---

### 6. UI支持服务

#### **LoggingService** (`ILoggingService`)
- **职责**: 统一日志输出（支持i18n）
- **方法**: `LogInfo(key, args)` / `LogError(ex, key, args)`
- **输出**: UI日志窗口 + 调试控制台

#### **NavigationService** (`INavigationService`)
- **职责**: 主窗口View切换
- **用途**: 主视图 ↔ 设置视图 导航

#### **ThemeService** (`IThemeService`)
- **职责**: 应用主题切换（Light/Dark）
- **用途**: UI主题管理

#### **AnnouncementService** (`IAnnouncementService`)
- **职责**: 获取和显示公告消息
- **数据源**: 远程announcement.json
- **显示方式**: 使用WebView2控件渲染富文本公告内容（AnnouncementWindow）
- **用户选项**: 提供"不再显示此公告"选项，保存用户选择

#### **ModBrowserViewModel** (UI状态管理)
- **职责**: Mod浏览页的UI状态和交互逻辑
- **核心功能**:
  - Mod列表筛选（文本搜索、Tag筛选、已安装/未安装过滤）
  - 新增Mod横幅提示：检测到新增Mod时显示会话级横幅（不持久化）
  - 游戏版本显示：从Latest.log读取当前游戏版本
- **横幅机制**: 
  - `HasNewMods`: 是否有新增Mod
  - `NewModsCount`: 新增Mod数量
  - `NewModsPreview`: 新增Mod名称列表（显示所有新增Mod）
  - `IsNewModsBannerDismissed`: 用户是否已关闭横幅
  - 横幅可见性：`CanShowNewModsBanner = HasNewMods && !IsNewModsBannerDismissed`

---

### 7. 文件追踪服务

#### **IFileOperationTracker & TrackedFileOperations**
- **职责**: 跟踪Mod安装时的文件操作
- **用途**:
  - 记录所有文件复制、重命名操作
  - 支持卸载时精确回滚
  - 防止文件残留
- **压缩支持**: 使用SharpCompress库支持多种压缩格式（ZIP、RAR、7z、TAR、GZ等）

---

## Data Storage Structure

```
[program_directory]/
├── app_data/
│   ├── settings.json                      # 全局配置
│   ├── mod_install_manifest.json          # 已安装Mod清单
│   ├── translation_install_manifest.json  # 翻译安装清单
│   ├── mod_i18n.json                      # Mod配置多语言
│   └── cache/
│       └── github_releases/               # GitHub API缓存 (7天过期)
│           └── {owner}_{repo}_{YYYYMMDD}.json
│
└── [GameRoot]/
    ├── Mods/                              # Mod安装目录
    ├── MelonLoader/                       # MelonLoader目录
    ├── Plugins/                           # 翻译插件目录
    ├── Translation/                       # 翻译文件目录
    └── GHPCMM/
        └── modbackup/                     # Mod备份
            ├── {modId}/
            │   └── {version}/             # 版本备份
            └── disabled/                  # 禁用的Mod备份
                └── {modId}/
```

---

## Key Technical Requirements

1. **Administrator Privileges**: 应用需要管理员权限 (app.manifest配置)
2. **Process Monitoring**: GHPC.exe运行时禁用所有Mod操作
3. **Security Warnings**: 脚本类Mod安装前必须显示安全警告
4. **Certificate Handling**: HttpClient使用系统证书存储（代理兼容性）
5. **Progress Reporting**: 下载使用 `HttpCompletionOption.ResponseHeadersRead` 支持进度条
6. **Async Operations**: 所有网络和文件I/O必须异步
7. **Error Handling**: 所有异常必须捕获并记录i18n日志
8. **Resource Management**: HttpClient、FileStream等资源正确释放

---

## Key Dependencies

- **CommunityToolkit.Mvvm** (8.2.2): MVVM框架，ObservableProperty/RelayCommand
- **Microsoft.Extensions.DependencyInjection/Hosting**: 依赖注入容器
- **Newtonsoft.Json**: JSON序列化
- **SharpCompress** (0.47.3): 多格式压缩文件处理（ZIP、RAR、7z、TAR等）
- **Microsoft.Web.WebView2** (1.0.3537.50): 富文本公告显示（AnnouncementWindow）
- **LibGit2Sharp**: Git仓库操作（翻译系统）

---

## Critical Implementation Patterns

### 1. ModConfig.ReleaseUrl 格式
```csharp
// ✅ ReleaseUrl 已经是完整的GitHub API URL
// 格式: https://api.github.com/repos/owner/repo/releases/latest
// 直接使用，无需手动解析

// ModManagerService.GetModListAsync 中:
var releases = await _networkService.GetGitHubReleasesAsync(
    GetRepoOwner(modConfig.ReleaseUrl),   // 从API URL提取owner
    GetRepoName(modConfig.ReleaseUrl),    // 从API URL提取repo
    forceRefresh
);

// ModManagerService 内部的URL解析:
private string GetRepoOwner(string repoUrl)
{
    var segments = new Uri(repoUrl).AbsolutePath.Trim('/').Split('/');
    // https://api.github.com/repos/owner/repo/releases/latest
    // -> ["repos", "owner", "repo", "releases", "latest"]
    return segments.Length >= 2 ? segments[1] : "";
}

private string GetRepoName(string repoUrl)
{
    var segments = new Uri(repoUrl).AbsolutePath.Trim('/').Split('/');
    return segments.Length >= 3 ? segments[2] : "";
}

// ❌ 不要在其他地方重新实现URL解析逻辑
// ❌ 不要假设ReleaseUrl是 github.com 格式
```

### 2. Mod更新检查流程
```csharp
// MainViewModel.CheckForModUpdatesAsync 逻辑:
1. 根据 OnlyCheckInstalledMods 筛选要检查的Mod列表
2. 调用 ModManagerService.GetModListAsync(forceRefresh=true) 获取最新版本
3. 遍历筛选后的Mod，对比 InstalledVersion vs LatestVersion
4. 更新UI显示

// ✅ 正确方式: 利用 ModManagerService 已有的版本查询逻辑
var modList = await _modManagerService.GetModListAsync(forceRefresh: true);
foreach (var mod in modsToCheck)
{
    var latestMod = modList.FirstOrDefault(m => m.Id == mod.Id);
    if (latestMod != null && mod.InstalledVersion != latestMod.LatestVersion)
    {
        // 发现更新
    }
}

// ❌ 不要重复实现版本查询逻辑
// ❌ 不要绕过 ModManagerService 直接调用 NetworkService
```

#### 版本排序与 LatestVersion 规范（2026-07 修复）

```csharp
// ✅ "最新版本" 的确定方式：按 PublishedAt 倒序取第一个
// NetworkService.GetGitHubReleasesAsync 返回前已 OrderByDescending(r => r.PublishedAt)
// 所有调用方的 releases.FirstOrDefault() / releases[0] 自动取到最新发布版本
// 不要依赖 GitHub API 默认的 created_at 排序（修改发布时间后顺序可能不一致）

// ✅ LatestVersion 必须保持 GitHub TagName 原样（含 v 前缀）
// DownloadModPackageAsync 用 r.TagName == version 精确匹配定位 release
// 任何对 LatestVersion 做 TrimStart('v') 都会破坏下载匹配

// ✅ 版本比较（判断是否有更新）必须归一化
// 用 NormalizeVersion(去 v/V 前缀 + Trim) 后再比较，避免 v2.0 vs 2.0 误判
// MainViewModel / LaunchCheckService 已有 NormalizeVersion，CanUpdate 也应统一
```

#### forceRefresh 一致性（2026-07 修复）

所有安装/更新入口在执行前必须 `GetModReleasesAsync(forceRefresh: true)` 刷新版本，避免用 7 天缓存的旧 TagName 下载到非最新版：

| 入口 | 实现 |
|---|---|
| ModBrowserViewModel.InstallModCoreAsync | 已有（:318-327 模式） |
| InstalledModsViewModel.UpdateModAsync / ReinstallModAsync | 已补 |
| ModDetailViewModel.InstallSelectedVersionCoreAsync / UpdateAsync | 已补（保留用户已选版本） |

```csharp
// 标准模式：安装前强制刷新，失败容忍继续
try
{
    var freshReleases = await _modManagerService.GetModReleasesAsync(mod.Id, forceRefresh: true);
    var freshVersion = freshReleases.FirstOrDefault()?.TagName;
    if (!string.IsNullOrEmpty(freshVersion) && freshVersion != mod.LatestVersion)
        mod.LatestVersion = freshVersion;
}
catch { /* 刷新失败时使用已有版本继续 */ }
```

### 3. 缓存策略
```
// 两套独立的缓存机制:

① GitHub Releases 专用缓存
   路径: app_data/cache/github_releases/{owner}_{repo}_{YYYYMMDD}.json
   过期: 7天（按文件名日期判断，同一天内命中同一文件）
   forceRefresh=false → 读缓存
   forceRefresh=true  → 跳过缓存，请求成功后覆盖写入
   失败兜底: ignoreExpiry=true 读取任意日期旧文件

② 通用持久化缓存
   路径: app_data/cache/{cacheKey}.json（内含 Timestamp 字段）
   过期: 24小时（适用于非 DMR.gg 的 URL）
   forceRefresh=false → 读缓存
   forceRefresh=true  → 跳过缓存，请求成功后覆盖写入
   失败兜底: ignoreExpiry=true 读取过期文件

③ GHPC.DMR.gg 不缓存
   ShouldCacheUrl("ghpc.dmr.gg") = false
   ModConfig / ModI18n 每次直接请求网络，forceRefresh 对其无效

// Rate Limit 处理:
   触发 403/rate limit → 标记 _rateLimitBlocks[repo]
   后续请求该 repo → 跳过 API，返回过期缓存
   ClearCache() / ClearRateLimitBlocks() → 清除锁，允许重试
```

---

## Mod 安装/更新安全与并发机制（2026-07 修复）

> 详细审查报告与未修问题清单见 `docs/MOD-LIFECYCLE-AUDIT.md`。
> 本轮修复了 Critical 3 + High 8 + M3 = 13 项，剩余 23 项（Medium/Low）记录于审查报告。

### 1. 路径安全（zip-slip 防护）

所有压缩包解压必须校验目标路径未越界：

```csharp
// Helpers/PathSecurityHelper.cs
PathSecurityHelper.EnsurePathInsideDirectory(fullPath, baseDirectory)
// 用 Path.GetFullPath 解析 .. 段，基目录补分隔符后 StartsWith 校验
// 越界抛 IOException，由调用方 catch 统一处理

// ✅ 4 处调用点（Path.Combine 之后、写文件之前）:
//   TrackedFileOperations.ExtractArchiveAsync / ExtractZipAsync
//   ModManagerService.Replace.ExtractReplaceArchiveAsync
//   MelonLoaderService.InstallMelonLoaderAsync

// ❌ 不要直接 Path.Combine(targetDir, entryKey) 后写文件，必须先校验
// 应用以管理员权限运行，越界写可直达系统目录
```

### 2. Manifest 并发安全

`_installManifest` / `_availableMods` 是实例字段（ModManagerService 为 Singleton），被多个 ViewModel 并发访问。安装中点「刷新」曾导致清单全部丢失。

```csharp
// ✅ 使用 _manifestLock (SemaphoreSlim) 保护文件 I/O
private readonly SemaphoreSlim _manifestLock = new(1, 1);

// LoadManifestAsync: 加锁 + 不再预先置空 + 失败保留旧引用
//   （避免读异常期间字段被置空后被后续 Save 覆盖磁盘为空清单）
// SaveManifestAsync: 加锁 + 原子写（temp 文件 + File.Move(overwrite:true)）

// ✅ 拆分 SaveManifestCoreAsync（不持锁）避免重入死锁
//   SemaphoreSlim 不可重入，MigrateManifestIfNeededAsync 在 LoadManifestAsync 持锁时调 SaveManifestCoreAsync

// ❌ 不要用同步 _manifestLock.Wait()（UI 线程会阻塞消息循环）
// ❌ 不要把整个 InstallModAsync 包进锁（含下载/解压长耗时操作会阻塞 Refresh）
//    只锁 Load/Save 两个 I/O 点即可消除数据丢失灾难
```

### 3. Replace 模式安装回滚

Replace 模式替换游戏核心文件，安装失败必须回滚，否则游戏原文件滞留 `.GHPCMM` 致游戏无法启动。

```csharp
// ✅ InstallReplaceModeAsync 包 try-catch
//   catch 中调 RollbackReplaceInstallAsync(modId, installedPaths, backupFiles) 后 throw
//   RollbackReplaceInstallAsync = DeleteManagedFilesAsync(删已写新文件) + RestoreImmediateBackupFilesAsync(还原.GHPCMM备份)
//   复用现有方法，无需新原语

// ✅ EnsureReplaceBackupStateIsCleanAsync 「否」分支:
//   从 File.Delete 改为 RestoreImmediateBackupFilesAsync（还原原文件而非删除）
//   .GHPCMM{modId} 可能含活动安装的游戏原文件备份，直接删除会永久丢失

// ✅ InstallModAsync 入口检测已安装:
//   _installManifest.InstalledMods.ContainsKey(modConfig.Id) 则转 UpdateModAsync
//   避免重复安装已装 Replace Mod 时活动备份被误删
```

### 4. 备份完整性校验

```csharp
// ✅ RestoreDirectFilesFromPlanAsync / RestoreReplaceFilesFromPlanAsync:
//   File.Copy 前校验 entry.Sha256（复用 CalculateFileSha256Async），不一致抛 IOException
//   entry.Sha256 为空时跳过（旧 manifest 向后兼容）

// ✅ ReadBackupPlanAsync: 返回前比对 sha256Map.Keys 与磁盘文件
//   缺失则 LogError + 返回 null（中止快速重装，fall through 到标准下载）
```

### 5. IsGameRunning 检查时机

```csharp
// ✅ InstallModAsync / UpdateModAsync: 下载前检查 + 写入前复查（TOCTOU 防护）
//    下载可能持续数分钟，期间用户启动游戏，写入前复查可拦截
//    残余窗口约 2 秒（ProcessService 轮询粒度）

// ⚠️ 未修: UninstallModAsync / ToggleModAsync 仍缺 IsGameRunning 检查（M12）
//    建议后续在入口及关键文件操作前统一检查
```

### 6. 线程安全集合

```csharp
// ✅ NetworkService:
//   _rateLimitBlocks: static ConcurrentDictionary（并行 Task.WhenAll 并发读写）
//   _sessionCache: ConcurrentDictionary（Refresh 双击并发防御）
//   ConcurrentDictionary 的 ContainsKey/索引赋值/Clear 与 Dictionary 签名兼容，访问点零改动
```

---

## 缓存与刷新机制

### 三个刷新入口的行为对比

| | 主界面「刷新」 | 主界面「检查更新」 | 设置「强制刷新云端配置」 |
|---|---|---|---|
| GitHub Releases | 读7天缓存 | `forceRefresh=true` 绕过 | ClearCache后重拉 |
| ModConfig (mod_list.json) | 读24h缓存/DMR直连 | 不涉及 | ClearCache后重拉 |
| main.json | 不涉及 | 不涉及 | 直接重新拉取并等待 |
| mod_i18n.json | 不涉及 | 不涉及 | `forceRefresh=true` |
| 翻译更新状态 | 读缓存检查 | `forceRefresh=true` | 不涉及 |
| 清缓存文件 | ❌ | ❌ | ✅ 删整个 cache/ |
| 清 rate limit 锁 | ✅ | ✅ | ✅ |

### forceRefresh 传递链路

```
// 检查更新按钮 → CheckForModUpdatesAsync
GetGitHubReleasesAsync(forceRefresh: true)   // 所有待检查 Mod 并行

// 主界面刷新按钮 → RefreshDataAsync
GetModListAsync(forceRefresh: false)
  └── LoadAvailableModsAsync(false)
        └── GetModConfigAsync(url, false)       // DMR.gg 直连，其他读缓存
  └── GetGitHubReleasesAsync(forceRefresh: false) // 读7天缓存

// 设置强制刷新按钮 → ForceRefreshConfigurationsAsync
ClearCache()                                   // 删整个 cache/ + 清 rate limit
ForceReloadAsync()                             // 直接 await 拉取 main.json
RefreshModI18nConfigAsync(forceRefresh: true)  // DMR.gg 直连（forceRefresh 无实际影响）
GetModListAsync(forceRefresh: true)            // 重拉 ModConfig + 所有 GitHub Releases
```

### 注意事项

- **DMR.gg 的 forceRefresh 无意义**：`ShouldCacheUrl` 返回 false，该域名下的请求永远不经过缓存层，`forceRefresh` 参数对其没有任何效果
- **GitHub Releases 缓存粒度是"天"**：同一天内 `forceRefresh=false` 的多次调用命中同一个缓存文件，只有 `forceRefresh=true` 才能在同一天内获取更新
- **ClearCache 的副作用**：会同时清空 `github_releases/` 目录，导致下次 GetModListAsync 必须重新拉取所有 Mod 版本，耗时较长

---

## Localization Architecture

- **UI字符串**: `Resources/Strings.resx` (英文) + `Strings.zh-CN.resx` (中文)
- **Designer.cs生成**: ResGen.exe自动生成强类型访问类
- **语言切换**: `CultureInfo.CurrentUICulture` 决定当前语言
- **日志i18n**: `LoggingService.LogInfo(key, args)` 自动查找资源
- **Mod配置i18n**: `mod_i18n.json` 提供配置项的多语言标签

---

## UI Icon System

项目使用统一的矢量图标系统，所有图标定义为 `Geometry` 资源，通过 `{StaticResource}` 引用。

### 图标资源位置

- **定义**: `Themes/Icons.xaml` - 所有图标的 Geometry 定义
- **注册**: `App.xaml` 的 `MergedDictionaries` 中引用，**必须在 ControlStyles.xaml 之前**
- **引用**: 所有 XAML 中使用 `Data="{StaticResource IconXXX}"`

### 图标清单

**对话框图标** (28x28 坐标系):
- `IconInformation` - 圆圈+i (信息)
- `IconWarning` - 三角+! (警告)
- `IconError` - 圆圈+X (错误)
- `IconSuccess` - 圆圈+✓ (成功)
- `IconQuestion` - 圆圈+? (问题)

**标题栏图标** (12x12 坐标系):
- `IconMinimize` - 横线 (最小化)
- `IconMaximize` - 方框 (最大化)
- `IconRestore` - 重叠方框 (还原)
- `IconClose` - X (关闭)

**通用UI图标** (24x24 坐标系):
- `IconChevronDown` - 下拉箭头 (ComboBox)
- `IconSparkle` - 四角星 (新增Mod横幅)
- `IconDownload` - 下载箭头 (App更新横幅)
- `IconGlobe` - 地球 (翻译更新横幅)
- `IconRemove` - 方框减号 (下架Mod横幅)

### 使用规范

```xaml
<!-- 对话框图标 - Stroke only -->
<Path Data="{StaticResource IconWarning}"
      Stroke="{DynamicResource WarningBrush}"
      StrokeThickness="2"
      Fill="Transparent"
      Stretch="Uniform"
      Width="22" Height="22"/>

<!-- 横幅图标 - Stroke + Fill -->
<Path Data="{StaticResource IconSparkle}"
      Stroke="{DynamicResource AccentBrush}"
      StrokeThickness="1.5"
      Fill="{DynamicResource AccentBrush}"
      Stretch="Uniform"
      Width="18" Height="18"/>

<!-- 标题栏图标 - Stroke only, 固定尺寸 -->
<Path Data="{StaticResource IconClose}"
      Stroke="{Binding Foreground, RelativeSource={RelativeSource AncestorType=Button}}"
      StrokeThickness="1.5"
      Width="12" Height="12"/>
```

### 关键注意事项

1. **加载顺序**: Icons.xaml 必须在 ControlStyles.xaml **之前**加载，否则 ComboBox 模板找不到 `IconChevronDown`
2. **StaticResource**: 图标使用 `StaticResource` 引用（编译时解析），不支持 `DynamicResource`
3. **主题适配**: 图标颜色通过 `Stroke`/`Fill` 绑定到主题色，自动适配深色/浅色主题
4. **坐标系**: 不同图标使用不同坐标系，通过 `Stretch="Uniform"` 自动缩放到指定尺寸
5. **WindowTitleBar.cs**: 最大化/还原图标切换使用 `App.Current.FindResource("IconXXX")` 动态查找

### 禁止事项

- ❌ 不要使用 Unicode emoji（✨、✕等）— 渲染不一致，不支持主题色
- ❌ 不要内联 Path Data — 无法复用，难以维护
- ❌ 不要在 App.xaml 的 ResourceDictionary 中直接定义图标 — 会导致 MergedDictionaries 中的 XAML 找不到资源

---

## Theme System (主题系统)

### 主题文件结构

```
Themes/
├── Foundation.xaml      # 基础资源：颜色、圆角、字体定义
├── LightTheme.xaml       # 浅色主题（默认）
├── DarkTheme.xaml        # 深色主题
├── EndfieldTheme.xaml    # 终末地主题（工业风）
├── Icons.xaml            # 矢量图标定义
├── ControlStyles.xaml    # 控件样式
└── PageStyles.xaml      # 页面样式
```

### 主题加载顺序 (App.xaml)

```xml
<ResourceDictionary.MergedDictionaries>
    <ResourceDictionary Source="/Themes/Foundation.xaml"/>      <!-- 基础资源 -->
    <ResourceDictionary Source="/Themes/LightTheme.xaml"/>       <!-- 默认主题 -->
    <ResourceDictionary Source="/Themes/Icons.xaml"/>           <!-- 图标（必须在ControlStyles前） -->
    <ResourceDictionary Source="/Themes/ControlStyles.xaml"/>    <!-- 控件样式 -->
    <ResourceDictionary Source="/Themes/PageStyles.xaml"/>      <!-- 页面样式 -->
</ResourceDictionary.MergedDictionaries>
```

### 动态主题切换

主题通过 `ThemeService` 动态加载/卸载：

```csharp
// Services/ThemeService.cs
public void ApplyTheme(AppTheme theme)
{
    // 1. 移除现有主题资源
    var toRemove = Application.Current.Resources.MergedDictionaries
        .Where(d => d.Source?.ToString().Contains("Theme.xaml") == true)
        .ToList();
    foreach (var dict in toRemove)
        Application.Current.Resources.MergedDictionaries.Remove(dict);

    // 2. 加载新主题
    var themeResourcePath = theme switch
    {
        AppTheme.Light => "/Themes/LightTheme.xaml",
        AppTheme.Dark => "/Themes/DarkTheme.xaml",
        AppTheme.Endfield => "/Themes/EndfieldTheme.xaml",
        _ => "/Themes/LightTheme.xaml"
    };

    var themeResource = new ResourceDictionary { Source = new Uri(themeResourcePath, UriKind.Relative) };
    Application.Current.Resources.MergedDictionaries.Add(themeResource);
}
```

---

### 如何创建新主题

#### 1. 创建主题文件

在 `Themes/` 目录下创建 `MyTheme.xaml`：

```xml
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">

    <!-- ── 圆角覆盖 ── -->
    <CornerRadius x:Key="RadiusSmall">4</CornerRadius>
    <CornerRadius x:Key="RadiusMedium">8</CornerRadius>

    <!-- ── 颜色定义 ── -->
    <Color x:Key="AccentColor">#0078D4</Color>
    <Color x:Key="SurfaceColor">#FFFFFF</Color>
    <Color x:Key="OnSurfaceColor">#1A1A1A</Color>

    <!-- ── 画刷定义 ── -->
    <SolidColorBrush x:Key="AccentBrush" Color="{StaticResource AccentColor}"/>
    <SolidColorBrush x:Key="SurfaceBrush" Color="{StaticResource SurfaceColor}"/>
    <SolidColorBrush x:Key="OnSurfaceBrush" Color="{StaticResource OnSurfaceColor}"/>

    <!-- ── 覆盖控件样式（可选） ── -->
    <Style x:Key="FilledButtonStyle" TargetType="Button">
        <Setter Property="Background" Value="{DynamicResource AccentBrush}"/>
        <Setter Property="Foreground" Value="White"/>
        <Setter Property="Padding" Value="16,8"/>
        <!-- 更多Setter... -->
    </Style>

</ResourceDictionary>
```

#### 2. 注册主题枚举

在 `Models/DataModels.cs` 的 `AppTheme` 枚举中添加：

```csharp
public enum AppTheme
{
    Light,
    Dark,
    Endfield,
    MyTheme  // 新增
}
```

#### 3. 在 ThemeService 中添加路径

```csharp
// Services/ThemeService.cs
var themeResourcePath = theme switch
{
    AppTheme.Light => "/Themes/LightTheme.xaml",
    AppTheme.Dark => "/Themes/DarkTheme.xaml",
    AppTheme.Endfield => "/Themes/EndfieldTheme.xaml",
    AppTheme.MyTheme => "/Themes/MyTheme.xaml",  // 新增
    _ => "/Themes/LightTheme.xaml"
};
```

#### 4. 在设置界面添加选项（可选）

如果需要让用户在设置中切换主题，可能需要修改 `ViewModels/SettingsViewModel.cs`。

---

### 如何添加主题专属动画

#### 1. 在主题 XAML 中定义动画 Storyboard

```xml
<!-- Themes/MyTheme.xaml -->

<!-- 主视图切换动画：水平滑动 -->
<Storyboard x:Key="MyThemePageSlideIn">
    <DoubleAnimation Storyboard.TargetProperty="(UIElement.RenderTransform).(TranslateTransform.X)"
                     From="40" To="0" Duration="0:0:0.25">
        <DoubleAnimation.EasingFunction>
            <CubicEase EasingMode="EaseOut"/>
        </DoubleAnimation.EasingFunction>
    </DoubleAnimation>
    <DoubleAnimation Storyboard.TargetProperty="Opacity"
                     From="0.6" To="1" Duration="0:0:0.25"/>
</Storyboard>

<!-- 子页面切换动画：垂直滑动 -->
<Storyboard x:Key="MyThemeSubPageSlideIn">
    <DoubleAnimation Storyboard.TargetProperty="(UIElement.RenderTransform).(TranslateTransform.Y)"
                     From="25" To="0" Duration="0:0:0.18">
        <DoubleAnimation.EasingFunction>
            <QuadraticEase EasingMode="EaseOut"/>
        </DoubleAnimation.EasingFunction>
    </DoubleAnimation>
    <DoubleAnimation Storyboard.TargetProperty="Opacity"
                     From="0.7" To="1" Duration="0:0:0.18"/>
</Storyboard>
```

#### 2. 创建动画触发器（TransitionContentControl）

在 `Helpers/PageTransitionBehavior.cs` 中创建自定义控件：

```csharp
// 监听 Content 变化的控件，用于子页面动画
public class TransitionContentControl : ContentControl
{
    public TransitionContentControl()
    {
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        PlayTransition();
    }

    protected override void OnContentChanged(object oldContent, object newContent)
    {
        base.OnContentChanged(oldContent, newContent);
        Dispatcher.InvokeAsync(PlayTransition);
    }

    private void PlayTransition()
    {
        // 尝试从主题资源中找到动画
        var storyboard = TryFindResource("MyThemeSubPageSlideIn") as Storyboard
                      ?? Application.Current.TryFindResource("MyThemeSubPageSlideIn") as Storyboard;

        if (storyboard != null)
        {
            if (RenderTransform is not TranslateTransform)
                RenderTransform = new TranslateTransform();
            storyboard.Begin(this);
        }
    }
}
```

#### 3. 应用到视图

**MainWindow.xaml**（主视图切换动画）：

```xml
<Window ...>
    xmlns:helpers="clr-namespace:GHPC_Mod_Manager.Helpers"

    <ContentPresenter Grid.Row="1"
                      Content="{Binding CurrentView}"
                      helpers:PageTransitionBehavior.IsMainTransition="True"/>
</Window>
```

**MainView.xaml**（子页面切换动画）：

```xml
<UserControl ...>
    xmlns:helpers="clr-namespace:GHPC_Mod_Manager.Helpers"

    <helpers:TransitionContentControl Margin="16"
                                      Content="{Binding CurrentPageView}"/>
</UserControl>
```

#### 4. PageTransitionBehavior 实现（主视图动画）

```csharp
public static class PageTransitionBehavior
{
    public static readonly DependencyProperty IsMainTransitionProperty =
        DependencyProperty.RegisterAttached(
            "IsMainTransition",
            typeof(bool),
            typeof(PageTransitionBehavior),
            new PropertyMetadata(false, OnIsMainTransitionChanged));

    private static void OnIsMainTransitionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ContentPresenter presenter && (bool)e.NewValue)
        {
            presenter.Loaded += OnPresenterLoaded;
        }
    }

    private static void OnPresenterLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is ContentPresenter presenter)
        {
            presenter.Loaded -= OnPresenterLoaded;

            var storyboard = presenter.TryFindResource("MyThemePageSlideIn") as Storyboard
                          ?? Application.Current.TryFindResource("MyThemePageSlideIn") as Storyboard;

            if (storyboard != null)
            {
                if (presenter.RenderTransform is not TranslateTransform)
                    presenter.RenderTransform = new TranslateTransform();
                storyboard.Begin(presenter);
            }
        }
    }
}
```

---

### 终末地主题动画实现参考

终末地主题是工业风格，动画设计为硬朗的滑动效果：

| 动画场景 | 动画类型 | 持续时间 | 缓动函数 |
|----------|----------|----------|----------|
| 主视图切换 | 水平滑动 40px + 透明度 | 250ms | CubicEase EaseOut |
| 子页面切换 | 垂直滑动 25px + 透明度 | 180ms | QuadraticEase EaseOut |

**关键实现点**：
- 使用 `TransitionContentControl` 监听 `OnContentChanged` 触发子页面动画
- 使用 `ContentPresenter.Loaded` 触发主视图首次加载动画
- 动画 Storyboard 定义在主题 XAML 中，通过 `TryFindResource` 查找
- 使用 `RenderTransform` 而非 `Margin` 动画以获得更好性能

---

### 主题覆盖机制

子主题可以覆盖父主题的资源：

```xml
<!-- 终末地主题覆盖所有圆角为0（工业风硬线条） -->
<CornerRadius x:Key="RadiusNone">0</CornerRadius>
<CornerRadius x:Key="RadiusExtraSmall">0</CornerRadius>
<CornerRadius x:Key="RadiusSmall">0</CornerRadius>
<CornerRadius x:Key="RadiusMedium">0</CornerRadius>
<CornerRadius x:Key="RadiusLarge">0</CornerRadius>
<CornerRadius x:Key="BadgeCornerRadius">0</CornerRadius>

<!-- 覆盖按钮样式 -->
<Style x:Key="FilledButtonStyle" TargetType="Button" BasedOn="{StaticResource FilledButtonStyle}">
    <Setter Property="CornerRadius" Value="0"/>
    <!-- 其他覆盖 -->
</Style>
```

---

## Security Considerations

- **文件操作追踪**: 所有安装操作记录到manifest，支持完整卸载
- **SSL证书验证**: NetworkService使用系统证书存储，支持企业代理

---

## Development Notes

### 通用规范
- **框架**: .NET 8.0-windows with WPF
- **语言特性**: 启用 implicit usings 和 nullable reference types
- **IDE**: Visual Studio 2022 或更高版本（WPF设计器支持）
- **i18n要求**: 所有面向用户的文本必须使用 `Strings.ResourceKey`，禁止硬编码

### Shell命令规范
- ✅ 使用 `rm` 删除文件
- ❌ 不要使用 `del` (Windows CMD命令)
- ✅ 文件操作优先使用专用工具: Read/Write/Edit
- ❌ 不要用 `cat`/`echo` 等bash命令操作文件

### ResGen操作
- 修改 `.resx` 后运行 `dotnet build` 自动生成
- 手动生成使用上述 PowerShell 命令

---

## Code Comment Standards (代码注释规范)

### 使用简明简体中文注释

**原则**: 代码注释使用简洁、直白的简体中文，避免冗长和过度解释。

#### ✅ 良好示例
```csharp
// 解析GitHub仓库URL，提取owner和repo名称
private string GetRepoOwner(string repoUrl)
{
    // URL格式: https://github.com/owner/repo/releases
    // 分割后: ["owner", "repo", "releases"]
    var segments = new Uri(repoUrl).AbsolutePath.Trim('/').Split('/');
    return segments.Length >= 1 ? segments[0] : "";
}

// 检查Mod更新，仅检查已安装或全部Mod（根据复选框）
private async Task CheckForModUpdatesAsync()
{
    // 根据复选框筛选要检查的Mod
    var modsToCheck = OnlyCheckInstalledMods
        ? Mods.Where(m => m.IsInstalled).ToList()
        : Mods.ToList();

    // 逐个查询GitHub最新版本
    foreach (var mod in modsToCheck)
    {
        var releases = await _networkService.GetGitHubReleasesAsync(owner, repo, forceRefresh: true);
        // ...
    }
}
```

#### ❌ 不良示例
```csharp
// 不良示例1: 注释过于冗长
// 这个方法的主要目的是从传入的GitHub仓库完整URL字符串中
// 通过解析URI对象并分割路径的方式提取出仓库的所有者名称
// 这样我们就可以在后续的GitHub API调用中使用这个所有者名称
private string GetRepoOwner(string repoUrl) { ... }

// 不良示例2: 使用英文注释
// Parse GitHub repository URL to extract owner name
private string GetRepoOwner(string repoUrl) { ... }

// 不良示例3: 无意义的注释
// 获取repo owner
private string GetRepoOwner(string repoUrl) { ... }
```

#### 注释内容指南

1. **复杂逻辑**: 解释"为什么"而不是"是什么"
   ```csharp
   // ✅ 好
   // forceRefresh时也要更新缓存，避免下次启动读取旧数据
   await SaveGitHubReleasesToPersistentCacheAsync(owner, repo, result);

   // ❌ 差
   // 保存到缓存
   await SaveGitHubReleasesToPersistentCacheAsync(owner, repo, result);
   ```

2. **数据格式**: 注明数据结构和示例
   ```csharp
   // ✅ 好
   // segments = ["owner", "repo", "releases"]
   var segments = uri.AbsolutePath.Trim('/').Split('/');

   // ❌ 差
   // 分割路径
   var segments = uri.AbsolutePath.Trim('/').Split('/');
   ```

3. **重要决策**: 说明设计选择的原因
   ```csharp
   // ✅ 好
   // 不调用GetModListAsync，避免检查所有Mod，忽略用户筛选条件
   foreach (var mod in modsToCheck)
   {
       var releases = await _networkService.GetGitHubReleasesAsync(...);
   }

   // ❌ 差
   // 检查更新
   foreach (var mod in modsToCheck) { ... }
   ```

4. **临时方案/已知问题**: 明确标注
   ```csharp
   // TODO: 未来支持增量更新，避免重新下载整个Mod
   // FIXME: Rate Limit处理不完善，需要添加重试机制
   // HACK: 临时绕过LibGit2Sharp的proxy bug
   ```

5. **公共API/接口**: 使用XML注释
   ```csharp
   /// <summary>
   /// 获取GitHub仓库的最新Releases
   /// </summary>
   /// <param name="repoOwner">仓库所有者</param>
   /// <param name="repoName">仓库名称</param>
   /// <param name="forceRefresh">是否跳过缓存强制刷新</param>
   /// <returns>Release列表，按发布时间倒序</returns>
   Task<List<GitHubRelease>> GetGitHubReleasesAsync(string repoOwner, string repoName, bool forceRefresh = false);
   ```

---

## File Structure Guidelines

- **Views**: 只包含XAML和最小的code-behind
- **ViewModels**: 处理UI逻辑、命令绑定，依赖Service
- **Services**: 无状态业务逻辑，通过DI注入
- **Models**: 数据模型和DTO，无业务逻辑
- **配置文件**: 统一使用JSON格式

---

## 翻译系统更新检查与更新流程总结

### 检查更新触发流程

**自动检查（应用启动时）**

```csharp
// MainViewModel.InitializeAsync() → RefreshDataAsync()
// 使用默认缓存行为，避免频繁API请求
IsTranslationPluginUpdateAvailable = await _translationManagerService.IsXUnityUpdateAvailableAsync();
IsTranslationResourceUpdateAvailable = await _translationManagerService.IsTranslationUpdateAvailableAsync();
```

**手动检查（点击"检查更新"按钮）**

```csharp
// MainViewModel.CheckForModUpdatesAsync() → CheckTranslationUpdatesInModUpdateFlowAsync()
// 强制刷新缓存，绕过缓存直接从API获取最新数据
IsTranslationPluginUpdateAvailable = await _translationManagerService.IsXUnityUpdateAvailableAsync(forceRefresh: true);
IsTranslationResourceUpdateAvailable = await _translationManagerService.IsTranslationUpdateAvailableAsync(forceRefresh: true);
LatestXUnityVersion = await _translationManagerService.GetLatestXUnityVersionAsync(forceRefresh: true);
```

### UI显示逻辑

**横幅提示**
- 显示条件：`IsTranslationUpdateAvailable = PluginUpdateAvailable || ResourceUpdateAvailable`
- 显示内容：根据不同更新类型显示相应文本
- 点击跳转：`GoToTranslationManagementCommand` 切换到翻译管理选项卡

**更新按钮分离**
- 更新翻译插件：仅当 `IsTranslationPluginUpdateAvailable = true` 时显示
- 更新翻译资源：仅当 `IsTranslationResourceUpdateAvailable = true` 时显示

### 更新执行流程

**更新翻译插件**

```csharp
// MainViewModel.UpdateTranslationPluginAsync()
1. 备份当前插件文件到 translation_backup/
2. 下载最新 XUnity.AutoTranslator 插件
3. 安装到 Plugins/ 目录
4. 更新 translation_install_manifest.json
5. 刷新状态并重新检查更新
```

**更新翻译资源**

```csharp
// MainViewModel.UpdateTranslationFilesAsync()
1. 调用 TranslationManagerService.UpdateTranslationFilesAsync()
2. 删除旧的翻译资源文件
3. 下载最新 ghpc-translation-* 压缩包
4. 解压到 Translation/ 目录
5. 重建翻译安装清单
6. 刷新状态并重新检查更新
```

### 缓存策略对比

| 场景 | 缓存策略 | 目的 |
|------|---------|------|
| 应用启动检查 | 使用默认缓存 | 避免频繁API请求，提升启动速度 |
| 手动更新检查 | `forceRefresh: true` | 绕过缓存获取最新数据，确保更新准确性 |
| 更新后状态检查 | 使用默认缓存 | 操作后的状态确认，无需重新请求API |

### API调用链路

```
TranslationManagerService
├── GetTranslationReleasesAsync(forceRefresh)
│   └── NetworkService.GetGitHubReleasesAsync(owner, repo, forceRefresh)
├── GetXUnityReleasesAsync(forceRefresh)
│   └── NetworkService.GetGitHubReleasesAsync(owner, repo, forceRefresh)
└── GitHub API 缓存策略
    ├── 默认：7天持久化缓存
    └── forceRefresh：跳过缓存并更新缓存
```

### 关键实现细节

**已解决的问题**
- 缓存绕过：手动检查更新时翻译插件和资源现在都能绕过缓存
- 分离检查：翻译插件和资源更新检查独立进行
- UI集成：翻译更新检查集成到Mod更新检查流程中

**技术实现**
- 接口扩展：所有更新检查方法都支持 `forceRefresh` 参数
- 条件显示：使用 `MultiBinding` 和 `BooleanAndToVisibilityConverter` 实现复杂的UI显示逻辑
- 状态管理：分别跟踪插件和资源的更新状态
- 错误处理：完整的异常捕获和用户友好的错误提示
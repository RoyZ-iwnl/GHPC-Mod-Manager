**feat: Release version 1.3.11 with release notes window, markdown rendering, and sponsor title fix / 发布1.3.11版本，新增发布日志窗口、Markdown渲染与赞助者标题修复**

- Bump version to 1.3.11 "Cellinia Texas" / 版本升级至1.3.11 "Cellinia Texas"
- Add complete `ReleaseNotesWindow.xaml` and `ReleaseNotesWindow.xaml.cs` with WebView2 markdown rendering, theme support, external link handling, and GitHub base URL / 新增完整的发布日志窗口，使用WebView2结合Markdig进行Markdown渲染，支持主题切换、外部链接处理与GitHub base URL
- Fix sponsor title suffix bug in `SponsorInfo.cs` (incorrectly using `ReleaseNotesTitle` instead of `SponsorInfoTitle`) / 修复`SponsorInfo.cs`中的赞助者标题后缀bug（错误使用`ReleaseNotesTitle`而非`SponsorInfoTitle`）
- Update `ModManagerService.cs` and related files for version bump and compatibility / 更新`ModManagerService.cs`等文件以支持版本升级与兼容性
- Add localized strings for release notes feature / 为发布日志功能新增国际化字符串
- Improve markdown sanitization, rendering safety, and external link handling in ReleaseNotesWindow / 改进ReleaseNotesWindow中的Markdown安全渲染与外部链接处理
- Update `DataModels.cs` with new ReleaseNotesWindow references / 更新`DataModels.cs`中的ReleaseNotesWindow引用
- Add `?? Views/ReleaseNotesWindow.xaml` and `?? Views/ReleaseNotesWindow.xaml.cs` to git status / 将新文件`Views/ReleaseNotesWindow.xaml`和`.cs`标记为未暂存

**Sources:** `git status` + `Views/ReleaseNotesWindow.*` review
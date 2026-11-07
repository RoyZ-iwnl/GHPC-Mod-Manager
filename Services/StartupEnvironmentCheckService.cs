using System.IO;
using System.Linq;
using System.Windows;
using GHPC_Mod_Manager.Resources;

namespace GHPC_Mod_Manager.Services
{
    /// <summary>
    /// 启动环境检查服务，检测可能导致程序崩溃的环境问题
    /// </summary>
    public static class StartupEnvironmentCheckService
    {
        // MelonLoader 的关键 DLL 文件列表（这些文件不应出现在程序根目录）
        private static readonly string[] ConflictingDllNames = new[]
        {
            "version.dll",           // MelonLoader
            "dobby.dll",             // 老版MelonLoader
            "MonoBleedingEdge.dll",
            "winhttp.dll"            // BepinEx有时候用这个逼玩意
        };

        /// <summary>
        /// 检查程序根目录是否存在冲突的 DLL 文件
        /// </summary>
        /// <returns>如果环境安全返回 true，否则显示错误并返回 false</returns>
        public static bool CheckAndWarnConflictingDlls()
        {
            try
            {
                var appDirectory = AppDomain.CurrentDomain.BaseDirectory;
                var conflictingFiles = ConflictingDllNames
                    .Select(dllName => Path.Combine(appDirectory, dllName))
                    .Where(File.Exists)
                    .Select(Path.GetFileName)
                    .ToList();

                if (conflictingFiles.Count > 0)
                {
                    var fileList = string.Join("\n  - ", conflictingFiles);
                    var message = string.Format(Strings.ConflictingDllsDetected, fileList)
                        .Replace("\\n", "\n");  // 将资源文件中的 \n 转换为实际换行符

                    MessageBox.Show(
                        message,
                        Strings.StartupEnvironmentError,
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);

                    return false;
                }

                return true;
            }
            catch
            {
                // 检查失败时允许继续启动（避免因检查逻辑本身导致无法启动）
                return true;
            }
        }

        /// <summary>
        /// 检查程序是否被安装在游戏根目录（警告但不阻止启动）
        /// </summary>
        public static void WarnIfInstalledInGameDirectory()
        {
            try
            {
                var appDirectory = AppDomain.CurrentDomain.BaseDirectory;
                var ghpcExe = Path.Combine(appDirectory, "Bin", "GHPC.exe");

                if (File.Exists(ghpcExe))
                {
                    var message = Strings.InstalledInGameDirectory.Replace("\\n", "\n");

                    MessageBox.Show(
                        message,
                        Strings.InstallLocationWarning,
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
            }
            catch
            {
                // 检查失败时静默忽略
            }
        }
    }
}

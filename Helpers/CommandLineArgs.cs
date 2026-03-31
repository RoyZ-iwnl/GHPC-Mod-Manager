using GHPC_Mod_Manager.Models;

namespace GHPC_Mod_Manager.Helpers
{
    /// <summary>
    /// 存储全局命令行参数（App.xaml.cs 启动时写入，只读）
    /// </summary>
    public static class CommandLineArgs
    {
        /// <summary>
        /// 是否带 -log 参数启动
        /// </summary>
        public static bool ShowLogWindow { get; set; }

        /// <summary>
        /// 是否启用开发模式（-dev 参数）
        /// </summary>
        public static bool DevModeEnabled { get; set; }

        /// <summary>
        /// 开发模式下覆盖的主配置 URL/路径
        /// </summary>
        public static string? DevConfigUrlOverride { get; set; }

        /// <summary>
        /// 解析命令行参数
        /// 支持格式：
        ///   -log, --log              显示日志窗口
        ///   -dev, --dev              仅启用开发模式
        ///   -dev:"path", --dev:"path" 启用开发模式并指定配置路径（冒号分隔）
        ///   -dev="path", --dev="path" 启用开发模式并指定配置路径（等号分隔）
        ///   -dev "path"              启用开发模式并指定配置路径（空格分隔）
        /// </summary>
        public static void Parse(string[] args)
        {
            // 解析 -log 参数
            ShowLogWindow = args.Contains("-log") || args.Contains("--log");

            // 解析 -dev 参数
            for (int i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                if (arg.StartsWith("-dev") || arg.StartsWith("--dev"))
                {
                    DevModeEnabled = true;

                    // 检查冒号或等号分隔的路径
                    int separatorIndex = -1;
                    if (arg.Contains(':'))
                        separatorIndex = arg.IndexOf(':');
                    else if (arg.Contains('='))
                        separatorIndex = arg.IndexOf('=');

                    if (separatorIndex > 0 && separatorIndex < arg.Length - 1)
                    {
                        // 提取冒号/等号后的路径
                        DevConfigUrlOverride = arg.Substring(separatorIndex + 1);
                    }
                    else if (arg == "-dev" || arg == "--dev")
                    {
                        // 单独的 -dev 参数，检查下一个参数是否是路径（不以 - 开头）
                        if (i + 1 < args.Length && !args[i + 1].StartsWith("-"))
                        {
                            DevConfigUrlOverride = args[i + 1];
                            i++; // 跳过路径参数
                        }
                    }
                    break;
                }
            }
        }
    }
}

using System.Globalization;
using System.Windows.Data;
using GHPC_Mod_Manager.Services;

namespace GHPC_Mod_Manager.Converters;

/// <summary>
/// Converts game translation language codes (e.g., "en", "zh") to friendly display names
/// </summary>
public class TranslationLanguageDisplayConverter : IValueConverter
{
    public static TranslationLanguageDisplayConverter Instance { get; } = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string languageCode)
            return value?.ToString() ?? "";

        // Get current UI culture from settings service if available
        try
        {
            var settingsService = App.GetService<ISettingsService>();
            var currentUICulture = settingsService?.Settings.Language ?? "en-US";
            return GetFriendlyLanguageName(languageCode, currentUICulture);
        }
        catch
        {
            // Fallback if service is not available
            return GetFriendlyLanguageName(languageCode, "en-US");
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        // Not needed for display-only conversion
        throw new NotImplementedException();
    }

    /// <summary>
    /// Convert language code to friendly display name
    /// </summary>
    private static string GetFriendlyLanguageName(string languageCode, string currentUICulture)
    {
        return languageCode.ToLowerInvariant() switch
        {
            "en" => currentUICulture == "zh-CN" ? "英语 (English)" : "English",
            "zh" => currentUICulture == "zh-CN" ? "中文 (Chinese)" : "Chinese (中文)",
            "zh-cn" => currentUICulture == "zh-CN" ? "简体中文 (Simplified Chinese)" : "Simplified Chinese (简体中文)",
            "zh-tw" => currentUICulture == "zh-CN" ? "繁体中文 (Traditional Chinese)" : "Traditional Chinese (繁體中文)",
            "ja" => currentUICulture == "zh-CN" ? "日语 (Japanese)" : "Japanese (日本語)",
            "ko" => currentUICulture == "zh-CN" ? "韩语 (Korean)" : "Korean (한국어)",
            "fr" => currentUICulture == "zh-CN" ? "法语 (French)" : "French (Français)",
            "de" => currentUICulture == "zh-CN" ? "德语 (German)" : "German (Deutsch)",
            "es" => currentUICulture == "zh-CN" ? "西班牙语 (Spanish)" : "Spanish (Español)",
            "ru" => currentUICulture == "zh-CN" ? "俄语 (Russian)" : "Russian (Русский)",
            "pt" => currentUICulture == "zh-CN" ? "葡萄牙语 (Portuguese)" : "Portuguese (Português)",
            "it" => currentUICulture == "zh-CN" ? "意大利语 (Italian)" : "Italian (Italiano)",
            _ => languageCode // Fallback to original code if not recognized
        };
    }
}

using System.Globalization;
using System.Windows.Data;

namespace GHPC_Mod_Manager.Converters;

public class LanguageDisplayConverter : IValueConverter
{
    public static LanguageDisplayConverter Instance { get; } = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value?.ToString() switch
        {
            "zh-CN" => "简体中文",
            "en-US" => "English",
            _ => value?.ToString() ?? ""
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
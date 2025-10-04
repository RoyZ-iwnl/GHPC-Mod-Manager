using System.Globalization;
using System.Windows.Data;
using GHPC_Mod_Manager.Models;
using GHPC_Mod_Manager.Resources;

namespace GHPC_Mod_Manager.Converters;

public class UpdateChannelDisplayConverter : IValueConverter
{
    public static UpdateChannelDisplayConverter Instance { get; } = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is UpdateChannel channel)
        {
            return channel switch
            {
                UpdateChannel.Stable => Strings.UpdateChannelStable,
                UpdateChannel.Beta => Strings.UpdateChannelBeta,
                _ => value.ToString()
            };
        }
        return value?.ToString() ?? "";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

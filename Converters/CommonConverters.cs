using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using GHPC_Mod_Manager.Models;
using GHPC_Mod_Manager.Resources;

namespace GHPC_Mod_Manager.Converters;

public class BooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool boolValue)
            return boolValue ? Visibility.Visible : Visibility.Collapsed;
        // 支持int（如Count属性）：非零为Visible
        if (value is int intValue)
            return intValue > 0 ? Visibility.Visible : Visibility.Collapsed;
        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is Visibility visibility)
        {
            return visibility == Visibility.Visible;
        }
        return false;
    }
}

public class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool boolValue)
            return boolValue ? Visibility.Collapsed : Visibility.Visible;
        // 支持int（如Count属性）：零为Visible
        if (value is int intValue)
            return intValue > 0 ? Visibility.Collapsed : Visibility.Visible;
        return Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is Visibility visibility)
        {
            return visibility == Visibility.Collapsed;
        }
        return true;
    }
}

public class InverseBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool boolValue)
        {
            return !boolValue;
        }
        return true;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool boolValue)
        {
            return !boolValue;
        }
        return false;
    }
}

public class StepVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is int currentStep && parameter is string stepString && int.TryParse(stepString, out int targetStep))
        {
            return currentStep == targetStep ? Visibility.Visible : Visibility.Collapsed;
        }
        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

public class TranslationPluginVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool isTranslationPlugin)
        {
            return isTranslationPlugin ? Visibility.Collapsed : Visibility.Visible;
        }
        return Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

// MOD启用状态转换器 - 只有安装的MOD才能启用复选框
public class ModEnableStateConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool isInstalled)
        {
            return isInstalled;
        }
        return false;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

// 配置项类型转换器 - 用于控制是否显示配置项控件还是注释
public class ConfigurationItemTypeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool isStandaloneComment)
        {
            var expectStandalone = parameter?.ToString() == "Standalone";
            var shouldShow = expectStandalone ? isStandaloneComment : !isStandaloneComment;
            return shouldShow ? Visibility.Visible : Visibility.Collapsed;
        }
        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

// 主题显示转换器
public class ThemeDisplayConverter : IValueConverter
{
    public static readonly ThemeDisplayConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is AppTheme theme)
        {
            return theme switch
            {
                AppTheme.Light => Strings.LightTheme,
                AppTheme.Dark => Strings.DarkTheme,
                AppTheme.Endfield => Strings.EndfieldTheme,
                _ => value.ToString() ?? string.Empty
            };
        }
        return value?.ToString() ?? string.Empty;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

// 字符串非空转可见性转换器
public class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return !string.IsNullOrEmpty(value?.ToString()) ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

// 字符串非空转反向可见性转换器
public class StringToInverseVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return string.IsNullOrEmpty(value?.ToString()) ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

// 依赖状态转可见性转换器（Missing时显示警告图标）
public class DependencyStatusToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is DependencyStatus status)
            return status == DependencyStatus.Missing ? Visibility.Visible : Visibility.Collapsed;
        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

// 多布尔值AND转可见性转换器
public class BooleanAndToVisibilityConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values == null || values.Length == 0)
            return Visibility.Collapsed;
        foreach (var value in values)
        {
            if (value is bool boolValue && !boolValue)
                return Visibility.Collapsed;
        }
        return Visibility.Visible;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

public class ResponsiveCardWidthConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length == 0 || values[0] is not double viewportWidth || double.IsNaN(viewportWidth) || viewportWidth <= 0)
            return 520d;

        var availableWidth = Math.Max(360d, viewportWidth - 12d);
        const double columnGap = 12d;
        const double singleColumnThreshold = 960d;
        const double minSingleColumnWidth = 360d;
        const double minTwoColumnWidth = 420d;

        if (availableWidth < singleColumnThreshold)
            return Math.Max(minSingleColumnWidth, availableWidth);

        var twoColumnWidth = Math.Floor((availableWidth - columnGap) / 2d);
        return Math.Max(minTwoColumnWidth, twoColumnWidth);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

public class ResponsiveColumnCountConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not double viewportWidth || double.IsNaN(viewportWidth) || viewportWidth <= 0)
            return 1;

        return viewportWidth >= 880d ? 2 : 1;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

public class CollectionSummaryConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var noneText = culture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase) ? "无" : "None";

        if (value is not IEnumerable enumerable)
            return noneText;

        var maxItems = 2;
        if (parameter is string parameterText && int.TryParse(parameterText, out var parsedMax) && parsedMax > 0)
            maxItems = parsedMax;

        var items = new List<string>();
        foreach (var item in enumerable)
        {
            var text = item?.ToString()?.Trim();
            if (string.IsNullOrEmpty(text))
                continue;

            items.Add(text);
        }

        if (items.Count == 0)
            return noneText;

        if (items.Count <= maxItems)
            return string.Join(", ", items);

        var visibleItems = items.Take(maxItems);
        return $"{string.Join(", ", visibleItems)} +{items.Count - maxItems}";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// MultiValueConverter：[0] = 单个版本号字符串, [1] = CurrentGameVersion(string?)
/// 匹配返回 SuccessBrush，有版本但不匹配返回 WarningBrush，无版本返回 Transparent
/// </summary>
public class GameVersionMatchBrushConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0] is not string version)
            return System.Windows.Media.Brushes.Transparent;

        // 没有检测到游戏版本，不着色
        if (values[1] is not string currentVersion || string.IsNullOrEmpty(currentVersion))
            return System.Windows.Media.Brushes.Transparent;

        return string.Equals(version.Trim(), currentVersion.Trim(), StringComparison.OrdinalIgnoreCase)
            ? (object)(System.Windows.Application.Current.TryFindResource("SuccessBrush") ?? System.Windows.Media.Brushes.Green)
            : (System.Windows.Application.Current.TryFindResource("WarningBrush") ?? System.Windows.Media.Brushes.Orange);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>
/// MultiValueConverter：[0] = 单个版本号字符串, [1] = CurrentGameVersion(string?)
/// 匹配返回 "match"，有版本不匹配返回 "mismatch"，无版本返回 "none"，用于 DataTrigger
/// </summary>
public class GameVersionMatchBoolConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0] is not string version)
            return "none";

        if (values[1] is not string currentVersion || string.IsNullOrEmpty(currentVersion))
            return "none";

        return string.Equals(version.Trim(), currentVersion.Trim(), StringComparison.OrdinalIgnoreCase)
            ? "match" : "mismatch";
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

// 中文语言转可见性转换器（仅中文用户显示代理设置）
public class ChineseLanguageToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string language)
            return language == "zh-CN" ? Visibility.Visible : Visibility.Collapsed;
        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

// Null转可见性转换器（null时隐藏）
public class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value != null ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

// Null转反向可见性转换器（null时显示）
public class NullToVisibilityInverseConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value == null ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

// 任务完成状态颜色转换器
public class BoolToCompletionBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var isCompleted = value is bool b && b;
        return isCompleted
            ? Application.Current.TryFindResource("AccentBrush") ?? System.Windows.Media.Brushes.Green
            : Application.Current.TryFindResource("SecondaryTextBrush") ?? System.Windows.Media.Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

public class BoolToTextDecorationsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is bool b && !b ? TextDecorations.Strikethrough : null;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}


// 阵营文字颜色转换器
public class FactionForegroundConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var faction = value as string;
        return faction switch
        {
            "Blue" => Application.Current.TryFindResource("FactionBlueBrush") ?? System.Windows.Media.Brushes.DodgerBlue,
            "Red" => Application.Current.TryFindResource("FactionRedBrush") ?? System.Windows.Media.Brushes.OrangeRed,
            "Neutral" => Application.Current.TryFindResource("FactionNeutralBrush") ?? System.Windows.Media.Brushes.Gray,
            _ => Application.Current.TryFindResource("PrimaryTextBrush") ?? System.Windows.Media.Brushes.White
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

// 阵营背景颜色转换器
public class FactionBackgroundConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var faction = value as string;
        return faction switch
        {
            "Blue" => Application.Current.TryFindResource("FactionBlueContainerBrush") ?? System.Windows.Media.Brushes.LightBlue,
            "Red" => Application.Current.TryFindResource("FactionRedContainerBrush") ?? System.Windows.Media.Brushes.MistyRose,
            "Neutral" => Application.Current.TryFindResource("FactionNeutralContainerBrush") ?? System.Windows.Media.Brushes.LightGray,
            _ => Application.Current.TryFindResource("SurfaceContainerBrush") ?? System.Windows.Media.Brushes.White
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

public class FactionBadgeForegroundConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var faction = values.Length > 0 ? values[0] as string : null;
        var isCompleted = values.Length > 1 && values[1] is bool b && b;
        var isCompletedBrush = faction switch
        {
            "Blue" => Application.Current.TryFindResource("FactionBlueBrush") ?? System.Windows.Media.Brushes.DodgerBlue,
            "Red" => Application.Current.TryFindResource("FactionRedBrush") ?? System.Windows.Media.Brushes.OrangeRed,
            "Neutral" => Application.Current.TryFindResource("FactionNeutralBrush") ?? System.Windows.Media.Brushes.Gray,
            _ => Application.Current.TryFindResource("PrimaryTextBrush") ?? System.Windows.Media.Brushes.White
        };

        return isCompleted
            ? isCompletedBrush
            : Application.Current.TryFindResource("FactionIncompleteBrush") ?? System.Windows.Media.Brushes.Gray;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

public class FactionBadgeBackgroundConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var faction = values.Length > 0 ? values[0] as string : null;
        var isCompleted = values.Length > 1 && values[1] is bool b && b;
        if (!isCompleted)
        {
            return Application.Current.TryFindResource("FactionIncompleteContainerBrush") ?? System.Windows.Media.Brushes.Transparent;
        }

        return faction switch
        {
            "Blue" => Application.Current.TryFindResource("FactionBlueContainerBrush") ?? System.Windows.Media.Brushes.LightBlue,
            "Red" => Application.Current.TryFindResource("FactionRedContainerBrush") ?? System.Windows.Media.Brushes.MistyRose,
            "Neutral" => Application.Current.TryFindResource("FactionNeutralContainerBrush") ?? System.Windows.Media.Brushes.LightGoldenrodYellow,
            _ => Application.Current.TryFindResource("SurfaceContainerBrush") ?? System.Windows.Media.Brushes.White
        };
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

public class FactionBadgeBorderConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var faction = values.Length > 0 ? values[0] as string : null;
        var isCompleted = values.Length > 1 && values[1] is bool b && b;
        if (!isCompleted)
        {
            return Application.Current.TryFindResource("FactionIncompleteBorderBrush") ?? System.Windows.Media.Brushes.Gray;
        }

        return faction switch
        {
            "Blue" => Application.Current.TryFindResource("FactionBlueBrush") ?? System.Windows.Media.Brushes.DodgerBlue,
            "Red" => Application.Current.TryFindResource("FactionRedBrush") ?? System.Windows.Media.Brushes.OrangeRed,
            "Neutral" => Application.Current.TryFindResource("FactionNeutralBrush") ?? System.Windows.Media.Brushes.Goldenrod,
            _ => Application.Current.TryFindResource("PrimaryTextBrush") ?? System.Windows.Media.Brushes.White
        };
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

// 是否有修改转提示文字转换器
public class BoolToSaveHintConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool hasChanges && hasChanges)
        {
            return "有未保存的更改";
        }
        return "";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

// 终末地主题专用：标题文本括号转换器（支持主题切换）
public class BracketTextConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0] == null)
            return string.Empty;

        var text = values[0].ToString();
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        // values[1] 是 ThemeTracker.CurrentTheme
        if (values[1] is Models.AppTheme theme && theme == Models.AppTheme.Endfield)
        {
            return $"[ {text} ]";
        }

        return text;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

// 启动检查步骤状态转Visibility转换器
public class LaunchCheckStatusToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is ViewModels.LaunchCheckStepStatus status && parameter is string targetStatus)
        {
            if (Enum.TryParse<ViewModels.LaunchCheckStepStatus>(targetStatus, out var target))
            {
                return status == target ? Visibility.Visible : Visibility.Collapsed;
            }
        }
        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

// 启动检查步骤状态转颜色转换器
public class LaunchCheckStatusToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is ViewModels.LaunchCheckStepStatus status)
        {
            return status switch
            {
                ViewModels.LaunchCheckStepStatus.Passed => Application.Current.TryFindResource("SuccessBrush"),
                ViewModels.LaunchCheckStepStatus.Warning => Application.Current.TryFindResource("WarningBrush"),
                ViewModels.LaunchCheckStepStatus.Failed => Application.Current.TryFindResource("ErrorBrush"),
                ViewModels.LaunchCheckStepStatus.InProgress => Application.Current.TryFindResource("AccentBrush"),
                ViewModels.LaunchCheckStepStatus.Skipped => Application.Current.TryFindResource("DisabledBrush"),
                _ => Application.Current.TryFindResource("BorderBrush")
            };
        }
        return Application.Current.TryFindResource("BorderBrush");
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

// 启动检查进度条宽度转换器
public class ProgressToWidthConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 3) return 0.0;

        if (values[0] is int currentStep &&
            values[1] is int totalSteps &&
            values[2] is double containerWidth &&
            totalSteps > 0)
        {
            double stepWidth = (containerWidth - 20) / totalSteps;
            return stepWidth * (currentStep + 1);
        }
        return 0.0;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}


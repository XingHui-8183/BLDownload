using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace BLDownload;

/// <summary>bool 取反（用于按钮 IsEnabled）</summary>
public class InvertBoolConverter : IValueConverter
{
    public object Convert(object? v, Type t, object? p, CultureInfo c) => v is not true;
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => v is not true;
}

/// <summary>bool → Visibility</summary>
public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? v, Type t, object? p, CultureInfo c) => v is true ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => v is Visibility.Visible;
}

/// <summary>bool → Visibility（true 时隐藏）</summary>
public class InvertBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? v, Type t, object? p, CultureInfo c) => v is true ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => v is not Visibility.Visible;
}

/// <summary>bool → 解析按钮文字（忙碌时显示"解析中…"）</summary>
public class BusyTextConverter : IValueConverter
{
    public static readonly BusyTextConverter Instance = new();
    public object Convert(object? v, Type t, object? p, CultureInfo c) => v is true ? "解析中…" : "解析链接";
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>XAML 静态实例入口</summary>
public static class ObjectToBoolConverter
{
    public static readonly IValueConverter Invert = new InvertBoolConverter();
    public static readonly IValueConverter ToVisible = new BoolToVisibilityConverter();
    public static readonly IValueConverter ToCollapsed = new InvertBoolToVisibilityConverter();
}

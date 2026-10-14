using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using OpenUsage.Core.Models;
using Application = System.Windows.Application;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace OpenUsage.App.Services;

public static class ThemeService
{
    public static bool IsDark { get; private set; }

    public static void Apply(AppTheme theme)
    {
        IsDark = theme switch
        {
            AppTheme.Dark => true,
            AppTheme.Light => false,
            _ => SystemDark()
        };
        var app = Application.Current;
        if (app is null) return;
        app.Resources["TextBrush"] = Brush(IsDark ? "#F5F5F7" : "#1C1C1E");
        app.Resources["MutedBrush"] = Brush(IsDark ? "#8E8E93" : "#6C6C70");
        app.Resources["BgBrush"] = Brush(IsDark ? "#1C1C1E" : "#F2F2F7");
        app.Resources["CardBrush"] = Brush(IsDark ? "#2C2C2E" : "#FFFFFF");
        app.Resources["FieldBrush"] = Brush(IsDark ? "#3A3A3C" : "#FFFFFF");
        app.Resources["PopupBrush"] = Brush(IsDark ? "#2C2C2E" : "#FFFFFF");
        app.Resources["TrackBrush"] = Brush(IsDark ? "#48484A" : "#E5E5EA");
        app.Resources["HoverBrush"] = Brush(IsDark ? "#3A3A3C" : "#E5E5EA");
        app.Resources["SeparatorBrush"] = Brush(IsDark ? "#48484A" : "#D1D1D6");
        app.Resources["WindowBorderBrush"] = Brush(IsDark ? "#3A3A3C" : "#D1D1D6");
        app.Resources["Accent"] = Brush("#0A84FF");
    }

    public static SolidColorBrush Meter(MeterSeverity? severity) => severity switch
    {
        MeterSeverity.Warning => Brush("#FF9F0A"),
        MeterSeverity.Critical => Brush("#FF453A"),
        MeterSeverity.Normal => Brush("#0A84FF"),
        _ => Brush("#8E8E93")
    };

    private static bool SystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var value = key?.GetValue("AppsUseLightTheme");
            return value is int i && i == 0;
        }
        catch
        {
            return true;
        }
    }

    private static SolidColorBrush Brush(string hex)
    {
        var color = (Color)ColorConverter.ConvertFromString(hex)!;
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}

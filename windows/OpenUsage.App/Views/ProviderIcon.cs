using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SharpVectors.Converters;
using OpenUsage.App.Services;
using OpenUsage.Core.Providers;
using Brush = System.Windows.Media.Brush;

namespace OpenUsage.App.Views;

public static class ProviderIcon
{
    public static UIElement Create(string iconId, double size = 16)
    {
        try
        {
            var uri = new Uri($"pack://application:,,,/OpenUsage;component/Assets/{iconId}.svg");
            var viewbox = new SvgViewbox
            {
                Width = size,
                Height = size,
                Stretch = Stretch.Uniform,
                Source = uri
            };
            return viewbox;
        }
        catch
        {
            var hex = BrandColors.Hex(iconId, ThemeService.IsDark);
            return new Border
            {
                Width = size,
                Height = size,
                CornerRadius = new CornerRadius(size / 4),
                Background = (Brush)new BrushConverter().ConvertFrom(hex)!
            };
        }
    }
}

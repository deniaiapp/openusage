using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using OpenUsage.App.Views;
using OpenUsage.Core.Support;
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace OpenUsage.App;

/// <summary>Onboarding pieces of the dashboard: the one-time Customize hint, the empty state, and the card caret.</summary>
public partial class DashboardWindow
{
    public const string ReportIssueUrl = "https://github.com/robinebers/openusage/issues/new/choose";

    internal static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Error("ui", $"could not open {url}", ex);
            System.Windows.MessageBox.Show($"OpenUsage couldn't open your browser.\n\n{url}", "OpenUsage",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private UIElement HintCard()
    {
        var card = Card();
        var grid = new Grid { Margin = new Thickness(12, 10, 12, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new TextBlock
        {
            Text = "OpenUsage turned on the providers you connected. Turn more on or off in Customize.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12
        };
        var close = new Button { Content = "✕", Style = (Style)FindResource("GhostButton") };
        close.Click += (_, _) =>
        {
            _container.Settings.CustomizeHintDismissed = true;
            _container.Settings.Save();
            Render();
        };
        Grid.SetColumn(close, 1);
        grid.Children.Add(text);
        grid.Children.Add(close);
        card.Child = grid;
        card.Margin = new Thickness(0, 0, 0, 10);
        return card;
    }

    /// <summary>Shown when no provider is on (for example after Skip on the welcome screen).</summary>
    private UIElement EmptyDashboard()
    {
        var stack = new StackPanel { Margin = new Thickness(16, 22, 16, 26), HorizontalAlignment = HorizontalAlignment.Center };
        var icon = ProviderIcon.Create("openusage", 56);
        icon.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 0, 14));
        icon.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        stack.Children.Add(icon);
        stack.Children.Add(new TextBlock
        {
            Text = "Make It Yours",
            FontSize = 19,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 7)
        });
        stack.Children.Add(new TextBlock
        {
            Text = "Bring your AI usage, limits, and spend together. Choose what goes on your dashboard.",
            Foreground = (Brush)FindResource("MutedBrush"),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            MaxWidth = 230,
            Margin = new Thickness(0, 0, 0, 20)
        });
        var choose = new Button
        {
            Content = "+  Choose Providers",
            Style = (Style)FindResource("PrimaryButton"),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        choose.Click += (_, _) => ShowScreen(DashboardScreen.Customize);
        stack.Children.Add(choose);
        return stack;
    }

    private UIElement HiddenMetricsDashboard()
    {
        var stack = new StackPanel { Margin = new Thickness(16, 22, 16, 26) };
        stack.Children.Add(new TextBlock
        {
            Text = "Every metric is hidden. Turn metrics back on in Customize.",
            Style = (Style)FindResource("MutedText"),
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 0, 0, 12)
        });
        var customize = new Button
        {
            Content = "Customize",
            Style = (Style)FindResource("PrimaryButton"),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        customize.Click += (_, _) => ShowScreen(DashboardScreen.Customize);
        stack.Children.Add(customize);
        return stack;
    }

    /// <summary>The centered caret at the bottom of a provider card that shows or hides its On Demand rows and links.</summary>
    private UIElement CardCaret(string providerId, bool open)
    {
        var caret = new Button
        {
            Content = open ? "⌃" : "⌄",
            Style = (Style)FindResource("GhostButton"),
            Foreground = (Brush)FindResource("MutedBrush"),
            FontSize = 12,
            Padding = new Thickness(0, 2, 0, 4),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(6, 0, 6, 4)
        };
        AutomationProperties.SetName(caret, open ? "Show Less" : "Show More");
        caret.Click += (_, _) =>
        {
            _container.Layout.ToggleCard(providerId);
            Render();
        };
        return caret;
    }
}

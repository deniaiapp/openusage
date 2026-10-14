using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using OpenUsage.App.Services;
using OpenUsage.Core.Models;
using OpenUsage.Core.Stores;
using OpenUsage.Core.Support;
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using Orientation = System.Windows.Controls.Orientation;

namespace OpenUsage.App.Views;

/// <summary>
/// The first-launch welcome screen: lists the providers signed in on this PC, preselects them, and
/// turns on the ones the user connects. Closing it before finishing quits OpenUsage, so the welcome
/// screen returns on the next launch.
/// </summary>
public partial class WelcomeWindow : Window
{
    private readonly FirstLaunchSetup _setup;
    private readonly CancellationTokenSource _cts = new();
    private bool _finishing;

    public WelcomeWindow(FirstLaunchSetup setup)
    {
        _setup = setup;
        InitializeComponent();
        AppIconHost.Content = ProviderIcon.Create("openusage", 64);
        _setup.Changed += () => Dispatcher.Invoke(Render);
        Loaded += async (_, _) =>
        {
            Render();
            try { await _setup.DetectAsync(_cts.Token); }
            catch (OperationCanceledException) { }
        };
    }

    /// <summary>Raised once the user leaves the welcome screen through Skip or Open Dashboard.</summary>
    public event Action? Finished;

    private void Render()
    {
        var detecting = _setup.IsDetecting;
        DetectingLabel.Visibility = detecting ? Visibility.Visible : Visibility.Collapsed;
        ShowAllButton.Visibility = detecting ? Visibility.Collapsed : Visibility.Visible;
        ShowAllButton.Content = _setup.ShowAll ? "Show Detected" : "Show All";

        SkipButton.IsEnabled = !detecting && !_setup.IsConnecting;
        RetryButton.Visibility = _setup.HasAttemptedConnection && _setup.HasFailures ? Visibility.Visible : Visibility.Collapsed;
        RetryButton.IsEnabled = !_setup.IsConnecting;
        MainButton.Content = _setup.IsConnecting ? "Checking…" : _setup.HasAttemptedConnection ? "Open Dashboard" : "Connect Selected";
        MainButton.IsEnabled = !detecting && !_setup.IsConnecting && (_setup.HasAttemptedConnection || _setup.SelectedIds.Count > 0);

        ListHost.Children.Clear();
        if (detecting) return;
        var visible = _setup.VisibleChoices;
        if (visible.Count == 0)
        {
            ListHost.Children.Add(new TextBlock
            {
                Text = "No sign-ins found yet. Choose Show All to add a provider, or set up later.",
                Foreground = (Brush)FindResource("MutedBrush"),
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(24)
            });
            return;
        }
        for (var i = 0; i < visible.Count; i++)
        {
            ListHost.Children.Add(Row(visible[i]));
            if (i < visible.Count - 1)
                ListHost.Children.Add(new Border { Height = 1, Background = (Brush)FindResource("SeparatorBrush"), Margin = new Thickness(46, 0, 0, 0) });
        }
    }

    private UIElement Row(FirstLaunchChoice choice)
    {
        var locked = _setup.IsConnecting || _setup.HasAttemptedConnection;
        var grid = new Grid { Margin = new Thickness(14, 10, 14, 10), IsEnabled = !locked };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var box = new CheckBox
        {
            IsChecked = _setup.SelectedIds.Contains(choice.Id),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0)
        };
        AutomationProperties.SetName(box, choice.Provider.DisplayName);
        box.Checked += (_, _) => _setup.SetSelected(choice.Id, true);
        box.Unchecked += (_, _) => _setup.SetSelected(choice.Id, false);
        grid.Children.Add(box);

        var icon = ProviderIcon.Create(choice.Provider.IconId, 22);
        icon.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 10, 0));
        icon.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        Grid.SetColumn(icon, 1);
        grid.Children.Add(icon);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = choice.Provider.DisplayName, FontWeight = FontWeights.SemiBold, FontSize = 13 });
        var failed = choice.Connection == ConnectionState.Failed;
        text.Children.Add(new TextBlock
        {
            Text = Status(choice),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = failed ? ThemeService.Meter(MeterSeverity.Critical) : (Brush)FindResource("MutedBrush"),
            Margin = new Thickness(0, 2, 0, 0)
        });
        Grid.SetColumn(text, 2);
        grid.Children.Add(text);

        var mark = choice.Connection switch
        {
            ConnectionState.Checking => new TextBlock { Text = "…", Foreground = (Brush)FindResource("MutedBrush") },
            ConnectionState.Connected => new TextBlock { Text = "✓", Foreground = (Brush)new BrushConverter().ConvertFrom("#34C759")! },
            ConnectionState.Failed => new TextBlock { Text = "✕", Foreground = ThemeService.Meter(MeterSeverity.Critical) },
            _ => null
        };
        if (mark is not null)
        {
            mark.FontSize = 14;
            mark.FontWeight = FontWeights.SemiBold;
            mark.VerticalAlignment = VerticalAlignment.Center;
            mark.Margin = new Thickness(8, 0, 0, 0);
            if (failed) AutomationProperties.SetName(mark, "Needs Attention");
            Grid.SetColumn(mark, 3);
            grid.Children.Add(mark);
        }
        return grid;
    }

    private static string Status(FirstLaunchChoice choice) => choice.Connection switch
    {
        ConnectionState.Checking => "Checking…",
        ConnectionState.Connected => "Enabled",
        ConnectionState.Failed => choice.FailureMessage ?? "Couldn't connect.",
        _ => choice.Detected ? "Sign-In Detected" : "Set Up or Sign In First"
    };

    private void OnShowAll(object sender, RoutedEventArgs e) => _setup.ToggleShowAll();

    private void OnPrimary(object sender, RoutedEventArgs e)
    {
        if (_setup.HasAttemptedConnection) Finish();
        else Connect();
    }

    private void OnRetry(object sender, RoutedEventArgs e) => Connect();

    private void OnSkip(object sender, RoutedEventArgs e) => Finish();

    private async void Connect()
    {
        try
        {
            await _setup.ConnectSelectedAsync(_cts.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AppLog.Error("welcome", "connect failed", ex);
            System.Windows.MessageBox.Show(this, $"OpenUsage couldn't connect your providers.\n\n{ex.Message}",
                "OpenUsage", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Finish()
    {
        _finishing = true;
        _cts.Cancel();
        Close();
        Finished?.Invoke();
    }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_finishing) return;
        // Leaving without finishing quits; the welcome screen returns on the next launch.
        _cts.Cancel();
        AppLog.Info("welcome", "closed before finishing; quitting");
        Dispatcher.BeginInvoke(() => System.Windows.Application.Current.Shutdown());
    }
}

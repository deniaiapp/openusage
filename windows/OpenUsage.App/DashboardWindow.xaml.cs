using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using OpenUsage.App.Services;
using OpenUsage.App.Views;
using OpenUsage.Core;
using OpenUsage.Core.Models;
using OpenUsage.Core.Providers;
using OpenUsage.Core.Providers.Devin;
using OpenUsage.Core.Services;
using OpenUsage.Core.Support;
using Application = System.Windows.Application;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using Rectangle = System.Windows.Shapes.Rectangle;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using Cursors = System.Windows.Input.Cursors;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MessageBox = System.Windows.MessageBox;
using Orientation = System.Windows.Controls.Orientation;
using Panel = System.Windows.Controls.Panel;
using TextBox = System.Windows.Controls.TextBox;

namespace OpenUsage.App;

public enum DashboardScreen { Dashboard, Customize, CustomizeDetail, Settings }

public partial class DashboardWindow : Window
{
    private readonly AppContainer _container;
    private readonly DispatcherTimer _clock;
    private DashboardScreen _screen = DashboardScreen.Dashboard;
    private string? _detailProviderId;
    private ApiKeyAccountsView? _accountEditor;
    private DevinSignInView? _devinSignIn;
    private CodexLoginView? _codexLoginEditor;
    private CopilotLoginView? _copilotLoginEditor;
    private CursorLoginView? _cursorLoginEditor;
    public event Action<string?>? HotkeyChanged;
    private DeviceLoginView? _deviceLoginEditor;

    public DashboardWindow(AppContainer container)
    {
        _container = container;
        InitializeComponent();
        VersionLabel.Text = $"OpenUsage {AppContainer.Version}";
        _container.Data.Changed += () => Dispatcher.Invoke(() => Render(preserveEditor: true));
        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _clock.Tick += (_, _) => Render(preserveEditor: true);
        _clock.Start();
        Loaded += (_, _) => Render();
        PreviewKeyDown += OnKey;
    }

    public void ToggleFromTray()
    {
        if (!IsVisible && _deviceLoginEditor?.ResumeIfPending() == true)
        {
            ShowAnchored();
            Activate();
            return;
        }
        if (IsVisible)
        {
            Hide();
            return;
        }
        if (_codexLoginEditor?.ResumeIfPending() == true || _copilotLoginEditor?.ResumeIfPending() == true || _cursorLoginEditor?.ResumeIfPending() == true)
            Render(preserveEditor: true);
        else if (_devinSignIn?.IsPending != true)
        {
            _screen = DashboardScreen.Dashboard;
            Render();
        }
        ShowAnchored();
        Activate();
    }

    public void ShowScreen(DashboardScreen screen)
    {
        _screen = screen;
        Render();
        if (!IsVisible) ShowAnchored();
        Activate();
    }

    private const double TrayGap = 12;
    // The panel's 8px transparent shadow margin already separates it from the taskbar.
    private const double TaskbarGap = 0;

    private void ShowAnchored()
    {
        PositionNearTray();
        Show();
    }

    /// <summary>
    /// Places the window in the taskbar's corner at its full height. The window itself never resizes or
    /// moves while open: the panel hugs the taskbar edge inside it and the transparent rest clicks
    /// through, so switching screens redraws in one frame instead of a resize-then-move flash.
    /// </summary>
    private void PositionNearTray()
    {
        var work = SystemParameters.WorkArea;
        Height = Math.Min(720, work.Height - TaskbarGap * 2);
        // The work area is inset on the taskbar's side; a top or left taskbar moves the panel there.
        var taskbarOnLeft = work.Left > 0;
        var taskbarOnTop = work.Top > 0;
        Left = taskbarOnLeft ? work.Left + TrayGap : work.Right - Width - TrayGap;
        Top = taskbarOnTop ? work.Top + TaskbarGap : work.Bottom - Height - TaskbarGap;
        RootBorder.VerticalAlignment = taskbarOnTop ? VerticalAlignment.Top : VerticalAlignment.Bottom;
    }

    private void OnDeactivated(object sender, EventArgs e)
    {
        if (IsVisible) Hide();
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (_screen != DashboardScreen.Dashboard) OnBack(sender, e);
            else Hide();
        }
        if (e.Key == Key.R && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            _ = _container.RefreshNowAsync();
    }

    private void OnBack(object sender, RoutedEventArgs e)
    {
        _screen = _screen == DashboardScreen.CustomizeDetail ? DashboardScreen.Customize : DashboardScreen.Dashboard;
        Render();
    }

    private async void OnRefreshClick(object sender, MouseButtonEventArgs e) =>
        await _container.RefreshNowAsync();

    private void OnOptions(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        menu.Items.Add(Item("Customize", () => ShowScreen(DashboardScreen.Customize)));
        menu.Items.Add(Item("Settings", () => ShowScreen(DashboardScreen.Settings)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Refresh", () => _ = _container.RefreshNowAsync()));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Report an Issue…", () => OpenUrl(ReportIssueUrl)));
        menu.Items.Add(Item("Quit OpenUsage", () => Application.Current.Shutdown()));
        menu.PlacementTarget = OptionsButton;
        menu.Placement = PlacementMode.Top;
        menu.IsOpen = true;
    }

    private static MenuItem Item(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    private void Render(bool preserveEditor = false)
    {
        if (preserveEditor && _deviceLoginEditor is not null) return;
        _deviceLoginEditor = null;
        var remaining = _container.Data.NextRefresh - DateTimeOffset.Now;
        RefreshLabel.Text = _container.Data.IsRefreshing
            ? "Updating…"
            : remaining <= TimeSpan.Zero
                ? "Next update now — click to refresh"
                : $"Next update in {Formatters.CompactDuration(remaining) ?? "soon"} — click to refresh";

        TopBar.Visibility = _screen == DashboardScreen.Dashboard ? Visibility.Collapsed : Visibility.Visible;
        DashboardTabs.Visibility = _screen == DashboardScreen.Dashboard && _container.Layout.VisibleProviders.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        TopTitle.Text = _screen switch
        {
            DashboardScreen.Customize => "Customize",
            DashboardScreen.CustomizeDetail => _container.Layout.Providers.FirstOrDefault(p => p.Provider.Id == _detailProviderId)?.Provider.DisplayName ?? "Provider",
            DashboardScreen.Settings => "Settings",
            _ => ""
        };

        if (preserveEditor && (_accountEditor is not null || _devinSignIn is not null || _codexLoginEditor is not null || _copilotLoginEditor is not null || _cursorLoginEditor is not null)) return;
        _accountEditor = null;
        _devinSignIn?.Cancel();
        _devinSignIn = null;
        _codexLoginEditor = null;
        _copilotLoginEditor = null;
        _cursorLoginEditor = null;
        ContentHost.Children.Clear();
        switch (_screen)
        {
            case DashboardScreen.Dashboard:
                RenderDashboard();
                break;
            case DashboardScreen.Customize:
                RenderCustomize();
                break;
            case DashboardScreen.CustomizeDetail:
                RenderCustomizeDetail();
                break;
            case DashboardScreen.Settings:
                RenderSettings();
                break;
        }
    }

    private void RenderDashboard()
    {
        if (!_container.Settings.CustomizeHintDismissed)
        {
            ContentHost.Children.Add(HintCard());
        }

        var providers = _container.Layout.VisibleProviders;
        if (providers.Count == 0)
        {
            ContentHost.Children.Add(EmptyDashboard());
            return;
        }

        if (PriorityTab.IsChecked == true)
        {
            RenderPriorityBoard();
            return;
        }

        foreach (var runtime in providers)
            ContentHost.Children.Add(ProviderCard(runtime));
    }

    private UIElement ProviderCard(IProviderRuntime runtime)
    {
        var provider = runtime.Provider;
        var snapshot = _container.Data.Snapshot(provider.Id);
        var open = _container.Layout.IsCardOpen(provider.Id);
        var metrics = _container.Data.ResolveCardRows(_container.Layout.VisibleMetrics(provider.Id, includeOnDemand: open));
        var links = provider.VisibleLinks;
        var hasExpandedContent = links.Count > 0 || (open
            ? metrics.Any(row => _container.Layout.IsOnDemand(row.Descriptor.Id))
            : _container.Data.ResolveCardRows(_container.Layout.OnDemandMetrics(provider.Id)).Count > 0);
        var card = Card();
        var stack = new StackPanel();

        var header = new Grid { Margin = new Thickness(12, 10, 12, 4) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition());
        var icon = ProviderIcon.Create(provider.IconId, 16);
        icon.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 1, 8, 0));
        var title = new StackPanel { Orientation = Orientation.Horizontal };
        title.Children.Add(new TextBlock { Text = provider.DisplayName, FontWeight = FontWeights.SemiBold, FontSize = 13 });
        if (!string.IsNullOrEmpty(snapshot?.Plan))
        {
            title.Children.Add(new TextBlock
            {
                Text = snapshot!.Plan,
                Style = (Style)FindResource("MutedText"),
                Margin = new Thickness(8, 2, 0, 0)
            });
        }
        var error = _container.Data.ProviderError(provider.Id);
        var notice = error ?? snapshot?.Warning;
        if (!string.IsNullOrEmpty(notice))
        {
            var warn = new TextBlock
            {
                Text = "⚠",
                Foreground = ThemeService.Meter(error is null ? MeterSeverity.Warning : MeterSeverity.Critical),
                Margin = new Thickness(6, 0, 0, 0),
                ToolTip = notice
            };
            title.Children.Add(warn);
        }
        var headerButton = new Button { Style = (Style)FindResource("GhostButton"), Padding = new Thickness(0, 4, 0, 4) };
        headerButton.Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { icon, title } };
        headerButton.Click += (_, _) =>
        {
            _detailProviderId = provider.Id;
            ShowScreen(DashboardScreen.CustomizeDetail);
        };
        Grid.SetColumn(headerButton, 0);
        Grid.SetColumnSpan(headerButton, 2);
        header.Children.Add(headerButton);

        stack.Children.Add(header);

        foreach (var (descriptor, data) in metrics)
            stack.Children.Add(MetricRow(descriptor, data));

        if (open && links.Count > 0)
        {
            stack.Children.Add(new Border
            {
                Height = 1,
                Background = (Brush)FindResource("SeparatorBrush"),
                Margin = new Thickness(14, 5, 14, 5)
            });
            var linkPanel = new WrapPanel { Margin = new Thickness(10, 4, 10, 0) };
            foreach (var link in links)
            {
                var btn = new Button { Content = link.Label, Style = (Style)FindResource("GhostButton"), Margin = new Thickness(0, 0, 6, 4) };
                var url = link.Url;
                btn.Click += (_, _) => OpenUrl(url);
                linkPanel.Children.Add(btn);
            }
            stack.Children.Add(linkPanel);
        }
        if (hasExpandedContent)
            stack.Children.Add(CardCaret(provider.Id, open));

        card.Child = stack;
        card.Margin = new Thickness(0, 0, 0, 10);
        return card;
    }

    private UIElement MetricRow(WidgetDescriptor descriptor, WidgetData? resolved = null, DateTimeOffset? evaluatedAt = null)
    {
        var data = resolved ?? _container.Data.Resolve(descriptor);
        var now = evaluatedAt ?? DateTimeOffset.Now;
        var panel = new StackPanel
        {
            Margin = new Thickness(12, data.IsBounded ? 8 : 4, 12, data.IsBounded ? 8 : 4),
            Background = Brushes.Transparent,
            ContextMenu = MetricContextMenu(descriptor)
        };

        var labelLine = new Grid();
        labelLine.ColumnDefinitions.Add(new ColumnDefinition());
        labelLine.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        labelLine.Children.Add(new TextBlock
        {
            Text = data.Title,
            FontWeight = FontWeights.SemiBold,
            FontSize = _container.Settings.Density == DensitySetting.Compact ? 11 : 12
        });
        if (data.IsChart && data.HasData && data.ChartPoints.Count > 0)
        {
            panel.Children.Add(labelLine);
            panel.Children.Add(Sparkline(data));
            if (!string.IsNullOrEmpty(data.ChartNote))
                panel.Children.Add(new TextBlock { Text = data.ChartNote, Style = (Style)FindResource("MutedText"), Margin = new Thickness(0, 4, 0, 0) });
            return panel;
        }
        if (data.IsBounded)
        {
            var state = data.GetMeterState(now);
            var note = state.Kind switch
            {
                MeterStateKind.Spent => "Limit reached",
                MeterStateKind.RunningOut => state.Eta ?? "",
                MeterStateKind.CloseToLimit => state.Spare ?? "",
                _ => ""
            };
            if (!string.IsNullOrEmpty(note))
            {
                var noteBlock = new TextBlock
                {
                    Text = state.Kind is MeterStateKind.Spent or MeterStateKind.RunningOut ? $"🔥 {note}" : note,
                    FontSize = 11,
                    Foreground = ThemeService.Meter(state.Severity)
                };
                Grid.SetColumn(noteBlock, 1);
                labelLine.Children.Add(noteBlock);
            }
        }
        panel.Children.Add(labelLine);

        if (data.IsBounded)
        {
            var state = data.GetMeterState(now);
            var track = new Grid { Height = 7, Margin = new Thickness(0, 6, 0, 6) };
            track.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(4),
                Background = (Brush)FindResource("TrackBrush")
            });
            var fraction = data.Limit is { } limit && limit > 0
                ? Math.Clamp((data.DisplayMode == WidgetDisplayMode.Remaining ? (limit - data.Used) : data.Used) / limit, 0, 1)
                : 0;
            if (data.HasData && state.Kind != MeterStateKind.NoData)
            {
                var fill = new Border
                {
                    CornerRadius = new CornerRadius(4),
                    Background = ThemeService.Meter(state.Severity),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Width = 1
                };
                fill.Loaded += (_, _) =>
                {
                    if (track.ActualWidth > 0)
                        fill.Width = Math.Max(2, track.ActualWidth * fraction);
                };
                track.SizeChanged += (_, _) => fill.Width = Math.Max(2, track.ActualWidth * fraction);
                track.Children.Add(fill);
                if (data.PaceTick(state, now) is { } tick)
                {
                    var marker = new Rectangle
                    {
                        Width = 2,
                        Height = 7,
                        Fill = (Brush)FindResource("TextBrush"),
                        HorizontalAlignment = HorizontalAlignment.Left,
                        Opacity = 0.85
                    };
                    marker.Loaded += (_, _) => marker.Margin = new Thickness(track.ActualWidth * tick, 0, 0, 0);
                    track.SizeChanged += (_, _) => marker.Margin = new Thickness(track.ActualWidth * tick, 0, 0, 0);
                    track.Children.Add(marker);
                }
            }
            track.ToolTip = state.Tooltip;
            panel.Children.Add(track);

            var primary = new Grid();
            primary.ColumnDefinitions.Add(new ColumnDefinition());
            primary.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var headline = new TextBlock
            {
                Text = data.Headline,
                FontSize = 12,
                Cursor = data.HasMeterStyleToggle ? Cursors.Hand : Cursors.Arrow,
                ToolTip = data.MeterStyleTooltip
            };
            if (data.HasMeterStyleToggle)
            {
                headline.MouseLeftButtonUp += (_, _) =>
                {
                    _container.Settings.MeterStyle = _container.Settings.MeterStyle == WidgetDisplayMode.Remaining
                        ? WidgetDisplayMode.Used
                        : WidgetDisplayMode.Remaining;
                    _container.Settings.Save();
                    Render();
                };
            }
            primary.Children.Add(headline);
            var trailing = data.BoundedTrailingText(now);
            if (trailing is not null)
            {
                var trail = new TextBlock
                {
                    Text = trailing,
                    Style = (Style)FindResource("MutedText"),
                    Cursor = data.HasResetLabel(now) ? Cursors.Hand : Cursors.Arrow,
                    ToolTip = data.ResetTooltip(now)
                };
                if (data.HasResetLabel(now))
                {
                    trail.MouseLeftButtonUp += (_, _) =>
                    {
                        _container.Settings.ResetDisplay = _container.Settings.ResetDisplay == ResetDisplayMode.Relative
                            ? ResetDisplayMode.Absolute
                            : ResetDisplayMode.Relative;
                        _container.Settings.Save();
                        Render();
                    };
                }
                Grid.SetColumn(trail, 1);
                primary.Children.Add(trail);
            }
            panel.Children.Add(primary);
        }
        else
        {
            panel.Children.Clear();
            var unbounded = new Grid { Margin = new Thickness(0) };
            unbounded.ColumnDefinitions.Add(new ColumnDefinition());
            unbounded.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            unbounded.Children.Add(new TextBlock { Text = data.Title, FontWeight = FontWeights.SemiBold, FontSize = 12 });
            var value = new TextBlock { Text = data.HasData ? data.UnboundedDetail : WidgetData.NoDataSubtitle, FontSize = 12 };
            Grid.SetColumn(value, 1);
            unbounded.Children.Add(value);
            panel.Children.Add(unbounded);
            foreach (var expiry in data.ResetExpiryLabels(now))
                panel.Children.Add(new TextBlock
                {
                    Text = expiry,
                    Style = (Style)FindResource("MutedText"),
                    Margin = new Thickness(0, 2, 0, 0)
                });
        }

        return panel;
    }

    private ContextMenu MetricContextMenu(WidgetDescriptor descriptor)
    {
        var context = new ContextMenu();
        context.Items.Add(Item("Hide", () => { _container.Layout.SetMetricEnabled(descriptor.Id, false); Render(); }));
        if (descriptor.Pinnable)
            context.Items.Add(Item(_container.Layout.IsPinned(descriptor.Id) ? "Unstar" : "Star for Tray",
                () => { _container.Layout.TogglePin(descriptor.Id); Render(); }));
        context.Items.Add(Item($"Refresh {descriptor.ProviderId}", () => _ = _container.Data.RefreshProviderAsync(descriptor.ProviderId)));
        context.Items.Add(Item("Customize…", () =>
        {
            _detailProviderId = descriptor.ProviderId;
            ShowScreen(DashboardScreen.CustomizeDetail);
        }));
        return context;
    }

    private void RenderCustomize()
    {
        var layout = _container.Layout;
        var undo = new Button
        {
            Content = "Undo Reorder", IsEnabled = layout.CanUndoProviderOrder,
            Style = (Style)FindResource("GhostButton"), HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 0, 0, 8)
        };
        undo.Click += (_, _) => { if (layout.UndoProviderOrder()) Render(); };
        ContentHost.Children.Add(undo);

        var groups = layout.ProviderGroups();
        var rows = groups.SelectMany((group, groupIndex) => group.Select((runtime, index) =>
            (Runtime: runtime, GroupIndex: groupIndex, GroupId: group.Key, First: index == 0)));
        foreach (var (runtime, groupIndex, groupId, first) in rows)
        {
            var provider = runtime.Provider;
            var row = Card();
            var grid = new Grid { Margin = new Thickness(12, 8, 12, 8) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var icon = ProviderIcon.Create(provider.IconId, 18);
            icon.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 10, 0));
            var name = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
            name.Children.Add(new TextBlock { Text = provider.DisplayName, FontWeight = FontWeights.SemiBold });
            name.Children.Add(new TextBlock
            {
                Text = $"{runtime.WidgetDescriptors.Count} metrics",
                Style = (Style)FindResource("MutedText")
            });
            var toggle = new CheckBox { IsChecked = _container.Layout.IsProviderEnabled(provider.Id), VerticalAlignment = VerticalAlignment.Center };
            toggle.Checked += (_, _) => { _container.Layout.SetProviderEnabled(provider.Id, true); row.Opacity = 1; };
            toggle.Unchecked += (_, _) => { _container.Layout.SetProviderEnabled(provider.Id, false); row.Opacity = 0.55; };
            // The checkbox's Click bubbles to the row button; stop it so toggling doesn't open the detail page.
            toggle.Click += (_, e) => e.Handled = true;
            Grid.SetColumn(name, 1);
            Grid.SetColumn(toggle, 2);
            grid.Children.Add(icon);
            grid.Children.Add(name);
            if (!runtime.IsAccountTemplate) grid.Children.Add(toggle);
            var button = new Button { Style = (Style)FindResource("GhostButton"), Padding = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Stretch };
            button.Content = grid;
            button.Click += (_, _) =>
            {
                _detailProviderId = provider.Id;
                ShowScreen(DashboardScreen.CustomizeDetail);
            };
            if (first)
            {
                var orderedRow = new DockPanel();
                var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                var providerName = runtime.Provider.DisplayName;
                Button MoveButton(string arrow, string direction, int offset, bool canMove)
                {
                    var move = new Button
                    {
                        Content = arrow, IsEnabled = canMove, Style = (Style)FindResource("GhostButton"),
                        Padding = new Thickness(7, 4, 7, 4)
                    };
                    AutomationProperties.SetName(move, $"Move {providerName} group {direction}");
                    move.Click += (_, _) => { if (layout.MoveProviderGroup(groupId, offset)) Render(); };
                    return move;
                }
                actions.Children.Add(MoveButton("↑", "up", -1, groupIndex > 0));
                actions.Children.Add(MoveButton("↓", "down", 1, groupIndex < groups.Length - 1));
                DockPanel.SetDock(actions, Dock.Right);
                orderedRow.Children.Add(actions);
                orderedRow.Children.Add(button);
                row.Child = orderedRow;
            }
            else
            {
                row.Child = button;
            }
            row.Margin = new Thickness(0, 0, 0, 8);
            row.Opacity = runtime.IsAccountTemplate || _container.Layout.IsProviderEnabled(provider.Id) ? 1 : 0.55;
            ContentHost.Children.Add(row);
        }
    }

    private void RenderCustomizeDetail()
    {
        var runtime = _container.Layout.Providers.FirstOrDefault(p => p.Provider.Id == _detailProviderId);
        if (runtime is null) return;
        if (runtime is DevinProvider devin)
        {
            ContentHost.Children.Add(SectionTitle("Devin Account"));
            _devinSignIn = new DevinSignInView(_container, devin);
            ContentHost.Children.Add(_devinSignIn);
        }
        if (runtime is OpenUsage.Core.Providers.Codex.CodexProvider codex)
        {
            ContentHost.Children.Add(SectionTitle("ChatGPT Sign-In"));
            _codexLoginEditor = new CodexLoginView(_container, codex, (Style)FindResource("GhostButton"));
            ContentHost.Children.Add(_codexLoginEditor);
        }
        if (runtime is OpenUsage.Core.Providers.Copilot.CopilotProvider copilot)
        {
            ContentHost.Children.Add(SectionTitle("GitHub Sign-In"));
            _copilotLoginEditor = new CopilotLoginView(_container, copilot, (Style)FindResource("GhostButton"));
            ContentHost.Children.Add(_copilotLoginEditor);
        }
        if (runtime is OpenUsage.Core.Providers.Cursor.CursorProvider cursor)
        {
            ContentHost.Children.Add(SectionTitle("Cursor Sign-In"));
            _cursorLoginEditor = new CursorLoginView(_container, cursor, (Style)FindResource("GhostButton"));
            ContentHost.Children.Add(_cursorLoginEditor);
        }
        if (runtime is IApiKeyManaging keys)
        {
            ContentHost.Children.Add(SectionTitle("API Key"));
            var box = new TextBox
            {
                Text = keys.LoadKey() ?? "",
                Padding = new Thickness(8),
                Margin = new Thickness(0, 0, 0, 10)
            };
            box.LostFocus += (_, _) => keys.SaveKey(string.IsNullOrWhiteSpace(box.Text) ? null : box.Text.Trim());
            ContentHost.Children.Add(box);
        }

        if (ApiKeyAccounts.Supports(runtime.Provider.Id))
        {
            ContentHost.Children.Add(SectionTitle(runtime.IsAccountTemplate ? "Accounts" : "Additional Accounts"));
            _accountEditor = new ApiKeyAccountsView(_container, runtime.Provider.Id);
            ContentHost.Children.Add(_accountEditor);
        }

        if (runtime.IsAccountTemplate) return;

        if (runtime is IDeviceSignInProvider deviceProvider)
        {
            ContentHost.Children.Add(SectionTitle($"{deviceProvider.SignInAuthority} Sign-In"));
            _deviceLoginEditor = new DeviceLoginView(_container, deviceProvider, (Style)FindResource("GhostButton"));
            ContentHost.Children.Add(_deviceLoginEditor);
        }

        ContentHost.Children.Add(SectionTitle("Always Visible"));
        foreach (var descriptor in runtime.WidgetDescriptors.Where(d => !_container.Layout.IsOnDemand(d.Id)))
            ContentHost.Children.Add(MetricCustomizeRow(descriptor));
        ContentHost.Children.Add(SectionTitle("On Demand"));
        foreach (var descriptor in runtime.WidgetDescriptors.Where(d => _container.Layout.IsOnDemand(d.Id)))
            ContentHost.Children.Add(MetricCustomizeRow(descriptor));
    }

    private UIElement MetricCustomizeRow(WidgetDescriptor descriptor)
    {
        var row = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(new TextBlock { Text = _container.Data.Resolve(descriptor).Title, VerticalAlignment = VerticalAlignment.Center });
        var onDemand = new Button
        {
            Content = _container.Layout.IsOnDemand(descriptor.Id) ? "On Demand" : "Always",
            Style = (Style)FindResource("GhostButton"),
            FontSize = 11
        };
        onDemand.Click += (_, _) =>
        {
            _container.Layout.SetOnDemand(descriptor.Id, !_container.Layout.IsOnDemand(descriptor.Id));
            Render();
        };
        var star = new Button
        {
            Content = _container.Layout.IsPinned(descriptor.Id) ? "★" : "☆",
            Style = (Style)FindResource("GhostButton"),
            IsEnabled = descriptor.Pinnable
        };
        star.Click += (_, _) => { _container.Layout.TogglePin(descriptor.Id); Render(); };
        var enabled = new CheckBox { IsChecked = _container.Layout.IsMetricEnabled(descriptor.Id), VerticalAlignment = VerticalAlignment.Center };
        enabled.Checked += (_, _) => _container.Layout.SetMetricEnabled(descriptor.Id, true);
        enabled.Unchecked += (_, _) => _container.Layout.SetMetricEnabled(descriptor.Id, false);
        Grid.SetColumn(onDemand, 1);
        Grid.SetColumn(star, 2);
        Grid.SetColumn(enabled, 3);
        row.Children.Add(onDemand);
        row.Children.Add(star);
        row.Children.Add(enabled);
        return row;
    }

    private void RenderSettings()
    {
        ContentHost.Children.Add(SectionTitle("General"));
        var launch = SettingToggle("Launch at Login", _container.Settings.LaunchAtLogin, value =>
        {
            _container.Settings.LaunchAtLogin = value;
            LaunchAtLogin.Apply(value);
            _container.Settings.Save();
        });
        ContentHost.Children.Add(launch);
        ContentHost.Children.Add(HotkeyRow());

        ContentHost.Children.Add(SectionTitle("Appearance"));
        ContentHost.Children.Add(SettingCombo("Theme", new[] { "System", "Light", "Dark" }, (int)_container.Settings.Theme, index =>
        {
            _container.Settings.Theme = (AppTheme)index;
            ThemeService.Apply(_container.Settings.Theme);
            _container.Settings.Save();
            Render();
        }));
        ContentHost.Children.Add(SettingCombo("Density", new[] { "Regular", "Compact" }, (int)_container.Settings.Density, index =>
        {
            _container.Settings.Density = (DensitySetting)index;
            _container.Settings.Save();
            Render();
        }));
        ContentHost.Children.Add(SettingToggle("Always Show Pacing", _container.Settings.AlwaysShowPacing, value =>
        {
            _container.Settings.AlwaysShowPacing = value;
            _container.Settings.Save();
        }));

        RenderPriorityLimitSettings();

        ContentHost.Children.Add(SectionTitle("Refresh"));
        ContentHost.Children.Add(SettingCombo("Interval", new[] { "1 minute", "5 minutes", "15 minutes" },
            _container.Settings.RefreshIntervalSeconds switch { 60 => 0, 900 => 2, _ => 1 },
            index =>
            {
                _container.Settings.RefreshIntervalSeconds = index switch { 0 => 60, 2 => 900, _ => 300 };
                _container.Settings.Save();
            }));

        ContentHost.Children.Add(SectionTitle("Usage Notifications"));
        ContentHost.Children.Add(SettingToggle("Burning Fast", _container.Settings.NotifyBurningFast, value =>
        {
            _container.Settings.NotifyBurningFast = value;
            _container.Settings.Save();
        }));
        ContentHost.Children.Add(SettingToggle("Use It or Lose It", _container.Settings.NotifyUseItOrLoseIt, value =>
        {
            _container.Settings.NotifyUseItOrLoseIt = value;
            _container.Settings.Save();
        }));

        var reset = new Button { Content = "Reset Layout to Default", Style = (Style)FindResource("GhostButton"), Margin = new Thickness(0, 12, 0, 0) };
        reset.Click += (_, _) =>
        {
            if (MessageBox.Show("Reset providers, metrics, and pins to the default layout?", "Reset Layout",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                _container.Layout.ResetToDefault();
                Render();
            }
        };
        ContentHost.Children.Add(reset);
        ContentHost.Children.Add(new TextBlock
        {
            Text = $"Logs: {AppPaths.LogFile}",
            Style = (Style)FindResource("MutedText"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 12, 0, 0)
        });
    }

    private UIElement Sparkline(WidgetData data)
    {
        var points = data.ChartPoints;
        var max = Math.Max(1, points.Max(p => p.Value));
        var row = new UniformGrid { Rows = 1, Height = 36, Margin = new Thickness(0, 6, 0, 0) };
        foreach (var point in points)
        {
            var fill = Math.Max(2, 32 * (point.Value / max));
            var bar = new Border
            {
                Width = 4,
                Height = fill,
                CornerRadius = new CornerRadius(1),
                Background = ThemeService.Meter(MeterSeverity.Normal),
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0.5, 0, 0.5, 0),
                ToolTip = $"{point.Label}: {point.ValueLabel ?? point.Value.ToString("N0")}"
            };
            row.Children.Add(bar);
        }
        return row;
    }

    private UIElement HotkeyRow()
    {
        var grid = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new TextBlock { Text = "Global Shortcut", VerticalAlignment = VerticalAlignment.Center });
        var field = new TextBox
        {
            MinWidth = 140,
            IsReadOnly = true,
            Text = string.IsNullOrEmpty(_container.Settings.GlobalHotkey) ? "Click, then press a combo" : _container.Settings.GlobalHotkey,
            Padding = new Thickness(8, 4, 8, 4)
        };
        field.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape || e.Key == Key.Back || e.Key == Key.Delete)
            {
                HotkeyChanged?.Invoke(null);
                field.Text = "Click, then press a combo";
                e.Handled = true;
                return;
            }
            var captured = HotkeyService.Capture(e);
            if (captured is null) return;
            HotkeyChanged?.Invoke(captured);
            field.Text = captured;
            e.Handled = true;
        };
        Grid.SetColumn(field, 1);
        grid.Children.Add(field);
        return grid;
    }

    private static TextBlock SectionTitle(string text) => new()
    {
        Text = text,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(0, 8, 0, 6)
    };

    private Border Card() => new()
    {
        Style = (Style)FindResource("CardBorder")
    };

    private UIElement SettingToggle(string label, bool value, Action<bool> onChange)
    {
        var grid = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        var box = new CheckBox { IsChecked = value, VerticalAlignment = VerticalAlignment.Center };
        box.Checked += (_, _) => onChange(true);
        box.Unchecked += (_, _) => onChange(false);
        Grid.SetColumn(box, 1);
        grid.Children.Add(box);
        return grid;
    }

    private UIElement SettingCombo(string label, string[] options, int selected, Action<int> onChange)
    {
        var grid = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        var combo = new ComboBox { ItemsSource = options, SelectedIndex = selected, MinWidth = 120 };
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedIndex >= 0) onChange(combo.SelectedIndex);
        };
        Grid.SetColumn(combo, 1);
        grid.Children.Add(combo);
        return grid;
    }
}

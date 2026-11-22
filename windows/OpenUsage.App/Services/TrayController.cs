using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using OpenUsage.Core;
using OpenUsage.Core.Models;
using OpenUsage.Core.Services;
using OpenUsage.Core.Support;
using Application = System.Windows.Application;
using Forms = System.Windows.Forms;

namespace OpenUsage.App.Services;

public sealed class TrayController : IDisposable
{
    private readonly AppContainer _container;
    private readonly DashboardWindow _window;
    private readonly Forms.NotifyIcon _notify;
    private readonly DispatcherTimer _clock;
    private readonly PaceAlerts _paceAlerts = new();
    private Icon? _icon;

    public TrayController(AppContainer container, DashboardWindow window)
    {
        _container = container;
        _window = window;
        _notify = new Forms.NotifyIcon
        {
            Visible = true,
            Text = "OpenUsage"
        };
        _notify.MouseClick += OnClick;
        _notify.BalloonTipClicked += (_, _) => _window.ShowScreen(DashboardScreen.Dashboard);
        _notify.ContextMenuStrip = BuildMenu();
        _container.Data.Changed += () => Application.Current.Dispatcher.Invoke(Update);
        _window.PrivacyModeChanged += Update;
        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _clock.Tick += (_, _) => Update();
        _clock.Start();
        Update();
    }

    private Forms.ContextMenuStrip BuildMenu()
    {
        var menu = new Forms.ContextMenuStrip();
        if (ThemeService.IsDark)
        {
            menu.ForeColor = Color.FromArgb(245, 245, 247);
            menu.BackColor = Color.FromArgb(44, 44, 46);
            menu.Renderer = new Forms.ToolStripProfessionalRenderer(new DarkColorTable());
        }
        menu.Items.Add("Open Dashboard", null, (_, _) => _window.ToggleFromTray());
        menu.Items.Add("Refresh", null, async (_, _) => await _container.RefreshNowAsync());
        menu.Items.Add("Customize…", null, (_, _) => _window.ShowScreen(DashboardScreen.Customize));
        menu.Items.Add("Settings…", null, (_, _) => _window.ShowScreen(DashboardScreen.Settings));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Report an Issue…", null, (_, _) => DashboardWindow.OpenUrl(DashboardWindow.ReportIssueUrl));
        menu.Items.Add("Quit OpenUsage", null, (_, _) => Application.Current.Shutdown());
        return menu;
    }

    private void OnClick(object? sender, Forms.MouseEventArgs e)
    {
        if (e.Button == Forms.MouseButtons.Left)
            _window.ToggleFromTray();
    }

    private void Update()
    {
        var now = DateTimeOffset.Now;
        var pins = _container.Settings.PrivacyMode ? [] : _container.Data.PinnedTiles();
        var tooltip = pins.Count == 0
            ? "OpenUsage"
            : string.Join(Environment.NewLine,
                pins.Select(p => $"{p.Data.Title}: {p.Data.MenuBarValue}").Take(8));
        _notify.Text = tooltip.Length > 63 ? tooltip[..60] + "…" : tooltip;
        var worst = pins.Select(p => p.Data.GetMeterState(now).Severity)
            .Where(s => s is not null)
            .DefaultIfEmpty(MeterSeverity.Normal)
            .Max();
        var icon = RenderIcon(worst ?? MeterSeverity.Normal);
        var previous = _icon;
        _notify.Icon = icon;
        _icon = icon;
        if (previous is not null)
        {
            DestroyIcon(previous.Handle);
            previous.Dispose();
        }
        UpdateAlerts(now);
    }

    private void UpdateAlerts(DateTimeOffset now)
    {
        var settings = _container.Settings;
        if (settings.PrivacyMode || (!settings.NotifyBurningFast && !settings.NotifyUseItOrLoseIt))
        {
            _paceAlerts.Clear();
            return;
        }

        var layout = _container.Layout;
        var data = _container.Data;
        var metrics = from runtime in layout.VisibleProviders
                      let provider = runtime.Provider
                      let healthy = data.ProviderError(provider.Id) is null
                      from descriptor in layout.VisibleMetrics(provider.Id, includeOnDemand: true)
                      select new PaceAlertMetric(descriptor.Id, $"{provider.DisplayName}: {descriptor.Title}",
                          healthy ? data.Resolve(descriptor) : null);
        var alerts = _paceAlerts.Observe(metrics, now).Where(alert => alert.Kind == PaceAlertKind.BurningFast
            ? settings.NotifyBurningFast
            : settings.NotifyUseItOrLoseIt).ToArray();
        if (alerts.Length == 0) return;

        var first = alerts[0];
        var reason = first.Kind == PaceAlertKind.BurningFast
            ? "May run out before it resets."
            : "Over half left; resets within 24 hours.";
        var title = first.Kind == PaceAlertKind.BurningFast ? "Burning Fast" : "Use It or Lose It";
        var summary = alerts.Length > 1 ? $" {alerts.Length - 1} more usage alerts." : "";
        _notify.ShowBalloonTip(5000, $"OpenUsage — {title}", $"{first.Label}\n{reason}{summary}", Forms.ToolTipIcon.Info);
    }

    private static Icon RenderIcon(MeterSeverity severity)
    {
        var color = severity switch
        {
            MeterSeverity.Warning => Color.FromArgb(255, 159, 10),
            MeterSeverity.Critical => Color.FromArgb(255, 69, 58),
            _ => Color.FromArgb(10, 132, 255)
        };
        var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);
            using var fill = new SolidBrush(color);
            g.FillEllipse(fill, 2, 2, 28, 28);
            using var inner = new SolidBrush(Color.FromArgb(32, 32, 32));
            g.FillEllipse(inner, 7, 7, 18, 18);
            using var mark = new Pen(color, 2.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLine(mark, 12, 16, 15, 19);
            g.DrawLine(mark, 15, 19, 21, 13);
        }
        var handle = bmp.GetHicon();
        bmp.Dispose();
        return Icon.FromHandle(handle);
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool DestroyIcon(IntPtr handle);

    public void Dispose()
    {
        _clock.Stop();
        _window.PrivacyModeChanged -= Update;
        _notify.Visible = false;
        _notify.Dispose();
        _icon?.Dispose();
    }

    private sealed class DarkColorTable : Forms.ProfessionalColorTable
    {
        public override Color MenuBorder => Color.FromArgb(58, 58, 60);
        public override Color MenuItemBorder => Color.FromArgb(58, 58, 60);
        public override Color MenuItemSelected => Color.FromArgb(58, 58, 60);
        public override Color MenuItemSelectedGradientBegin => Color.FromArgb(58, 58, 60);
        public override Color MenuItemSelectedGradientEnd => Color.FromArgb(58, 58, 60);
        public override Color ToolStripDropDownBackground => Color.FromArgb(44, 44, 46);
        public override Color ImageMarginGradientBegin => Color.FromArgb(44, 44, 46);
        public override Color ImageMarginGradientMiddle => Color.FromArgb(44, 44, 46);
        public override Color ImageMarginGradientEnd => Color.FromArgb(44, 44, 46);
        public override Color SeparatorDark => Color.FromArgb(72, 72, 74);
        public override Color SeparatorLight => Color.FromArgb(72, 72, 74);
    }
}

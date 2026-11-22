using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using OpenUsage.App.Services;

namespace OpenUsage.App;

public partial class DashboardWindow
{
    private bool _captureProtectionApplied;
    internal event Action? PrivacyModeChanged;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        AddHandler(ToolTipService.ToolTipOpeningEvent, new ToolTipEventHandler((_, args) =>
        {
            if (_container.Settings.PrivacyMode) args.Handled = true;
        }));
        if (_container.Settings.PrivacyMode)
            _captureProtectionApplied = CapturePrivacy.Apply(new WindowInteropHelper(this).Handle, true);
    }

    private void RenderPrivacySettings()
    {
        ContentHost.Children.Add(SectionTitle("Privacy"));
        ContentHost.Children.Add(SettingToggle("Privacy Mode", _container.Settings.PrivacyMode, enabled =>
        {
            _container.Settings.PrivacyMode = enabled;
            var applied = CapturePrivacy.Apply(new WindowInteropHelper(this).Handle, enabled);
            _captureProtectionApplied = applied && enabled;
            _container.Settings.Save();
            PrivacyModeChanged?.Invoke();
            Render();
        }));
    }

    private bool ShowPrivacyPlaceholder()
    {
        if (!_container.Settings.PrivacyMode || _captureProtectionApplied || _screen == DashboardScreen.Settings)
            return false;
        TopTitle.Text = "OpenUsage";
        ContentHost.Children.Add(new TextBlock
        {
            Text = "Privacy Mode hides usage and accounts because Windows could not protect this window.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(4, 6, 4, 8)
        });
        return true;
    }
}

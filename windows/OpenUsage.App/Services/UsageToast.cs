using System.Runtime.InteropServices;
using System.Xml.Linq;
using OpenUsage.Core.Support;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace OpenUsage.App.Services;

internal sealed class UsageToast
{
    internal const string AppId = "OpenUsage.Windows.Notifications";
    private const int SettingNotFound = unchecked((int)0x80070490);
    private bool? _enabled;
    private bool _configured;

    public void SetEnabled(bool enabled)
    {
        if (_enabled == enabled) return;
        _enabled = enabled;
        _configured = false;
        try
        {
            if (enabled)
            {
                _configured = NotificationActivation.CanReceiveShellActivation() && ToastRegistration.Install();
                if (!_configured)
                    AppLog.Warn("notifications", "Windows toast registration is unavailable; using tray balloons.");
            }
            else ToastRegistration.Remove();
        }
        catch (Exception ex)
        {
            AppLog.Warn("notifications",
                $"Could not update Windows toast registration ({ex.GetType().Name}, 0x{ex.HResult:X8}).");
        }
    }

    public bool TryShow(string title, string message)
    {
        if (!_configured) return false;
        try
        {
            var notifier = ToastNotificationManager.CreateToastNotifier(AppId);
            if (IsDisabled(notifier)) return true;
            var document = new XmlDocument();
            document.LoadXml(new XElement("toast",
                new XAttribute("activationType", "protocol"),
                new XAttribute("launch", NotificationRequest.Uri),
                new XElement("visual",
                    new XElement("binding", new XAttribute("template", "ToastGeneric"),
                        new XElement("text", title),
                        new XElement("text", message)))).ToString(SaveOptions.DisableFormatting));
            notifier.Show(new ToastNotification(document));
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn("notifications", $"Could not send a Windows toast ({ex.GetType().Name}, 0x{ex.HResult:X8}).");
            return false;
        }
    }

    private static bool IsDisabled(ToastNotifier notifier)
    {
        try { return notifier.Setting != NotificationSetting.Enabled; }
        catch (COMException ex) when (ex.HResult == SettingNotFound)
        {
            // An unpackaged toast can be delivered before Windows exposes an app setting.
            return false;
        }
    }
}

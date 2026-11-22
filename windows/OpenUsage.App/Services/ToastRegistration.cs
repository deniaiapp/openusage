using System.IO;
using Microsoft.Win32;

namespace OpenUsage.App.Services;

internal static class ToastRegistration
{
    private const string Owner = "OpenUsageWindowsToastRegistration";
    private static string ProtocolKey => $@"Software\Classes\{NotificationRequest.Scheme}";

    private static string? Shortcut =>
        Environment.GetFolderPath(Environment.SpecialFolder.Programs) is { Length: > 0 } programs
            ? Path.Combine(programs, "OpenUsage Usage Notifications.lnk")
            : null;

    public static bool Install()
    {
        var executable = Environment.ProcessPath;
        if (executable is null || !string.Equals(Path.GetFileName(executable), "OpenUsage.exe",
                StringComparison.OrdinalIgnoreCase)) return false;

        using var existing = Registry.CurrentUser.OpenSubKey(ProtocolKey);
        if (existing is not null && !Owns(existing)) return false;
        if (existing is null)
        {
            using var claimed = Registry.ClassesRoot.OpenSubKey(NotificationRequest.Scheme);
            if (claimed is not null) return false;
        }

        if (Shortcut is not { } shortcut) return false;
        var replaceShortcut = File.Exists(shortcut);
        if (replaceShortcut && !ToastShortcut.IsOwned(shortcut, UsageToast.AppId)) return false;
        var command = $"\"{executable}\" {NotificationRequest.Argument} \"%1\"";
        using (var current = existing?.OpenSubKey(@"shell\open\command"))
        {
            if (replaceShortcut && string.Equals(current?.GetValue(null) as string, command,
                    StringComparison.OrdinalIgnoreCase)) return true;
        }

        var staged = Path.Combine(Path.GetDirectoryName(shortcut)!, $".{Guid.NewGuid():N}.tmp");
        try
        {
            ToastShortcut.Write(staged, executable, UsageToast.AppId);
            File.Move(staged, shortcut, replaceShortcut);
        }
        finally { File.Delete(staged); }

        using var protocol = Registry.CurrentUser.CreateSubKey(ProtocolKey);
        protocol.SetValue(Owner, 1);
        protocol.SetValue("", "URL:OpenUsage Dashboard");
        protocol.SetValue("URL Protocol", "");
        using var commandKey = protocol.CreateSubKey(@"shell\open\command");
        commandKey.SetValue("", command);
        return true;
    }

    public static void Remove()
    {
        using var protocol = Registry.CurrentUser.OpenSubKey(ProtocolKey);
        if (protocol is not null && Owns(protocol))
            Registry.CurrentUser.DeleteSubKeyTree(ProtocolKey, throwOnMissingSubKey: false);

        if (Shortcut is { } shortcut && File.Exists(shortcut) && ToastShortcut.IsOwned(shortcut, UsageToast.AppId))
            File.Delete(shortcut);
    }

    private static bool Owns(RegistryKey key) => key.GetValue(Owner) is 1;
}

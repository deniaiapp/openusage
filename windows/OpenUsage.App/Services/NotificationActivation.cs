using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Windows.Interop;
using Microsoft.Win32.SafeHandles;
using OpenUsage.Core.Support;

namespace OpenUsage.App.Services;

internal sealed class NotificationActivation : IDisposable
{
    private const string WindowName = "OpenUsage.Windows.NotificationActivation";
    private static readonly nint MessageOnly = new(-3);
    private static readonly uint OpenDashboardMessage = RegisterWindowMessage(WindowName);
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const int TokenElevation = 20;
    private readonly HwndSource _source;

    public NotificationActivation(Action openDashboard)
    {
        _source = new HwndSource(new HwndSourceParameters(WindowName) { ParentWindow = MessageOnly, WindowStyle = 0 });
        _source.AddHook((nint _, int message, nint wParam, nint lParam, ref bool handled) =>
        {
            if (OpenDashboardMessage == 0 || message != OpenDashboardMessage) return 0;
            _source.Dispatcher.BeginInvoke(openDashboard);
            handled = true;
            return 0;
        });
    }

    public static bool CanReceiveShellActivation()
    {
        var shellWindow = GetShellWindow();
        if (shellWindow == 0) return false;
        GetWindowThreadProcessId(shellWindow, out var shellProcessId);
        if (shellProcessId == 0) return false;

        using var shellProcess = OpenProcess(ProcessQueryLimitedInformation, false, shellProcessId);
        if (shellProcess.IsInvalid || !OpenProcessToken(shellProcess, TokenQuery, out var shellToken)) return false;
        using (shellToken)
        using (var shellIdentity = new WindowsIdentity(shellToken.DangerousGetHandle()))
        using (var appIdentity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query))
        {
            // The shell must see the same user's registration and be allowed to message the app.
            return appIdentity.User is { } appUser && shellIdentity.User is { } shellUser && appUser.Equals(shellUser)
                && TryGetElevation(appIdentity.AccessToken, out var appElevated)
                && TryGetElevation(shellToken, out var shellElevated)
                && appElevated == shellElevated;
        }
    }

    private static bool TryGetElevation(SafeAccessTokenHandle token, out uint elevated) =>
        GetTokenInformation(token, TokenElevation, out elevated, sizeof(uint), out _);

    public static void Forward()
    {
        if (OpenDashboardMessage == 0) return;
        var window = FindWindowEx(MessageOnly, 0, null, WindowName);
        if (window == 0) return;
        try
        {
            GetWindowThreadProcessId(window, out var processId);
            if (processId == 0) return;
            using var target = Process.GetProcessById((int)processId);
            if (!string.Equals(target.MainModule?.FileName, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
                return;

            AllowSetForegroundWindow(processId);
            if (!PostMessage(window, OpenDashboardMessage, 0, 0))
                AppLog.Warn("notifications", "Could not forward the dashboard request to the running app.");
        }
        catch (Exception ex)
        {
            AppLog.Warn("notifications", $"Could not forward the dashboard request ({ex.GetType().Name}).");
        }
    }

    public void Dispose() => _source.Dispose();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string name);

    [DllImport("user32.dll")]
    private static extern nint GetShellWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindowEx(nint parent, nint childAfter, string? className, string windowName);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);

    [DllImport("kernel32.dll")]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [DllImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);

    [DllImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        SafeAccessTokenHandle token, int informationClass, out uint information, uint informationLength, out uint returnLength);
}

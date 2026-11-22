using System.Runtime.InteropServices;
using OpenUsage.Core.Support;

namespace OpenUsage.App.Services;

internal static class CapturePrivacy
{
    private const uint NoAffinity = 0;
    private const uint ExcludeFromCapture = 0x11;

    public static bool Apply(IntPtr window, bool enabled)
    {
        if (window == IntPtr.Zero) return false;
        if (SetWindowDisplayAffinity(window, enabled ? ExcludeFromCapture : NoAffinity)) return true;
        AppLog.Warn("privacy", $"Could not change screen capture protection (Win32 error {Marshal.GetLastPInvokeError()}).");
        return false;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(IntPtr window, uint affinity);
}

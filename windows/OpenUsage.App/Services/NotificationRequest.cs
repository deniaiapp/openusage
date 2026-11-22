namespace OpenUsage.App.Services;

internal static class NotificationRequest
{
    public const string Argument = "--notification-dashboard";
    public const string Scheme = "openusage-windows";
    public const string Uri = Scheme + "://dashboard";
    private const string WindowsUri = Uri + "/";

    public static bool OpensDashboard(string[] args) => args is [Argument, Uri or WindowsUri];
}

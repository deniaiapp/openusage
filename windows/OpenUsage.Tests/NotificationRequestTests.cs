using OpenUsage.App.Services;
using Xunit;

namespace OpenUsage.Tests;

public sealed class NotificationRequestTests
{
    [Fact]
    public void OpensDashboardOnlyForTheFixedToastDestination()
    {
        Assert.True(NotificationRequest.OpensDashboard([NotificationRequest.Argument, NotificationRequest.Uri]));
        Assert.True(NotificationRequest.OpensDashboard([NotificationRequest.Argument, NotificationRequest.Uri + "/"]));
        Assert.False(NotificationRequest.OpensDashboard([]));
        Assert.False(NotificationRequest.OpensDashboard([NotificationRequest.Uri]));
        Assert.False(NotificationRequest.OpensDashboard([NotificationRequest.Argument]));
        Assert.False(NotificationRequest.OpensDashboard([NotificationRequest.Argument, NotificationRequest.Uri + "?other=1"]));
        Assert.False(NotificationRequest.OpensDashboard([NotificationRequest.Argument, NotificationRequest.Uri + "/?other=1"]));
        Assert.False(NotificationRequest.OpensDashboard([NotificationRequest.Argument, NotificationRequest.Uri + "/#fragment"]));
        Assert.False(NotificationRequest.OpensDashboard([NotificationRequest.Argument, NotificationRequest.Uri + "/other"]));
        Assert.False(NotificationRequest.OpensDashboard([NotificationRequest.Argument, NotificationRequest.Uri + "//"]));
        Assert.False(NotificationRequest.OpensDashboard([NotificationRequest.Argument, NotificationRequest.Uri, "extra"]));
    }
}

namespace OpenUsage.Core.Providers.Claude;

public sealed class ClaudeOAuth
{
    public string? AccessToken { get; set; }
    public string? RefreshToken { get; set; }
    public double? ExpiresAt { get; set; }
    public string? SubscriptionType { get; set; }
    public string? RateLimitTier { get; set; }
    public List<string>? Scopes { get; set; }
}

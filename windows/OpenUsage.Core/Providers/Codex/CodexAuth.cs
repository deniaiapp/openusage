namespace OpenUsage.Core.Providers.Codex;

public sealed class CodexAuth
{
    public string? AccessToken { get; set; }
    public string? RefreshToken { get; set; }
    public string? AccountId { get; set; }
}

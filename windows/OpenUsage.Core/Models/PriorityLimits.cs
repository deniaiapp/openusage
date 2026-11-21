namespace OpenUsage.Core.Models;

public sealed record PriorityLimits
{
    public const int Step = 5;
    public const int MinWarning = 20;
    public const int MaxWarning = 70;
    public const int MinCritical = 5;
    public const int MaxCritical = 25;
    public static PriorityLimits Default { get; } = new(30, 10);

    public int Warning { get; }
    public int Critical { get; }

    public PriorityLimits(int warning, int critical)
    {
        Warning = Snap(warning, MinWarning, MaxWarning);
        Critical = Math.Min(Snap(critical, MinCritical, MaxCritical), Warning - Step);
    }

    public PriorityLimits WithWarning(int warning) => new(warning, Critical);

    public PriorityLimits WithCritical(int critical)
    {
        critical = Snap(critical, MinCritical, MaxCritical);
        return new(Math.Max(Warning, critical + Step), critical);
    }

    internal MeterSeverity Severity(double remainingFraction)
    {
        var percent = Math.Round(Math.Clamp(remainingFraction, 0, 1) * 100, MidpointRounding.AwayFromZero);
        if (percent <= Critical) return MeterSeverity.Critical;
        return percent <= Warning ? MeterSeverity.Warning : MeterSeverity.Normal;
    }

    private static int Snap(int value, int min, int max) =>
        (int)Math.Round(Math.Clamp(value, min, max) / (double)Step) * Step;
}

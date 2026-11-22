using OpenUsage.Core.Support;

namespace OpenUsage.Core.Models;

public sealed record WidgetData
{
    public const string NoDataHeadline = "—";
    public const string NoDataSubtitle = "No data";
    public const string FreshSessionTooltip = "Sessions start after you send your first message.";

    public required string Title { get; init; }
    public string IconId { get; init; } = "";
    public MetricKind Kind { get; init; }
    public double Used { get; init; }
    public double? Limit { get; init; }
    public string? CountSuffix { get; init; }
    public string? CurrencyCode { get; init; }
    public string? ValuePrefix { get; init; }
    public WidgetDisplayMode DisplayMode { get; set; } = WidgetDisplayMode.Used;
    public ResetDisplayMode ResetDisplayMode { get; set; } = ResetDisplayMode.Relative;
    public bool AlwaysShowPacing { get; set; }
    public PriorityLimits PriorityLimits { get; init; } = PriorityLimits.Default;
    public DateTimeOffset? ResetsAt { get; init; }
    public IReadOnlyList<DateTimeOffset> ExpiriesAt { get; init; } = [];
    public bool ShowsResetExpiries { get; init; }
    public IReadOnlyList<string> UnknownModels { get; init; } = [];
    public long? PeriodDurationMs { get; init; }
    public string? ValueTextOverride { get; init; }
    public string? SubtitleOverride { get; init; }
    public string? LimitNoun { get; init; }
    public string? UnboundedValueWord { get; init; }
    public string? InfoNote { get; init; }
    public string? ValueTooltipNote { get; init; }
    public bool HasData { get; init; } = true;
    public IReadOnlyList<MetricValue> Values { get; init; } = [];
    public ValueSelection Selection { get; init; } = ValueSelection.All;
    public bool IsUsagePeriod { get; init; }
    public string? TraySuffix { get; init; }
    public SessionStartSignal? SessionStartSignal { get; init; }
    public bool IsChart { get; init; }
    public IReadOnlyList<MetricChartPoint> ChartPoints { get; init; } = [];
    public string? ChartNote { get; init; }

    public bool IsBounded => Limit is not null;
    public IReadOnlyList<MetricValue> SelectedValues => Selection.Apply(Values);
    public double DisplayedValue => DisplayMode == WidgetDisplayMode.Remaining && Limit is { } limit
        ? Math.Max(0, limit - Used)
        : Used;

    public string ValueText
    {
        get
        {
            if (!HasData) return NoDataHeadline;
            if (ValueTextOverride is not null) return ValueTextOverride;
            if (SelectedValues.FirstOrDefault() is { } first)
                return (ValuePrefix ?? "") + MetricFormatter.Number(first.Number, first.Kind, MetricFormatter.Style.Row, first.CurrencyCode);
            return (ValuePrefix ?? "") + MetricFormatter.Number(DisplayedValue, Kind, MetricFormatter.Style.Full, CurrencyCode);
        }
    }

    public string MenuBarValue
    {
        get
        {
            if (!HasData) return ValueText;
            if (Limit is { } limit && limit > 0)
            {
                if (Kind == MetricKind.Percent)
                {
                    var percent = (int)Math.Clamp(Math.Round(DisplayedValue / limit * 100), 0, 100);
                    return $"{percent}%";
                }
                return MetricFormatter.Number(DisplayedValue, Kind, MetricFormatter.Style.Tray, CurrencyCode);
            }
            if (SelectedValues.FirstOrDefault() is { } first)
            {
                if (TraySuffix is not null && first.Kind == MetricKind.Count)
                    return $"{MetricFormatter.Number(first.Number, MetricKind.Count, MetricFormatter.Style.Tray)} {TraySuffix}";
                return MetricFormatter.String(first, MetricFormatter.Style.Tray);
            }
            if (ValueTextOverride is not null) return ValueTextOverride;
            var number = MetricFormatter.Number(DisplayedValue, Kind, MetricFormatter.Style.Tray);
            if (Kind == MetricKind.Count && CountSuffix is not null) return $"{number} {CountSuffix}";
            return number;
        }
    }

    public string Headline
    {
        get
        {
            if (!HasData) return NoDataHeadline;
            if (IsBounded)
            {
                if (ValueTextOverride is not null) return ValueTextOverride;
                return $"{ValueText} {DisplayMode.Label().ToLowerInvariant()}";
            }
            return ValueText;
        }
    }

    public string UnboundedDetail
    {
        get
        {
            if (!HasData) return NoDataSubtitle;
            if (ValueTextOverride is not null) return ValueTextOverride;
            var selected = SelectedValues;
            if (selected.Count == 1)
            {
                var value = selected[0];
                if (value.Kind == MetricKind.Dollars && UnboundedValueWord is not null)
                    return $"{MetricFormatter.Number(value.Number, value.Kind, MetricFormatter.Style.Row, value.CurrencyCode)} {UnboundedValueWord}";
                return MetricFormatter.String(value, MetricFormatter.Style.Row);
            }
            if (selected.Count > 1)
                return string.Join(" · ", selected.Select(v => MetricFormatter.String(v, MetricFormatter.Style.Row)));
            var word = UnboundedValueWord ?? DisplayMode.Label().ToLowerInvariant();
            if (Kind == MetricKind.Count && CountSuffix is not null)
                return $"{ValueText} {CountSuffix} {word}";
            return $"{ValueText} {word}";
        }
    }

    public bool HasMeterStyleToggle => HasData && IsBounded && ValueTextOverride is null;

    public string? MeterStyleTooltip
    {
        get
        {
            if (!HasMeterStyleToggle || Limit is not { } limit) return null;
            var opposite = DisplayMode == WidgetDisplayMode.Remaining ? Used : Math.Max(0, limit - Used);
            var word = (DisplayMode == WidgetDisplayMode.Remaining ? WidgetDisplayMode.Used : WidgetDisplayMode.Remaining)
                .Label().ToLowerInvariant();
            return $"{(ValuePrefix ?? "") + MetricFormatter.Number(opposite, Kind, MetricFormatter.Style.Full, CurrencyCode)} {word}";
        }
    }

    public bool IsFreshSessionWindow(DateTimeOffset now)
    {
        if (SessionStartSignal is null || !HasData || Limit is null || Used > 0) return false;
        return SessionStartSignal switch
        {
            Models.SessionStartSignal.ZeroUsage => ResetsAt is { } reset && now < reset,
            Models.SessionStartSignal.MissingResetDate => ResetsAt is null,
            _ => false
        };
    }

    public bool HasResetLabel(DateTimeOffset now) =>
        HasData && SubtitleOverride is null && ResetsAt is not null && !IsFreshSessionWindow(now);

    public string? BoundedTrailingText(DateTimeOffset now)
    {
        if (!HasData) return NoDataSubtitle;
        if (SubtitleOverride is not null) return SubtitleOverride;
        if (IsFreshSessionWindow(now)) return "Not started";
        if (ResetsAt is { } resetsAt)
        {
            return ResetDisplayMode == ResetDisplayMode.Absolute
                ? Formatters.ResetAbsoluteLabel(resetsAt, now)
                : Formatters.ResetRelativeLabel(resetsAt, now);
        }
        if (PeriodDurationMs is { } period)
        {
            var duration = Formatters.CompactDuration(TimeSpan.FromMilliseconds(period));
            if (duration is not null) return $"Resets in {duration}";
        }
        return Kind switch
        {
            MetricKind.Dollars when Limit is { } limit =>
                $"{(CurrencyCode is null ? Formatters.Currency(limit, limit == Math.Round(limit) ? 0 : 2)
                    : MetricFormatter.Number(limit, Kind, MetricFormatter.Style.Full, CurrencyCode))} {LimitNoun ?? "limit"}",
            MetricKind.Count => CountSuffix,
            _ => null
        };
    }

    public IEnumerable<string> ResetExpiryLabels(DateTimeOffset now)
    {
        if (!HasData || !ShowsResetExpiries) yield break;
        foreach (var group in ExpiriesAt.Where(expiry => expiry > now).Order().GroupBy(expiry => expiry))
        {
            var deadline = Formatters.DeadlineLabel("Expires", group.Key, ResetDisplayMode, now);
            if (deadline is not null)
                yield return group.Count() == 1 ? deadline : $"{group.Count()} × {deadline}";
        }
    }

    public string? ResetTooltip(DateTimeOffset now)
    {
        if (IsFreshSessionWindow(now)) return FreshSessionTooltip;
        if (!HasResetLabel(now) || ResetsAt is not { } resetsAt) return null;
        return ResetDisplayMode == ResetDisplayMode.Absolute
            ? Formatters.ResetRelativeLabel(resetsAt, now)
            : Formatters.ResetAbsoluteLabel(resetsAt, now);
    }

    public MeterState GetMeterState(DateTimeOffset now)
    {
        if (!HasData || Limit is not { } limit || limit <= 0)
            return HasData ? MeterState.Level(MeterSeverity.Normal) : MeterState.NoData;
        if (RoundedAtDisplayPrecision(limit - Used) <= 0) return MeterState.Spent;
        var severity = PriorityLimits.Severity((limit - Used) / limit);
        if (IsFreshSessionWindow(now)) return MeterState.Level(severity);

        if (ResetsAt is { } resetsAt && PeriodDurationMs is { } periodMs and > 0)
        {
            var period = TimeSpan.FromMilliseconds(periodMs);
            var result = Pace.Evaluate(Used, limit, resetsAt, period, now);
            if (result is { } pace)
            {
                switch (pace.Status)
                {
                    case Pace.Status.Ahead:
                        return MeterState.Healthy(pace.ProjectedUsage / limit) with { Severity = severity };
                    case Pace.Status.OnTrack:
                        if (Used / limit < 0.05) return MeterState.Level(severity);
                        var projected = pace.ProjectedUsage / limit;
                        var spare = (int)Math.Round((1 - projected) * 100);
                        if (spare < 1) return MeterState.RunningOut(null, projected);
                        return MeterState.CloseToLimit($"~{spare}% spare", projected) with
                        {
                            Severity = severity == MeterSeverity.Critical ? severity : MeterSeverity.Warning
                        };
                    case Pace.Status.Behind:
                        if (Used / limit < 0.05) return MeterState.Level(severity);
                        var etaSeconds = Pace.SecondsToRunOut(Used, limit, resetsAt, period, now);
                        string? eta = null;
                        if (etaSeconds is { } seconds)
                            eta = Formatters.DeadlineLabel("Limit", now.Add(seconds), ResetDisplayMode, now);
                        return MeterState.RunningOut(eta, pace.ProjectedUsage / limit);
                }
            }
        }
        return MeterState.Level(severity);
    }

    public double? PaceTick(MeterState state, DateTimeOffset now)
    {
        switch (state.Kind)
        {
            case MeterStateKind.Spent:
            case MeterStateKind.NoData:
            case MeterStateKind.Level:
                return null;
            case MeterStateKind.Healthy:
                if (!AlwaysShowPacing) return null;
                break;
        }
        if (Limit is not { } limit || limit <= 0 || ResetsAt is not { } resetsAt || PeriodDurationMs is not { } periodMs || periodMs <= 0)
            return null;
        var period = TimeSpan.FromMilliseconds(periodMs);
        var windowStart = resetsAt - period;
        var elapsed = now - windowStart;
        if (elapsed < Pace.MinimumElapsed(period) || now >= resetsAt) return null;
        var elapsedFraction = Math.Clamp(elapsed / period, 0, 1);
        return DisplayMode == WidgetDisplayMode.Remaining ? 1 - elapsedFraction : elapsedFraction;
    }

    private double RoundedAtDisplayPrecision(double value) => Kind switch
    {
        MetricKind.Percent => Math.Round(value),
        MetricKind.Count => Math.Round(value * 10) / 10,
        MetricKind.Dollars when CurrencyCode is not (null or "USD") => Math.Round(value, MetricFormatter.NonUsdFractionDigits),
        _ => Math.Round(value * 100) / 100
    };

    public static WidgetData Resolve(
        WidgetDescriptor descriptor,
        MetricLine? line,
        WidgetDisplayMode displayMode,
        ResetDisplayMode resetDisplayMode,
        bool alwaysShowPacing,
        PriorityLimits? priorityLimits = null)
    {
        var sample = descriptor.Sample with
        {
            Title = line?.Title ?? descriptor.Sample.Title,
            DisplayMode = displayMode,
            ResetDisplayMode = resetDisplayMode,
            AlwaysShowPacing = alwaysShowPacing,
            PriorityLimits = priorityLimits ?? PriorityLimits.Default,
            HasData = false
        };
        if (line is null) return sample;

        return line switch
        {
            MetricLine.ProgressLine progress => sample with
            {
                HasData = true,
                Used = progress.Used,
                Limit = progress.Limit,
                Kind = progress.Format.MetricKind,
                CurrencyCode = progress.Format.CurrencyCode,
                CountSuffix = progress.Format.Suffix,
                ResetsAt = progress.ResetsAt,
                PeriodDurationMs = progress.PeriodDurationMs ?? sample.PeriodDurationMs
            },
            MetricLine.ValuesLine values => sample with
            {
                HasData = true,
                Limit = null,
                Values = values.Items,
                ExpiriesAt = values.ExpiriesAt ?? [],
                UnknownModels = values.UnknownModels ?? []
            },
            MetricLine.BadgeLine badge => sample with
            {
                HasData = true,
                Limit = null,
                ValueTextOverride = badge.Message,
                SubtitleOverride = badge.Subtitle
            },
            MetricLine.TextLine text => sample with
            {
                HasData = true,
                Limit = null,
                ValueTextOverride = text.Value,
                SubtitleOverride = text.Subtitle
            },
            MetricLine.ChartLine chart => sample with
            {
                HasData = chart.Points.Count > 0,
                IsChart = true,
                ChartPoints = chart.Points,
                ChartNote = chart.Note
            },
            _ => sample
        };
    }
}

public enum MeterSeverity { Normal, Warning, Critical }

public enum MeterStateKind { NoData, Spent, RunningOut, CloseToLimit, Healthy, Level }

public sealed record MeterState(
    MeterStateKind Kind,
    MeterSeverity? Severity,
    string? Spare = null,
    string? Eta = null,
    double ProjectedFraction = 0)
{
    public static MeterState NoData { get; } = new(MeterStateKind.NoData, null);
    public static MeterState Spent { get; } = new(MeterStateKind.Spent, MeterSeverity.Critical);
    public static MeterState RunningOut(string? eta, double projected) =>
        new(MeterStateKind.RunningOut, MeterSeverity.Critical, Eta: eta, ProjectedFraction: projected);
    public static MeterState CloseToLimit(string spare, double projected) =>
        new(MeterStateKind.CloseToLimit, MeterSeverity.Warning, Spare: spare, ProjectedFraction: projected);
    public static MeterState Healthy(double projected) =>
        new(MeterStateKind.Healthy, MeterSeverity.Normal, ProjectedFraction: projected);
    public static MeterState Level(MeterSeverity severity) =>
        new(MeterStateKind.Level, severity);

    public string? Tooltip => Kind switch
    {
        MeterStateKind.NoData or MeterStateKind.Level => null,
        MeterStateKind.Spent => "Limit reached",
        MeterStateKind.Healthy => $"~{(int)Math.Round((1 - ProjectedFraction) * 100)}% left at reset",
        MeterStateKind.CloseToLimit => $"~{(int)Math.Round(ProjectedFraction * 100)}% used at reset",
        MeterStateKind.RunningOut when ProjectedFraction <= 1 => "~100% used at reset",
        MeterStateKind.RunningOut => $"~{Math.Max(1, (int)Math.Round((ProjectedFraction - 1) * 100))}% over limit at reset",
        _ => null
    };
}

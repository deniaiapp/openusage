using System.Text.Json.Nodes;
using OpenUsage.Core.Support;

namespace OpenUsage.Core.Providers.Codex;

/// <summary>
/// Everything a rollout parse carries from one line to the next. Kept separate so a growing file can
/// resume from its last complete line instead of being re-read from the start (#1383).
/// </summary>
public sealed class CodexParseState
{
    public CodexLogParser.RawUsage? PreviousTotals { get; set; }
    public string? CurrentModel { get; set; }
    public bool CurrentTierIsFast { get; set; }
    public bool CurrentTierIsUltrafast { get; set; }
    public bool SawSessionMeta { get; set; }

    /// <summary>A child session replays its parent's history; the gate stays shut until its own first turn.</summary>
    public bool ReplayGateOpen { get; set; } = true;

    /// <summary>The child's creation time (Unix seconds); null means compare against the line's own timestamp.</summary>
    public double? ReplayGateStartedAt { get; set; }

    public CodexParseState Clone() => (CodexParseState)MemberwiseClone();
}

/// <summary>
/// Parses Codex rollout lines: tracks the current model from <c>turn_context</c> and the current
/// service tier from <c>thread_settings_applied</c>, normalizes each <c>token_count</c> into a delta
/// event, and skips a child session's replayed parent history.
/// </summary>
public sealed class CodexLogParser
{
    public CodexLogParser(CodexParseState? state = null) => State = state ?? new CodexParseState();

    public CodexParseState State { get; }

    public CodexLogUsageScanner.Event? Parse(string line)
    {
        if (!line.Contains("\"type\":", StringComparison.Ordinal)) return null;
        var obj = JsonUtil.ParseObject(line);
        if (obj is null) return null;
        var type = JsonUtil.String(obj["type"]);
        var payload = JsonUtil.Object(obj["payload"]);
        var state = State;

        if (type == "turn_context")
        {
            state.CurrentModel = ModelName(payload) ?? state.CurrentModel;
            return null;
        }
        // A child rollout also replays its parent's metadata; only its first metadata is its own.
        if (type == "session_meta" && !state.SawSessionMeta)
        {
            state.SawSessionMeta = true;
            if (IsChild(payload))
            {
                state.ReplayGateOpen = false;
                state.ReplayGateStartedAt = Iso8601.Parse(JsonUtil.String(obj["timestamp"])?.Trim()) is { } created
                    ? Math.Floor(created.ToUnixTimeMilliseconds() / 1000.0)
                    : null;
            }
            return null;
        }
        if (type != "event_msg" || payload is null) return null;
        var payloadType = JsonUtil.String(payload["type"]);
        if (payloadType == "thread_settings_applied")
        {
            // A record without a tier keeps the session's current tier (#1331).
            if (ServiceTier(payload) is { } tier)
            {
                state.CurrentTierIsUltrafast = tier == "ultrafast";
                state.CurrentTierIsFast = tier is "fast" or "priority";
            }
            return null;
        }
        // Replayed task starts retain the parent's older start time; the first live turn opens the gate.
        if (payloadType == "task_started")
        {
            if (!state.ReplayGateOpen && JsonUtil.Number(payload["started_at"]) is { } startedAt)
            {
                var gate = state.ReplayGateStartedAt
                    ?? (Iso8601.Parse(JsonUtil.String(obj["timestamp"])?.Trim()) is { } lineDate
                        ? Math.Floor(lineDate.ToUnixTimeMilliseconds() / 1000.0)
                        : double.PositiveInfinity);
                if (startedAt >= gate) state.ReplayGateOpen = true;
            }
            return null;
        }
        if (payloadType != "token_count") return null;
        var timestamp = Iso8601.Parse(JsonUtil.String(obj["timestamp"]));
        if (timestamp is null) return null;
        var info = JsonUtil.Object(payload["info"]);
        var totals = ReadUsage(JsonUtil.Object(info?["total_token_usage"]));
        // Parent history still seeds cumulative totals, but never becomes chargeable child usage.
        if (!state.ReplayGateOpen)
        {
            if (totals is { } t) state.PreviousTotals = t;
            return null;
        }
        // Codex can repeat a cumulative snapshot without recording another completed turn.
        if (totals is { } next && state.PreviousTotals is { } prev && next.EqualCounts(prev))
            return null;
        RawUsage usage;
        if (ReadUsage(JsonUtil.Object(info?["last_token_usage"])) is { } last)
            usage = last;
        else if (totals is { } totalUsage)
            usage = totalUsage.Subtract(state.PreviousTotals);
        else
            return null;
        if (totals is { } keep) state.PreviousTotals = keep;
        if (usage.Input == 0 && usage.Cached == 0 && usage.Output == 0 && usage.Reasoning == 0) return null;
        var model = ModelName(payload) ?? ModelName(info) ?? state.CurrentModel ?? "gpt-5";
        state.CurrentModel = model;
        return new CodexLogUsageScanner.Event(
            timestamp.Value,
            model,
            model == CodexUsagePricing.ReserveModel ? CodexUsagePricing.ReservePricingModel : null,
            usage.Input,
            Math.Min(usage.Cached, usage.Input),
            usage.Output,
            usage.Reasoning,
            usage.Total == 0 ? usage.Input + usage.Output + usage.Reasoning : usage.Total,
            state.CurrentTierIsFast,
            state.CurrentTierIsUltrafast);
    }

    private static string? ServiceTier(JsonObject payload)
    {
        var candidates = new[]
        {
            JsonUtil.Object(payload["thread_settings"])?["service_tier"],
            JsonUtil.Object(payload["settings"])?["service_tier"],
            payload["service_tier"]
        };
        foreach (var value in candidates)
        {
            var text = JsonUtil.String(value)?.Trim();
            if (!string.IsNullOrEmpty(text)) return text;
        }
        return null;
    }

    private static bool IsChild(JsonObject? payload)
    {
        if (payload is null) return false;
        if (JsonUtil.String(payload["type"]) is "fork" or "thread_spawn") return true;
        return payload["parent_id"] is not null || payload["parentId"] is not null;
    }

    private static string? ModelName(JsonObject? obj)
    {
        if (obj is null) return null;
        var candidates = new[]
        {
            obj["model"], obj["model_name"],
            JsonUtil.Object(obj["metadata"])?["model"], JsonUtil.Object(obj["turn"])?["model"]
        };
        foreach (var value in candidates)
        {
            var text = JsonUtil.String(value)?.Trim();
            if (!string.IsNullOrEmpty(text)) return text;
        }
        return null;
    }

    private static long ToCount(double? value)
    {
        if (value is not { } number || number <= 0 || !double.IsFinite(number)) return 0;
        return (long)Math.Min(number, long.MaxValue);
    }

    public readonly record struct RawUsage(long Input, long Cached, long Output, long Reasoning, long Total)
    {
        public bool EqualCounts(RawUsage other) =>
            Input == other.Input && Cached == other.Cached && Output == other.Output && Reasoning == other.Reasoning;

        public RawUsage Subtract(RawUsage? previous)
        {
            if (previous is not { } p) return this;
            return new RawUsage(
                Math.Max(0, Input - p.Input),
                Math.Max(0, Cached - p.Cached),
                Math.Max(0, Output - p.Output),
                Math.Max(0, Reasoning - p.Reasoning),
                Math.Max(0, Total - p.Total));
        }
    }

    private static RawUsage? ReadUsage(JsonObject? obj)
    {
        if (obj is null) return null;
        long N(string a, string b) => ToCount(JsonUtil.Number(obj[a]) ?? JsonUtil.Number(obj[b]));
        var input = N("input_tokens", "input");
        var cached = N("cached_input_tokens", "cache_read_input_tokens");
        var output = N("output_tokens", "output");
        var reasoning = N("reasoning_output_tokens", "reasoning_tokens");
        var total = ToCount(JsonUtil.Number(obj["total_tokens"]));
        if (input == 0 && cached == 0 && output == 0 && reasoning == 0 && total == 0) return null;
        return new RawUsage(input, cached, output, reasoning, total);
    }
}

using System.Net;
using System.Text;
using System.Text.Json;
using OpenUsage.Core.Models;
using OpenUsage.Core.Stores;
using OpenUsage.Core.Support;

namespace OpenUsage.Core.Services;

public sealed class LocalApiServer : IDisposable
{
    public const int Port = 6736;
    private readonly WidgetDataStore _data;
    private readonly LayoutStore _layout;
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;

    public LocalApiServer(WidgetDataStore data, LayoutStore layout)
    {
        _data = data;
        _layout = layout;
    }

    public void Start()
    {
        try
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
            _listener.Start();
            _cts = new CancellationTokenSource();
            _ = Task.Run(() => LoopAsync(_cts.Token));
            AppLog.Info("api", $"local HTTP API listening on 127.0.0.1:{Port}");
        }
        catch (Exception ex)
        {
            AppLog.Warn("api", $"local HTTP API off (port {Port} unavailable): {ex.Message}");
            _listener = null;
        }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener is { IsListening: true })
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync(); }
            catch { break; }
            _ = Task.Run(() => Handle(context), ct);
        }
    }

    private void Handle(HttpListenerContext context)
    {
        try
        {
            var path = context.Request.Url?.AbsolutePath ?? "/";
            if (path is "/v1/limits" or "/v1/usage" or "/")
            {
                var payload = BuildPayload();
                var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
                var bytes = Encoding.UTF8.GetBytes(json);
                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json; charset=utf-8";
                context.Response.Headers["Access-Control-Allow-Origin"] = "*";
                context.Response.OutputStream.Write(bytes);
            }
            else
            {
                context.Response.StatusCode = 404;
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("api", $"request failed: {ex.Message}");
            try { context.Response.StatusCode = 500; } catch { /* ignore */ }
        }
        finally
        {
            try { context.Response.Close(); } catch { /* ignore */ }
        }
    }

    internal object BuildPayload()
    {
        var providers = _layout.VisibleProviders.Where(runtime => !runtime.Provider.Id.Contains('@')).Select(runtime =>
        {
            var snapshot = _data.Snapshot(runtime.Provider.Id);
            return new
            {
                id = runtime.Provider.Id,
                displayName = runtime.Provider.DisplayName,
                plan = snapshot?.Plan,
                refreshedAt = snapshot?.RefreshedAt,
                warning = snapshot?.Warning,
                metrics = (snapshot?.Lines ?? []).Select(Describe).ToList()
            };
        }).ToList();
        return new
        {
            version = "v1",
            generatedAt = DateTimeOffset.Now,
            providers
        };
    }

    private static object Describe(MetricLine line) => line switch
    {
        MetricLine.ProgressLine p => new
        {
            type = "progress",
            label = p.Label,
            used = p.Used,
            limit = p.Limit,
            format = p.Format.FormatKind.ToString().ToLowerInvariant(),
            resetsAt = p.ResetsAt
        },
        MetricLine.ValuesLine v => new
        {
            type = "values",
            label = v.Label,
            values = v.Items.Select(x => new { x.Number, kind = x.Kind.ToString().ToLowerInvariant(), x.Label })
        },
        MetricLine.BadgeLine b => new { type = "badge", label = b.Label, text = b.Message },
        MetricLine.TextLine t => new { type = "text", label = t.Label, value = t.Value },
        _ => new { type = "unknown", label = line.Label }
    };

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { /* ignore */ }
        try { _listener?.Stop(); } catch { /* ignore */ }
        _listener?.Close();
    }
}

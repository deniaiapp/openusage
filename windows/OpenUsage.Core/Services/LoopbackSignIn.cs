using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Web;
using OpenUsage.Core.Support;

namespace OpenUsage.Core.Services;

internal sealed class LoopbackSignIn : IDisposable
{
    private const int HeaderLimit = 16_384;
    private readonly TcpListener _listener;

    public LoopbackSignIn(bool useLocalhost = false, int port = 0, string path = "/callback")
    {
        _listener = new(IPAddress.Loopback, port);
        _listener.Start();
        var host = useLocalhost ? "localhost" : "127.0.0.1";
        RedirectUri = new Uri($"http://{host}:{((IPEndPoint)_listener.LocalEndpoint).Port}{path}");
    }

    public Uri RedirectUri { get; }

    public async Task<string> ReceiveCodeAsync(string state, CancellationToken ct)
    {
        while (true)
        {
            using var client = await _listener.AcceptTcpClientAsync(ct);
            using var connection = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connection.CancelAfter(TimeSpan.FromSeconds(2));
            try
            {
                var stream = client.GetStream();
                var target = await ReadTargetAsync(stream, connection.Token);
                var callback = RedirectUri.AbsolutePath + "?";
                var query = target?.StartsWith(callback, StringComparison.Ordinal) == true
                    ? HttpUtility.ParseQueryString(target[callback.Length..])
                    : null;
                var matched = query?.GetValues("state") is [var received] && received == state;
                var code = matched && query?.GetValues("error") is null &&
                    query?.GetValues("code") is [var value] && !string.IsNullOrWhiteSpace(value) &&
                    !value.Any(char.IsControl) ? value : null;
                await ReplyAsync(stream, code is not null, connection.Token);
                if (code is not null) return code;
                if (matched) throw new ProviderRequestException("Sign-in was not completed. Try again.");
            }
            catch (IOException) when (!ct.IsCancellationRequested) { }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        }
    }

    private async Task<string?> ReadTargetAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[HeaderLimit];
        var size = 0;
        while (size < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(size), ct);
            if (read == 0) return null;
            var searchFrom = Math.Max(0, size - 3);
            size += read;
            var found = buffer.AsSpan(searchFrom, size - searchFrom).IndexOf("\r\n\r\n"u8);
            if (found < 0) continue;
            var header = Encoding.ASCII.GetString(buffer, 0, searchFrom + found);
            var lines = header.Split("\r\n", StringSplitOptions.None);
            var hosts = lines.Skip(1)
                .Where(line => line.StartsWith("Host:", StringComparison.OrdinalIgnoreCase))
                .Select(line => line[5..].Trim()).ToArray();
            if (hosts is not [var host] || host != RedirectUri.Authority) return null;
            return lines[0].Split(' ') is ["GET", var target, "HTTP/1.1" or "HTTP/1.0"] ? target : null;
        }
        return null;
    }

    private static async Task ReplyAsync(NetworkStream stream, bool accepted, CancellationToken ct)
    {
        var status = accepted ? "200 OK" : "400 Bad Request";
        var body = accepted ? "Sign-in received. Return to OpenUsage." : "This sign-in callback was not accepted.";
        var response = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: text/plain; charset=utf-8\r\n" +
            $"Content-Length: {body.Length}\r\nCache-Control: no-store\r\nReferrer-Policy: no-referrer\r\nConnection: close\r\n\r\n{body}");
        try { await stream.WriteAsync(response, ct); }
        catch (IOException) { }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    public void Dispose() => _listener.Stop();
}

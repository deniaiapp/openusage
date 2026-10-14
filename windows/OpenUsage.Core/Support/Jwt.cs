using System.Text;
using System.Text.Json.Nodes;

namespace OpenUsage.Core.Support;

public static class Jwt
{
    public static JsonObject? Payload(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var parts = token.Split('.');
        if (parts.Length < 2) return null;
        try
        {
            var json = Encoding.UTF8.GetString(Base64Url(parts[1]));
            return JsonUtil.ParseObject(json);
        }
        catch
        {
            return null;
        }
    }

    public static DateTimeOffset? Expiration(string? token)
    {
        var exp = JsonUtil.Number(Payload(token)?["exp"]);
        return exp is { } seconds ? DateTimeOffset.FromUnixTimeSeconds((long)seconds) : null;
    }

    public static string? Subject(string? token) => JsonUtil.String(Payload(token)?["sub"]);

    private static byte[] Base64Url(string input)
    {
        var padded = input.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2: padded += "=="; break;
            case 3: padded += "="; break;
        }
        return Convert.FromBase64String(padded);
    }
}

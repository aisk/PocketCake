using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace PocketCake.Core.Agent;

/// <summary>
/// Request checks for the local MCP endpoint: loopback Host / Origin only (DNS-rebinding guard),
/// plus a bearer token so other local processes and web pages can't drive the pedal.
/// </summary>
public static class AgentAuth
{
    private const string BearerPrefix = "Bearer ";

    /// <summary>A fresh random token: 32 bytes, base64url.</summary>
    public static string NewToken() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    /// <summary>Whether <paramref name="host"/> (without port) and the Origin header (may be empty) are loopback.</summary>
    public static bool IsLocal(string host, string? origin)
    {
        static bool IsLoopbackHost(string h) => h is "127.0.0.1" or "localhost" or "[::1]";

        if (!IsLoopbackHost(host))
        {
            return false;
        }

        return string.IsNullOrEmpty(origin) || (Uri.TryCreate(origin, UriKind.Absolute, out var uri) && IsLoopbackHost(uri.Host));
    }

    /// <summary>Whether the Authorization header carries <paramref name="token"/> as a bearer token (constant-time).</summary>
    public static bool HasToken(string? authHeader, string? token)
    {
        if (string.IsNullOrEmpty(token) || authHeader is null || !authHeader.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var presented = Encoding.UTF8.GetBytes(authHeader[BearerPrefix.Length..].Trim());

        return CryptographicOperations.FixedTimeEquals(presented, Encoding.UTF8.GetBytes(token));
    }

    /// <summary>A request is served only from a loopback Host / Origin and with the right bearer token.</summary>
    public static bool IsAuthorized(string host, string? origin, string? authHeader, string? token) =>
        IsLocal(host, origin) && HasToken(authHeader, token);
}

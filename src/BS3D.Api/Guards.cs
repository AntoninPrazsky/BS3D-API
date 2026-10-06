using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace BS3D.Api;

/// <summary>
/// The player's token (issue #2): 32 random bytes the game generated, which proves "the same install" and nothing
/// else. Only its SHA-256 is stored, and a presented token is compared in constant time.
/// </summary>
public static class Tokens
{
    public static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public static bool Matches(string token, string storedHash) =>
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Hash(token)), Encoding.ASCII.GetBytes(storedHash));

    /// <summary>The bearer token of a request, or null. A token of an implausible length is no token.</summary>
    public static string? FromRequest(HttpRequest request)
    {
        string? header = request.Headers.Authorization;
        if (header == null || !header.StartsWith("Bearer ", StringComparison.Ordinal)) return null;

        string token = header["Bearer ".Length..].Trim();
        return token.Length is >= 20 and <= 200 ? token : null;
    }
}

/// <summary>
/// The client's address as the audit row keeps it (issue #2): salted and hashed, so the log is not a register of
/// addresses. Behind the tunnel every request arrives from loopback; the forwarded-headers middleware has already
/// put <c>CF-Connecting-IP</c> in its place by the time this reads it.
/// </summary>
public sealed class AddressHasher(string salt)
{
    public string Hash(HttpContext context)
    {
        string address = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(salt + "|" + address)))[..16];
    }

    public static string AddressOf(HttpContext context) => context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    /// <summary>
    /// What a note's limits count by (#10): an IPv4 address, or an IPv6 address's /64 — one host is handed a whole /64,
    /// so a limit by the full address is no limit for it.
    /// </summary>
    public static string NetworkOf(HttpContext context)
    {
        System.Net.IPAddress? address = context.Connection.RemoteIpAddress;
        if (address == null) return "unknown";
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6) return address.ToString();

        byte[] bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return new System.Net.IPAddress(bytes) + "/64";
    }
}

/// <summary>
/// Sliding one-minute windows, by address and by player (issue #2), in memory: a restart forgets them, which costs a
/// burst at most. On the clock the service is given, so the tests can move it.
/// </summary>
public sealed class RateLimits(TimeProvider clock)
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> _seen = new();

    /// <summary>Records an attempt under <paramref name="key"/>; false with the wait when the window is full.</summary>
    public bool TryTake(string key, int perMinute, out TimeSpan retryAfter) => TryTake(key, perMinute, Window, out retryAfter);

    /// <summary>
    /// The same over a window of its own (a note's day, #10). A key belongs to one window: the notes use keys of their
    /// own, so a day's queue never meets a minute's.
    /// </summary>
    public bool TryTake(string key, int limit, TimeSpan window, out TimeSpan retryAfter)
    {
        DateTimeOffset now = clock.GetUtcNow();
        Queue<DateTimeOffset> times = _seen.GetOrAdd(key, _ => new Queue<DateTimeOffset>());

        lock (times)
        {
            while (times.Count > 0 && now - times.Peek() >= window) times.Dequeue();

            if (times.Count >= limit)
            {
                retryAfter = window - (now - times.Peek());
                return false;
            }

            times.Enqueue(now);
            retryAfter = TimeSpan.Zero;
            return true;
        }
    }
}

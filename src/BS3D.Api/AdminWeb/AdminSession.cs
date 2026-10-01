using System.Security.Cryptography;
using System.Text;

namespace BS3D.Api.AdminWeb;

/// <summary>
/// Who may see the admin page (issue #5, requirement 7). Starting it is the login: being <c>rdt</c> on the Pi. It then
/// prints one link carrying a key — 32 random bytes, good once, for <see cref="AdminWebOptions.LinkLifetime"/>, void
/// after <see cref="AdminWebOptions.MaxWrongKeys"/> wrong ones — and the link opens the run's one session, a cookie of
/// another 32 random bytes. Both are held only as SHA-256 digests and compared in constant time. Nothing outlives the
/// process: a new run is a new key and a new session.
/// </summary>
public sealed class AdminSession(AdminWebOptions options)
{
    private readonly object _gate = new();
    private byte[]? _keyDigest;
    private DateTimeOffset _keyExpires;
    private int _wrongKeys;
    private byte[]? _sessionDigest;
    private DateTimeOffset _lastSeen;

    public const string Cookie = "bs3d-admin";

    /// <summary>A fresh key, replacing any earlier one. Only the digest is kept.</summary>
    public string IssueKey()
    {
        string key = NewSecret();
        lock (_gate)
        {
            _keyDigest = Digest(key);
            _keyExpires = options.Clock.GetUtcNow() + options.LinkLifetime;
            _wrongKeys = 0;
        }
        return key;
    }

    /// <summary>The session's cookie value when <paramref name="key"/> is the live key, which it then stops being.</summary>
    public string? TryLogin(string? key)
    {
        lock (_gate)
        {
            if (_keyDigest == null || options.Clock.GetUtcNow() > _keyExpires) return null;
            if (string.IsNullOrEmpty(key) || !CryptographicOperations.FixedTimeEquals(Digest(key), _keyDigest))
            {
                if (++_wrongKeys >= options.MaxWrongKeys) _keyDigest = null;
                return null;
            }
            _keyDigest = null;
            string session = NewSecret();
            _sessionDigest = Digest(session);
            _lastSeen = options.Clock.GetUtcNow();
            return session;
        }
    }

    /// <summary>Whether <paramref name="cookie"/> is this run's session; <paramref name="activity"/> resets the idle clock.</summary>
    public bool IsSession(string? cookie, bool activity)
    {
        lock (_gate)
        {
            if (_sessionDigest == null || string.IsNullOrEmpty(cookie)) return false;
            if (!CryptographicOperations.FixedTimeEquals(Digest(cookie), _sessionDigest)) return false;
            if (activity) _lastSeen = options.Clock.GetUtcNow();
            return true;
        }
    }

    /// <summary>How long the session has gone without activity, or null before anyone logged in.</summary>
    public TimeSpan? Idle()
    {
        lock (_gate) return _sessionDigest == null ? null : options.Clock.GetUtcNow() - _lastSeen;
    }

    private static string NewSecret() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Digest(string secret) => SHA256.HashData(Encoding.UTF8.GetBytes(secret));
}

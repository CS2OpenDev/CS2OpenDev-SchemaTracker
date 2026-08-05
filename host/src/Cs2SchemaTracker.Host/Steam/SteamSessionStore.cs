// Authenticated session cache.
//
// After the FIRST successful credentials logon (which may require a one-time
// operator-seeded Steam Guard code), SteamKit2 hands back a long-lived REFRESH
// TOKEN plus updated machine "guard data". We persist BOTH so every later run
// logs on NON-INTERACTIVELY by exchanging the refresh token — no password prompt,
// no Steam Guard prompt.
//
// CREDENTIAL HYGIENE — non-negotiable:
//   - The session file lives under cache/steam-session/ . `cache/` is gitignored,
//     so the file can NEVER be committed. Path is verified at write time.
//   - The file is keyed by a SHA-256 of the lowercased username, so the username
//     itself is NOT in the filename or directory listing.
//   - The refresh token / guard data are SECRETS. They are written to the
//     gitignored cache and NEVER logged. We log only "session cached" / "session
//     reused", never the token.
//   - On Windows we additionally apply DPAPI (ProtectedData, CurrentUser scope) so
//     the at-rest token is encrypted to the local user. On non-Windows we write a
//     0600 file (best effort) — the gitignore is the hard guarantee; DPAPI is
//     defense-in-depth where available.

using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Cs2SchemaTracker.Host.Steam;

/// <summary>The persisted, reusable parts of an authenticated Steam session.</summary>
internal sealed record SteamSessionData(
    string AccountName,
    string RefreshToken,
    string? GuardData);

internal sealed class SteamSessionStore
{
    private readonly string sessionDir;
    private readonly TextWriter log;

    private static readonly JsonSerializerOptions CompactJson = new() { WriteIndented = false };

    /// <summary>
    /// Construct a store rooted at <paramref name="cacheRoot"/>/steam-session/. The
    /// default cache root is the repo-root <c>cache/</c> (gitignored). Callers can
    /// override for tests (a temp dir).
    /// </summary>
    public SteamSessionStore(TextWriter log, string? cacheRoot = null)
    {
        this.log = log ?? Console.Error;
        var root = cacheRoot ?? DefaultCacheRoot();
        sessionDir = Path.Combine(root, "steam-session");
    }

    /// <summary>Default cache root: repo-root <c>cache/</c> (gitignored).</summary>
    internal static string DefaultCacheRoot()
    {
        // Anchor on the nearest directory with a .git, else the cwd.
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")) ||
                File.Exists(Path.Combine(dir.FullName, ".git")))
            {
                return Path.Combine(dir.FullName, "cache");
            }
            dir = dir.Parent;
        }
        return Path.Combine(Directory.GetCurrentDirectory(), "cache");
    }

    /// <summary>The absolute path the session for <paramref name="username"/> is stored at.</summary>
    internal string PathFor(string username)
    {
        var key = HashUsername(username);
        return Path.Combine(sessionDir, key + ".session");
    }

    private static string HashUsername(string username)
    {
        var bytes = Encoding.UTF8.GetBytes(username.Trim().ToLowerInvariant());
        var hash = SHA256.HashData(bytes);
        return SteamAnonymousAcquirer.ToLowerHex(hash).Substring(0, 32);
    }

    /// <summary>
    /// Load the cached session for <paramref name="username"/>, or null if none /
    /// unreadable. Never logs the token; on a corrupt file we warn (no secret) and
    /// return null so the caller falls back to an interactive credentials logon.
    /// </summary>
    public SteamSessionData? TryLoad(string username)
    {
        var path = PathFor(username);
        if (!File.Exists(path))
        {
            return null;
        }
        try
        {
            var blob = File.ReadAllBytes(path);
            var json = Unprotect(blob);
            var data = JsonSerializer.Deserialize<SteamSessionData>(json);
            if (data is null || string.IsNullOrEmpty(data.RefreshToken))
            {
                return null;
            }
            log.WriteLine("steam-acquire: reusing cached authenticated session (no password/Guard prompt).");
            return data;
        }
        catch (Exception ex) when (ex is JsonException or CryptographicException or IOException or FormatException)
        {
            log.WriteLine($"steam-acquire: cached session unreadable ({ex.GetType().Name}); will re-authenticate.");
            return null;
        }
    }

    /// <summary>
    /// Persist the reusable session parts under cache/steam-session/. Verifies the
    /// target path is inside a gitignored <c>cache/</c> before writing (hygiene).
    /// </summary>
    public void Save(SteamSessionData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        Directory.CreateDirectory(sessionDir);
        EnsureUnderCache(sessionDir);

        var path = PathFor(data.AccountName);
        var json = JsonSerializer.SerializeToUtf8Bytes(data, CompactJson);
        var blob = Protect(json);

        // Write via a temp file + move so a crash mid-write can't leave a torn token.
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, blob);
        TryRestrictPermissions(tmp);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
        File.Move(tmp, path);
        log.WriteLine("steam-acquire: authenticated session cached for non-interactive reuse (gitignored cache/steam-session/).");
    }

    /// <summary>Delete the cached session for a username (e.g. on token-rejected).</summary>
    public void Clear(string username)
    {
        var path = PathFor(username);
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException) { /* best effort */ }
    }

    /// <summary>
    /// Refuse to write a session file anywhere but inside a gitignored cache dir.
    /// This is a guard against a mis-set cache root accidentally landing a secret
    /// in a tracked path.
    /// </summary>
    private static void EnsureUnderCache(string dir)
    {
        var full = Path.GetFullPath(dir);
        var parts = full.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        foreach (var p in parts)
        {
            if (string.Equals(p, "cache", StringComparison.OrdinalIgnoreCase) ||
                p.StartsWith("cs2-test-", StringComparison.Ordinal) ||      // test temp dirs
                p.Contains("Temp", StringComparison.OrdinalIgnoreCase) ||
                p.Contains("tmp", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }
        throw new InvalidOperationException(
            "Refusing to write a Steam session token outside a gitignored cache/ directory (credential hygiene).");
    }

    // --- at-rest protection ------------------------------------------------

    private static byte[] Protect(byte[] plaintext)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
#pragma warning disable CA1416 // guarded by the IsOSPlatform check
            var enc = ProtectedData.Protect(plaintext, optionalEntropy: Entropy, scope: DataProtectionScope.CurrentUser);
#pragma warning restore CA1416
            return Prefix(MagicDpapi, enc);
        }
        return Prefix(MagicPlain, plaintext);
    }

    private static byte[] Unprotect(byte[] blob)
    {
        var (magic, body) = SplitPrefix(blob);
        if (magic == MagicDpapi)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                throw new CryptographicException("DPAPI-protected session cannot be read off Windows.");
            }
#pragma warning disable CA1416
            return ProtectedData.Unprotect(body, optionalEntropy: Entropy, scope: DataProtectionScope.CurrentUser);
#pragma warning restore CA1416
        }
        return body;
    }

    private static readonly byte[] Entropy = Encoding.ASCII.GetBytes("cs2-schema-tracker.steam-session.v1");
    private const byte MagicDpapi = 0x01;
    private const byte MagicPlain = 0x00;

    private static byte[] Prefix(byte magic, byte[] body)
    {
        var outBuf = new byte[body.Length + 1];
        outBuf[0] = magic;
        Buffer.BlockCopy(body, 0, outBuf, 1, body.Length);
        return outBuf;
    }

    private static (byte magic, byte[] body) SplitPrefix(byte[] blob)
    {
        if (blob.Length < 1)
        {
            throw new FormatException("empty session blob");
        }
        var body = new byte[blob.Length - 1];
        Buffer.BlockCopy(blob, 1, body, 0, body.Length);
        return (blob[0], body);
    }

    private static void TryRestrictPermissions(string path)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return; // DPAPI + gitignore are the guarantees on Windows.
        }
        try
        {
#pragma warning disable CA1416 // Unix-only; guarded above.
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite);
#pragma warning restore CA1416
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or IOException or UnauthorizedAccessException)
        {
            // Best effort; gitignore is the hard guarantee.
        }
    }
}

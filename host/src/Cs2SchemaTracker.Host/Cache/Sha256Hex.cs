// content-addressing key helpers (lowercase-hex SHA-256).
//
// One chokepoint for SHA-256 hex computation + validation so the cache, the
// fetch command, and the populate path all agree on the exact key form that
// provenance.json's InputBinary.sha256 uses (lowercase hex, no separators).

using System.Security.Cryptography;

namespace Cs2SchemaTracker.Host.Cache;

internal static class Sha256Hex
{
    /// <summary>Lowercase-hex SHA-256 of <paramref name="bytes"/>.</summary>
    public static string Of(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    /// <summary>Lowercase-hex SHA-256 streamed from a file (no full-file buffer).</summary>
    public static string OfFile(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
    }

    /// <summary>
    /// Returns a normalized (lowercase, trimmed) key if <paramref name="candidate"/>
    /// is a syntactically valid 64-char hex SHA-256; otherwise throws
    /// <see cref="ArgumentException"/> (fail-loud — a malformed key is a usage error,
    /// never a silent miss).
    /// </summary>
    public static string Validate(string candidate)
    {
        ArgumentException.ThrowIfNullOrEmpty(candidate);
        var key = candidate.Trim().ToLowerInvariant();
        if (key.Length != 64 || !IsHex(key))
        {
            throw new ArgumentException(
                $"Not a valid SHA-256 (expected 64 lowercase hex chars): '{candidate}'.", nameof(candidate));
        }
        return key;
    }

    private static bool IsHex(string s)
    {
        foreach (var c in s)
        {
            var ok = c is >= '0' and <= '9' or >= 'a' and <= 'f';
            if (!ok)
                return false;
        }
        return true;
    }
}

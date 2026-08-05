// Credential resolution + auth-mode selection.
//
// The intent is "limit the need for credentials": anonymous stays
// the DEFAULT (current-build / forward-capture, which works fully without any
// account). Authenticated mode is used ONLY when needed — explicitly via --auth,
// or AUTO-selected for the historical/explicit-manifest path when credentials are
// present (because anonymous Steam only issues a manifest request code for the
// CURRENT manifest; historical manifests require a CS2-owning account).
//
// CREDENTIAL HYGIENE: this type holds the username + password in memory only for
// the duration of a logon. It NEVER overrides ToString (so it can't be
// accidentally logged), exposes the password only to the SteamKit2 auth call, and
// is read from the environment (populated by DotEnv from the gitignored .env).
// Nothing here writes the secret anywhere.

namespace Cs2SchemaTracker.Host.Steam;

/// <summary>How a Steam session should authenticate for a given acquire/probe.</summary>
internal enum SteamAuthMode
{
    /// <summary>Anonymous logon (free-to-play). No account. Default.</summary>
    Anonymous,

    /// <summary>Authenticated logon with username/password (+ cached session reuse).</summary>
    Authenticated,
}

/// <summary>
/// Resolved credential material for an authenticated logon. Never logged; the
/// password is consumed only by the SteamKit2 auth call. A one-time Steam Guard
/// code (operator-seeded) may accompany the first interactive logon.
/// </summary>
internal sealed class SteamCredentials
{
    public required string Username { get; init; }
    public required string Password { get; init; }

    /// <summary>
    /// Optional one-time Steam Guard code (STEAM_GUARD_CODE / --guard-code) to seed
    /// the FIRST authenticated logon when no cached session exists. Null on reuse.
    /// </summary>
    public string? GuardCode { get; init; }

    // Standard env var names (documented; values never appear in code/logs).
    public const string UsernameVar = "STEAM_USERNAME";
    public const string PasswordVar = "STEAM_PASSWORD";
    public const string GuardCodeVar = "STEAM_GUARD_CODE";

    /// <summary>
    /// Resolve credentials from the environment (already populated by
    /// <see cref="DotEnv.LoadFromRepoRoot"/>). Returns null when either the
    /// username or password is missing/blank — callers decide whether that is a
    /// fatal error (explicit --auth) or a silent "stay anonymous" (auto path).
    /// </summary>
    public static SteamCredentials? FromEnvironment(string? guardCodeOverride = null)
    {
        var user = DotEnv.GetSecret(UsernameVar);
        var pass = DotEnv.GetSecret(PasswordVar);
        if (user is null || pass is null)
        {
            return null;
        }
        var guard = guardCodeOverride ?? DotEnv.GetSecret(GuardCodeVar);
        return new SteamCredentials
        {
            Username = user,
            Password = pass,
            GuardCode = string.IsNullOrWhiteSpace(guard) ? null : guard,
        };
    }

    /// <summary>True iff both username and password are present in the environment.</summary>
    public static bool AvailableInEnvironment()
        => DotEnv.GetSecret(UsernameVar) is not null && DotEnv.GetSecret(PasswordVar) is not null;

    // Defense in depth: refuse to leak the secret via string interpolation / logging.
    public override string ToString() => $"SteamCredentials(user=<redacted>, password=<redacted>)";
}

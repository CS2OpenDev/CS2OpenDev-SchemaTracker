// Fail-loud Steam Guard signal.
//
// Thrown when an authenticated logon REQUIRES a Steam Guard code but none is
// available non-interactively (no cached session, no STEAM_GUARD_CODE / --guard-code).
// The CLI maps this to a non-zero exit and prints the EXACT one-time
// command the operator must run to seed the session. The message never contains a
// secret.

namespace Cs2SchemaTracker.Host.Steam;

/// <summary>The kind of Steam Guard challenge the account presented.</summary>
internal enum SteamGuardKind
{
    /// <summary>Mobile authenticator / TOTP device code.</summary>
    DeviceCode,

    /// <summary>Email one-time code.</summary>
    EmailCode,

    /// <summary>Mobile-app device confirmation (approve-on-phone).</summary>
    DeviceConfirmation,
}

internal sealed class SteamGuardRequiredException : Exception
{
    public SteamGuardKind Kind { get; }

    public SteamGuardRequiredException(SteamGuardKind kind, string message)
        : base(message)
    {
        Kind = kind;
    }
}

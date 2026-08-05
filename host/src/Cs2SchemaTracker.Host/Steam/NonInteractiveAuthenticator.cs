// Non-interactive SteamKit2 IAuthenticator.
//
// SteamKit2's credentials auth flow calls back into an IAuthenticator when the
// account needs a Steam Guard code. This implementation is NON-INTERACTIVE by
// design (the host runs in CI / unattended): it supplies a ONE-TIME operator-seeded
// code (STEAM_GUARD_CODE / --guard-code) exactly once, and otherwise FAILS LOUD
// with a SteamGuardRequiredException rather than blocking on a console prompt.
//
// Why no Console.ReadLine: a CI runner has no TTY; a silent hang is the opposite of
// fail-loud. When a code is genuinely required and none was seeded, the
// operator must run the host once with --guard-code <code> (or STEAM_GUARD_CODE)
// to mint + cache the durable session; thereafter the cached refresh token logs on
// with no code at all.
//
// CREDENTIAL HYGIENE: the seeded code is consumed once and never logged.

using SteamKit2.Authentication;

namespace Cs2SchemaTracker.Host.Steam;

internal sealed class NonInteractiveAuthenticator : IAuthenticator
{
    private readonly string? seededCode;
    private bool codeConsumed;

    public NonInteractiveAuthenticator(string? seededCode)
    {
        this.seededCode = string.IsNullOrWhiteSpace(seededCode) ? null : seededCode.Trim();
    }

    public Task<string> GetDeviceCodeAsync(bool previousCodeWasIncorrect)
        => ProvideCodeOrFail(SteamGuardKind.DeviceCode, previousCodeWasIncorrect);

    public Task<string> GetEmailCodeAsync(string email, bool previousCodeWasIncorrect)
        => ProvideCodeOrFail(SteamGuardKind.EmailCode, previousCodeWasIncorrect);

    public Task<bool> AcceptDeviceConfirmationAsync()
    {
        // Device confirmation = "approve this login on your phone". We cannot do
        // that unattended. Refuse it so SteamKit2 falls through to a code path (or
        // we fail loud). Returning false tells SteamKit2 NOT to wait on a phone tap.
        return Task.FromResult(false);
    }

    private Task<string> ProvideCodeOrFail(SteamGuardKind kind, bool previousWasIncorrect)
    {
        if (seededCode is not null && !codeConsumed && !previousWasIncorrect)
        {
            codeConsumed = true;
            return Task.FromResult(seededCode);
        }

        var what = kind == SteamGuardKind.EmailCode ? "email" : "mobile-authenticator (TOTP)";
        var reason = previousWasIncorrect
            ? $"the seeded Steam Guard {what} code was REJECTED"
            : $"this account requires a Steam Guard {what} code and none was seeded";

        throw new SteamGuardRequiredException(kind,
            $"authenticated logon blocked — {reason}. " +
            "Seed a fresh code once via STEAM_GUARD_CODE=<code> (or --guard-code <code>); " +
            "a durable session is then cached for non-interactive reuse.");
    }
}

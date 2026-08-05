// Credential resolution + hygiene tests.
//
// Verifies:
//   - FromEnvironment resolves username/password from the process env
//   - FromEnvironment returns null when either is missing (auto path stays anon)
//   - guard-code override beats STEAM_GUARD_CODE
//   - ToString NEVER leaks the password (anti-logging defense)
//
// CREDENTIAL HYGIENE: uses synthetic values, sets+clears them; never references a
// real account secret.

using System;

using Cs2SchemaTracker.Host.Steam;

using Xunit;

namespace Cs2SchemaTracker.Tests.Steam;

[Collection("env-mutating")]
public class SteamCredentialsTest
{
    [Fact]
    public void FromEnvironment_resolves_user_and_pass()
    {
        WithEnv("tester", "pw123", null, () =>
        {
            var creds = SteamCredentials.FromEnvironment();
            Assert.NotNull(creds);
            Assert.Equal("tester", creds!.Username);
            Assert.Equal("pw123", creds.Password);
            Assert.Null(creds.GuardCode);
        });
    }

    [Fact]
    public void FromEnvironment_null_when_password_missing()
    {
        WithEnv("tester", null, null, () =>
        {
            Assert.Null(SteamCredentials.FromEnvironment());
            Assert.False(SteamCredentials.AvailableInEnvironment());
        });
    }

    [Fact]
    public void Guard_code_override_beats_env()
    {
        WithEnv("tester", "pw", "FROMENV", () =>
        {
            var creds = SteamCredentials.FromEnvironment(guardCodeOverride: "FROMARG");
            Assert.Equal("FROMARG", creds!.GuardCode);
        });
    }

    [Fact]
    public void ToString_does_not_leak_password()
    {
        var creds = new SteamCredentials { Username = "u", Password = "TOPSECRET" };
        Assert.DoesNotContain("TOPSECRET", creds.ToString(), StringComparison.Ordinal);
        Assert.Contains("redacted", creds.ToString(), StringComparison.Ordinal);
    }

    private static void WithEnv(string? user, string? pass, string? guard, Action body)
    {
        var oldUser = Environment.GetEnvironmentVariable(SteamCredentials.UsernameVar);
        var oldPass = Environment.GetEnvironmentVariable(SteamCredentials.PasswordVar);
        var oldGuard = Environment.GetEnvironmentVariable(SteamCredentials.GuardCodeVar);
        try
        {
            Environment.SetEnvironmentVariable(SteamCredentials.UsernameVar, user);
            Environment.SetEnvironmentVariable(SteamCredentials.PasswordVar, pass);
            Environment.SetEnvironmentVariable(SteamCredentials.GuardCodeVar, guard);
            body();
        }
        finally
        {
            Environment.SetEnvironmentVariable(SteamCredentials.UsernameVar, oldUser);
            Environment.SetEnvironmentVariable(SteamCredentials.PasswordVar, oldPass);
            Environment.SetEnvironmentVariable(SteamCredentials.GuardCodeVar, oldGuard);
        }
    }
}

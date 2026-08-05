// --auth CLI argument-path tests (fail-loud).
//
// These exercise the real acquirer-construction seam (acquirerFactory == null) for
// the auth-mode decision WITHOUT any Steam contact:
//   - --auth with NO credentials in the env exits 64 (fail loud, no Steam call)
//   - --probe --auth with NO credentials exits 64 (same)
//
// The successful authenticated path itself requires a live Steam account and is
// validated out-of-band by the operator probe (reported separately), not here.
//
// CREDENTIAL HYGIENE: this test asserts the credentials are ABSENT and the command
// refuses; it never sets a real secret.

using System;
using System.Threading.Tasks;

using Cs2SchemaTracker.Host.Cli;
using Cs2SchemaTracker.Host.Steam;

using Xunit;

namespace Cs2SchemaTracker.Tests.Steam;

[Collection("env-mutating")]
public class AcquireAuthArgsTest
{
    private static readonly string[] AuthCurrentArgs =
        { "--build", "latest", "--platform", "windows-x86_64", "--auth" };

    private static readonly string[] AuthProbeArgs =
        { "--probe", "--auth", "--build", "23669931", "--platform", "windows-x86_64" };

    [Fact]
    public async Task Auth_without_credentials_exits_64_without_steam()
    {
        // acquirerFactory == null forces the real BuildRealAcquirer path, which must
        // reject --auth-with-no-creds at exit 64 BEFORE any Steam contact.
        var code = await RunWithoutCreds(AuthCurrentArgs);
        Assert.Equal(64, code);
    }

    [Fact]
    public async Task Auth_flag_on_probe_without_credentials_exits_64()
    {
        var code = await RunWithoutCreds(AuthProbeArgs);
        Assert.Equal(64, code);
    }

    private static async Task<int> RunWithoutCreds(string[] args)
    {
        var oldUser = Environment.GetEnvironmentVariable(SteamCredentials.UsernameVar);
        var oldPass = Environment.GetEnvironmentVariable(SteamCredentials.PasswordVar);
        try
        {
            Environment.SetEnvironmentVariable(SteamCredentials.UsernameVar, null);
            Environment.SetEnvironmentVariable(SteamCredentials.PasswordVar, null);
            return await AcquireCommand.RunAsync(args, acquirerFactory: null);
        }
        finally
        {
            Environment.SetEnvironmentVariable(SteamCredentials.UsernameVar, oldUser);
            Environment.SetEnvironmentVariable(SteamCredentials.PasswordVar, oldPass);
        }
    }
}

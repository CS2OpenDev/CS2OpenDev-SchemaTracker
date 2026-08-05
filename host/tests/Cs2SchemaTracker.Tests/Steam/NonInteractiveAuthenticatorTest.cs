// Non-interactive Steam Guard authenticator tests (fail-loud).
//
// The authenticator must:
//   - supply a seeded code exactly ONCE for a device/email challenge
//   - FAIL LOUD (SteamGuardRequiredException) when no code is seeded
//   - FAIL LOUD when a previously-supplied code was rejected (no infinite retry)
//   - refuse device-confirmation (approve-on-phone) since CI has no phone

using System.Threading.Tasks;

using Cs2SchemaTracker.Host.Steam;

using Xunit;

namespace Cs2SchemaTracker.Tests.Steam;

public class NonInteractiveAuthenticatorTest
{
    [Fact]
    public async Task Supplies_seeded_device_code_once()
    {
        var auth = new NonInteractiveAuthenticator("ABCDE");
        var code = await auth.GetDeviceCodeAsync(previousCodeWasIncorrect: false);
        Assert.Equal("ABCDE", code);
    }

    [Fact]
    public async Task Fails_loud_when_no_code_seeded()
    {
        var auth = new NonInteractiveAuthenticator(seededCode: null);
        var ex = await Assert.ThrowsAsync<SteamGuardRequiredException>(
            () => auth.GetDeviceCodeAsync(previousCodeWasIncorrect: false));
        Assert.Equal(SteamGuardKind.DeviceCode, ex.Kind);
        Assert.Contains("none was seeded", ex.Message);
    }

    [Fact]
    public async Task Fails_loud_when_seeded_code_rejected()
    {
        var auth = new NonInteractiveAuthenticator("WRONG");
        var ex = await Assert.ThrowsAsync<SteamGuardRequiredException>(
            () => auth.GetEmailCodeAsync("a@b.c", previousCodeWasIncorrect: true));
        Assert.Equal(SteamGuardKind.EmailCode, ex.Kind);
        Assert.Contains("REJECTED", ex.Message);
    }

    [Fact]
    public async Task Does_not_reuse_seeded_code_after_first_consumption()
    {
        var auth = new NonInteractiveAuthenticator("ONCE");
        _ = await auth.GetDeviceCodeAsync(false);
        // Second challenge (e.g. a different guard type) must fail loud, not re-send.
        await Assert.ThrowsAsync<SteamGuardRequiredException>(
            () => auth.GetDeviceCodeAsync(false));
    }

    [Fact]
    public async Task Refuses_device_confirmation()
    {
        var auth = new NonInteractiveAuthenticator("ABCDE");
        Assert.False(await auth.AcceptDeviceConfirmationAsync());
    }
}

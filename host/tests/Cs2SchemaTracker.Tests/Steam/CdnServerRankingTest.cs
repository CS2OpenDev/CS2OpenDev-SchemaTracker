// CDN server ranking + rotation tests.
//
// Background: a live windows-x86_64 acquire connected, did anonymous
// logon, resolved the windows binary depot, then died at the CDN download with
// "No such host is known. (cache1-blv2.valve.org:443)". The SteamPipe content
// directory for CellID=31 had handed back a Valve-INTERNAL host that does not
// resolve from the public internet, and the old acquirer picked exactly ONE
// server by lowest host ordinal ('b' < 'i' -> cache1-blv2.valve.org) with NO
// failover, so the whole ~1.7 GB download died.
//
// The fix ranks ALL returned servers so public *.steamcontent.com hosts are
// tried first and internal/other hosts only as later failover candidates, and
// fails over on transport/DNS errors. Output bytes are identical regardless of
// which mirror serves a chunk (every chunk is SHA-1-verified against the
// manifest), so server choice does not affect determinism.
//
// The live failover control-flow needs a real CDN and stays under the
// CS2_ACQUIRE_INTEGRATION-gated end-to-end test. These unit tests cover the
// pure ranking/ordering logic and the empty-list guard in isolation.

using Cs2SchemaTracker.Host.Steam;

using SteamKit2.CDN;

using Xunit;

namespace Cs2SchemaTracker.Tests.Steam;

public class CdnServerRankingTest
{
    private static readonly string?[] PublicBeforeValveExpected =
        { "cache1-iad1.steamcontent.com", "cache1-blv2.valve.org" };

    private static readonly string?[] GroupedExpected =
    {
        "cache1-iad1.steamcontent.com",
        "cache9-fra.steamcontent.com",
        "another-internal.valve.org",
        "cache1-blv2.valve.org",
    };

    // ---- HostRankKey: public CDN preferred, internal/other deprioritized ----

    [Theory]
    [InlineData("cache1-iad1.steamcontent.com", 0)]
    [InlineData("CACHE9-FRA.STEAMCONTENT.COM", 0)] // case-insensitive suffix match
    [InlineData("cache1-blv2.valve.org", 1)]        // the offending internal host
    [InlineData("some-random-host.example", 1)]
    [InlineData("", 2)]
    [InlineData(null, 2)]
    public void HostRankKey_classifies_public_internal_and_empty(string? host, int expectedRank)
    {
        Assert.Equal(expectedRank, SteamAnonymousAcquirer.Session.HostRankKey(host));
    }

    // ---- RankHosts: deterministic ordering with steamcontent first ----

    [Fact]
    public void RankHosts_puts_steamcontent_host_before_valve_org_host()
    {
        // The exact regression scenario: the internal valve.org host sorts
        // BEFORE the public steamcontent host by raw ordinal ('b' < 'i'), which
        // is precisely why the old single-pick chose the unreachable one. The
        // ranked list must invert that: public CDN first.
        var input = new[]
        {
            "cache1-blv2.valve.org",
            "cache1-iad1.steamcontent.com",
        };
        var ranked = SteamAnonymousAcquirer.Session.RankHosts(input);

        Assert.Equal(PublicBeforeValveExpected, ranked);
    }

    [Fact]
    public void RankHosts_groups_all_steamcontent_first_then_others_ordinal_within_groups()
    {
        var input = new[]
        {
            "cache9-fra.steamcontent.com",
            "cache1-blv2.valve.org",
            "cache1-iad1.steamcontent.com",
            "another-internal.valve.org",
        };

        var ranked = SteamAnonymousAcquirer.Session.RankHosts(input);

        // Group 0 (steamcontent) ordinal-sorted, then group 1 (others) ordinal-sorted.
        Assert.Equal(GroupedExpected, ranked);
    }

    [Fact]
    public void RankHosts_is_deterministic_for_same_input_regardless_of_initial_order()
    {
        var a = new[] { "z.steamcontent.com", "a.valve.org", "a.steamcontent.com", "b.other" };
        var b = new[] { "b.other", "a.steamcontent.com", "z.steamcontent.com", "a.valve.org" };

        var rankedA = SteamAnonymousAcquirer.Session.RankHosts(a);
        var rankedB = SteamAnonymousAcquirer.Session.RankHosts(b);

        Assert.Equal(rankedA, rankedB);
    }

    [Fact]
    public void RankHosts_does_not_drop_non_steamcontent_hosts()
    {
        // Non-steamcontent hosts must be DEPRIORITIZED, not excluded — the
        // directory contents vary and failover must still be able to reach them.
        var input = new[] { "only-internal.valve.org", "another.example.net" };

        var ranked = SteamAnonymousAcquirer.Session.RankHosts(input);

        Assert.Equal(input.Length, ranked.Count);
        Assert.Contains("only-internal.valve.org", ranked);
        Assert.Contains("another.example.net", ranked);
    }

    [Fact]
    public void RankServers_orders_underlying_server_objects_when_constructible()
    {
        // Server has internal ctor/setter in SteamKit2; only run the Server-typed
        // path when we can actually build instances with custom hosts. The
        // host-string ordering itself is already covered by RankHosts tests.
        var valveServer = TryMakeServer("cache1-blv2.valve.org");
        var publicServer = TryMakeServer("cache1-iad1.steamcontent.com");
        if (valveServer is null || publicServer is null)
        {
            return; // environment can't construct Server; ranking covered elsewhere.
        }

        var ranked = SteamAnonymousAcquirer.Session.RankServers(
            new[] { valveServer, publicServer });

        Assert.Equal("cache1-iad1.steamcontent.com", ranked[0].Host);
        Assert.Equal("cache1-blv2.valve.org", ranked[1].Host);
    }

    // ---- CdnServerRotation: empty-list guard + advance semantics ----

    [Fact]
    public void Rotation_throws_on_empty_candidate_list()
    {
        // An empty rotation has no valid Current; tolerating it silently would
        // let a download proceed with nowhere to fetch from (violation).
        var ex = Assert.Throws<InvalidOperationException>(
            () => new SteamAnonymousAcquirer.CdnServerRotation(Array.Empty<Server>()));
        Assert.Contains("cannot build a CDN rotation", ex.Message);
    }

    [Fact]
    public void Rotation_advances_through_candidates_then_reports_exhaustion()
    {
        var s0 = TryMakeServer("a.steamcontent.com");
        var s1 = TryMakeServer("b.steamcontent.com");
        if (s0 is null || s1 is null)
        {
            return; // cannot construct Server in this environment.
        }

        var rotation = new SteamAnonymousAcquirer.CdnServerRotation(new[] { s0, s1 });
        Assert.Equal(2, rotation.CandidateCount);
        Assert.Equal(0, rotation.CurrentIndex);
        Assert.Same(s0, rotation.Current);

        // First transport failure: advance to the second candidate.
        Assert.True(rotation.AdvanceOnTransportFailure(out var next));
        Assert.Same(s1, next);
        Assert.Same(s1, rotation.Current);
        Assert.Equal(1, rotation.CurrentIndex);

        // Second transport failure: candidates exhausted -> false (caller fails loud).
        Assert.False(rotation.AdvanceOnTransportFailure(out _));
        Assert.Same(s1, rotation.Current); // cursor stays on the last candidate.
    }

    // ---- IsTransportFailure: transport rotates, corruption fails loud ----

    [Fact]
    public void IsTransportFailure_true_for_dns_and_socket_and_timeout_errors()
    {
        // "No such host is known." is an HttpRequestException — the exact live error.
        Assert.True(SteamAnonymousAcquirer.IsTransportFailure(
            new HttpRequestException("No such host is known. (cache1-blv2.valve.org:443)"),
            CancellationToken.None));
        Assert.True(SteamAnonymousAcquirer.IsTransportFailure(
            new System.Net.Sockets.SocketException(), CancellationToken.None));
        Assert.True(SteamAnonymousAcquirer.IsTransportFailure(
            new TimeoutException(), CancellationToken.None));
        Assert.True(SteamAnonymousAcquirer.IsTransportFailure(
            new IOException("short read"), CancellationToken.None));
    }

    [Fact]
    public void IsTransportFailure_false_for_hash_mismatch_so_corruption_fails_loud()
    {
        // A chunk/file SHA-1 mismatch is CORRUPTION. SteamKit2 surfaces it as a
        // plain InvalidDataException, which must NOT be classified as a transport
        // failure — otherwise we would silently retry another mirror and mask
        // corruption. It must propagate and fail loud.
        var corruption = new InvalidDataException("chunk SHA-1 mismatch");
        Assert.False(SteamAnonymousAcquirer.IsTransportFailure(corruption, CancellationToken.None));

        // Same for a generic InvalidOperationException (e.g. integrity failure).
        Assert.False(SteamAnonymousAcquirer.IsTransportFailure(
            new InvalidOperationException("manifest integrity failure"),
            CancellationToken.None));
    }

    [Fact]
    public void IsTransportFailure_false_when_caller_requested_cancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        // A cancellation we asked for is not a failover-worthy transport error.
        Assert.False(SteamAnonymousAcquirer.IsTransportFailure(
            new TaskCanceledException(), cts.Token));
    }

    // ---- helper ----

    /// <summary>
    /// SteamKit2's Server type has an internal constructor and an internal Host
    /// setter, so it cannot be constructed directly from the test assembly. Try
    /// reflection; return null if the environment won't allow it (the pure
    /// host-string ranking tests cover the ordering contract regardless).
    /// </summary>
    private static Server? TryMakeServer(string host)
    {
        try
        {
            var ctor = typeof(Server).GetConstructor(
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Public,
                binder: null, types: Type.EmptyTypes, modifiers: null);
            if (ctor is null)
            {
                return null;
            }
            var server = (Server)ctor.Invoke(null);
            var hostProp = typeof(Server).GetProperty(nameof(Server.Host));
            var setter = hostProp?.GetSetMethod(nonPublic: true);
            if (setter is null)
            {
                return null;
            }
            setter.Invoke(server, new object?[] { host });
            return server;
        }
        catch
        {
            return null;
        }
    }
}

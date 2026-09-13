// `content-backfill --execute` Steam-session tests — ONE logon per run, and what the run does when
// that one session misbehaves.
//
// The campaign that motivated these ran 381 content GIDs and was refused after 118 with
// AccountLoginDeniedThrottle, because each GID's fetch stood up its own SteamClient: Steam limits the
// NUMBER of authenticated logons, not the bytes behind them, so --delay-seconds bought nothing. The
// command now opens ONE ISteamAcquirer.BeginSharedSession scope around the whole per-GID loop.
//
// The fake acquirer mirrors SteamAnonymousAcquirer's lease contract rather than merely counting calls:
// outside a scope every fetch logs on, inside a scope the FIRST fetch logs on and the rest reuse it,
// and a fetch can be told to DROP the session so the next lease has to reconnect. That makes the
// load-bearing quantity — logons — observable, and lets the recovery paths be asserted without Steam.
//
// Covered here: one logon across many GIDs; a session drop costing only the GID it hit; a reconnect
// that itself fails stopping the run instead of converting every remaining GID into a failure —
// including one refused with the messages ConnectAndLogonAsync really produces, not just a synthetic
// EResult one; the run total counting the Phase-A directory index the acquire's own figure omits,
// without double-counting an acquire whose Phase B was skipped; a
// data-level fetch failure still fail-isolating; a throttle still stopping the run resumably; an
// unclassifiable cascade stopping on the consecutive-failure guard; and the single-build
// `acquire --content` paths opening NO shared scope (their connect-once-then-done lifecycle is
// untouched).

using System.Globalization;

using Cs2SchemaTracker.Host.Cli;
using Cs2SchemaTracker.Host.Steam;

using Xunit;

namespace Cs2SchemaTracker.Tests.Cli;

[Collection("console-capturing")]
public sealed class ContentBackfillSessionTest
{
    private const string Platform = "windows-x86_64";

    // ---- fake acquirer: models the session lease, not just the call ----------------------------

    private sealed class ContentFakeAcquirer : ISteamAcquirer
    {
        /// <summary>Connect+logon count — the quantity Steam rate-limits, and the point of the fix.</summary>
        public int LogonCount { get; private set; }

        /// <summary>How many shared-session scopes were opened, and how many were disposed.</summary>
        public int ScopeOpenCount { get; private set; }
        public int ScopeDisposeCount { get; private set; }

        /// <summary>Content GIDs the fetch was actually invoked for, in order.</summary>
        public List<ulong> Calls { get; } = new();

        /// <summary>Per-GID synthetic failures.</summary>
        public Dictionary<ulong, Func<Exception>> Failures { get; } = new();

        /// <summary>GIDs whose failure also kills the session, so the next lease must reconnect.</summary>
        public HashSet<ulong> DropsSession { get; } = new();

        /// <summary>When set, the reconnect after a drop throws this instead of logging on.</summary>
        public Func<Exception>? OnReconnect { get; set; }

        /// <summary>Bytes each successful fetch reports as transferred from the CDN.</summary>
        public long DownloadedBytes { get; set; } = 4096;

        /// <summary>Per-GID override of <see cref="DownloadedBytes"/> — what the acquire itself counts.</summary>
        public Dictionary<ulong, long> DownloadedBytesByGid { get; } = new();

        // Modelled because the run's reported byte total is derived partly from the directory files in
        // the result (the Phase-A index the acquire's own figure omits), so a fake that always returns
        // an empty file list cannot exercise that accounting at all.
        /// <summary>Per-GID file list the successful fetch reports (default: none, as the session tests want).</summary>
        public Dictionary<ulong, IReadOnlyList<AcquiredFileInfo>> FilesByGid { get; } = new();

        /// <summary>The dirOnly flag the last content fetch received (single-build path assertions).</summary>
        public bool LastDirOnly { get; private set; }

        private bool scopeActive;
        private bool scopeLoggedOn;
        private bool reconnectFails;

        public IDisposable BeginSharedSession()
        {
            ScopeOpenCount++;
            scopeActive = true;
            scopeLoggedOn = false;
            return new Scope(this);
        }

        private sealed class Scope : IDisposable
        {
            private readonly ContentFakeAcquirer owner;
            public Scope(ContentFakeAcquirer owner) => this.owner = owner;
            public void Dispose()
            {
                owner.ScopeDisposeCount++;
                owner.scopeActive = false;
                owner.scopeLoggedOn = false;
            }
        }

        // The lease: inside a scope the session is established once and reused; outside one, every
        // fetch establishes its own (the pre-fix per-GID lifecycle the single-build paths still use).
        private void EnsureLogon()
        {
            if (scopeActive && scopeLoggedOn)
            {
                return;
            }
            if (reconnectFails)
            {
                throw OnReconnect!();
            }
            LogonCount++;
            scopeLoggedOn = true;
        }

        public Task<AcquireResult> AcquireContentPakAsync(
            uint appId, uint contentDepotId, uint buildId, string outDir, bool minimalGameEvents,
            ManifestSpec? explicitSpec, bool dirOnly, CancellationToken ct)
        {
            LastDirOnly = dirOnly;
            try
            {
                EnsureLogon();
            }
            catch (Exception ex)
            {
                return Task.FromException<AcquireResult>(ex);
            }

            var gid = explicitSpec?.Depots.FirstOrDefault(d => d.DepotId == contentDepotId)?.ManifestId ?? 0UL;
            Calls.Add(gid);

            if (Failures.TryGetValue(gid, out var make))
            {
                if (DropsSession.Contains(gid))
                {
                    scopeLoggedOn = false;                       // the connection went away mid-fetch
                    reconnectFails = OnReconnect is not null;
                }
                return Task.FromException<AcquireResult>(make());
            }

            var files = FilesByGid.TryGetValue(gid, out var f) ? f : Array.Empty<AcquiredFileInfo>();
            return Task.FromResult(new AcquireResult(
                OutDir: outDir,
                ResolvedBuildId: explicitSpec?.BuildId ?? buildId,
                Depots: Array.Empty<AcquiredDepotInfo>(),
                Files: files,
                TotalBytes: files.Sum(x => x.SizeBytes),
                DownloadedBytes: DownloadedBytesByGid.TryGetValue(gid, out var db) ? db : DownloadedBytes));
        }

        // No other acquire leg is reachable from content-backfill or `acquire --content`.
        public Task<AcquireResult> AcquireAsync(
            uint a, IReadOnlyList<uint> d, uint b, string o, CancellationToken c)
            => throw new NotSupportedException();
        public Task<AcquireResult> AcquireExplicitAsync(ManifestSpec s, string o, CancellationToken c)
            => throw new NotSupportedException();
        public Task<AcquireResult> AcquireBinariesOnlyAsync(
            uint a, IReadOnlyList<uint> d, uint b, string o, string p, ManifestSpec? s, CancellationToken c)
            => throw new NotSupportedException();
        public Task<AcquireResult> AcquireToolsAsync(
            uint a, uint td, uint b, string o, ManifestSpec? s, CancellationToken c)
            => throw new NotSupportedException();
        public Task<CurrentPicsResult> ProbeCurrentPicsAsync(
            uint a, IReadOnlyList<uint> d, CancellationToken c)
            => throw new NotSupportedException();
        public Task<ExplicitManifestProbe> ProbeExplicitManifestAsync(
            ManifestSpec s, bool p, CancellationToken c)
            => throw new NotSupportedException();
    }

    // ---- fixture ------------------------------------------------------------------------------

    private static string NewRoot()
    {
        var dir = Path.Combine(Path.GetTempPath(), "backfill-session-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDelete(string dir)
    {
        try
        { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { /* best effort */ }
    }

    /// <summary>
    /// A committed tuple dir whose manifest-record carries <paramref name="gid"/> as the 2347770
    /// content GID. With no store under the root, every such GID plans as a core-pak backfill target.
    /// </summary>
    private static void WriteBuild(string root, string build, ulong gid)
    {
        var tupleDir = Path.Combine(root, build, Platform);
        Directory.CreateDirectory(tupleDir);
        new ManifestRecord(730, uint.Parse(build, CultureInfo.InvariantCulture), new[]
        {
            new ManifestRecordDepot(ContentStore.ContentDepotId, gid, "2026-06-10T00:00:00Z"),
            new ManifestRecordDepot(2347771, 111UL, "2026-06-10T00:00:00Z"),
        }).WriteToTupleDir(tupleDir);
    }

    /// <summary>Write <paramref name="count"/> builds whose GIDs are 100, 200, 300 ... in plan order.</summary>
    private static void WriteBuilds(string root, int count)
    {
        for (int i = 1; i <= count; i++)
        {
            WriteBuild(root, (1000 + i).ToString(CultureInfo.InvariantCulture), (ulong)(100 * i));
        }
    }

    private static (int Code, string Err) RunExecute(string root, ContentFakeAcquirer fake)
    {
        var (code, _, err) = ConsoleCapture.Run(() =>
            ContentBackfillCommand.RunAsync(
                new[] { "--binaries-root", root, "--execute" }, () => fake).GetAwaiter().GetResult());
        return (code, err);
    }

    private static InvalidOperationException Throttle() => new(
        "authenticated logon rejected at credentials stage (EResult=AccountLoginDeniedThrottle).");

    private static InvalidOperationException Drop() => new(
        "SteamApps must be connected to Steam to issue a PICS request.");

    /// <summary>One entry of a successful acquire's <see cref="AcquireResult.Files"/>.</summary>
    private static AcquiredFileInfo PakFile(string rel, long size)
        => new(RelativePath: rel, Sha256Hex: new string('0', 64), SizeBytes: size, MtimeUtc: null);

    // ---- one logon per run ---------------------------------------------------------------------

    [Fact]
    public void Execute_Logs_On_Once_Across_Every_Gid()
    {
        var root = NewRoot();
        try
        {
            WriteBuilds(root, 5);
            var fake = new ContentFakeAcquirer();

            var (code, err) = RunExecute(root, fake);

            Assert.Equal(0, code);
            Assert.Equal(5, fake.Calls.Count);          // all five GIDs fetched...
            Assert.Equal(1, fake.LogonCount);           // ...on ONE logon, not five.
            Assert.Equal(1, fake.ScopeOpenCount);
            Assert.Equal(1, fake.ScopeDisposeCount);    // the scope tears the session down at the end
            Assert.Contains("fetched=5 failed=0 skipped=0", err, StringComparison.Ordinal);
            Assert.DoesNotContain("STOPPED", err, StringComparison.Ordinal);
        }
        finally
        {
            TryDelete(root);
        }
    }

    // ---- a dropped session costs the GID it hit, not the remainder -----------------------------

    [Fact]
    public void Session_Drop_Costs_One_Gid_Then_The_Run_Reconnects_And_Continues()
    {
        var root = NewRoot();
        try
        {
            WriteBuilds(root, 4);
            var fake = new ContentFakeAcquirer();
            fake.Failures[200UL] = Drop;
            fake.DropsSession.Add(200UL);

            var (code, err) = RunExecute(root, fake);

            Assert.Equal(1, code);                      // one failed GID => non-zero run exit
            Assert.Equal(4, fake.Calls.Count);          // every GID still attempted
            Assert.Equal(2, fake.LogonCount);           // the original logon plus ONE reconnect
            Assert.Equal(1, fake.ScopeOpenCount);
            Assert.Contains("the shared Steam session dropped on this GID", err, StringComparison.Ordinal);
            Assert.Contains("fetched=3 failed=1 skipped=0", err, StringComparison.Ordinal);
            Assert.DoesNotContain("STOPPED", err, StringComparison.Ordinal);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void Unrecoverable_Session_Stops_The_Run_Rather_Than_Failing_Every_Remaining_Gid()
    {
        var root = NewRoot();
        try
        {
            WriteBuilds(root, 6);
            var fake = new ContentFakeAcquirer
            {
                OnReconnect = () => new InvalidOperationException(
                    "anonymous logon failed with EResult=ServiceUnavailable ExtendedResult=OK."),
            };
            fake.Failures[200UL] = Drop;
            fake.DropsSession.Add(200UL);

            var (code, err) = RunExecute(root, fake);

            Assert.Equal(1, code);
            // GID 100 fetched, GID 200 dropped, GID 300's reconnect failed and ended the run: GIDs 400+
            // were never attempted, so the drop cost two GIDs rather than all six.
            Assert.Equal(new List<ulong> { 100UL, 200UL }, fake.Calls);
            Assert.Equal(1, fake.LogonCount);
            Assert.Contains("STOPPED (Steam session unavailable)", err, StringComparison.Ordinal);
            Assert.Contains("fetched=1 failed=2 skipped=0", err, StringComparison.Ordinal);
            Assert.Contains("5 GID(s) still missing", err, StringComparison.Ordinal);   // resumable remainder
            Assert.Contains("Re-run to resume", err, StringComparison.Ordinal);
        }
        finally
        {
            TryDelete(root);
        }
    }

    // The test above uses a synthetic EResult message; these are the messages a refused
    // ConnectAndLogonAsync ACTUALLY produces, copied verbatim from SteamAnonymousAcquirer. They are
    // the whole point of the classifier: the one-session-per-run change made the reconnect path
    // reachable, and a probe set that does not match these leaves every refused reconnect
    // unclassified — the run then burns GIDs until the consecutive-failure guard trips and tells the
    // operator the wrong thing about why it stopped.
    [Theory]
    [InlineData("disconnected before connect completed (UserInitiated=False).")]
    [InlineData("disconnected before logon completed (UserInitiated=False).")]
    [InlineData("authenticated logon produced no LoggedOnCallback after token exchange.")]
    public void A_Refused_Reconnect_Stops_The_Run(string message)
    {
        var root = NewRoot();
        try
        {
            WriteBuilds(root, 6);
            var fake = new ContentFakeAcquirer
            {
                OnReconnect = () => new InvalidOperationException(message),
            };
            fake.Failures[200UL] = Drop;
            fake.DropsSession.Add(200UL);

            var (code, err) = RunExecute(root, fake);

            Assert.Equal(1, code);
            Assert.Equal(new List<ulong> { 100UL, 200UL }, fake.Calls);
            Assert.Equal(1, fake.LogonCount);
            Assert.Contains("STOPPED (Steam session unavailable)", err, StringComparison.Ordinal);
            Assert.Contains("fetched=1 failed=2 skipped=0", err, StringComparison.Ordinal);
            Assert.Contains("5 GID(s) still missing", err, StringComparison.Ordinal);
            Assert.Contains("Re-run to resume", err, StringComparison.Ordinal);
        }
        finally
        {
            TryDelete(root);
        }
    }

    // ---- the reported transfer is what Steam actually sent ---------------------------------------

    [Fact]
    public void Phase_A_Directory_Index_Is_Counted_In_The_Reported_Transfer()
    {
        var root = NewRoot();
        try
        {
            WriteBuilds(root, 3);
            var fake = new ContentFakeAcquirer();
            // GID 100: an ordinary Phase-B acquire — the returned result counts Phase B only, so the
            // 7 MB directory index Phase A pulled (and Phase B re-pulled whole) is missing from it.
            fake.FilesByGid[100UL] = new[]
            {
                PakFile("game/csgo/pak01_dir.vpk", 7_000_000),
                PakFile("game/csgo/pak01_462.vpk", 2_500),
            };
            fake.DownloadedBytesByGid[100UL] = 9_000_000;
            // GID 200: Phase B SKIPPED, so the acquirer handed back Phase A's OWN result — its figure
            // already counts the index, and its file list is directory-only. Nothing to add here.
            fake.FilesByGid[200UL] = new[] { PakFile("game/csgo/pak01_dir.vpk", 7_000_000) };
            fake.DownloadedBytesByGid[200UL] = 7_000_000;
            // GID 300: a Phase-B acquire that also staged the engine core pak's index — both indexes
            // were paid for twice, so both are added.
            fake.FilesByGid[300UL] = new[]
            {
                PakFile("game/csgo/pak01_dir.vpk", 7_000_000),
                PakFile("game/core/pak01_dir.vpk", 1_000_000),
                PakFile("game/csgo/pak01_462.vpk", 2_500),
            };
            fake.DownloadedBytesByGid[300UL] = 9_000_000;

            var (code, err) = RunExecute(root, fake);

            Assert.Equal(0, code);
            Assert.Contains(
                $"GID 100 done — {16_000_000L.ToString("N0", CultureInfo.CurrentCulture)} byte(s)",
                err, StringComparison.Ordinal);
            Assert.Contains(
                $"GID 200 done — {7_000_000L.ToString("N0", CultureInfo.CurrentCulture)} byte(s)",
                err, StringComparison.Ordinal);
            Assert.Contains(
                $"GID 300 done — {17_000_000L.ToString("N0", CultureInfo.CurrentCulture)} byte(s)",
                err, StringComparison.Ordinal);
            Assert.Contains(
                $"{40_000_000L.ToString("N0", CultureInfo.CurrentCulture)} byte(s) transferred in total",
                err, StringComparison.Ordinal);
            Assert.Contains("Phase-A directory index", err, StringComparison.Ordinal);
        }
        finally
        {
            TryDelete(root);
        }
    }

    // ---- per-GID data faults still fail-isolate -------------------------------------------------

    [Fact]
    public void Per_Gid_Fetch_Failure_Isolates_And_The_Run_Continues()
    {
        var root = NewRoot();
        try
        {
            WriteBuilds(root, 4);
            var fake = new ContentFakeAcquirer();
            fake.Failures[200UL] = () => new InvalidDataException(
                "chunk SHA-1 mismatch for game/csgo/pak01_dir.vpk.");

            var (code, err) = RunExecute(root, fake);

            Assert.Equal(1, code);
            Assert.Equal(4, fake.Calls.Count);          // the other three still fetched
            Assert.Equal(1, fake.LogonCount);           // a data fault does not disturb the session
            Assert.Contains("FAILED GID 200", err, StringComparison.Ordinal);
            Assert.Contains("fetched=3 failed=1 skipped=0", err, StringComparison.Ordinal);
            Assert.DoesNotContain("STOPPED", err, StringComparison.Ordinal);
        }
        finally
        {
            TryDelete(root);
        }
    }

    // ---- throttle and cascade stops ------------------------------------------------------------

    [Fact]
    public void Throttle_Stops_The_Run_Resumably()
    {
        var root = NewRoot();
        try
        {
            WriteBuilds(root, 5);
            var fake = new ContentFakeAcquirer();
            fake.Failures[200UL] = Throttle;

            var (code, err) = RunExecute(root, fake);

            Assert.Equal(1, code);
            Assert.Equal(new List<ulong> { 100UL, 200UL }, fake.Calls);   // GIDs 300+ never attempted
            Assert.Equal(1, fake.LogonCount);
            Assert.Equal(1, fake.ScopeDisposeCount);                      // the scope closed on the break
            Assert.Contains("STOPPED (Steam throttle)", err, StringComparison.Ordinal);
            Assert.Contains("4 GID(s) still missing", err, StringComparison.Ordinal);
            Assert.Contains("Re-run to resume", err, StringComparison.Ordinal);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void Throttle_Advice_No_Longer_Blames_A_Per_Gid_Re_Auth()
    {
        var root = NewRoot();
        try
        {
            WriteBuilds(root, 2);
            var fake = new ContentFakeAcquirer();
            fake.Failures[100UL] = Throttle;

            var (_, err) = RunExecute(root, fake);

            Assert.Contains("This run logs on ONCE", err, StringComparison.Ordinal);
            Assert.DoesNotContain("re-auths per GID", err, StringComparison.Ordinal);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void Unclassifiable_Failures_Stop_On_The_Consecutive_Guard()
    {
        var root = NewRoot();
        try
        {
            WriteBuilds(root, 6);
            var fake = new ContentFakeAcquirer();
            foreach (var gid in new ulong[] { 100UL, 200UL, 300UL, 400UL })
            {
                fake.Failures[gid] = () => new IOException("the remote host closed the connection.");
            }

            var (code, err) = RunExecute(root, fake);

            Assert.Equal(1, code);
            // Three in a row is a run-level fault, not three bad GIDs: stop rather than burn the rest.
            Assert.Equal(ContentBackfillCommand.ConsecutiveStop, fake.Calls.Count);
            Assert.Contains("STOPPED (3 consecutive failures)", err, StringComparison.Ordinal);
            Assert.Contains("Re-run to resume", err, StringComparison.Ordinal);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void A_Success_Clears_The_Consecutive_Failure_Count()
    {
        var root = NewRoot();
        try
        {
            WriteBuilds(root, 6);
            var fake = new ContentFakeAcquirer();
            // Two failures, a success, then two more: never three running, so the run completes.
            foreach (var gid in new ulong[] { 100UL, 200UL, 400UL, 500UL })
            {
                fake.Failures[gid] = () => new IOException("the remote host closed the connection.");
            }

            var (code, err) = RunExecute(root, fake);

            Assert.Equal(1, code);
            Assert.Equal(6, fake.Calls.Count);
            Assert.Contains("fetched=2 failed=4 skipped=0", err, StringComparison.Ordinal);
            Assert.DoesNotContain("STOPPED", err, StringComparison.Ordinal);
        }
        finally
        {
            TryDelete(root);
        }
    }

    // ---- the single-build acquire paths keep their own lifecycle --------------------------------
    // `acquire --content` (and --dir-only) fetch ONE build and must NOT open a shared scope: their
    // connect-once-then-done lifecycle is what the backfill's scope deliberately replaces, and only
    // there.

    [Fact]
    public async Task Single_Build_Content_Acquire_Opens_No_Shared_Session()
    {
        var outDir = NewRoot();
        try
        {
            var fake = new ContentFakeAcquirer();
            var args = new[]
            {
                "--content", "--build", "23669931", "--platform", Platform, "--out", outDir,
            };

            var code = await AcquireCommand.RunAsync(args, () => fake);

            Assert.Equal(0, code);
            Assert.Equal(0, fake.ScopeOpenCount);   // no batch scope on the single-build path
            Assert.Equal(1, fake.LogonCount);       // one acquire, one logon — unchanged
            Assert.False(fake.LastDirOnly);
        }
        finally
        {
            TryDelete(outDir);
        }
    }

    [Fact]
    public async Task Single_Build_Content_Dir_Only_Acquire_Opens_No_Shared_Session()
    {
        var outDir = NewRoot();
        try
        {
            var fake = new ContentFakeAcquirer();
            var args = new[]
            {
                "--content", "--dir-only", "--build", "23669931", "--platform", Platform,
                "--out", outDir,
            };

            var code = await AcquireCommand.RunAsync(args, () => fake);

            Assert.Equal(0, code);
            Assert.Equal(0, fake.ScopeOpenCount);
            Assert.Equal(1, fake.LogonCount);
            Assert.True(fake.LastDirOnly);
        }
        finally
        {
            TryDelete(outDir);
        }
    }
}

// Required-set generation marker tests — the fix for the self-referential store-completeness check.
//
// THE BUG these pin down: IsCompleteTrimmedStore used to derive its required-entry set FROM the
// stored (already-trimmed) pak. A store trimmed under an OLDER required set therefore contained
// exactly the entries the probe went looking for, every one read + CRC-verified, and the answer was
// always "complete" — so a grown required set (adding scripts/weapons.vdata_c) left every existing
// store silently stale: acquires skipped the repack, backfills reported nothing to do, and extract
// recorded a FALSE CONTENT_NOT_SHIPPED_THIS_ERA omission for a resource the build genuinely ships.
//
// Coverage: the reduced-required-set regression (with and without a marker — the no-marker case is
// the one that PASSES against the pre-fix code and must not), unparseable + newer markers, the
// marker's determinism and its survival of PruneStrayStoreChunks, EnsureTrimmedStore's
// self-heal/skip dispositions, and the deliberate core-pak exemption.

using Cs2SchemaTracker.Host.Steam;
using Cs2SchemaTracker.Host.Vpk;
using Cs2SchemaTracker.Tests.Content;

using Xunit;

namespace Cs2SchemaTracker.Tests.Steam;

public class ContentStoreGenerationTest
{
    private const ulong Gid = 20260909UL;

    private static int Current => ContentPakSelector.RequiredSetGeneration;
    private static int Previous => ContentPakSelector.RequiredSetGeneration - 1;

    private static string NewRoot()
    {
        var dir = Path.Combine(Path.GetTempPath(), "trim-gen-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDelete(string dir)
    {
        try
        { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { /* best effort */ }
    }

    /// <summary>The full synthetic source pak every case trims from.</summary>
    private static VpkArchive SourcePak(string work)
        => VpkArchive.Open(ContentVpkFixture.Write(Path.Combine(work, "src"), ContentSamples.StandardEntries()));

    /// <summary>
    /// The required set MINUS the compiled weapons.vdata_c — i.e. exactly what
    /// EnumerateRequiredEntries selected at generation 1, before weapon VData was tracked. Trimming
    /// this is how a real generation-1 store came to be.
    /// </summary>
    private static List<VpkDirectoryEntry> GenerationOneRequired(VpkArchive source)
        => ContentPakSelector.EnumerateRequiredEntries(source)
            .Where(e => !string.Equals(
                e.FullPath, ContentPakSelector.WeaponVDataRelPath, StringComparison.OrdinalIgnoreCase))
            .ToList();

    /// <summary>
    /// Write a store trim through the PRODUCTION writer, optionally stamping a marker generation.
    /// A null <paramref name="markerGeneration"/> reproduces a pre-marker store exactly (no sidecar
    /// at all) — the shape the whole corpus is in today.
    /// </summary>
    private static void WriteStoreTrim(
        string contentRoot, VpkArchive source, IReadOnlyList<VpkDirectoryEntry> required,
        int? markerGeneration, ContentPak? pak = null)
    {
        VpkTrimWriter.Write(source, required, ContentStore.ResolveDirVpk(contentRoot, Gid, pak),
            markerGeneration is { } g
                ? new VpkTrimSidecar(ContentStore.TrimMarkerFileName, ContentStore.TrimMarkerContent(g))
                : null);
    }

    // THE REGRESSION TEST. A proper, uncorrupted trim of the generation-1 required set, stamped
    // generation 1: every byte in it is valid, nothing is missing relative to what it claims, and
    // the old self-referential probe called it complete. It is NOT — it predates weapons.vdata_c.
    [Fact]
    public void Trim_Written_For_An_Older_Required_Set_Is_Incomplete()
    {
        var work = NewRoot();
        try
        {
            var contentRoot = Path.Combine(work, "_content");
            var source = SourcePak(work);
            WriteStoreTrim(contentRoot, source, GenerationOneRequired(source), Previous);

            Assert.False(ContentStore.IsCompleteTrimmedStore(contentRoot, Gid, out var reason));
            Assert.Contains($"required-set generation {Previous}", reason, StringComparison.Ordinal);
            Assert.Contains($"current is {Current}", reason, StringComparison.Ordinal);

            // Why the old check could not see this: the stored pak is internally perfect. Its own
            // required set reads + CRC-verifies entry for entry, and simply never mentions the path
            // it is missing — so the probe had nothing to notice.
            var stored = VpkArchive.Open(ContentStore.ResolveDirVpk(contentRoot, Gid));
            var selfDerived = ContentPakSelector.EnumerateRequiredEntries(stored);
            Assert.NotEmpty(selfDerived);
            Assert.DoesNotContain(selfDerived,
                e => string.Equals(e.FullPath, ContentPakSelector.WeaponVDataRelPath, StringComparison.Ordinal));
            foreach (var entry in selfDerived)
            {
                Assert.NotNull(stored.ReadEntryBytes(entry));
            }
        }
        finally
        {
            TryDelete(work);
        }
    }

    // The corpus's ACTUAL shape: a generation-1 trim written before markers existed, so it carries
    // no marker at all. This is the case that returns `true` against the pre-fix code.
    [Fact]
    public void Trim_With_No_Generation_Marker_Is_Incomplete()
    {
        var work = NewRoot();
        try
        {
            var contentRoot = Path.Combine(work, "_content");
            var source = SourcePak(work);
            WriteStoreTrim(contentRoot, source, GenerationOneRequired(source), markerGeneration: null);

            Assert.False(File.Exists(ContentStore.TrimMarkerPath(contentRoot, Gid)));
            Assert.False(ContentStore.IsCompleteTrimmedStore(contentRoot, Gid, out var reason));
            Assert.Contains(ContentStore.TrimMarkerFileName, reason, StringComparison.Ordinal);
            Assert.Contains($"generation {Previous}", reason, StringComparison.Ordinal);
            Assert.Contains($"current is {Current}", reason, StringComparison.Ordinal);
        }
        finally
        {
            TryDelete(work);
        }
    }

    [Fact]
    public void Trim_With_An_Unparseable_Marker_Is_Incomplete()
    {
        var work = NewRoot();
        try
        {
            var contentRoot = Path.Combine(work, "_content");
            var source = SourcePak(work);
            var required = ContentPakSelector.EnumerateRequiredEntries(source);
            WriteStoreTrim(contentRoot, source, required, Current);

            // Truncated / garbage marker: a fact we cannot read is not a fact we may trust.
            File.WriteAllText(ContentStore.TrimMarkerPath(contentRoot, Gid), "{ not json");

            Assert.False(ContentStore.IsCompleteTrimmedStore(contentRoot, Gid, out var reason));
            Assert.Contains("unreadable", reason, StringComparison.Ordinal);
            Assert.Contains($"current is {Current}", reason, StringComparison.Ordinal);

            // Same for a well-formed object whose generation is not an integer.
            File.WriteAllText(ContentStore.TrimMarkerPath(contentRoot, Gid), "{\"required_set_generation\":\"two\"}");
            Assert.False(ContentStore.IsCompleteTrimmedStore(contentRoot, Gid, out _));
        }
        finally
        {
            TryDelete(work);
        }
    }

    [Fact]
    public void Trim_At_Or_Above_The_Current_Generation_Is_Complete()
    {
        var work = NewRoot();
        try
        {
            var contentRoot = Path.Combine(work, "_content");
            var source = SourcePak(work);
            var required = ContentPakSelector.EnumerateRequiredEntries(source);

            WriteStoreTrim(contentRoot, source, required, Current);
            Assert.True(ContentStore.IsCompleteTrimmedStore(contentRoot, Gid, out var reason), reason);

            // A store written by a LATER build of the tool is a superset, never something to
            // downgrade — accepted, not re-trimmed.
            ContentStore.WriteTrimGenerationMarker(contentRoot, Gid, Current + 1);
            Assert.True(ContentStore.IsCompleteTrimmedStore(contentRoot, Gid, out reason), reason);
        }
        finally
        {
            TryDelete(work);
        }
    }

    // A store whose marker is current but whose BYTES are broken must still fail: the marker is an
    // extra gate, not a replacement for the corruption checks.
    [Fact]
    public void Current_Marker_Does_Not_Excuse_A_Corrupt_Store()
    {
        var work = NewRoot();
        try
        {
            var contentRoot = Path.Combine(work, "_content");
            var source = SourcePak(work);
            WriteStoreTrim(contentRoot, source, ContentPakSelector.EnumerateRequiredEntries(source), Current);

            // Delete the body chunk the trimmed tree points every external entry at.
            File.Delete(Path.Combine(ContentStore.StoreDirForGid(contentRoot, Gid), "pak01_000.vpk"));

            Assert.False(ContentStore.IsCompleteTrimmedStore(contentRoot, Gid, out var reason));
            Assert.Contains("not a complete self-contained trim", reason, StringComparison.Ordinal);
        }
        finally
        {
            TryDelete(work);
        }
    }

    [Fact]
    public void Marker_Content_Is_Deterministic_And_Carries_Only_The_Generation()
    {
        // Byte-identical for the same generation, and free of timestamps / paths / environment.
        Assert.Equal(ContentStore.TrimMarkerContent(7), ContentStore.TrimMarkerContent(7));
        Assert.Equal("{\"required_set_generation\":7}\n", ContentStore.TrimMarkerContent(7));
        Assert.NotEqual(ContentStore.TrimMarkerContent(7), ContentStore.TrimMarkerContent(8));
    }

    [Fact]
    public void EnsureTrimmedStore_Writes_The_Marker_With_The_Trimmed_Pair()
    {
        var work = NewRoot();
        try
        {
            var contentRoot = Path.Combine(work, "_content");
            var source = SourcePak(work);
            var required = ContentPakSelector.EnumerateRequiredEntries(source);

            var action = ContentStore.EnsureTrimmedStore(
                source, required, contentRoot, Gid, force: false, out _);
            Assert.Equal(ContentStore.StoreEnsureAction.Built, action);

            var storeDir = ContentStore.StoreDirForGid(contentRoot, Gid);
            Assert.True(File.Exists(Path.Combine(storeDir, "pak01_dir.vpk")));
            Assert.True(File.Exists(Path.Combine(storeDir, "pak01_000.vpk")));
            // BYTE-level: UTF-8, no BOM, nothing but the generation — two stores at the same
            // generation are byte-identical here.
            Assert.Equal(
                System.Text.Encoding.UTF8.GetBytes(ContentStore.TrimMarkerContent(Current)),
                File.ReadAllBytes(Path.Combine(storeDir, ContentStore.TrimMarkerFileName)));
            Assert.True(ContentStore.TryReadTrimGeneration(contentRoot, Gid, null, out var stored));
            Assert.Equal(Current, stored);
        }
        finally
        {
            TryDelete(work);
        }
    }

    [Fact]
    public void Marker_Survives_PruneStrayStoreChunks()
    {
        var work = NewRoot();
        try
        {
            var contentRoot = Path.Combine(work, "_content");
            var source = SourcePak(work);
            ContentStore.EnsureTrimmedStore(
                source, ContentPakSelector.EnumerateRequiredEntries(source), contentRoot, Gid,
                force: false, out _);

            // A legacy original chunk left behind next to the trimmed pair — what the prune sweeps.
            var storeDir = ContentStore.StoreDirForGid(contentRoot, Gid);
            File.WriteAllBytes(Path.Combine(storeDir, "pak01_154.vpk"), new byte[] { 1, 2, 3 });

            Assert.Equal(1, ContentStore.PruneStrayStoreChunks(contentRoot, Gid));

            Assert.False(File.Exists(Path.Combine(storeDir, "pak01_154.vpk")));
            Assert.True(File.Exists(Path.Combine(storeDir, "pak01_dir.vpk")));
            Assert.True(File.Exists(Path.Combine(storeDir, "pak01_000.vpk")));
            Assert.True(File.Exists(Path.Combine(storeDir, ContentStore.TrimMarkerFileName)),
                "the prune must never take the generation marker with it");
            Assert.True(ContentStore.IsCompleteTrimmedStore(contentRoot, Gid, out var reason), reason);
        }
        finally
        {
            TryDelete(work);
        }
    }

    // EnsureTrimmedStore needed NO change: routing a stale store down its existing incomplete branch
    // is enough to make it self-heal.
    [Fact]
    public void EnsureTrimmedStore_ReTrims_A_Stale_Store_And_Skips_A_Current_One()
    {
        var work = NewRoot();
        try
        {
            var contentRoot = Path.Combine(work, "_content");
            var source = SourcePak(work);
            var required = ContentPakSelector.EnumerateRequiredEntries(source);

            // A proper generation-1 trim already in the store.
            WriteStoreTrim(contentRoot, source, GenerationOneRequired(source), Previous);

            var action = ContentStore.EnsureTrimmedStore(
                source, required, contentRoot, Gid, force: false, out var detail);
            Assert.Equal(ContentStore.StoreEnsureAction.ReTrimmedIncomplete, action);
            Assert.Contains($"required-set generation {Previous}", detail, StringComparison.Ordinal);

            // Healed: the newly-required resource is now in the store and the marker is current.
            var healed = VpkArchive.Open(ContentStore.ResolveDirVpk(contentRoot, Gid));
            Assert.NotNull(healed.Find(ContentPakSelector.WeaponVDataRelPath));
            Assert.True(ContentStore.TryReadTrimGeneration(contentRoot, Gid, null, out var gen));
            Assert.Equal(Current, gen);

            // Second pass over the now-current store is the fast content-addressed no-op.
            Assert.Equal(
                ContentStore.StoreEnsureAction.SkippedComplete,
                ContentStore.EnsureTrimmedStore(source, required, contentRoot, Gid, force: false, out _));
        }
        finally
        {
            TryDelete(work);
        }
    }

    // The core-pak decision, pinned: the gate is enforced for REQUIRED paks only. The engine core
    // pak selects `.gameevents` and nothing else — a rule unchanged since generation 1 — so a
    // marker-less core store stays a graceful HIT rather than forcing a Steam re-fetch that could
    // not change one emitted byte.
    [Fact]
    public void Core_Pak_Store_Is_Not_Gated_On_The_Generation_Marker()
    {
        var work = NewRoot();
        try
        {
            var contentRoot = Path.Combine(work, "_content");
            var source = SourcePak(work);
            var events = ContentPakSelector.EnumerateRequiredEntries(source)
                .Where(e => e.FullPath.EndsWith(".gameevents", StringComparison.Ordinal))
                .ToList();
            Assert.NotEmpty(events);

            // A core store with NO marker at all — the shape every existing core copy is in.
            WriteStoreTrim(contentRoot, source, events, markerGeneration: null, ContentPak.Core);
            Assert.False(File.Exists(ContentStore.TrimMarkerPath(contentRoot, Gid, ContentPak.Core)));

            Assert.True(
                ContentStore.IsCompleteTrimmedStore(contentRoot, Gid, out var reason, ContentPak.Core),
                reason);

            // ... while the SAME marker-less shape for the required csgo pak is rejected.
            WriteStoreTrim(contentRoot, source, events, markerGeneration: null, ContentPak.Csgo);
            Assert.False(ContentStore.IsCompleteTrimmedStore(contentRoot, Gid, out _, ContentPak.Csgo));
        }
        finally
        {
            TryDelete(work);
        }
    }

    // The marker is still WRITTEN for the core pak (so the fact is on disk if the core selection
    // ever changes and the gate has to widen) — it is only the ENFORCEMENT that is scoped.
    [Fact]
    public void Core_Pak_Store_Is_Still_Stamped_When_Written_Through_EnsureTrimmedStore()
    {
        var work = NewRoot();
        try
        {
            var contentRoot = Path.Combine(work, "_content");
            var source = SourcePak(work);
            var events = ContentPakSelector.EnumerateRequiredEntries(source)
                .Where(e => e.FullPath.EndsWith(".gameevents", StringComparison.Ordinal))
                .ToList();

            ContentStore.EnsureTrimmedStore(
                source, events, contentRoot, Gid, force: false, out _, ContentPak.Core);

            Assert.True(ContentStore.TryReadTrimGeneration(contentRoot, Gid, ContentPak.Core, out var gen));
            Assert.Equal(Current, gen);
        }
        finally
        {
            TryDelete(work);
        }
    }
}

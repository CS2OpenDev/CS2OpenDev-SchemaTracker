// aligned — manifest-record persistence tests.
//
// determinism: canonical JSON, sorted keys, depots sorted by id, uint64 as
// string, byte-identical regardless of input depot order. Round-trips losslessly
// into a re-fetchable ManifestSpec (the whole point of the history record).
// fail-loud: a zero-depot record refuses to materialize.

using System.IO;

using Cs2SchemaTracker.Host.Steam;

using Xunit;

namespace Cs2SchemaTracker.Tests.Steam;

public class ManifestRecordTest
{
    private static AcquiredDepotInfo Depot(uint id, ulong gid, string created) =>
        new(AppId: 730, DepotId: id, ManifestId: gid, ManifestCreatedUtc: created);

    [Fact]
    public void Canonical_json_is_deterministic_and_depot_order_independent()
    {
        var a = ManifestRecord.FromAcquireResult(23669931, new[]
        {
            Depot(2347771, 8287382081622299196UL, "2026-06-10T00:00:00Z"),
            Depot(2347770, 5146470907583764090UL, "2026-06-10T00:00:00Z"),
        });
        var b = ManifestRecord.FromAcquireResult(23669931, new[]
        {
            Depot(2347770, 5146470907583764090UL, "2026-06-10T00:00:00Z"),
            Depot(2347771, 8287382081622299196UL, "2026-06-10T00:00:00Z"),
        });

        // Byte-identical regardless of input order.
        Assert.Equal(a.ToCanonicalJson(), b.ToCanonicalJson());
    }

    [Fact]
    public void Canonical_json_emits_uint64_as_string_and_sorted_keys()
    {
        var rec = KnownManifestHistory.Build23669931WindowsClient;
        var json = rec.ToCanonicalJson();

        // uint64 GID carried as a JSON string (proto3 convention).
        Assert.Contains("\"5146470907583764090\"", json);
        Assert.Contains("\"8287382081622299196\"", json);
        // Top-level keys are sorted alphabetically: appId < buildId < depots.
        int iApp = json.IndexOf("\"appId\"", System.StringComparison.Ordinal);
        int iBuild = json.IndexOf("\"buildId\"", System.StringComparison.Ordinal);
        int iDepots = json.IndexOf("\"depots\"", System.StringComparison.Ordinal);
        Assert.True(iApp >= 0 && iApp < iBuild && iBuild < iDepots);
        // LF line endings, not CRLF.
        Assert.DoesNotContain("\r\n", json);
    }

    [Fact]
    public void Round_trips_to_manifest_spec()
    {
        var rec = KnownManifestHistory.Build23669931WindowsClient;
        var spec = rec.ToManifestSpec();
        Assert.Equal(rec.AppId, spec.AppId);
        Assert.Equal(rec.BuildId, spec.BuildId);
        Assert.Equal(rec.Depots.Count, spec.Depots.Count);
        Assert.Equal(2347770u, spec.OrderedDepots[0].DepotId);
        Assert.Equal(5146470907583764090UL, spec.OrderedDepots[0].ManifestId);
    }

    [Fact]
    public void Write_to_tuple_dir_writes_canonical_file()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cs2-mr-" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var rec = KnownManifestHistory.Build23669931WindowsClient;
            rec.WriteToTupleDir(dir);
            var path = Path.Combine(dir, ManifestRecord.FileName);
            Assert.True(File.Exists(path));
            var onDisk = File.ReadAllText(path);
            Assert.Equal(rec.ToCanonicalJson(), onDisk);
        }
        finally
        {
            try
            { Directory.Delete(dir, recursive: true); }
            catch { }
        }
    }

    [Fact]
    public void Zero_depot_record_fails_loud()
    {
        Assert.Throws<InvalidDataException>(() =>
            ManifestRecord.FromAcquireResult(1, System.Array.Empty<AcquiredDepotInfo>()));
    }

    [Fact]
    public void Read_from_file_round_trips_canonical_json_and_sorts_depots()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cs2-mr-read-" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var rec = KnownManifestHistory.Build23669931WindowsClient;
            rec.WriteToTupleDir(dir);
            var path = Path.Combine(dir, ManifestRecord.FileName);

            var read = ManifestRecord.ReadFromFile(path);

            Assert.Equal(rec.AppId, read.AppId);
            Assert.Equal(rec.BuildId, read.BuildId);
            Assert.Equal(2, read.Depots.Count);
            // Depots sorted by depotId.
            Assert.Equal(2347770u, read.Depots[0].DepotId);
            Assert.Equal(5146470907583764090UL, read.Depots[0].ManifestId);
            Assert.Equal(2347771u, read.Depots[1].DepotId);
            Assert.Equal(8287382081622299196UL, read.Depots[1].ManifestId);
            // Re-serializing the read-back record is byte-identical to the original.
            Assert.Equal(rec.ToCanonicalJson(), read.ToCanonicalJson());
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public void Read_from_file_sorts_depots_regardless_of_on_disk_order()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cs2-mr-order-" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, ManifestRecord.FileName);
            // On-disk in REVERSE depotId order.
            File.WriteAllText(path,
                "{\"appId\":730,\"buildId\":23669931,\"depots\":[" +
                "{\"depotId\":2347771,\"manifestCreatedUtc\":\"2026-06-10T22:05:05Z\",\"manifestId\":\"8287382081622299196\"}," +
                "{\"depotId\":2347770,\"manifestCreatedUtc\":\"2026-06-09T01:00:00Z\",\"manifestId\":\"5146470907583764090\"}" +
                "]}");

            var read = ManifestRecord.ReadFromFile(path);
            Assert.Equal(2347770u, read.Depots[0].DepotId);
            Assert.Equal(2347771u, read.Depots[1].DepotId);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public void Read_from_file_fails_loud_on_invalid_json()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cs2-mr-bad-" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, ManifestRecord.FileName);
            File.WriteAllText(path, "{ not json ]");
            Assert.Throws<InvalidDataException>(() => ManifestRecord.ReadFromFile(path));
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public void Read_from_file_fails_loud_on_non_numeric_manifest_id()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cs2-mr-mid-" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, ManifestRecord.FileName);
            File.WriteAllText(path,
                "{\"appId\":730,\"buildId\":1,\"depots\":[" +
                "{\"depotId\":2347770,\"manifestCreatedUtc\":\"\",\"manifestId\":\"not-a-number\"}]}");
            Assert.Throws<InvalidDataException>(() => ManifestRecord.ReadFromFile(path));
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public void Read_from_file_fails_loud_on_zero_depots()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cs2-mr-zero-" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, ManifestRecord.FileName);
            File.WriteAllText(path, "{\"appId\":730,\"buildId\":1,\"depots\":[]}");
            Assert.Throws<InvalidDataException>(() => ManifestRecord.ReadFromFile(path));
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public void Seeded_history_contains_build_23669931()
    {
        var rec = KnownManifestHistory.TryGet(23669931, 730);
        Assert.NotNull(rec);
        Assert.Equal(2, rec!.Depots.Count);
    }

    // ---- MergeIntoTupleDir / MergeWith (content-depot persist fix) -----------

    private static ManifestRecord BinaryRecord() =>
        ManifestRecord.FromAcquireResult(23669931, new[]
        {
            Depot(2347771, 8287382081622299196UL, "2026-06-10T22:05:05Z"),
        });

    private static ManifestRecord ContentRecord() =>
        ManifestRecord.FromAcquireResult(23669931, new[]
        {
            Depot(2347770, 5146470907583764090UL, "2026-06-09T01:00:00Z"),
        });

    [Fact]
    public void Merge_into_existing_record_unions_both_depots()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cs2-mr-merge-" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // Pre-existing binary depot 2347771 (the prior `acquire`).
            BinaryRecord().WriteToTupleDir(dir);

            // Content acquire merges in 2347770 WITHOUT clobbering.
            ContentRecord().MergeIntoTupleDir(dir);

            var read = ManifestRecord.ReadFromFile(Path.Combine(dir, ManifestRecord.FileName));
            Assert.Equal(2, read.Depots.Count);
            Assert.Equal(2347770u, read.Depots[0].DepotId); // sorted by depotId
            Assert.Equal(5146470907583764090UL, read.Depots[0].ManifestId);
            Assert.Equal(2347771u, read.Depots[1].DepotId);
            Assert.Equal(8287382081622299196UL, read.Depots[1].ManifestId);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public void Merge_with_no_existing_record_writes_just_the_content_depot()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cs2-mr-noexist-" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // Content acquired BEFORE any binary acquire: no record yet.
            ContentRecord().MergeIntoTupleDir(dir);

            var read = ManifestRecord.ReadFromFile(Path.Combine(dir, ManifestRecord.FileName));
            Assert.Single(read.Depots);
            Assert.Equal(2347770u, read.Depots[0].DepotId);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public void Merge_into_corrupt_existing_record_fails_loud()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cs2-mr-corrupt-" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, ManifestRecord.FileName);
            File.WriteAllText(path, "{ not json ]");

            // a present-but-corrupt record surfaces rather than being clobbered.
            Assert.Throws<InvalidDataException>(() => ContentRecord().MergeIntoTupleDir(dir));

            // The corrupt file is left untouched (not silently overwritten).
            Assert.Equal("{ not json ]", File.ReadAllText(path));
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public void Both_acquire_orders_converge_to_the_same_record()
    {
        var dirA = Path.Combine(Path.GetTempPath(), "cs2-mr-orderA-" + System.Guid.NewGuid().ToString("N"));
        var dirB = Path.Combine(Path.GetTempPath(), "cs2-mr-orderB-" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dirA);
        Directory.CreateDirectory(dirB);
        try
        {
            // Order 1: binary then content.
            BinaryRecord().MergeIntoTupleDir(dirA);
            ContentRecord().MergeIntoTupleDir(dirA);

            // Order 2: content then binary.
            ContentRecord().MergeIntoTupleDir(dirB);
            BinaryRecord().MergeIntoTupleDir(dirB);

            var jsonA = File.ReadAllText(Path.Combine(dirA, ManifestRecord.FileName));
            var jsonB = File.ReadAllText(Path.Combine(dirB, ManifestRecord.FileName));

            // Byte-identical regardless of acquire order.
            Assert.Equal(jsonA, jsonB);
        }
        finally
        {
            try
            { Directory.Delete(dirA, recursive: true); }
            catch { }
            try
            { Directory.Delete(dirB, recursive: true); }
            catch { }
        }
    }

    [Fact]
    public void Merge_with_mismatched_build_id_fails_loud()
    {
        var existing = BinaryRecord();
        var otherBuild = ManifestRecord.FromAcquireResult(99999999, new[]
        {
            Depot(2347770, 5146470907583764090UL, "2026-06-09T01:00:00Z"),
        });
        Assert.Throws<InvalidDataException>(() => existing.MergeWith(otherBuild));
    }

    [Fact]
    public void Merge_with_same_depot_lets_incoming_win()
    {
        var existing = ManifestRecord.FromAcquireResult(23669931, new[]
        {
            Depot(2347770, 1111111111111111111UL, "2026-01-01T00:00:00Z"),
        });
        var incoming = ManifestRecord.FromAcquireResult(23669931, new[]
        {
            Depot(2347770, 5146470907583764090UL, "2026-06-09T01:00:00Z"),
        });

        var merged = existing.MergeWith(incoming);
        Assert.Single(merged.Depots);
        // Incoming (freshly resolved) wins on the shared depotId.
        Assert.Equal(5146470907583764090UL, merged.Depots[0].ManifestId);
        Assert.Equal("2026-06-09T01:00:00Z", merged.Depots[0].ManifestCreatedUtc);
    }
}

// InventoryWriter.MergeDepotHistories — depots[].history refresh contract.
//
// Locks the properties that make this safe to run against the committed inventory, which is the
// single source of truth for "depot X's manifest at build Y":
//   - MERGE-ONLY: no existing entry is ever dropped and no recorded manifest is ever rewritten;
//   - a build-less (hand-entered) entry is RESOLVED to the build carrying the same manifest;
//   - entries end up newest-first with no consecutive duplicate manifest (an unchanged manifest
//     needs no second entry — the older one still resolves);
//   - a run of identical manifests keeps its OLDEST entry, because that is where the manifest
//     actually changed and what the `<=` resolution scan needs;
//   - build_id breaks a date_utc tie: CS2 builds share timestamps to the second, so date alone
//     picks a different representative run to run (this is how the ordering bug was first caught);
//   - observations are normalized onto the inventory's own build date/change_number, never the
//     capture's capture time;
//   - idempotent, and silent when there is nothing to do.
//
// Deterministic: throwaway temp files, no wall-clock, no Steam.

using System.Text.Json;

using Cs2SchemaTracker.Host.Inventory;

using Xunit;

namespace Cs2SchemaTracker.Tests.Inventory;

public sealed class InventoryDepotHistoryTest
{
    /// <summary>
    /// Two depots: a content depot (role maps onto builds[].content, so it can be re-derived from
    /// every build) and an "other" depot (no builds[] field — observations are its only source).
    /// </summary>
    private const string Seed = """
        {
          "_meta": {
            "note": "keep me"
          },
          "app": {
            "app_id": 730
          },
          "depots": [
            {
              "depot_id": 2347770,
              "role": "content",
              "platforms": [
                "windows-x86_64",
                "linux-x86_64"
              ],
              "history": [
                {
                  "build_id": 100,
                  "change_number": 1000,
                  "date_utc": "2026-01-01T00:00:00Z",
                  "manifest_id": "aaa"
                }
              ]
            },
            {
              "depot_id": 9999,
              "role": "cs2-other",
              "history": [
                {
                  "build_id": 100,
                  "change_number": 1000,
                  "date_utc": "2026-01-01T00:00:00Z",
                  "manifest_id": "zzz"
                }
              ]
            }
          ],
          "builds": [
            {
              "build_id": 300,
              "date_utc": "2026-03-01T00:00:00Z",
              "change_number": 3000,
              "content": "ccc"
            },
            {
              "build_id": 200,
              "date_utc": "2026-02-01T00:00:00Z",
              "change_number": 2000,
              "content": "bbb"
            },
            {
              "build_id": 100,
              "date_utc": "2026-01-01T00:00:00Z",
              "change_number": 1000,
              "content": "aaa"
            }
          ]
        }

        """;

    private static string NewInventory(string content = Seed)
    {
        var dir = Path.Combine(Path.GetTempPath(), "inv-depot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "cs2-assets-inventory.json");
        File.WriteAllText(path, content.Replace("\r\n", "\n"));
        return path;
    }

    private static JsonElement Depot(string path, int depotId)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var d in doc.RootElement.GetProperty("depots").EnumerateArray())
        {
            if (d.GetProperty("depot_id").GetInt32() == depotId)
                return d.Clone();
        }
        throw new InvalidOperationException($"depot {depotId} not in {path}");
    }

    private static List<(long? Build, long? Change, string Date, string Manifest)> History(
        string path, int depotId)
    {
        var entries = new List<(long?, long?, string, string)>();
        foreach (var e in Depot(path, depotId).GetProperty("history").EnumerateArray())
        {
            entries.Add((
                e.TryGetProperty("build_id", out var b) ? b.GetInt64() : null,
                e.TryGetProperty("change_number", out var c) ? c.GetInt64() : null,
                e.GetProperty("date_utc").GetString()!,
                e.GetProperty("manifest_id").GetString()!));
        }
        return entries;
    }

    [Fact]
    public void Rederive_From_Builds_Fills_The_Content_Depot_From_Every_Build()
    {
        var path = NewInventory();

        var result = InventoryWriter.MergeDepotHistories(path, [], rederiveFromBuilds: true);

        // builds[] records content aaa/bbb/ccc at 100/200/300; the depot only knew aaa.
        Assert.Equal(2, result.EntriesAdded);
        Assert.Equal(1, result.DepotsChanged);                 // only the content depot
        Assert.Equal(
            [(300L, 3000L, "2026-03-01T00:00:00Z", "ccc"),
             (200L, 2000L, "2026-02-01T00:00:00Z", "bbb"),
             (100L, 1000L, "2026-01-01T00:00:00Z", "aaa")],
            History(path, 2347770));

        // The depot with no builds[] field is untouched by a rederive.
        Assert.Single(History(path, 9999));
    }

    [Fact]
    public void A_Depot_With_No_Builds_Field_Is_Filled_Only_By_Observations()
    {
        var path = NewInventory();

        var result = InventoryWriter.MergeDepotHistories(
            path,
            [new DepotManifestObservation(9999, 300, 3000, "2026-03-01T00:00:00Z", "yyy")],
            rederiveFromBuilds: false);

        Assert.Equal(1, result.EntriesAdded);
        Assert.Equal(
            [(300L, 3000L, "2026-03-01T00:00:00Z", "yyy"),
             (100L, 1000L, "2026-01-01T00:00:00Z", "zzz")],
            History(path, 9999));
    }

    [Fact]
    public void An_Observation_Takes_The_Inventory_Build_Date_Not_The_Capture_Time()
    {
        // A PICS capture's captured_utc trails the build's manifest date by hours. Every existing
        // entry uses the BUILD date and resolution is an ordered scan over those dates, so taking
        // the capture clock would misorder the entry against its neighbours.
        var path = NewInventory();

        InventoryWriter.MergeDepotHistories(
            path,
            [new DepotManifestObservation(9999, 200, 999_999, "2099-12-31T23:59:59Z", "yyy")],
            rederiveFromBuilds: false);

        var entry = History(path, 9999).Single(e => e.Manifest == "yyy");
        Assert.Equal("2026-02-01T00:00:00Z", entry.Date);     // builds[] date, not 2099
        Assert.Equal(2000L, entry.Change);                    // builds[] change_number, not 999999
    }

    [Fact]
    public void An_Observation_For_An_Unlisted_Build_Keeps_Its_Own_Facts()
    {
        // Fallback: the inventory cannot normalize a build it does not list, so the observation's
        // own values stand rather than the entry being dropped.
        var path = NewInventory();

        InventoryWriter.MergeDepotHistories(
            path,
            [new DepotManifestObservation(9999, 400, 4000, "2026-04-01T00:00:00Z", "yyy")],
            rederiveFromBuilds: false);

        Assert.Equal(
            (400L, 4000L, "2026-04-01T00:00:00Z", "yyy"),
            History(path, 9999)[0]);
    }

    [Fact]
    public void An_Unchanged_Manifest_Adds_No_Second_Entry()
    {
        // The resolution rule is "newest entry whose key <= the build's", so a manifest that did
        // not change is already resolved by the entry that recorded it.
        var path = NewInventory();

        var result = InventoryWriter.MergeDepotHistories(
            path,
            [new DepotManifestObservation(9999, 200, 2000, "2026-02-01T00:00:00Z", "zzz")],
            rederiveFromBuilds: false);

        Assert.True(result.NoChange);
        Assert.Single(History(path, 9999));
        Assert.Equal(100L, History(path, 9999)[0].Build);     // still the ORIGINAL, older entry
    }

    [Fact]
    public void A_Run_Of_Identical_Manifests_Keeps_Its_Oldest_Entry()
    {
        var path = NewInventory();

        // Same manifest observed at two later builds: the change happened at the earlier one.
        InventoryWriter.MergeDepotHistories(
            path,
            [new DepotManifestObservation(9999, 300, 3000, "2026-03-01T00:00:00Z", "yyy"),
             new DepotManifestObservation(9999, 200, 2000, "2026-02-01T00:00:00Z", "yyy")],
            rederiveFromBuilds: false);

        var history = History(path, 9999);
        Assert.Equal(2, history.Count);
        Assert.Equal(200L, history[0].Build);                 // the OLDER of the run survives
        Assert.Equal("yyy", history[0].Manifest);
    }

    [Fact]
    public void Build_Id_Breaks_A_Date_Tie_So_Ordering_Is_Deterministic()
    {
        // Two builds sharing a timestamp to the second is real in this corpus (19083876/19086168,
        // 23354238/23355497). Ordering by date alone picks a different representative each run.
        const string tied = """
            {
              "depots": [
                {
                  "depot_id": 2347770,
                  "role": "content",
                  "history": []
                }
              ],
              "builds": [
                {
                  "build_id": 201,
                  "date_utc": "2026-02-01T00:00:00Z",
                  "change_number": 2001,
                  "content": "later"
                },
                {
                  "build_id": 200,
                  "date_utc": "2026-02-01T00:00:00Z",
                  "change_number": 2000,
                  "content": "earlier"
                }
              ]
            }

            """;
        var path = NewInventory(tied);

        InventoryWriter.MergeDepotHistories(path, [], rederiveFromBuilds: true);

        // Higher build_id is the newer of a tie, so it leads the newest-first history.
        Assert.Equal(
            [(201L, 2001L, "2026-02-01T00:00:00Z", "later"),
             (200L, 2000L, "2026-02-01T00:00:00Z", "earlier")],
            History(path, 2347770));
    }

    [Fact]
    public void A_Build_Less_Entry_Is_Resolved_To_The_Build_Carrying_That_Manifest()
    {
        // Two such entries exist in the committed inventory: hand-entered, date_utc + manifest_id
        // only. When a recorded build carries the same manifest, the entry gains that build's
        // identity — same manifest, strictly more provenance, and it stops a change_number-keyed
        // scan from skipping the entry.
        const string buildLess = """
            {
              "depots": [
                {
                  "depot_id": 9999,
                  "role": "cs2-other",
                  "history": [
                    {
                      "date_utc": "2026-01-31T12:00:00Z",
                      "manifest_id": "bbb"
                    }
                  ]
                }
              ],
              "builds": [
                {
                  "build_id": 200,
                  "date_utc": "2026-02-01T00:00:00Z",
                  "change_number": 2000,
                  "content": "bbb"
                }
              ]
            }

            """;
        var path = NewInventory(buildLess);

        var result = InventoryWriter.MergeDepotHistories(
            path,
            [new DepotManifestObservation(9999, 200, 2000, "2026-02-01T00:00:00Z", "bbb")],
            rederiveFromBuilds: false);

        Assert.Equal(1, result.EntriesResolvedToBuilds);
        Assert.Equal(0, result.EntriesAdded);                 // resolved in place, not appended
        Assert.Equal(
            [(200L, 2000L, "2026-02-01T00:00:00Z", "bbb")],
            History(path, 9999));
    }

    [Fact]
    public void A_Build_Less_Entry_No_Build_Carries_Is_Preserved_Verbatim()
    {
        // The other committed build-less entry matches no build's content (a manifest that was
        // live between builds). It must survive untouched rather than being discarded.
        const string orphan = """
            {
              "depots": [
                {
                  "depot_id": 9999,
                  "role": "cs2-other",
                  "history": [
                    {
                      "date_utc": "2026-01-15T00:00:00Z",
                      "manifest_id": "orphan"
                    }
                  ]
                }
              ],
              "builds": [
                {
                  "build_id": 200,
                  "date_utc": "2026-02-01T00:00:00Z",
                  "change_number": 2000,
                  "content": "bbb"
                }
              ]
            }

            """;
        var path = NewInventory(orphan);

        InventoryWriter.MergeDepotHistories(
            path,
            [new DepotManifestObservation(9999, 200, 2000, "2026-02-01T00:00:00Z", "bbb")],
            rederiveFromBuilds: false);

        var history = History(path, 9999);
        Assert.Equal(2, history.Count);
        var kept = history.Single(e => e.Manifest == "orphan");
        Assert.Null(kept.Build);                              // still build-less
        Assert.Null(kept.Change);
        Assert.Equal("2026-01-15T00:00:00Z", kept.Date);
        // And the shape stays the two-key form rather than gaining nulls.
        var raw = File.ReadAllText(path);
        Assert.DoesNotContain("\"build_id\": null", raw);
        Assert.DoesNotContain("\"change_number\": null", raw);
    }

    [Fact]
    public void Merge_Is_Idempotent_And_Writes_Nothing_When_Current()
    {
        var path = NewInventory();
        InventoryWriter.MergeDepotHistories(path, [], rederiveFromBuilds: true);
        var afterFirst = File.ReadAllText(path);

        var again = InventoryWriter.MergeDepotHistories(path, [], rederiveFromBuilds: true);

        Assert.True(again.NoChange);
        Assert.Equal(afterFirst, File.ReadAllText(path));
    }

    [Fact]
    public void Dry_Run_Reports_But_Writes_Nothing()
    {
        var path = NewInventory();
        var before = File.ReadAllText(path);

        var result = InventoryWriter.MergeDepotHistories(
            path, [], rederiveFromBuilds: true, dryRun: true);

        Assert.Equal(2, result.EntriesAdded);                 // would add
        Assert.Equal(before, File.ReadAllText(path));         // wrote nothing
    }

    [Fact]
    public void Everything_Outside_Depot_History_Survives_Verbatim()
    {
        var path = NewInventory();

        InventoryWriter.MergeDepotHistories(path, [], rederiveFromBuilds: true);
        var after = File.ReadAllText(path);

        Assert.Contains("\"note\": \"keep me\"", after);
        Assert.Contains("\"app_id\": 730", after);
        Assert.Contains("\"windows-x86_64\"", after);         // depot platforms[] preserved
        Assert.Contains("\"role\": \"content\"", after);
        Assert.EndsWith("\n", after);                          // canonical trailing newline
        Assert.DoesNotContain("\r\n", after);                  // LF only
    }

    [Fact]
    public void A_Binary_Depot_Naming_Two_Platforms_Gets_No_Derivation()
    {
        // builds[].binaries is keyed by platform, so a binary depot must name exactly one. Two is
        // ambiguous and must yield nothing rather than a guess at which platform it ships.
        const string ambiguous = """
            {
              "depots": [
                {
                  "depot_id": 2347771,
                  "role": "binary",
                  "platforms": [
                    "windows-x86_64",
                    "linux-x86_64"
                  ],
                  "history": []
                }
              ],
              "builds": [
                {
                  "build_id": 200,
                  "date_utc": "2026-02-01T00:00:00Z",
                  "change_number": 2000,
                  "binaries": {
                    "windows-x86_64": "win",
                    "linux-x86_64": "lin"
                  }
                }
              ]
            }

            """;
        var path = NewInventory(ambiguous);

        var result = InventoryWriter.MergeDepotHistories(path, [], rederiveFromBuilds: true);

        Assert.True(result.NoChange);
        Assert.Empty(History(path, 2347771));
    }

    [Fact]
    public void A_Binary_Depot_Derives_From_Its_Own_Platform()
    {
        const string binary = """
            {
              "depots": [
                {
                  "depot_id": 2347773,
                  "role": "binary",
                  "platforms": [
                    "linux-x86_64"
                  ],
                  "history": []
                }
              ],
              "builds": [
                {
                  "build_id": 200,
                  "date_utc": "2026-02-01T00:00:00Z",
                  "change_number": 2000,
                  "binaries": {
                    "windows-x86_64": "win",
                    "linux-x86_64": "lin"
                  }
                }
              ]
            }

            """;
        var path = NewInventory(binary);

        InventoryWriter.MergeDepotHistories(path, [], rederiveFromBuilds: true);

        Assert.Equal("lin", History(path, 2347773).Single().Manifest);
    }

    [Fact]
    public void A_Missing_Inventory_Fails_Loud()
        => Assert.Throws<InvalidDataException>(() => InventoryWriter.MergeDepotHistories(
            Path.Combine(Path.GetTempPath(), "nope-" + Guid.NewGuid().ToString("N") + ".json"),
            [],
            rederiveFromBuilds: true));
}

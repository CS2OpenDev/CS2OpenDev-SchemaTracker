// Optional 6th (content-derived) `localization` family in the build-to-build changelog.
//
// BuildChangelogEmitter appends families[5] == "localization" ONLY when both localization source
// paths are supplied (fromLocalizationPath / toLocalizationPath). The five binary families are
// ALWAYS present and their index never shifts. This suite drives BuildChangelogEmitter directly
// (its public Build/Emit, the fixture-sized seam — no Steam, no 199 MB emit) with two hand-built
// localization.json fixtures and asserts:
//   * a 6-family changelog: families[0..4] are the five binary families in fixed order, families[5]
//     is "localization", with token added/removed/changed rows producing the expected englishValue /
//     valuesHash FieldChange rows;
//   * the 5-family shape still holds when NO localization sources are supplied;
//   * determinism: re-emitting over the same inputs is byte-identical.

using System.Text;

using Cs2SchemaTracker.Host.Artifacts;
using Cs2SchemaTracker.Host.Changelog;
using Cs2SchemaTracker.Host.Serialization;
using Cs2SchemaTracker.Schemas;

using Google.Protobuf;

using Xunit;

using Schemas = Cs2SchemaTracker.Schemas;

namespace Cs2SchemaTracker.Tests.Changelog;

public sealed class LocalizationChangelogTest
{
    private const string Platform = "linux-x86_64";
    private const string Version = "0.4.0";
    private const string FromBuild = "1000";
    private const string ToBuild = "1001";

    private static readonly string[] FiveBinaryFamilies =
        { "classes", "enums", "convars", "commands", "engine_constants" };
    private static readonly string[] ExpectedAdded = { "weapon_added" };
    private static readonly string[] ExpectedRemoved = { "weapon_removed" };

    private static readonly JsonParser Parser =
        new(JsonParser.Settings.Default.WithIgnoreUnknownFields(true));

    private static string NewWorkDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "loc-changelog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void InWorkDir(Action<string> body)
    {
        var dir = NewWorkDir();
        try
        { body(dir); }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ } }
    }

    /// <summary>Write a minimal committed set dir with the five binary-family source files.</summary>
    private static string MakeSet(string root, string buildId)
    {
        var dir = Path.Combine(root, buildId, Platform);
        Directory.CreateDirectory(dir);
        AtomicWrite.WriteCanonical(
            new Schemas.EntitySchema { SchemaVersion = Version, BuildId = buildId, Platform = Platform },
            Path.Combine(dir, "entity_schema.json"));
        AtomicWrite.WriteCanonical(
            new Schemas.ConVars { SchemaVersion = Version, BuildId = buildId, Platform = Platform },
            Path.Combine(dir, "convars.json"));
        AtomicWrite.WriteCanonical(
            new Schemas.Commands { SchemaVersion = Version, BuildId = buildId, Platform = Platform },
            Path.Combine(dir, "commands.json"));
        AtomicWrite.WriteCanonical(
            new Schemas.EngineConstants { SchemaVersion = Version, BuildId = buildId, Platform = Platform },
            Path.Combine(dir, "engine_constants.json"));
        return dir;
    }

    private static LocalizedString Token(string token, string english, params (string Lang, string Value)[] values)
    {
        var ls = new LocalizedString { Token = token, EnglishValue = english };
        foreach (var (lang, value) in values)
        {
            ls.Values.Add(new LanguageValue { Language = lang, Value = value });
        }
        return ls;
    }

    /// <summary>Write a localization.json fixture and return its path.</summary>
    private static string WriteLocalization(string dir, string name, params LocalizedString[] tokens)
    {
        var msg = new Schemas.Localization { SchemaVersion = Version, BuildId = ToBuild, Platform = Platform };
        msg.Languages.Add("english");
        msg.Languages.Add("german");
        foreach (var t in tokens)
            msg.Tokens.Add(t);
        var path = Path.Combine(dir, name);
        AtomicWrite.WriteCanonical(msg, path);
        return path;
    }

    // FROM localization:
    //   weapon_ak47        english "AK-47"              (unchanged)
    //   weapon_removed     english "Gone"               (removed in TO)
    //   weapon_changed_en  english "Old"                (english string changes)
    //   weapon_changed_val english "Same" + de "Alt"    (a per-language value changes -> valuesHash)
    private static string FromLocalization(string dir) => WriteLocalization(dir, "from.json",
        Token("weapon_ak47", "AK-47", ("english", "AK-47")),
        Token("weapon_removed", "Gone", ("english", "Gone")),
        Token("weapon_changed_en", "Old", ("english", "Old")),
        Token("weapon_changed_val", "Same", ("english", "Same"), ("german", "Alt")));

    // TO localization:
    //   weapon_ak47        english "AK-47"              (unchanged)
    //   weapon_added       english "New"                (added)
    //   weapon_changed_en  english "New"                (english string changed)
    //   weapon_changed_val english "Same" + de "Neu"    (german value changed -> valuesHash differs)
    private static string ToLocalization(string dir) => WriteLocalization(dir, "to.json",
        Token("weapon_ak47", "AK-47", ("english", "AK-47")),
        Token("weapon_added", "New", ("english", "New")),
        Token("weapon_changed_en", "New", ("english", "New")),
        Token("weapon_changed_val", "Same", ("english", "Same"), ("german", "Neu")));

    private static BuildChangelogEmitter Emitter() =>
        new(Version, Platform, FromBuild, ToBuild);

    [Fact]
    public void SixFamilyChangelog_AppendsLocalization_AsFamilies5_WithExpectedRows()
    {
        InWorkDir(root =>
        {
            var fromSet = MakeSet(root, FromBuild);
            var toSet = MakeSet(root, ToBuild);
            var fromLoc = FromLocalization(root);
            var toLoc = ToLocalization(root);

            var changelog = Emitter().Build(fromSet, toSet, fromLoc, toLoc);

            // The five binary families are ALWAYS present, in fixed order, at indices 0..4.
            Assert.Equal(6, changelog.Families.Count);
            Assert.Equal(FiveBinaryFamilies, changelog.Families.Take(5).Select(f => f.Family).ToArray());

            // families[5] is the appended content-derived localization family.
            var loc = changelog.Families[5];
            Assert.Equal(BuildChangelogEmitter.LocalizationFamily, loc.Family);

            // added / removed tokens.
            Assert.Equal(ExpectedAdded, loc.Added.ToArray());
            Assert.Equal(ExpectedRemoved, loc.Removed.ToArray());

            // changed: englishValue row for weapon_changed_en (english string flipped Old -> New).
            // The english string also lives in the per-language values map, so a valuesHash row
            // accompanies it — both FieldChange row kinds are exercised here.
            var changedEn = loc.Changed.Single(c => c.Name == "weapon_changed_en");
            var enRow = changedEn.Fields.Single(f => f.Field == "englishValue");
            Assert.Equal("Old", enRow.OldValue);
            Assert.Equal("New", enRow.NewValue);
            Assert.Contains(changedEn.Fields, f => f.Field == "valuesHash");

            // changed: valuesHash row for weapon_changed_val (german value Alt -> Neu; english unchanged).
            var changedVal = loc.Changed.Single(c => c.Name == "weapon_changed_val");
            var hashRow = changedVal.Fields.Single(f => f.Field == "valuesHash");
            Assert.NotEqual(hashRow.OldValue, hashRow.NewValue);
            Assert.NotEmpty(hashRow.OldValue);
            Assert.NotEmpty(hashRow.NewValue);
            // englishValue ("Same") did not change, so no englishValue row here.
            Assert.DoesNotContain(changedVal.Fields, f => f.Field == "englishValue");

            // An unchanged token (weapon_ak47) produces no changed entry.
            Assert.DoesNotContain(loc.Changed, c => c.Name == "weapon_ak47");
        });
    }

    [Fact]
    public void FiveFamilyChangelog_WhenNoLocalizationSources_HasNoLocalizationFamily()
    {
        InWorkDir(root =>
        {
            var fromSet = MakeSet(root, FromBuild);
            var toSet = MakeSet(root, ToBuild);

            // No localization paths supplied -> the changelog stays the five binary families.
            var changelog = Emitter().Build(fromSet, toSet);

            Assert.Equal(5, changelog.Families.Count);
            Assert.Equal(FiveBinaryFamilies, changelog.Families.Select(f => f.Family).ToArray());
            Assert.DoesNotContain(changelog.Families,
                f => f.Family == BuildChangelogEmitter.LocalizationFamily);
        });
    }

    [Fact]
    public void SixFamilyChangelog_IsDeterministic_ByteIdentical_AcrossTwoEmits()
    {
        InWorkDir(root =>
        {
            var fromSet = MakeSet(root, FromBuild);
            var toSet = MakeSet(root, ToBuild);
            var fromLoc = FromLocalization(root);
            var toLoc = ToLocalization(root);

            var a = Path.Combine(root, "a.json");
            var b = Path.Combine(root, "b.json");
            Emitter().Emit(fromSet, toSet, a, fromLoc, toLoc);
            Emitter().Emit(fromSet, toSet, b, fromLoc, toLoc);

            Assert.Equal(File.ReadAllBytes(a), File.ReadAllBytes(b));

            // And the written changelog round-trips through the proto3 JSON parser with the 6th family.
            var parsed = Parser.Parse<BuildChangelog>(
                Encoding.UTF8.GetString(File.ReadAllBytes(a)));
            Assert.Equal(6, parsed.Families.Count);
            Assert.Equal(BuildChangelogEmitter.LocalizationFamily, parsed.Families[5].Family);
        });
    }
}

// explicit-manifest spec parsing tests.
//
// fail-loud coverage: every malformed-spec path must throw
// InvalidDataException before any Steam contact. Happy path must parse
// the documented shape and order depots deterministically.

using System.IO;

using Cs2SchemaTracker.Host.Steam;

using Xunit;

namespace Cs2SchemaTracker.Tests.Steam;

public class ManifestSpecTest
{
    private const string ValidJson = """
        {
          "buildId": 23669931,
          "appId": 730,
          "depots": [
            { "depotId": 2347771, "manifestId": "8287382081622299196" },
            { "depotId": 2347770, "manifestId": "5146470907583764090" }
          ]
        }
        """;

    [Fact]
    public void Parses_valid_spec_and_orders_depots_by_id()
    {
        var spec = ManifestSpec.Parse(ValidJson);
        Assert.Equal(730u, spec.AppId);
        Assert.Equal(23669931u, spec.BuildId);
        Assert.Equal(2, spec.Depots.Count);

        // OrderedDepots must be ascending by depotId regardless of JSON order.
        var ordered = spec.OrderedDepots;
        Assert.Equal(2347770u, ordered[0].DepotId);
        Assert.Equal(5146470907583764090UL, ordered[0].ManifestId);
        Assert.Equal(2347771u, ordered[1].DepotId);
        Assert.Equal(8287382081622299196UL, ordered[1].ManifestId);
    }

    [Fact]
    public void Accepts_manifest_id_as_number_too()
    {
        var spec = ManifestSpec.Parse("""
            { "appId": 730, "buildId": 1, "depots": [ { "depotId": 5, "manifestId": 12345 } ] }
            """);
        Assert.Equal(12345UL, spec.OrderedDepots[0].ManifestId);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("[]")]                                                  // root not an object
    [InlineData("""{ "appId": 730, "buildId": 1 }""")]                  // no depots
    [InlineData("""{ "appId": 730, "buildId": 1, "depots": [] }""")]    // empty depots
    [InlineData("""{ "buildId": 1, "depots": [ { "depotId": 5, "manifestId": "9" } ] }""")] // no appId
    [InlineData("""{ "appId": 730, "depots": [ { "depotId": 5, "manifestId": "9" } ] }""")] // no buildId
    [InlineData("""{ "appId": 730, "buildId": 1, "depots": [ { "manifestId": "9" } ] }""")] // depot missing depotId
    [InlineData("""{ "appId": 730, "buildId": 1, "depots": [ { "depotId": 5 } ] }""")]       // depot missing manifestId
    [InlineData("""{ "appId": 730, "buildId": 1, "depots": [ { "depotId": 5, "manifestId": "x" } ] }""")] // bad gid
    public void Rejects_malformed_spec_fail_loud(string json)
    {
        Assert.Throws<InvalidDataException>(() => ManifestSpec.Parse(json));
    }

    [Fact]
    public void Rejects_duplicate_depot()
    {
        var json = """
            { "appId": 730, "buildId": 1, "depots": [
              { "depotId": 5, "manifestId": "1" },
              { "depotId": 5, "manifestId": "2" }
            ] }
            """;
        Assert.Throws<InvalidDataException>(() => ManifestSpec.Parse(json));
    }

    [Fact]
    public void ParseFile_missing_file_fails_loud()
    {
        var path = Path.Combine(Path.GetTempPath(), "does-not-exist-" + System.Guid.NewGuid().ToString("N") + ".json");
        Assert.Throws<InvalidDataException>(() => ManifestSpec.ParseFile(path));
    }

    [Fact]
    public void ParseFile_reads_valid_file()
    {
        var path = Path.Combine(Path.GetTempPath(), "cs2-spec-" + System.Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, ValidJson);
        try
        {
            var spec = ManifestSpec.ParseFile(path);
            Assert.Equal(23669931u, spec.BuildId);
        }
        finally
        {
            try
            { File.Delete(path); }
            catch { }
        }
    }
}

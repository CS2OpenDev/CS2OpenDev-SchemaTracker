// Schema validation (cross-artifact round-trip suite).
//
// The round-trip contract: a test suite loads every produced artifact, parses it into its
// proto message, re-serializes with canonical-form options, and asserts the re-serialization
// equals the input.
//
// For each producible artifact (ArtifactCases.All — entity_schema, gameevents, modules) we:
//   1. emit a REAL artifact to disk from a synthetic fixture (no mocks; real bytes),
//   2. read the emitted JSON back,
//   3. parse it into its generated Google.Protobuf message with STRICT settings (unknown
//      fields rejected) — a clean parse is the "validates against its schema" proof,
//   4. re-serialize the parsed message via Google.Protobuf.JsonFormatter + CanonicalJson
//      (the canonical proto3 JSON form), and
//   5. assert byte-identical to the emitted file.
//
// Step 5 is asserted unconditionally for emitters that already write canonical proto3 JSON
// (entity_schema, gameevents — both route through JsonFormatter). modules.json's emitter
// writes a non-proto3-canonical shape (uint64 as a JSON number); its byte-identical leg is a
// separately-marked, tracked defect (see Modules_ByteIdentical_RoundTrip).
// Steps 1-3 (schema validation) run for modules too — proving the artifact is schema-valid.
//
// Extensibility hook: add a future artifact (convars/commands/network_messages/provenance)
// as ONE entry in ArtifactCases.All; this suite picks it up with zero edits here.

using Cs2SchemaTracker.Host.Serialization;

using Google.Protobuf;

using Xunit;

namespace Cs2SchemaTracker.Tests.Invariants;

public class SchemaValidationTest
{
    // The canonical proto3 JSON form, identical to what every JsonFormatter-based emitter
    // writes: format-default-values on (stable complete records), two-space indent, then the
    // shared CanonicalJson sorter (sorted keys, LF, UTF-8 no BOM).
    private static readonly JsonFormatter CanonicalFormatter = new(
        JsonFormatter.Settings.Default.WithFormatDefaultValues(true).WithIndentation("  "));

    private static string ReserializeCanonical(IMessage message)
        => CanonicalJson.SerializeRawJson(CanonicalFormatter.Format(message));

    public static TheoryData<string> AllArtifacts()
    {
        var data = new TheoryData<string>();
        foreach (var c in ArtifactCases.All)
        {
            data.Add(c.FileName);
        }
        return data;
    }

    private static ArtifactCase Case(string fileName)
        => ArtifactCases.All.Single(c => c.FileName == fileName);

    private static string NewWorkDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "schemaval-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    // ---- Schema validation: every producible artifact parses into its proto message ----

    [Theory]
    [MemberData(nameof(AllArtifacts))]
    public void Every_Artifact_Validates_Against_Its_Proto_Schema(string fileName)
    {
        var c = Case(fileName);
        var workDir = NewWorkDir();
        try
        {
            var outPath = Path.Combine(workDir, c.FileName);
            c.Emit(outPath);
            Assert.True(File.Exists(outPath), $"emitter produced no {c.FileName}");

            var json = File.ReadAllText(outPath);

            // Strict parse into the generated proto3 message. Throws on any unknown field or
            // type mismatch — i.e. this fails if the artifact does not validate against its
            // schemas/*.proto definition. A clean parse IS the schema-validation proof.
            var message = c.Parse(json);
            Assert.NotNull(message);
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    // ---- Full round-trip: re-serialization is byte-identical to the emitted artifact ----
    //
    // Asserted for every emitter that writes canonical proto3 JSON. modules.json is excluded
    // here (ByteIdenticalRoundTrip == false) and covered by the marked defect test below.

    public static TheoryData<string> CanonicalArtifacts()
    {
        var data = new TheoryData<string>();
        foreach (var c in ArtifactCases.All.Where(c => c.ByteIdenticalRoundTrip))
        {
            data.Add(c.FileName);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(CanonicalArtifacts))]
    public void Canonical_Artifact_RoundTrips_Byte_Identical(string fileName)
    {
        var c = Case(fileName);
        var workDir = NewWorkDir();
        try
        {
            var outPath = Path.Combine(workDir, c.FileName);
            c.Emit(outPath);

            var emittedBytes = File.ReadAllBytes(outPath);
            var emittedJson = System.Text.Encoding.UTF8.GetString(emittedBytes);

            var message = c.Parse(emittedJson);
            var reserialized = ReserializeCanonical(message);
            var reserializedBytes = System.Text.Encoding.UTF8.GetBytes(reserialized);

            Assert.Equal(emittedBytes, reserializedBytes);
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    // ---- modules.json: byte-identical round-trip. ModuleManifestEmitter now routes
    //      through Google.Protobuf.JsonFormatter (uint64 file_size uses the proto3 string
    //      mapping), so the canonical round-trip is byte-identical. (This is also covered by
    //      the generic Canonical_Artifact_RoundTrips_Byte_Identical theory now that the
    //      ArtifactCase flips ByteIdenticalRoundTrip=true; this explicit test stays as the
    // named regression guard.)

    [Fact]
    public void Modules_ByteIdentical_RoundTrip()
    {
        var c = Case("modules.json");
        var workDir = NewWorkDir();
        try
        {
            var outPath = Path.Combine(workDir, c.FileName);
            c.Emit(outPath);

            var emittedBytes = File.ReadAllBytes(outPath);
            var message = c.Parse(System.Text.Encoding.UTF8.GetString(emittedBytes));
            var reserializedBytes = System.Text.Encoding.UTF8.GetBytes(ReserializeCanonical(message));

            Assert.Equal(emittedBytes, reserializedBytes);
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }
}

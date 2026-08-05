// pics-appinfo.json — minimal self-verifying tests for the build-level PICS appinfo emitter.
//
//
// Locks the load-bearing invariants:
// - round-trip: the emitted pics-appinfo.json parses back through the generated
//     PicsAppInfo proto3 message byte-for-byte (re-serialize == on-disk bytes).
// - determinism: captured_utc comes from the supplied (manifest/provenance) time,
//     NOT DateTime.Now; re-emitting the same capture + framing is byte-identical.
//   - The verbatim canonical body survives end-to-end as the opaque appinfo_json string.

using Cs2SchemaTracker.Host.PicsAppInfo;
using Cs2SchemaTracker.Host.Serialization;
using Cs2SchemaTracker.Host.Steam;

using Google.Protobuf;

using Xunit;

namespace Cs2SchemaTracker.Tests.PicsAppInfo;

public sealed class PicsAppInfoEmitterTest
{
    private static readonly JsonParser Parser =
        new(JsonParser.Settings.Default.WithIgnoreUnknownFields(true));

    private const string CapturedUtc = "2026-06-10T22:07:09Z";
    // A canonical body (sorted keys, LF) — what PicsAppInfoRenderer.RenderCanonicalBody would produce.
    private const string Body = "{\n  \"appinfo\": {\n    \"appid\": \"730\"\n  }\n}";

    private static PicsAppInfoCapture SampleCapture() =>
        PicsAppInfoCapture.FromFetch(appId: 730, changeNumber: 36481865, appInfoSha1: "abcd", appInfoJson: Body);

    [Fact]
    public void Emitted_File_RoundTrips_Through_Proto_ByteIdentical()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pics-emit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var outPath = Path.Combine(dir, PicsAppInfoEmitter.FileName);
            new PicsAppInfoEmitter("0.4.0").Emit("23669931", CapturedUtc, SampleCapture(), outPath);

            var onDisk = File.ReadAllText(outPath);

            // parse the emitted bytes back through the generated message, re-serialize
            // canonically, and assert byte-identity with what landed on disk.
            var parsed = Parser.Parse<Cs2SchemaTracker.Schemas.PicsAppInfo>(onDisk);
            var reSerialized = AtomicWrite.SerializeCanonical(parsed);
            Assert.Equal(onDisk, reSerialized);

            // Framing fields are carried verbatim; captured_utc is the SUPPLIED time.
            Assert.Equal("0.4.0", parsed.SchemaVersion);
            Assert.Equal("23669931", parsed.BuildId);
            Assert.Equal(730u, parsed.AppId);
            Assert.Equal("36481865", parsed.ChangeNumber);
            Assert.Equal(CapturedUtc, parsed.CapturedUtc);
            Assert.Equal("abcd", parsed.AppinfoSha1);
            Assert.Equal(Body, parsed.AppinfoJson);   // opaque body survives end-to-end.
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ReEmitting_Same_Capture_Is_ByteIdentical()
    {
        var a = AtomicWrite.SerializeCanonical(
            new PicsAppInfoEmitter("0.4.0").Build("23669931", CapturedUtc, SampleCapture()));
        var b = AtomicWrite.SerializeCanonical(
            new PicsAppInfoEmitter("0.4.0").Build("23669931", CapturedUtc, SampleCapture()));
        Assert.Equal(a, b);
    }

    [Fact]
    public void Capture_Sidecar_RoundTrips_Through_Disk()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pics-cap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            SampleCapture().WriteToCacheDir(dir);
            var read = PicsAppInfoCapture.ReadFromFile(Path.Combine(dir, PicsAppInfoCapture.FileName));
            Assert.Equal(730u, read.AppId);
            Assert.Equal("36481865", read.ChangeNumber);
            Assert.Equal("abcd", read.AppInfoSha1);
            Assert.Equal(Body, read.AppInfoJson);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}

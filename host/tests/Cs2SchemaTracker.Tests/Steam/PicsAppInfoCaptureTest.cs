// PICS appinfo CAPTURE + RENDER layer — acquire-time capture coverage.
//
// SEAM NOTE: AcquireCommand.TryCapturePicsAppInfoAsync only fires when the acquirer is the
// CONCRETE SteamAnonymousAcquirer (it alone exposes DumpAppInfoAsync) AND buildId == 0 (head/
// latest). The only injectable acquire seam is a fake ISteamAcquirer, which is NOT a
// SteamAnonymousAcquirer, so the capture branch is deliberately skipped for any fake — it is
// unreachable through the acquire fake seam without a live Steam session. The capture behavior is
// therefore pinned HERE at the PicsAppInfoRenderer + PicsAppInfoCapture layer that
// TryCapturePicsAppInfoAsync delegates to: render a KeyValue appinfo tree to the verbatim canonical
// body, build the capture (change_number + sha1 + body), and write the pics-appinfo-capture.json
// sidecar — exactly the three steps the acquire path performs after DumpAppInfoAsync returns.
//
// Re-render determinism: re-rendering/re-writing the SAME appinfo tree is byte-identical, and a
// uint64-looking manifest GID is preserved as a STRING (no float coercion) — the whole reason the
// renderer keeps every VDF leaf as a string.
//
// Deterministic: throwaway temp dirs, cleaned up in finally. No network, no real Steam.

using Cs2SchemaTracker.Host.Steam;

using SteamKit2;

using Xunit;

namespace Cs2SchemaTracker.Tests.Steam;

public sealed class PicsAppInfoCaptureTest
{
    // A uint64-looking manifest GID that would lose precision if coerced to a double. Carried as a
    // VDF string leaf -> must survive as a quoted JSON string end-to-end.
    private const string ManifestGid = "8287382081622299196";

    // Build a small appinfo KeyValues tree (the shape DumpAppInfoAsync returns), including a
    // uint64-looking manifest GID leaf nested under depots/<id>/manifests/public.
    private static KeyValue SampleAppInfo()
    {
        var root = new KeyValue("appinfo");
        root.Children.Add(new KeyValue("appid", "730"));

        var depots = new KeyValue("depots");
        var depot = new KeyValue("2347771");
        var manifests = new KeyValue("manifests");
        manifests.Children.Add(new KeyValue("public", ManifestGid));
        depot.Children.Add(manifests);
        depots.Children.Add(depot);
        root.Children.Add(depots);
        return root;
    }

    [Fact]
    public void Capture_From_Rendered_AppInfo_Writes_Sidecar_With_ChangeNumber_Sha_And_Body()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pics-acq-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // The exact sequence TryCapturePicsAppInfoAsync runs after DumpAppInfoAsync:
            var body = PicsAppInfoRenderer.RenderCanonicalBody(SampleAppInfo());
            var capture = PicsAppInfoCapture.FromFetch(
                appId: 730, changeNumber: 36481865, appInfoSha1: "deadbeefsha1", appInfoJson: body);
            capture.WriteToCacheDir(dir);

            // The sidecar landed under the acquire output dir as pics-appinfo-capture.json.
            var sidecar = Path.Combine(dir, PicsAppInfoCapture.FileName);
            Assert.True(File.Exists(sidecar), "acquire-time capture must write the sidecar");

            // Round-trips back through the reader with the three capture facts intact.
            var read = PicsAppInfoCapture.ReadFromFile(sidecar);
            Assert.Equal(730u, read.AppId);
            Assert.Equal("36481865", read.ChangeNumber);    // change_number carried as a string.
            Assert.Equal("deadbeefsha1", read.AppInfoSha1);
            Assert.Equal(body, read.AppInfoJson);           // verbatim canonical body.

            // The uint64-looking GID is a quoted STRING in the body (no float coercion).
            Assert.Contains($"\"public\": \"{ManifestGid}\"", read.AppInfoJson);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void RenderCanonicalBody_Preserves_Uint64_Gid_As_String_No_Float_Coercion()
    {
        var body = PicsAppInfoRenderer.RenderCanonicalBody(SampleAppInfo());

        // The GID is a quoted string, NOT a bare number that a double round-trip would mangle.
        Assert.Contains($"\"{ManifestGid}\"", body);
        // A float coercion of 8287382081622299196 would round to 8287382081622299136 (or render in
        // exponential form); neither corrupted form may appear.
        Assert.DoesNotContain("8287382081622299136", body);
        Assert.DoesNotContain("8.2873820816223e", body);
        // The leaf appid is likewise a string (VDF leaves are all strings).
        Assert.Contains("\"appid\": \"730\"", body);
    }

    [Fact]
    public void RenderCanonicalBody_Is_Deterministic_ByteIdentical()
    {
        // rendering the SAME appinfo tree twice yields byte-identical canonical JSON.
        var a = PicsAppInfoRenderer.RenderCanonicalBody(SampleAppInfo());
        var b = PicsAppInfoRenderer.RenderCanonicalBody(SampleAppInfo());
        Assert.Equal(a, b);
    }

    [Fact]
    public void WriteRawDump_Writes_Verbatim_Body_Under_PicsDir_Keyed_By_BuildId()
    {
        var storeRoot = Path.Combine(Path.GetTempPath(), "pics-raw-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storeRoot);
        try
        {
            var body = PicsAppInfoRenderer.RenderCanonicalBody(SampleAppInfo());
            var capture = PicsAppInfoCapture.FromFetch(
                appId: 730, changeNumber: 36481865, appInfoSha1: "deadbeefsha1", appInfoJson: body);

            capture.WriteRawDump(storeRoot, "18058822");

            var dumpPath = Path.Combine(storeRoot, PicsAppInfoCapture.RawDumpDirName, "18058822.json");
            Assert.True(File.Exists(dumpPath), "raw dump must land at <storeRoot>/_pics/<buildId>.json");
            Assert.Equal(body, File.ReadAllText(dumpPath));

            // No stray .tmp left behind after a clean write.
            Assert.False(File.Exists(dumpPath + ".tmp"));
        }
        finally
        {
            Directory.Delete(storeRoot, recursive: true);
        }
    }

    [Fact]
    public void WriteRawDump_Overwrites_An_Existing_Dump_For_The_Same_BuildId()
    {
        var storeRoot = Path.Combine(Path.GetTempPath(), "pics-raw-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storeRoot);
        try
        {
            var first = PicsAppInfoCapture.FromFetch(
                appId: 730, changeNumber: 1, appInfoSha1: "sha-old",
                appInfoJson: PicsAppInfoRenderer.RenderCanonicalBody(SampleAppInfo()));
            first.WriteRawDump(storeRoot, "18058822");

            var updatedBody = PicsAppInfoRenderer.RenderCanonicalBody(SampleAppInfo()) + "\n";
            // Simulate a re-fetch with a different (still valid) canonical body for the same build.
            var second = PicsAppInfoCapture.FromFetch(
                appId: 730, changeNumber: 2, appInfoSha1: "sha-new", appInfoJson: updatedBody);
            second.WriteRawDump(storeRoot, "18058822");

            var dumpPath = Path.Combine(storeRoot, PicsAppInfoCapture.RawDumpDirName, "18058822.json");
            Assert.Equal(updatedBody, File.ReadAllText(dumpPath));
        }
        finally
        {
            Directory.Delete(storeRoot, recursive: true);
        }
    }

    [Fact]
    public void Capture_Sidecar_ReEmit_Is_ByteIdentical()
    {
        var dirA = Path.Combine(Path.GetTempPath(), "pics-acq-a-" + Guid.NewGuid().ToString("N"));
        var dirB = Path.Combine(Path.GetTempPath(), "pics-acq-b-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dirA);
        Directory.CreateDirectory(dirB);
        try
        {
            PicsAppInfoCapture Make() => PicsAppInfoCapture.FromFetch(
                appId: 730, changeNumber: 36481865, appInfoSha1: "deadbeefsha1",
                appInfoJson: PicsAppInfoRenderer.RenderCanonicalBody(SampleAppInfo()));

            Make().WriteToCacheDir(dirA);
            Make().WriteToCacheDir(dirB);

            // the same capture written twice is byte-identical on disk.
            Assert.Equal(
                File.ReadAllBytes(Path.Combine(dirA, PicsAppInfoCapture.FileName)),
                File.ReadAllBytes(Path.Combine(dirB, PicsAppInfoCapture.FileName)));
        }
        finally
        {
            Directory.Delete(dirA, recursive: true);
            Directory.Delete(dirB, recursive: true);
        }
    }
}

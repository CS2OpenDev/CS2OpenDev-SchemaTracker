// PICS appinfo CAPTURE sidecar — the bridge between forward-acquisition (where PICS is
// available) and the committed pics-appinfo.json (emitted at promote-into-artifacts time).
//
// PICS appinfo for app 730 is CURRENT-ONLY and mutable: Valve increments the app's
// change_number on every store/depot/config edit, and there is no durable public source
// for a clean, historical PICS response. So we CAPTURE it ONCE, at forward acquisition of
// the head build, and PRESERVE the rendered canonical body in the build cache (next to
// manifest-record.json) as `pics-appinfo-capture.json`. The promote-into-artifacts path
// (extract --commit) later folds this captured body + framing into the committed
// artifacts/<build>/pics-appinfo.json. Historical re-walks have no capture -> no committed
// pics-appinfo.json (it is OPTIONAL; absence is never an omission).
//
// This sidecar is NOT a public consumer artifact (it is an internal acquisition-history
// file, like manifest-record.json), so it carries no schema_version and requires no
// schema semver bump. It holds ONLY the three capture-time facts the emitter cannot derive
// from the depot binaries: the PICS change_number, the appinfo SHA-1, and the verbatim
// canonical-JSON body. The committed framing (build_id, app_id, captured_utc) comes from
// the build's provenance/manifest at emit time.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

using Cs2SchemaTracker.Host.Serialization;

namespace Cs2SchemaTracker.Host.Steam;

/// <summary>
/// The captured-at-acquisition PICS appinfo facts for one build: the PICS change_number,
/// the appinfo SHA-1 (hex), and the verbatim canonical-JSON body. Persisted in the build
/// cache as <see cref="FileName"/> and consumed by <c>PicsAppInfoEmitter</c> at promote time.
/// </summary>
internal sealed record PicsAppInfoCapture(
    uint AppId,
    string ChangeNumber,
    string AppInfoSha1,
    string AppInfoJson)
{
    /// <summary>Default file name written into the acquire/build-cache directory.</summary>
    public const string FileName = "pics-appinfo-capture.json";

    /// <summary>
    /// The `_pics` directory name under the binaries STORE ROOT (sibling of <c>_content</c> — see
    /// <see cref="ContentStore.ContentDirName"/> and the reserved-sidecar note in
    /// <c>ContentBackfillPlanner.EnumerateCommittedTupleDirs</c>). Holds one raw dump per build_id,
    /// independent of platform.
    /// </summary>
    public const string RawDumpDirName = "_pics";

    /// <summary>
    /// Acquire-immune forward-capture directory for (build, platform): <c>cache/pics/&lt;build&gt;/&lt;platform&gt;</c>.
    /// Deliberately OUTSIDE the binaries tree — a binary depot acquire wipes-and-replaces its own
    /// outDir, so a capture co-located with the binaries would be destroyed by the extract
    /// auto-acquire. This sibling location survives it, letting <c>capture-pics</c> run BEFORE the
    /// extract and still be found by <c>extract --commit</c> (see <c>ExtractCommand.FindPicsCapture</c>).
    /// </summary>
    public static string ForwardCaptureDir(string build, string platform)
        => Path.GetFullPath(Path.Combine("cache", "pics", build, platform));

    /// <summary>
    /// Build a capture from a PICS fetch result. <paramref name="appInfoJson"/> must already be
    /// the canonical-JSON body (render it through <see cref="PicsAppInfoRenderer.RenderCanonicalBody"/>).
    /// The change_number is carried as a STRING (proto3 JSON 64-bit convention, mutable/monotonic).
    /// </summary>
    public static PicsAppInfoCapture FromFetch(uint appId, uint changeNumber, string appInfoSha1, string appInfoJson)
    {
        ArgumentException.ThrowIfNullOrEmpty(appInfoJson);
        return new PicsAppInfoCapture(
            AppId: appId,
            ChangeNumber: changeNumber.ToString(CultureInfo.InvariantCulture),
            AppInfoSha1: appInfoSha1 ?? "",
            AppInfoJson: appInfoJson);
    }

    /// <summary>Write the capture sidecar into <paramref name="cacheDir"/> (canonical JSON, atomic).</summary>
    public void WriteToCacheDir(string cacheDir)
    {
        ArgumentException.ThrowIfNullOrEmpty(cacheDir);
        Directory.CreateDirectory(cacheDir);
        var path = Path.Combine(cacheDir, FileName);
        // Write the body through CanonicalJson (sorted keys, LF, no BOM) via a sibling .tmp
        // then atomic rename so a mid-write crash never leaves a partial capture.
        var json = CanonicalJson.Serialize(ToDocument());
        var tmp = path + ".tmp";
        try
        {
            File.WriteAllBytes(tmp, System.Text.Encoding.UTF8.GetBytes(json));
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            if (File.Exists(tmp))
            { try { File.Delete(tmp); } catch { /* best effort */ } }
            throw;
        }
    }

    /// <summary>
    /// Unconditional raw-response safety net: write the verbatim canonical-JSON body to
    /// <c>&lt;storeRoot&gt;/_pics/&lt;buildId&gt;.json</c>, keyed by build_id alone (not platform — the
    /// PICS appinfo for a given build is the same regardless of the requested platform's depot set).
    /// Independent of <see cref="WriteToCacheDir"/>: callers should write this FIRST so the raw PICS
    /// response is preserved even if the curated (build, platform) sidecar write fails afterward.
    /// Atomic (sibling <c>.tmp</c> + rename), same idiom as <see cref="WriteToCacheDir"/>.
    /// </summary>
    public void WriteRawDump(string storeRoot, string buildId)
    {
        ArgumentException.ThrowIfNullOrEmpty(storeRoot);
        ArgumentException.ThrowIfNullOrEmpty(buildId);
        var dir = Path.Combine(storeRoot, RawDumpDirName);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, buildId + ".json");
        var tmp = path + ".tmp";
        try
        {
            File.WriteAllBytes(tmp, System.Text.Encoding.UTF8.GetBytes(AppInfoJson));
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            if (File.Exists(tmp))
            { try { File.Delete(tmp); } catch { /* best effort */ } }
            throw;
        }
    }

    /// <summary>
    /// Read a capture sidecar from <paramref name="path"/>. Fail-loud on a
    /// present-but-corrupt file; callers that treat ABSENCE as benign check
    /// <see cref="File.Exists(string)"/> first (the captured PICS is optional).
    /// </summary>
    public static PicsAppInfoCapture ReadFromFile(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        string json = File.ReadAllText(path);

        Document? doc;
        try
        {
            doc = JsonSerializer.Deserialize<Document>(json);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                $"{FileName} at '{path}' is not valid JSON: {ex.Message}", ex);
        }
        if (doc is null)
        {
            throw new InvalidDataException(
                $"{FileName} at '{path}' deserialized to null (empty/invalid document).");
        }
        if (string.IsNullOrEmpty(doc.AppInfoJson))
        {
            throw new InvalidDataException(
                $"{FileName} at '{path}' carries an empty appinfoJson body.");
        }

        return new PicsAppInfoCapture(
            AppId: doc.AppId,
            ChangeNumber: doc.ChangeNumber ?? "",
            AppInfoSha1: doc.AppInfoSha1 ?? "",
            AppInfoJson: doc.AppInfoJson);
    }

    private Document ToDocument() => new()
    {
        AppId = AppId,
        ChangeNumber = ChangeNumber,
        AppInfoSha1 = AppInfoSha1,
        AppInfoJson = AppInfoJson,
    };

    internal sealed class Document
    {
        [JsonPropertyName("appId")]
        public uint AppId { get; set; }

        [JsonPropertyName("changeNumber")]
        public string ChangeNumber { get; set; } = "";

        [JsonPropertyName("appInfoSha1")]
        public string AppInfoSha1 { get; set; } = "";

        [JsonPropertyName("appInfoJson")]
        public string AppInfoJson { get; set; } = "";
    }
}

// Steam PICS appinfo emitter — BUILD-LEVEL committed artifact pics-appinfo.json.
//
// Builds the public PicsAppInfo proto message (schemas/pics_appinfo.proto) from a
// forward-acquisition capture (PicsAppInfoCapture) + the build's framing facts, then writes
// canonical proto3 JSON to artifacts/<build_id>/pics-appinfo.json (BUILD-LEVEL, next to
// omissions.json — NOT under a <platform>/ dir). PICS appinfo is a single per-build-change
// fact, not a per-OS/per-walk one.
//
// CAPTURE, NOT DERIVATION. The appinfo body + change_number + sha1 come VERBATIM from the
// capture taken at forward acquisition (PICS is current-only; it cannot be re-derived from
// the depot binaries). The framing timestamp (captured_utc) comes from the build's
// provenance/manifest — NEVER DateTime.Now. The body is carried as ONE opaque
// canonical-JSON string (appinfo_json); we do not model Valve's arbitrary appinfo internals.
//
// OPTIONAL ARTIFACT. Only forward-captured builds carry this file. Its absence on a
// historical / re-walked build is NOT an omission and is never recorded in omissions.json.
//
// Determinism: build the message in memory, serialize through the shared canonical
// proto3 JSON formatter (sorted keys, LF, no BOM), atomic write (sibling .tmp -> rename).
// Re-emitting the SAME capture + framing is byte-identical and round-trips through the proto
//.

using Cs2SchemaTracker.Host.Serialization;
using Cs2SchemaTracker.Host.Steam;
using Cs2SchemaTracker.Schemas;

namespace Cs2SchemaTracker.Host.PicsAppInfo;

/// <summary>
/// Emits the build-level <c>pics-appinfo.json</c> from a forward-acquisition
/// <see cref="PicsAppInfoCapture"/> + framing facts. See file header.
/// </summary>
internal sealed class PicsAppInfoEmitter
{
    /// <summary>The committed artifact file name (build-level, next to omissions.json).</summary>
    public const string FileName = "pics-appinfo.json";

    private readonly string _schemaVersion;

    /// <param name="schemaVersion">schemas/ family version (SchemaFamily.Version).</param>
    public PicsAppInfoEmitter(string schemaVersion)
    {
        ArgumentException.ThrowIfNullOrEmpty(schemaVersion);
        _schemaVersion = schemaVersion;
    }

    /// <summary>
    /// Build the <see cref="Schemas.PicsAppInfo"/> message from a capture + framing facts (no I/O).
    /// Exposed for tests + callers that want the message without writing it.
    /// </summary>
    /// <param name="buildId">Steam build ID this capture is committed under.</param>
    /// <param name="capturedUtc">
    /// Capture/manifest time, ISO 8601 UTC, sourced from the build's provenance/manifest —
    /// NEVER DateTime.Now. May be "" when the build carries no manifest time.
    /// </param>
    /// <param name="capture">The forward-acquisition PICS capture (body + change_number + sha1).</param>
    public Schemas.PicsAppInfo Build(string buildId, string capturedUtc, PicsAppInfoCapture capture)
    {
        ArgumentException.ThrowIfNullOrEmpty(buildId);
        ArgumentNullException.ThrowIfNull(capture);
        if (string.IsNullOrEmpty(capture.AppInfoJson))
        {
            throw new InvalidDataException(
                "PicsAppInfoEmitter: the capture carries an empty appinfo body.");
        }

        return new Schemas.PicsAppInfo
        {
            SchemaVersion = _schemaVersion,
            BuildId = buildId,
            AppId = capture.AppId,
            ChangeNumber = capture.ChangeNumber ?? "",
            CapturedUtc = capturedUtc ?? "",
            AppinfoSha1 = capture.AppInfoSha1 ?? "",
            AppinfoJson = capture.AppInfoJson,
        };
    }

    /// <summary>
    /// Build the message and write <c>pics-appinfo.json</c> to <paramref name="outputPath"/>
    /// (canonical proto3 JSON, atomic). Fail-loud BEFORE any byte hits disk.
    /// </summary>
    public void Emit(string buildId, string capturedUtc, PicsAppInfoCapture capture, string outputPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(outputPath);
        var message = Build(buildId, capturedUtc, capture);
        AtomicWrite.WriteCanonical(message, outputPath);
    }
}

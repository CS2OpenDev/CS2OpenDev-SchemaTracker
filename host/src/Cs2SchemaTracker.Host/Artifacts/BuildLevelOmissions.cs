// build-level omissions.json writer (per-artifact content omissions).
//
// The full extract (ExtractCommand.RunExtract) records, AFTER a clean per-(build, platform)
// promote, that a content artifact is genuinely absent for this build because its source family
// was never shipped this era (e.g. the 2023 baseline ships zero resource/csgo_<lang>.txt, so
// localization.json has no source). These land in the build-level artifacts/<build>/omissions.json
// as a CONTENT-CARRIER PlatformOmission: { platform, reason = REASON_UNSPECIFIED, content_omissions:
// [ { artifact, reason = CONTENT_NOT_SHIPPED_THIS_ERA, notes } … ] }. ArtifactSetValidator then
// treats those absent files as ACCEPTABLE (content depot present + recorded omission) instead of
// fail-louding.
//
// A CONTENT-CARRIER is distinct from a WHOLESALE platform omission (reason != UNSPECIFIED, platform
// dir absent on disk): a content carrier coexists with a PRESENT platform dir and only annotates
// which content artifacts that present platform legitimately lacks.
//
// Invariants:
// Determinism: platform entries sorted by (platform Ordinal, reason); each carrier's
//          content_omissions sorted by artifact Ordinal. Canonical proto3 JSON, LF, UTF-8 no BOM.
// A pre-existing-but-unparseable omissions.json fails loud (never silently overwritten).
// Idempotent + minimal: only writes when THIS platform's content-carrier actually
//          changes; never rewrites an unchanged manifest (so a clean build's empty omissions.json
//          is left exactly as the rest of the pipeline produced it).

using Cs2SchemaTracker.Host.Serialization;
using Cs2SchemaTracker.Schemas;

using Google.Protobuf;
using Google.Protobuf.Collections;

namespace Cs2SchemaTracker.Host.Artifacts;

/// <summary>
/// Reconciles the per-(build, platform) content-artifact omissions into the build-level
/// omissions.json. See file header.
/// </summary>
internal static class BuildLevelOmissions
{
    private static readonly JsonParser TolerantParser =
        new(JsonParser.Settings.Default.WithIgnoreUnknownFields(true));

    private static readonly JsonFormatter Formatter = new(
        JsonFormatter.Settings.Default
            .WithFormatDefaultValues(true)
            .WithIndentation("  "));

    /// <summary>
    /// Record (or clear) <paramref name="platform"/>'s per-artifact content omissions in
    /// <c>&lt;buildDir&gt;/omissions.json</c>. Preserves every OTHER platform entry and any
    /// wholesale-platform omission. No-op (no write) when nothing about this platform's
    /// content-carrier changes — so a clean build's manifest is never gratuitously rewritten.
    /// </summary>
    public static void ReconcilePlatformContentOmissions(
        string buildDir, string buildId, string platform,
        IReadOnlyList<ContentArtifactOmission> contentOmissions)
    {
        ArgumentException.ThrowIfNullOrEmpty(buildDir);
        ArgumentException.ThrowIfNullOrEmpty(buildId);
        ArgumentException.ThrowIfNullOrEmpty(platform);
        ArgumentNullException.ThrowIfNull(contentOmissions);

        var path = Path.Combine(buildDir, ArtifactSet.OmissionsFileName);
        bool exists = File.Exists(path);

        // Nothing to record and no manifest to reconcile against ⇒ leave omissions.json creation to
        // the rest of the pipeline (a clean build with no genuine absence needs no action here).
        if (!exists && contentOmissions.Count == 0)
        {
            return;
        }

        Omissions doc;
        if (exists)
        {
            try
            {
                doc = TolerantParser.Parse<Omissions>(File.ReadAllText(path));
            }
            catch (Exception ex) when (ex is InvalidJsonException or InvalidProtocolBufferException)
            {
                throw new InvalidDataException(
                    $"BuildLevelOmissions: existing '{path}' does not parse as Omissions JSON: "
                    + $"{ex.Message}. Refusing to overwrite a corrupt build manifest.");
            }
        }
        else
        {
            doc = new Omissions { BuildId = buildId };
        }

        // A content carrier for this platform = a PlatformOmission for this platform whose
        // platform-level reason is UNSPECIFIED (a wholesale omission would set reason).
        var existingCarrier = doc.Omissions_.FirstOrDefault(o =>
            string.Equals(o.Platform, platform, StringComparison.Ordinal)
            && o.Reason == PlatformOmission.Types.Reason.Unspecified);

        var desired = contentOmissions
            .OrderBy(o => o.Artifact, StringComparer.Ordinal)
            .ToList();

        bool changed = existingCarrier is null
            ? desired.Count > 0
            : !ContentOmissionsEqual(existingCarrier.ContentOmissions, desired);
        if (!changed)
        {
            return;
        }

        if (existingCarrier is not null)
        {
            doc.Omissions_.Remove(existingCarrier);
        }
        if (desired.Count > 0)
        {
            var carrier = new PlatformOmission
            {
                Platform = platform,
                Reason = PlatformOmission.Types.Reason.Unspecified,
            };
            carrier.ContentOmissions.AddRange(desired);
            doc.Omissions_.Add(carrier);
        }

        if (string.IsNullOrEmpty(doc.BuildId))
        {
            doc.BuildId = buildId;
        }
        doc.SchemaVersion = SchemaFamily.Version;

        // deterministic platform-entry order.
        var ordered = doc.Omissions_
            .OrderBy(o => o.Platform, StringComparer.Ordinal)
            .ThenBy(o => (int)o.Reason)
            .ToList();
        doc.Omissions_.Clear();
        doc.Omissions_.AddRange(ordered);

        AtomicWrite(path, SerializeCanonical(doc));
        Console.Error.WriteLine(
            $"extract: recorded {desired.Count} content omission(s) for {platform} in {path}");
    }

    private static bool ContentOmissionsEqual(
        RepeatedField<ContentArtifactOmission> a, List<ContentArtifactOmission> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }
        for (int i = 0; i < a.Count; i++)
        {
            if (!string.Equals(a[i].Artifact, b[i].Artifact, StringComparison.Ordinal)
                || a[i].Reason != b[i].Reason
                || !string.Equals(a[i].Notes, b[i].Notes, StringComparison.Ordinal))
            {
                return false;
            }
        }
        return true;
    }

    private static void AtomicWrite(string outputPath, string json)
    {
        var fullPath = Path.GetFullPath(outputPath);
        var parent = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }
        var tmpPath = fullPath + ".tmp";
        try
        {
            File.WriteAllBytes(tmpPath, System.Text.Encoding.UTF8.GetBytes(json));
            File.Move(tmpPath, fullPath, overwrite: true);
        }
        catch
        {
            if (File.Exists(tmpPath))
            {
                try
                { File.Delete(tmpPath); }
                catch { /* best effort */ }
            }
            throw;
        }
    }

    private static string SerializeCanonical(IMessage message)
        => CanonicalJson.SerializeRawJson(Formatter.Format(message));
}

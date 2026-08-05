// On-demand rebuild of the build-on-demand localization.json (`emit-localization`).
//
// localization.json is PRODUCED on every dump but NOT committed to the tree (at ~199 MB/set it is
// 96% of the working tree). This command regenerates it on demand from the same content input, so a
// consumer who wants the full token table can rebuild it byte-identically to what was dumped.
//
// Surface (README.md — named args, matching every other host command):
//   emit-localization --build <id> --platform <P> [--out <path>] [--verify]
//   --platform defaults from appsettings (ExtractPlatform), else required.
//   --out      defaults to the build's set dir: artifacts/<id>/<platform>/localization.json.
//   --verify   after regenerating, compare sha256/size against the committed
//              provenance.localization for the (build, platform) — exit non-zero on mismatch. This
//              is the byte-verifiable on-demand rebuild path.
//
// It resolves the content depot the same way `extract` does (ExtractCommand.TryResolveContentVpk:
// content-addressed store via the build's manifest-record GID, co-located pak fallback), runs the
// SAME LocalizationEmitter, and computes the SAME canonical fingerprint. Fail-loud: an unresolvable
// content depot, a malformed source, or (under --verify) a missing/ mismatched committed fingerprint
// all exit non-zero.

using Cs2SchemaTracker.Host.Artifacts;
using Cs2SchemaTracker.Host.Config;
using Cs2SchemaTracker.Host.Localization;
using Cs2SchemaTracker.Schemas;

using Google.Protobuf;

namespace Cs2SchemaTracker.Host.Cli;

internal static class EmitLocalizationCommand
{
    private const string DefaultArtifactsRoot = "artifacts";

    private static readonly JsonParser TolerantParser =
        new(JsonParser.Settings.Default.WithIgnoreUnknownFields(true));

    public static int Run(string[] args)
    {
        if (CliArgs.HasHelpFlag(args))
        {
            PrintHelp();
            return 0;
        }

        var parsed = CliArgs.Parse(args);

        if (!parsed.TryGetValue("build", out var build) || string.IsNullOrEmpty(build))
        {
            Console.Error.WriteLine("emit-localization: --build <id> is required.");
            return 64;   // EX_USAGE
        }

        parsed.TryGetValue("platform", out var platform);
        if (string.IsNullOrEmpty(platform))
        {
            platform = HostConfig.ExtractPlatform;
        }
        if (string.IsNullOrEmpty(platform))
        {
            Console.Error.WriteLine(
                "emit-localization: --platform <linux-x86_64|windows-x86_64> is required (or set it in appsettings.json).");
            return 64;
        }
        if (!ArtifactSet.CanonicalPlatforms.Contains(platform, StringComparer.Ordinal))
        {
            Console.Error.WriteLine(
                $"emit-localization: '{platform}' is not a canonical platform " +
                $"(expected one of: {string.Join(", ", ArtifactSet.CanonicalPlatforms)}).");
            return 64;
        }

        parsed.TryGetValue("out", out var outArg);
        var outPath = string.IsNullOrEmpty(outArg)
            ? Path.GetFullPath(Path.Combine(
                DefaultArtifactsRoot, build, platform, ArtifactSet.LocalizationFileName))
            : Path.GetFullPath(outArg);

        bool verify = parsed.ContainsKey("verify");

        // Resolve the content depot the same way extract does. Fail loud with acquire guidance when
        // the content is not resolvable — localization requires it.
        if (!ExtractCommand.TryResolveContentVpk(build, platform, out var vpkPath, out var resolveError))
        {
            Console.Error.WriteLine($"emit-localization: {resolveError}");
            return 65;   // EX_DATAERR
        }

        // Regenerate the canonical localization.json (fail loud on a malformed source / missing english).
        int tokenCount = new LocalizationEmitter(SchemaFamily.Version, build, platform)
            .EmitFromVpk(vpkPath, outPath);
        var fingerprint = ExtractCommand.ComputeLocalizationFingerprint(outPath, (ulong)tokenCount);
        Console.Error.WriteLine(
            $"emit-localization: wrote {outPath} " +
            $"(tokens={tokenCount}, size={fingerprint.SizeBytes}, sha256={fingerprint.Sha256}).");

        if (!verify)
        {
            return 0;
        }

        // --verify: the byte-verifiable rebuild check. Compare the just-computed fingerprint to the
        // committed provenance.localization for this (build, platform). Any mismatch — or a missing /
        // unpopulated committed fingerprint — is fail-loud.
        var provPath = Path.GetFullPath(Path.Combine(
            DefaultArtifactsRoot, build, platform, ArtifactSet.ProvenanceFileName));
        if (!File.Exists(provPath))
        {
            Console.Error.WriteLine(
                $"emit-localization --verify: no committed provenance at '{provPath}' to verify against.");
            return 65;
        }

        Schemas.Provenance provenance;
        try
        {
            provenance = TolerantParser.Parse<Schemas.Provenance>(File.ReadAllText(provPath));
        }
        catch (Exception ex) when (ex is InvalidProtocolBufferException or InvalidJsonException)
        {
            Console.Error.WriteLine(
                $"emit-localization --verify: committed provenance '{provPath}' does not parse: {ex.Message}");
            return 65;
        }

        if (provenance.Localization is not { } committed || string.IsNullOrEmpty(committed.Sha256))
        {
            Console.Error.WriteLine(
                $"emit-localization --verify: provenance.localization is not populated for " +
                $"(build {build}, {platform}) — nothing to verify against (was localization produced this era?).");
            return 65;
        }

        if (!string.Equals(committed.Sha256, fingerprint.Sha256, StringComparison.Ordinal)
            || committed.SizeBytes != fingerprint.SizeBytes)
        {
            Console.Error.WriteLine(
                $"emit-localization --verify: MISMATCH for (build {build}, {platform}).");
            Console.Error.WriteLine(
                $"  committed: sha256={committed.Sha256} size={committed.SizeBytes} tokens={committed.TokenCount}");
            Console.Error.WriteLine(
                $"  rebuilt:   sha256={fingerprint.Sha256} size={fingerprint.SizeBytes} tokens={fingerprint.TokenCount}");
            Console.Error.WriteLine(
                "  The on-demand rebuild does NOT match what was dumped (different content input or tool version).");
            return 65;
        }

        Console.Error.WriteLine(
            $"emit-localization --verify: OK — rebuilt localization.json is byte-identical to the " +
            $"committed provenance.localization fingerprint (sha256={fingerprint.Sha256}).");
        return 0;
    }

    private static void PrintHelp()
    {
        Console.WriteLine(@"cs2-schema-tracker emit-localization — regenerate the build-on-demand localization.json.

localization.json is produced on every dump but NOT committed (at ~199 MB/set it is 96% of the
tree). This command rebuilds it on demand from the build's content, byte-identically to what was
dumped (verifiable against provenance.localization).

Usage:
  cs2-schema-tracker emit-localization --build <id> --platform <P> [--out <path>] [--verify]

Arguments:
  --build <id>     Steam build id.
  --platform <P>   linux-x86_64 or windows-x86_64 (required unless set in appsettings.json).
  --out <path>     Output path (default: artifacts/<id>/<platform>/localization.json).
  --verify         After regenerating, compare sha256/size against the committed
                   provenance.localization for the (build, platform); exit non-zero on mismatch.

Exit codes: 0 ok · 64 usage error · 65 unresolvable content / verify mismatch or missing fingerprint.");
    }
}

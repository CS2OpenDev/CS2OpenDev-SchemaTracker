// Host-owned commit planner (`commit-plan`).
//
// The host never runs git (by design). But the git-commit scripts (scripts/commit-dump.ps1,
// scripts/commit-linux-all.sh) previously re-derived, in shell, WHAT to stage, WHETHER a set was
// complete (a hand-maintained required-file list that had already drifted — it was missing
// demo_messages.json), and the commit message. This command moves all of that judgement into the
// host so the scripts shrink to a thin `git add/commit/tag` driven by one authoritative plan:
//   - COMPLETENESS: the same ArtifactSet / content-depot gating verify-artifacts uses
//     (ArtifactSetValidator.ValidateTuple) — never a second copy of the file list.
//   - MESSAGE: the commit + tag text, derived from the promoted provenance.json (schemaRevision +
//     depot ids), byte-for-byte what the scripts built.
//   - STAGING SET: the repo-relative paths to `git add` (the tuple dir, the sibling omissions.json
//     when present) plus the inventory path the script stages iff git shows it changed, and the
//     removePaths to `git rm` (a preserved data/pics-captures/<build>.json made redundant by the
//     staged build-level pics-appinfo.json).
//   - PROVENANCE FACTS: schemaRevision + the joined depot ids as structured fields, so a caller
//     composing a multi-platform message never parses them back out of commitMessage.
//
// Output is a single JSON object on stdout (the scripts parse it). Fail-loud: an incomplete set
// exits 65 (EX_DATAERR) with the violations on stderr and NO plan — the script must not commit it.
//
// Exit codes: 0 plan emitted · 64 usage error · 65 set incomplete / provenance unreadable.

using System.Text;
using System.Text.Json;

using Cs2SchemaTracker.Host.Artifacts;

using Google.Protobuf;

namespace Cs2SchemaTracker.Host.Cli;

internal static class CommitPlanCommand
{
    private const string DefaultArtifactsRoot = "artifacts";

    public static int Run(string[] args)
    {
        if (CliArgs.HasHelpFlag(args))
        {
            Console.WriteLine(@"cs2-schema-tracker commit-plan — emit the authoritative git-commit plan for one
already-promoted (build, platform) artifact set. The host does NOT run git; it produces the
plan (completeness verdict, commit/tag message, staging paths) the thin commit scripts execute.

Usage:
  cs2-schema-tracker commit-plan --build <id> --platform <p> [--artifacts <root>] [--emit <field>]

Arguments:
  --build <id>       Build id of the promoted set (required).
  --platform <p>     linux-x86_64 or windows-x86_64 (required).
  --artifacts <root> Artifacts root, repo-relative (default: artifacts).
  --emit <field>     What to print (the completeness gate runs regardless):
                       plan (default)  the full JSON object
                       commit-message  the raw commit message (real newlines)
                       tag-name        the tag ref (build/<id>)
                       tag-message      the tag message
                       stage-paths     the repo-relative paths to stage, one per line
                       inventory-path  the inventory path to stage iff git shows it changed
                     The raw fields let a shell consumer skip JSON parsing.

Behavior:
  Validates the (build, platform) set is complete using the SAME ArtifactSet / content-depot gating
  as verify-artifacts (including the changelog predecessor gate), reads the promoted provenance.json
  for the message, and emits the plan (JSON: { build, platform, stagePaths[], removePaths[],
  inventoryPath, schemaRevision, depots, commitMessage, tagName, tagMessage }). removePaths names a
  preserved data/pics-captures/<build>.json to `git rm` when the staged set carries pics-appinfo.json.

Exit codes: 0 plan emitted · 64 usage error · 65 incomplete set / unreadable provenance.");
            return 0;
        }

        var parsed = CliArgs.Parse(args);
        if (!parsed.TryGetValue("build", out var build) || string.IsNullOrEmpty(build))
        {
            Console.Error.WriteLine("commit-plan: --build <id> is required.");
            return 64;
        }
        if (!parsed.TryGetValue("platform", out var platform) || string.IsNullOrEmpty(platform))
        {
            Console.Error.WriteLine("commit-plan: --platform <linux-x86_64|windows-x86_64> is required.");
            return 64;
        }
        var artifactsRel = parsed.TryGetValue("artifacts", out var a) && !string.IsNullOrEmpty(a)
            ? a
            : DefaultArtifactsRoot;
        var emit = parsed.TryGetValue("emit", out var e) && !string.IsNullOrEmpty(e) ? e : "plan";
        if (emit is not ("plan" or "commit-message" or "tag-name" or "tag-message" or "stage-paths" or "inventory-path"))
        {
            Console.Error.WriteLine($"commit-plan: --emit '{emit}' is not valid (plan | commit-message | tag-name | tag-message | stage-paths | inventory-path).");
            return 64;
        }

        // COMPLETENESS — the single source of truth (same logic verify-artifacts runs).
        var verdict = new ArtifactSetValidator(Path.GetFullPath(artifactsRel)).ValidateTuple(build, platform);
        if (!verdict.Passed)
        {
            foreach (var viol in verdict.Violations)
            {
                Console.Error.WriteLine($"VIOLATION: {viol.Message}");
            }
            Console.Error.WriteLine(
                $"commit-plan: build '{build}' ({platform}) set is incomplete — refusing to plan a commit.");
            return 65;
        }

        // MESSAGE — from the promoted provenance.json (schemaRevision + depot ids), no network.
        var provPath = Path.Combine(Path.GetFullPath(artifactsRel), build, platform, ArtifactSet.ProvenanceFileName);
        string schemaRevision;
        string depots;
        try
        {
            var parser = new JsonParser(JsonParser.Settings.Default.WithIgnoreUnknownFields(true));
            var prov = parser.Parse<Schemas.Provenance>(File.ReadAllText(provPath));
            schemaRevision = prov.Cs2Build?.SchemaRevision ?? "";
            depots = prov.Steam is null
                ? ""
                : string.Join(",", prov.Steam.Depots.Select(d => d.DepotId));
        }
        catch (Exception ex) when (ex is IOException or InvalidProtocolBufferException or InvalidJsonException)
        {
            Console.Error.WriteLine($"commit-plan: could not read provenance '{provPath}': {ex.Message}");
            return 65;
        }

        // STAGING SET + MESSAGES. Repo-relative, forward slashes (git-friendly on every OS).
        var tupleRel = $"{artifactsRel}/{build}/{platform}";
        var omissionsRel = $"{artifactsRel}/{build}/{ArtifactSet.OmissionsFileName}";
        var stagePaths = new List<string> { tupleRel };
        if (File.Exists(Path.Combine(Path.GetFullPath(artifactsRel), build, ArtifactSet.OmissionsFileName)))
        {
            stagePaths.Add(omissionsRel);
        }
        // Build-level pics-appinfo.json (sibling of omissions.json) is emitted by extract --commit
        // ONLY when a forward-capture sidecar was present, so it is optional — stage it when it
        // exists so a captured build's PICS artifact is committed alongside the set (never dropped).
        var picsRel = $"{artifactsRel}/{build}/{PicsAppInfo.PicsAppInfoEmitter.FileName}";
        if (File.Exists(Path.Combine(Path.GetFullPath(artifactsRel), build, PicsAppInfo.PicsAppInfoEmitter.FileName)))
        {
            stagePaths.Add(picsRel);
        }
        // The fixed-path cumulative schema-evolution artifact (repo-level, one per platform). The
        // commit wrapper refreshes it (host `evolution`) after the set is promoted; stage it here so
        // its update rides in the same commit as the build that produced it. Optional: absent until
        // the platform is seeded (`evolution --full`).
        var evolutionRel = $"{artifactsRel}/{ArtifactSet.SchemaEvolutionRelativePath(platform)}";
        if (File.Exists(Path.Combine(Path.GetFullPath(artifactsRel), ArtifactSet.SchemaEvolutionRelativePath(platform))))
        {
            stagePaths.Add(evolutionRel);
        }

        var commitMessage = $"build {build} ({platform})\n\nschemaRevision={schemaRevision} depots={depots}";
        var tagName = $"build/{build}";
        var tagMessage = $"build {build} ({platform}) schemaRevision={schemaRevision}";
        var inventoryPath = Inventory.InventoryCatalog.DefaultRelativePath;

        // A preserved current-only PICS capture (data/pics-captures/<build>.json, a sibling tree of
        // the artifacts root) is redundant the moment the build-level pics-appinfo.json is staged;
        // the plan names it for `git rm` so EVERY commit path drops it in the same commit.
        var removePaths = new List<string>();
        if (stagePaths.Contains(picsRel))
        {
            var artifactsParent = Path.GetDirectoryName(
                Path.GetFullPath(artifactsRel).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var preservedFull = artifactsParent is null
                ? null
                : Path.Combine(artifactsParent, "data", "pics-captures", $"{build}.json");
            if (preservedFull is not null && File.Exists(preservedFull))
            {
                var relParent = Path.GetDirectoryName(
                    artifactsRel.TrimEnd('/', '\\').Replace('\\', '/'))?.Replace('\\', '/');
                removePaths.Add(string.IsNullOrEmpty(relParent)
                    ? $"data/pics-captures/{build}.json"
                    : $"{relParent}/data/pics-captures/{build}.json");
            }
        }

        // Raw single-field emits let a shell consumer skip JSON parsing (the gate above already ran).
        switch (emit)
        {
            case "commit-message":
                Console.Out.Write(commitMessage);
                Console.Out.Write('\n');
                return 0;
            case "tag-name":
                Console.Out.Write(tagName);
                Console.Out.Write('\n');
                return 0;
            case "tag-message":
                Console.Out.Write(tagMessage);
                Console.Out.Write('\n');
                return 0;
            case "inventory-path":
                Console.Out.Write(inventoryPath);
                Console.Out.Write('\n');
                return 0;
            case "stage-paths":
                foreach (var p in stagePaths)
                { Console.Out.Write(p); Console.Out.Write('\n'); }
                return 0;
        }

        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("build", build);
            w.WriteString("platform", platform);
            w.WriteStartArray("stagePaths");
            foreach (var p in stagePaths)
                w.WriteStringValue(p);
            w.WriteEndArray();
            w.WriteStartArray("removePaths");
            foreach (var p in removePaths)
                w.WriteStringValue(p);
            w.WriteEndArray();
            w.WriteString("inventoryPath", inventoryPath);
            w.WriteString("schemaRevision", schemaRevision);
            w.WriteString("depots", depots);
            w.WriteString("commitMessage", commitMessage);
            w.WriteString("tagName", tagName);
            w.WriteString("tagMessage", tagMessage);
            w.WriteEndObject();
        }
        Console.Out.Write(Encoding.UTF8.GetString(buffer.ToArray()));
        Console.Out.Write('\n');
        return 0;
    }
}

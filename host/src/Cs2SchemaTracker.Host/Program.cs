// Cs2SchemaTracker.Host — entry point.
//
// The public CLI surface (subcommand + argument names) is documented in README.md. Those names are
// stable; argument additions and help-text changes are non-breaking.
//
// Dispatch is System.CommandLine (first-party Microsoft CLI parser/binder). The
// RootCommand carries one Command per subcommand, each declaring its full option
// set (System.CommandLine rejects unknown options by default, so EVERY accepted
// flag below must be declared). Each handler is a thin DELEGATION layer: it
// reconstructs the canonical `--name value` string[] the existing command parser
// expects and calls the existing XxxCommand.Run(string[]). That keeps every
// command's internal logic AND its Run(args, fake[, …]) test seams intact (the
// test suite drives those seams directly). Because those seams bypass this parser,
// BuildRootCommand is exposed internally so a parser-level test can guard that every
// accepted flag is actually declared here (an undeclared flag parses fine through a
// Run seam yet is rejected by the real CLI).

using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;

using Cs2SchemaTracker.Host.Cli;
using Cs2SchemaTracker.Host.Steam;

namespace Cs2SchemaTracker.Host;

internal static class Program
{
    private const string ToolName = "cs2-schema-tracker";

    public static int Main(string[] args)
    {
        // Populate the process env from a repo-root .env if present (and only for vars not already
        // set). This is how STEAM_USERNAME / STEAM_PASSWORD reach the authenticated acquire path
        // without being passed on the command line. The loader NEVER logs the values it sets.
        DotEnv.LoadFromRepoRoot();

        RootCommand root = BuildRootCommand();

        // Compose the pipeline explicitly (rather than UseDefaults) so the
        // parse-error exit code is the documented EX_USAGE (64) instead of the
        // System.CommandLine default of 1. UseHelp + UseVersionOption give
        // `--help` / `--version` (-v preserved as a version alias). The exception
        // handler keeps fail-loud: any escaped throw exits non-zero with the message on stderr,
        // never a silent success.
        Parser parser = new CommandLineBuilder(root)
            .UseHelp()
            .UseVersionOption("--version", "-v")
            .UseParseErrorReporting(errorExitCode: 64)   // EX_USAGE — unknown subcommand / option / missing required.
            .UseExceptionHandler(
                (ex, ctx) =>
                {
                    Console.Error.WriteLine($"{ToolName}: {ex.GetType().Name}: {ex.Message}");
                    ctx.ExitCode = 1;
                },
                errorExitCode: 1)
            .Build();

        return parser.Invoke(args);
    }

    internal static RootCommand BuildRootCommand()
    {
        var root = new RootCommand(
            "cs2-schema-tracker — extract structured CS2 data from game binaries. " +
            "See README.md for the stable subcommand + argument surface.");

        root.AddCommand(BuildExtractCommand());
        root.AddCommand(BuildDiffCommand());
        root.AddCommand(BuildEvolutionCommand());
        root.AddCommand(BuildBackfillPredecessorsCommand());
        root.AddCommand(BuildEmitLocalizationCommand());
        root.AddCommand(BuildAcquireCommand());
        root.AddCommand(BuildProbeLayoutCommand());
        root.AddCommand(BuildAuditCommand());
        root.AddCommand(BuildVerifyArtifactsCommand());
        root.AddCommand(BuildDumpAppInfoCommand());
        root.AddCommand(BuildCapturePicsCommand());
        root.AddCommand(BuildContentStoreCommand());
        root.AddCommand(BuildContentBackfillCommand());
        root.AddCommand(BuildReconcileContentGidsCommand());
        root.AddCommand(BuildBackfillLocalizationCommand());
        root.AddCommand(BuildPlanCommand());
        root.AddCommand(BuildCommitPlanCommand());
        root.AddCommand(BuildRecordBuildCommand());
        root.AddCommand(BuildEmitPicsCommand());
        root.AddCommand(BuildMergeOmissionsCommand());
        root.AddCommand(BuildReconcileChangelogCommand());
        root.AddCommand(BuildVerifyNativesCommand());
        root.AddCommand(BuildVerifyEraParityCommand());

        return root;
    }

    // ---------------------------------------------------------------------------
    // verify-era-parity  (internal build tooling — NOT part of the documented CLI surface)
    //   Compare a linux-x86_64 walk's record counts to the committed windows-x86_64 artifact.
    // ---------------------------------------------------------------------------
    private static Command BuildVerifyEraParityCommand()
    {
        var cmd = new Command(
            "verify-era-parity",
            "Internal: compare a linux-x86_64 walk's record counts to the committed windows-x86_64 artifact for a build.");

        var walk = new Option<string?>("--walk", "The raw WalkerOutput protobuf a linux-x86_64 walker produced (walk --out).");
        var build = new Option<string?>("--build", "Build id whose committed windows-x86_64 set is the reference.");
        var artifacts = new Option<string?>("--artifacts", "Artifacts root, repo-relative (default: artifacts).");

        cmd.AddOption(walk);
        cmd.AddOption(build);
        cmd.AddOption(artifacts);

        cmd.SetHandler(ctx =>
        {
            var p = ctx.ParseResult;
            var a = new ArgList();
            a.AddValue("--walk", p.GetValueForOption(walk));
            a.AddValue("--build", p.GetValueForOption(build));
            a.AddValue("--artifacts", p.GetValueForOption(artifacts));
            ctx.ExitCode = VerifyEraParityCommand.Run(a.ToArray());
        });

        return cmd;
    }

    // ---------------------------------------------------------------------------
    // verify-natives  (internal build tooling — NOT part of the documented CLI surface)
    //   Assert the native bundle carries every compile-pin era's walker binary for both platforms.
    // ---------------------------------------------------------------------------
    private static Command BuildVerifyNativesCommand()
    {
        var cmd = new Command(
            "verify-natives",
            "Internal: assert natives/<platform>/<era>[.exe] carries every compile-pin era binary for both platforms.");

        var natives = new Option<string?>("--natives", "The natives/ root produced by the build-era-walkers scripts.");
        var platform = new Option<string?>("--platform", "Narrow the check to one platform (linux-x86_64|windows-x86_64); default: both.");
        var inventory = new Option<string?>("--inventory", "Inventory path (default: data/cs2-assets-inventory.json).");

        cmd.AddOption(natives);
        cmd.AddOption(platform);
        cmd.AddOption(inventory);

        cmd.SetHandler(ctx =>
        {
            var p = ctx.ParseResult;
            var a = new ArgList();
            a.AddValue("--natives", p.GetValueForOption(natives));
            a.AddValue("--platform", p.GetValueForOption(platform));
            a.AddValue("--inventory", p.GetValueForOption(inventory));
            ctx.ExitCode = VerifyNativesCommand.Run(a.ToArray());
        });

        return cmd;
    }

    // ---------------------------------------------------------------------------
    // commit-plan  (internal git tooling — NOT part of the documented CLI surface)
    //   Emit the authoritative commit plan (completeness verdict + message + staging paths) for one
    //   promoted (build, platform) set; the thin commit scripts run git against it. Host never gits.
    // ---------------------------------------------------------------------------
    private static Command BuildCommitPlanCommand()
    {
        var cmd = new Command(
            "commit-plan",
            "Internal: emit the git-commit plan (completeness + message + staging paths) for a promoted (build, platform) set.");

        var build = new Option<string?>("--build", "Build id of the promoted set.");
        var platform = new Option<string?>("--platform", "linux-x86_64 or windows-x86_64.");
        var artifacts = new Option<string?>("--artifacts", "Artifacts root, repo-relative (default: artifacts).");
        var emit = new Option<string?>("--emit", "What to print: plan (default) | commit-message | tag-name | tag-message | stage-paths | inventory-path.");

        cmd.AddOption(build);
        cmd.AddOption(platform);
        cmd.AddOption(artifacts);
        cmd.AddOption(emit);

        cmd.SetHandler(ctx =>
        {
            var p = ctx.ParseResult;
            var a = new ArgList();
            a.AddValue("--build", p.GetValueForOption(build));
            a.AddValue("--platform", p.GetValueForOption(platform));
            a.AddValue("--artifacts", p.GetValueForOption(artifacts));
            a.AddValue("--emit", p.GetValueForOption(emit));
            ctx.ExitCode = CommitPlanCommand.Run(a.ToArray());
        });

        return cmd;
    }

    // ---------------------------------------------------------------------------
    // plan  (internal build tooling — NOT part of the documented CLI surface)
    //   Project the assets inventory into the authoritative build-target selection the
    //   build/validate/bundle scripts consume (compile-pin eras, bundle-validation build set).
    // ---------------------------------------------------------------------------
    private static Command BuildPlanCommand()
    {
        var cmd = new Command(
            "plan",
            "Internal: project the assets inventory into the authoritative build-target selection (compile-pin eras / validation build set).");

        var targets = new Option<string?>("--targets", "Selection to emit: compile-pins | validation.");
        var platform = new Option<string?>("--platform", "linux-x86_64 or windows-x86_64 (scopes compile-pins' layoutSignature).");
        var format = new Option<string?>("--format", "Output format: json (default) or tsv.");
        var inventory = new Option<string?>("--inventory", "Inventory path (default: data/cs2-assets-inventory.json).");

        cmd.AddOption(targets);
        cmd.AddOption(platform);
        cmd.AddOption(format);
        cmd.AddOption(inventory);

        cmd.SetHandler(ctx =>
        {
            var p = ctx.ParseResult;
            var a = new ArgList();
            a.AddValue("--targets", p.GetValueForOption(targets));
            a.AddValue("--platform", p.GetValueForOption(platform));
            a.AddValue("--format", p.GetValueForOption(format));
            a.AddValue("--inventory", p.GetValueForOption(inventory));
            ctx.ExitCode = PlanCommand.Run(a.ToArray());
        });

        return cmd;
    }

    // ---------------------------------------------------------------------------
    // backfill-localization  (internal dev tooling — NOT part of the documented CLI surface)
    //   One-time corpus migration: populate provenance.localization + append the 6th localization
    //   changelog family from the on-disk committed localization.json files (no acquire, no re-dump).
    // ---------------------------------------------------------------------------
    private static Command BuildBackfillLocalizationCommand()
    {
        var cmd = new Command(
            "backfill-localization",
            "Internal: backfill provenance.localization + the localization changelog family from on-disk localization.json (corpus migration).");

        var artifacts = new Option<string?>("--artifacts", "Artifacts root (default: artifacts).");
        var build = new Option<string?>("--build", "Limit to specific build id(s), comma-separated (default: whole corpus).");
        var platform = new Option<string?>("--platform", "Limit to one platform (default: both canonical platforms).");

        cmd.AddOption(artifacts);
        cmd.AddOption(build);
        cmd.AddOption(platform);

        cmd.SetHandler(ctx =>
        {
            var p = ctx.ParseResult;
            var a = new ArgList();
            a.AddValue("--artifacts", p.GetValueForOption(artifacts));
            a.AddValue("--build", p.GetValueForOption(build));
            a.AddValue("--platform", p.GetValueForOption(platform));
            ctx.ExitCode = BackfillLocalizationCommand.Run(a.ToArray());
        });

        return cmd;
    }

    // ---------------------------------------------------------------------------
    // reconcile-content-gids  (internal dev tooling — NOT part of the documented CLI surface)
    //   Fix stale on-disk manifest-record 2347770 content GIDs against data/cs2-assets-inventory.json.
    // ---------------------------------------------------------------------------
    private static Command BuildContentBackfillCommand()
    {
        var cmd = new Command(
            "content-backfill",
            "Internal: fetch newly-tracked content paks (the engine core pak / resource/core.gameevents) for committed content GIDs that predate them. Dry-run by default; --execute performs the Steam fetch.");

        var binariesRoot = new Option<string?>("--binaries-root", "Store root (default: CS2_BINARIES_ROOT / appsettings BinariesRoot).");
        var execute = new Option<bool>("--execute", "Perform the Steam fetch (default: dry-run plan only, no Steam contact).");
        var limit = new Option<string?>("--limit", "Fetch at most N content GIDs this run (for a controlled rollout).");
        var delaySeconds = new Option<string?>("--delay-seconds", "Pause N seconds between GIDs to avoid Steam logon throttling (default 0).");
        var steamGuard = new Option<string?>("--steam-guard", "Steam Guard code, if credentialed auth is required for historical manifests.");

        cmd.AddOption(binariesRoot);
        cmd.AddOption(execute);
        cmd.AddOption(limit);
        cmd.AddOption(delaySeconds);
        cmd.AddOption(steamGuard);

        cmd.SetHandler(ctx =>
        {
            var p = ctx.ParseResult;
            var a = new ArgList();
            a.AddValue("--binaries-root", p.GetValueForOption(binariesRoot));
            a.AddFlag("--execute", p.GetValueForOption(execute));
            a.AddValue("--limit", p.GetValueForOption(limit));
            a.AddValue("--delay-seconds", p.GetValueForOption(delaySeconds));
            a.AddValue("--steam-guard", p.GetValueForOption(steamGuard));
            ctx.ExitCode = ContentBackfillCommand.Run(a.ToArray());
        });

        return cmd;
    }

    private static Command BuildReconcileContentGidsCommand()
    {
        var cmd = new Command(
            "reconcile-content-gids",
            "Internal: reconcile per-build manifest-record 2347770 content GIDs against the inventory's authoritative builds[].content.");

        var binariesRoot = new Option<string?>("--binaries-root", "Store root (default: CS2_BINARIES_ROOT / appsettings BinariesRoot).");
        var inventory = new Option<string?>("--inventory", "Inventory path (default: data/cs2-assets-inventory.json).");
        var check = new Option<bool>("--check", "Report-only: drift + cross-platform disagreement (exit 1 on drift). Default.");
        var apply = new Option<bool>("--apply", "Rewrite each stale record's 2347770 GID to the authoritative inventory value.");
        var build = new Option<string?>("--build", "Limit to one build id.");
        var platform = new Option<string?>("--platform", "Limit to one platform (linux-x86_64 / windows-x86_64).");

        cmd.AddOption(binariesRoot);
        cmd.AddOption(inventory);
        cmd.AddOption(check);
        cmd.AddOption(apply);
        cmd.AddOption(build);
        cmd.AddOption(platform);

        cmd.SetHandler(ctx =>
        {
            var p = ctx.ParseResult;
            var a = new ArgList();
            a.AddValue("--binaries-root", p.GetValueForOption(binariesRoot));
            a.AddValue("--inventory", p.GetValueForOption(inventory));
            a.AddFlag("--check", p.GetValueForOption(check));
            a.AddFlag("--apply", p.GetValueForOption(apply));
            a.AddValue("--build", p.GetValueForOption(build));
            a.AddValue("--platform", p.GetValueForOption(platform));
            ctx.ExitCode = ReconcileContentGidsCommand.Run(a.ToArray());
        });

        return cmd;
    }

    // ---------------------------------------------------------------------------
    // content-store  (internal dev tooling — NOT part of the documented CLI surface)
    //   content-store migrate: trim co-located content paks into _content/<gid>, validate, reclaim.
    // ---------------------------------------------------------------------------
    private static Command BuildContentStoreCommand()
    {
        var cmd = new Command(
            "content-store",
            "Internal: manage the content-addressed trimmed content store (_content/<gid>).");

        var migrate = new Command(
            "migrate",
            "Trim each build's co-located content pak into _content/<gid>, validate byte-identical, then (with --reclaim) delete the co-located pak.");

        var binariesRoot = new Option<string?>("--binaries-root", "Store root (default: CS2_BINARIES_ROOT / appsettings BinariesRoot).");
        var build = new Option<string?>("--build", "Limit to one build id.");
        var platform = new Option<string?>("--platform", "Limit to one platform (linux-x86_64 / windows-x86_64).");
        var force = new Option<bool>("--force", "Re-trim even if _content/<gid> already exists.");
        var reclaim = new Option<bool>("--reclaim", "Delete co-located pak01_*.vpk AFTER a validated trim (default: off).");

        migrate.AddOption(binariesRoot);
        migrate.AddOption(build);
        migrate.AddOption(platform);
        migrate.AddOption(force);
        migrate.AddOption(reclaim);

        migrate.SetHandler(ctx =>
        {
            var p = ctx.ParseResult;
            var a = new ArgList();
            a.AddValue("--binaries-root", p.GetValueForOption(binariesRoot));
            a.AddValue("--build", p.GetValueForOption(build));
            a.AddValue("--platform", p.GetValueForOption(platform));
            a.AddFlag("--force", p.GetValueForOption(force));
            a.AddFlag("--reclaim", p.GetValueForOption(reclaim));
            // ContentStoreCommand.Run expects the verb as the first token.
            var args = new List<string> { "migrate" };
            args.AddRange(a.ToArray());
            ctx.ExitCode = ContentStoreCommand.Run(args.ToArray());
        });

        cmd.AddCommand(migrate);
        return cmd;
    }

    // ---------------------------------------------------------------------------
    // extract
    //          + the merged multi-build / batch / --commit promotion (formerly `rewalk`).
    // ---------------------------------------------------------------------------
    private static Command BuildExtractCommand()
    {
        var cmd = new Command(
            "extract",
            "Full extraction for one or more (build, platform); off-repo by default, --commit promotes into artifacts/.");

        // Selection (exactly one family — ExtractCommand enforces mutual exclusivity). --build is a
        // multi-value option: a SINGLE value is the forward path; TWO OR MORE is a batch.
        var build = new Option<string[]>("--build", "Steam build ID, or 'latest' for current head. Repeatable (single = forward, many = batch).") { AllowMultipleArgumentsPerToken = false };
        var all = new Option<bool>("--all", "BATCH over every build in the inventory for the platform (data/cs2-assets-inventory.json builds[]).");
        var backfill = new Option<bool>("--backfill", "BATCH over every committed build MISSING this platform's set whose input binaries are on disk (produce the missing-platform sets).");
        var era = new Option<string?>("--era", "BATCH over every inventory build in the named era (data/cs2-assets-inventory.json eras[]).");
        var pin = new Option<string?>("--pin", "BATCH over every inventory build whose hl2sdk pin starts with this sha.");
        var onlyExisting = new Option<bool>("--only-existing-builds", "Modifier for --all/--era/--pin: restrict to builds already committed for the platform (the legacy re-walk-only behavior).");

        var platform = new Option<string?>("--platform", "Target platform: linux-x86_64 or windows-x86_64 (or set ExtractPlatform in appsettings.json).");
        var outOpt = new Option<string?>("--out", "Off-repo output ROOT; per-build sets go to <dir>/<build>/<platform> (default: ./extract-out/). Ignored with --commit.");
        var commit = new Option<bool>("--commit", "Promote the produced set INTO artifacts/<build>/<platform> (clobber; NO git). Fires build-level pics-appinfo + inventory upsert.");
        var verify = new Option<bool>("--verify", "Byte-compare the produced CORE set to the committed artifacts/ set (schemaVersion/toolGitSha normalized).");
        var noGate = new Option<bool>("--no-gate", "Disable the era-aware entity_schema class-count sanity gate (on by default).");
        var force = new Option<bool>("--force", "Re-walk builds whose off-repo output already exists (default: skip them).");
        var noAcquire = new Option<bool>("--no-acquire", "Do NOT auto-acquire missing input binaries; fail loud with guidance instead.");
        var noChangelog = new Option<bool>("--no-changelog", "Do NOT emit the inline build-to-build changelog.json.");
        var noLocalizationChangelog = new Option<bool>("--no-localization-changelog", "Emit the changelog WITHOUT the content-derived localization family (the five binary families only). For the forward-capture path, where the predecessor build's content is not re-acquirable.");
        var singleWalk = new Option<bool>("--single-walk", "Disable the commit-path determinism gate (armed by default under --commit): walk once instead of twice.");
        var allowMixedWalkers = new Option<bool>("--allow-mixed-walkers", "Bypass the commit-path walker identity gate (exit 78) on a mixed/unverified per-era walker set; warns loudly instead. Never use for a corpus-committing run.");

        // Undocumented descriptor-only development hook (not part of the public surface); declared so it parses.
        var binaries = new Option<string?>("--binaries", "Dev hook: run ONLY the descriptor extractor over this dir.");

        cmd.AddOption(build);
        cmd.AddOption(all);
        cmd.AddOption(backfill);
        cmd.AddOption(era);
        cmd.AddOption(pin);
        cmd.AddOption(onlyExisting);
        cmd.AddOption(platform);
        cmd.AddOption(outOpt);
        cmd.AddOption(commit);
        cmd.AddOption(verify);
        cmd.AddOption(noGate);
        cmd.AddOption(force);
        cmd.AddOption(noAcquire);
        cmd.AddOption(noChangelog);
        cmd.AddOption(noLocalizationChangelog);
        cmd.AddOption(singleWalk);
        cmd.AddOption(allowMixedWalkers);
        cmd.AddOption(binaries);

        cmd.SetHandler(ctx =>
        {
            var p = ctx.ParseResult;
            var a = new ArgList();
            foreach (var b in p.GetValueForOption(build) ?? Array.Empty<string>())
            {
                a.AddValue("--build", b);
            }
            a.AddFlag("--all", p.GetValueForOption(all));
            a.AddFlag("--backfill", p.GetValueForOption(backfill));
            a.AddFlag("--only-existing-builds", p.GetValueForOption(onlyExisting));
            a.AddValue("--era", p.GetValueForOption(era));
            a.AddValue("--pin", p.GetValueForOption(pin));
            a.AddValue("--platform", p.GetValueForOption(platform));
            a.AddValue("--out", p.GetValueForOption(outOpt));
            a.AddFlag("--commit", p.GetValueForOption(commit));
            a.AddFlag("--verify", p.GetValueForOption(verify));
            a.AddFlag("--no-gate", p.GetValueForOption(noGate));
            a.AddFlag("--force", p.GetValueForOption(force));
            a.AddFlag("--no-acquire", p.GetValueForOption(noAcquire));
            a.AddFlag("--no-changelog", p.GetValueForOption(noChangelog));
            a.AddFlag("--no-localization-changelog", p.GetValueForOption(noLocalizationChangelog));
            a.AddFlag("--single-walk", p.GetValueForOption(singleWalk));
            a.AddFlag("--allow-mixed-walkers", p.GetValueForOption(allowMixedWalkers));
            a.AddValue("--binaries", p.GetValueForOption(binaries));
            ctx.ExitCode = ExtractCommand.Run(a.ToArray());
        });

        return cmd;
    }

    // ---------------------------------------------------------------------------
    // diff  (build-to-build changelog; emits committed changelog.json)
    // ---------------------------------------------------------------------------
    private static Command BuildDiffCommand()
    {
        var cmd = new Command(
            "diff",
            "Build-to-build changelog between two committed builds; emits committed changelog.json under the newer build.");

        var from = new Option<string?>("--from", "Predecessor (baseline) build id (changelog.from_build).");
        var to = new Option<string?>("--to", "Newer build id (changelog.to_build); the build the file is committed under.");
        var platform = new Option<string?>("--platform", "Target platform: linux-x86_64 or windows-x86_64 (or set in appsettings.json).");
        var artifacts = new Option<string?>("--artifacts", "Artifacts root (default: artifacts).");

        cmd.AddOption(from);
        cmd.AddOption(to);
        cmd.AddOption(platform);
        cmd.AddOption(artifacts);

        cmd.SetHandler(ctx =>
        {
            var p = ctx.ParseResult;
            var a = new ArgList();
            a.AddValue("--from", p.GetValueForOption(from));
            a.AddValue("--to", p.GetValueForOption(to));
            a.AddValue("--platform", p.GetValueForOption(platform));
            a.AddValue("--artifacts", p.GetValueForOption(artifacts));
            ctx.ExitCode = DiffCommand.Run(a.ToArray());
        });

        return cmd;
    }

    // ---------------------------------------------------------------------------
    // evolution  (cumulative schema-evolution artifact; emits schema_evolution/<platform>.json)
    // ---------------------------------------------------------------------------
    private static Command BuildEvolutionCommand()
    {
        var cmd = new Command(
            "evolution",
            "Cumulative schema-evolution artifact for a platform; emits schema_evolution/<platform>.json.");

        var platform = new Option<string?>("--platform", "Target platform: linux-x86_64 or windows-x86_64 (or set in appsettings.json).");
        var artifacts = new Option<string?>("--artifacts", "Artifacts root (default: artifacts).");
        var full = new Option<bool>("--full", "Force a from-scratch backfill (default: incremental when a contiguous prior exists).");

        cmd.AddOption(platform);
        cmd.AddOption(artifacts);
        cmd.AddOption(full);

        cmd.SetHandler(ctx =>
        {
            var p = ctx.ParseResult;
            var a = new ArgList();
            a.AddValue("--platform", p.GetValueForOption(platform));
            a.AddValue("--artifacts", p.GetValueForOption(artifacts));
            a.AddFlag("--full", p.GetValueForOption(full));
            ctx.ExitCode = EvolutionCommand.Run(a.ToArray());
        });

        return cmd;
    }

    // ---------------------------------------------------------------------------
    // backfill-predecessors  (maintenance: (re)derive builds[].predecessor in the inventory)
    // ---------------------------------------------------------------------------
    private static Command BuildBackfillPredecessorsCommand()
    {
        var cmd = new Command(
            "backfill-predecessors",
            "Maintenance: (re)derive builds[].predecessor in data/cs2-assets-inventory.json (idempotent).");

        var inventory = new Option<string?>("--inventory", "Inventory path (default: CS2_INVENTORY_PATH / appsettings / repo-root data/cs2-assets-inventory.json).");
        cmd.AddOption(inventory);

        cmd.SetHandler(ctx =>
        {
            var p = ctx.ParseResult;
            var a = new ArgList();
            a.AddValue("--inventory", p.GetValueForOption(inventory));
            ctx.ExitCode = BackfillPredecessorsCommand.Run(a.ToArray());
        });

        return cmd;
    }

    // ---------------------------------------------------------------------------
    // emit-localization  (on-demand rebuild of the build-on-demand localization.json)
    // ---------------------------------------------------------------------------
    private static Command BuildEmitLocalizationCommand()
    {
        var cmd = new Command(
            "emit-localization",
            "Regenerate the build-on-demand localization.json from a build's content; --verify checks it against provenance.localization.");

        var build = new Option<string?>("--build", "Steam build id.");
        var platform = new Option<string?>("--platform", "Target platform: linux-x86_64 or windows-x86_64 (or set in appsettings.json).");
        var outOpt = new Option<string?>("--out", "Output path (default: artifacts/<id>/<platform>/localization.json).");
        var verify = new Option<bool>("--verify", "After regenerating, compare sha256/size against the committed provenance.localization; non-zero on mismatch.");

        cmd.AddOption(build);
        cmd.AddOption(platform);
        cmd.AddOption(outOpt);
        cmd.AddOption(verify);

        cmd.SetHandler(ctx =>
        {
            var p = ctx.ParseResult;
            var a = new ArgList();
            a.AddValue("--build", p.GetValueForOption(build));
            a.AddValue("--platform", p.GetValueForOption(platform));
            a.AddValue("--out", p.GetValueForOption(outOpt));
            a.AddFlag("--verify", p.GetValueForOption(verify));
            ctx.ExitCode = EmitLocalizationCommand.Run(a.ToArray());
        });

        return cmd;
    }

    // ---------------------------------------------------------------------------
    // acquire
    // ---------------------------------------------------------------------------
    private static Command BuildAcquireCommand()
    {
        var cmd = new Command("acquire", "Steam depot fetch + manifest verify for one (build, platform), or a batch over the assets inventory.");

        // --build is a multi-value option: a SINGLE value is the unchanged single-(build,platform)
        // acquire; TWO OR MORE values (or --all) engages the inventory-driven BATCH mode (historical
        // binary backfill).
        var build = new Option<string[]>("--build", "Steam build ID, or 'latest' (required unless --from-manifest). Repeatable for a batch over specific builds.") { AllowMultipleArgumentsPerToken = false };
        var all = new Option<bool>("--all", "BATCH: acquire every inventory build that has a binary manifest for the platform(s).");
        var force = new Option<bool>("--force", "BATCH: re-acquire a (build, platform) even if its output is already present (default: skip).");
        var inventory = new Option<string?>("--inventory", "BATCH: path to the assets inventory (default: data/cs2-assets-inventory.json).");
        var platform = new Option<string?>("--platform", "Target platform: linux-x86_64 or windows-x86_64.");
        var outOpt = new Option<string?>("--out", "Output directory (default: CS2_BINARIES_ROOT/<build>/<platform> when set, else cache/binaries/<build>/<platform>).");
        var fromManifest = new Option<string?>("--from-manifest", "Explicit (depot->manifestId) spec.json for a specific historical build.");
        var fromProvenance = new Option<string?>("--from-provenance", "Re-acquire the exact inputs pinned by a committed provenance.json, then SHA-256-verify each.");
        var guardCode = new Option<string?>("--guard-code", "One-time Steam Guard code to seed an authenticated session.");

        var auth = new Option<bool>("--auth", "Use an authenticated account (env STEAM_USERNAME / STEAM_PASSWORD).");
        var probe = new Option<bool>("--probe", "Manifest-level reachability check; no bulk download.");
        var probeChunk = new Option<bool>("--probe-chunk", "With --probe: also pull one sample chunk per depot.");
        var content = new Option<bool>("--content", "Target the cross-platform content depot (2347770) instead of the binary depot.");
        var dirOnly = new Option<bool>("--dir-only", "With --content: fetch only game/csgo/pak01_dir.vpk and stop.");
        var fullPak = new Option<bool>("--full-pak", "With --content: fetch the whole pak01_*.vpk set (fallback).");
        var printGameEventsCrc = new Option<bool>("--print-gameevents-crc", "With --content: print fetched .gameevents CRC32s to stdout.");
        // Additive flag (README.md documents it in prose): binary-depot, loadable-binaries-only
        // acquire for the backfill. Declared so it parses.
        var binariesOnly = new Option<bool>("--binaries-only", "Fetch ONLY the loadable native binaries from the binary depot (backfill).");
        // Workshop Tools co-location (windows-x86_64 only): the editor-DLL slice of depot 2347779
        // merged into the same per-build windows binaries dir. DEFAULT ON for windows (schema
        // coverage); --tools is now a redundant-but-accepted explicit (fail-loud) request, --no-tools
        // opts out entirely.
        var toolsOpt = new Option<bool>("--tools", "EXPLICITLY fetch the Workshop Tools (2347779) editor-DLL slice (windows-x86_64 only; fail-loud on failure). Already the default on windows — see --no-tools.");
        var noToolsOpt = new Option<bool>("--no-tools", "Opt out of the default Workshop Tools (2347779) editor-DLL leg on windows.");
        // Cache-resolution overrides (default = cache-first -> Steam-fallback).
        var cacheOnly = new Option<bool>("--cache-only", "Resolve only from the local binary cache; never contact Steam (fail if absent).");
        var noCache = new Option<bool>("--no-cache", "Skip the local cache; force a fresh Steam download and refresh the cache on success.");

        cmd.AddOption(build);
        cmd.AddOption(all);
        cmd.AddOption(force);
        cmd.AddOption(inventory);
        cmd.AddOption(platform);
        cmd.AddOption(outOpt);
        cmd.AddOption(fromManifest);
        cmd.AddOption(fromProvenance);
        cmd.AddOption(guardCode);
        cmd.AddOption(auth);
        cmd.AddOption(probe);
        cmd.AddOption(probeChunk);
        cmd.AddOption(content);
        cmd.AddOption(dirOnly);
        cmd.AddOption(fullPak);
        cmd.AddOption(printGameEventsCrc);
        cmd.AddOption(binariesOnly);
        cmd.AddOption(toolsOpt);
        cmd.AddOption(noToolsOpt);
        cmd.AddOption(cacheOnly);
        cmd.AddOption(noCache);

        cmd.SetHandler(ctx =>
        {
            var p = ctx.ParseResult;
            var a = new ArgList();
            foreach (var b in p.GetValueForOption(build) ?? Array.Empty<string>())
            {
                a.AddValue("--build", b);
            }
            a.AddFlag("--all", p.GetValueForOption(all));
            a.AddFlag("--force", p.GetValueForOption(force));
            a.AddValue("--inventory", p.GetValueForOption(inventory));
            a.AddValue("--platform", p.GetValueForOption(platform));
            a.AddValue("--out", p.GetValueForOption(outOpt));
            a.AddValue("--from-manifest", p.GetValueForOption(fromManifest));
            a.AddValue("--from-provenance", p.GetValueForOption(fromProvenance));
            a.AddValue("--guard-code", p.GetValueForOption(guardCode));
            a.AddFlag("--auth", p.GetValueForOption(auth));
            a.AddFlag("--probe", p.GetValueForOption(probe));
            a.AddFlag("--probe-chunk", p.GetValueForOption(probeChunk));
            a.AddFlag("--content", p.GetValueForOption(content));
            a.AddFlag("--dir-only", p.GetValueForOption(dirOnly));
            a.AddFlag("--full-pak", p.GetValueForOption(fullPak));
            a.AddFlag("--print-gameevents-crc", p.GetValueForOption(printGameEventsCrc));
            a.AddFlag("--binaries-only", p.GetValueForOption(binariesOnly));
            a.AddFlag("--tools", p.GetValueForOption(toolsOpt));
            a.AddFlag("--no-tools", p.GetValueForOption(noToolsOpt));
            a.AddFlag("--cache-only", p.GetValueForOption(cacheOnly));
            a.AddFlag("--no-cache", p.GetValueForOption(noCache));
            ctx.ExitCode = AcquireCommand.Run(a.ToArray());
        });

        return cmd;
    }

    // ---------------------------------------------------------------------------
    // probe-layout
    // ---------------------------------------------------------------------------
    private static Command BuildProbeLayoutCommand()
    {
        var cmd = new Command("probe-layout", "Report the schema-system layout signature; non-zero on unknown.");

        var binaries = new Option<string?>("--binaries", "Directory containing the Source 2 DLLs to probe.");
        cmd.AddOption(binaries);

        cmd.SetHandler(ctx =>
        {
            var p = ctx.ParseResult;
            var a = new ArgList();
            a.AddValue("--binaries", p.GetValueForOption(binaries));
            ctx.ExitCode = ProbeLayoutCommand.Run(a.ToArray());
        });

        return cmd;
    }

    // ---------------------------------------------------------------------------
    // audit
    // ---------------------------------------------------------------------------
    private static Command BuildAuditCommand()
    {
        var cmd = new Command("audit", "Regenerate registry_audit.json deterministically.");

        var artifacts = new Option<string?>("--artifacts", "Path to the (build, platform) artifact directory.");
        cmd.AddOption(artifacts);

        cmd.SetHandler(ctx =>
        {
            var p = ctx.ParseResult;
            var a = new ArgList();
            a.AddValue("--artifacts", p.GetValueForOption(artifacts));
            ctx.ExitCode = AuditCommand.Run(a.ToArray());
        });

        return cmd;
    }

    // ---------------------------------------------------------------------------
    // verify-artifacts  (all-or-nothing completeness validator; host-owned)
    // ---------------------------------------------------------------------------
    private static Command BuildVerifyArtifactsCommand()
    {
        var cmd = new Command(
            "verify-artifacts",
            "Assert committed (build, platform) artifact sets are a legal all-or-nothing shape (read-only).");

        var artifacts = new Option<string?>("--artifacts", "Artifacts root (default: artifacts). With no --build/--changed-paths, validates every build dir under it.");
        var build = new Option<string[]>("--build", "Validate a specific build directory (repeatable).") { AllowMultipleArgumentsPerToken = false };
        var changedPaths = new Option<string[]>("--changed-paths", "Repo-relative paths a CI diff touched (newline/comma/space separated; repeatable). Build ids under the root are extracted.") { AllowMultipleArgumentsPerToken = false };
        var inventory = new Option<string?>("--inventory", "Inventory path; when given, also assert the inventory predecessor chain agrees with the on-disk rule (predecessor-drift check).");

        cmd.AddOption(artifacts);
        cmd.AddOption(build);
        cmd.AddOption(changedPaths);
        cmd.AddOption(inventory);

        cmd.SetHandler(ctx =>
        {
            var p = ctx.ParseResult;
            var a = new ArgList();
            a.AddValue("--artifacts", p.GetValueForOption(artifacts));
            foreach (var b in p.GetValueForOption(build) ?? Array.Empty<string>())
            {
                a.AddValue("--build", b);
            }
            foreach (var cp in p.GetValueForOption(changedPaths) ?? Array.Empty<string>())
            {
                a.AddValue("--changed-paths", cp);
            }
            a.AddValue("--inventory", p.GetValueForOption(inventory));
            ctx.ExitCode = VerifyArtifactsCommand.Run(a.ToArray());
        });

        return cmd;
    }

    // ---------------------------------------------------------------------------
    // record-build  (forward-capture tooling: the commit job's inventory writer)
    // ---------------------------------------------------------------------------
    private static Command BuildRecordBuildCommand()
    {
        var cmd = new Command(
            "record-build",
            "Append or fact-merge one build's assets-inventory row from the promoted provenance.json in the checkout.");

        var build = new Option<string?>("--build", "Build id whose promoted set is on disk.");
        var platform = new Option<string?>("--platform", "linux-x86_64 or windows-x86_64.");
        var inventory = new Option<string?>("--inventory", "Inventory path (default: the repo's data/cs2-assets-inventory.json).");

        cmd.AddOption(build);
        cmd.AddOption(platform);
        cmd.AddOption(inventory);

        cmd.SetHandler(ctx =>
        {
            var p = ctx.ParseResult;
            var a = new ArgList();
            a.AddValue("--build", p.GetValueForOption(build));
            a.AddValue("--platform", p.GetValueForOption(platform));
            a.AddValue("--inventory", p.GetValueForOption(inventory));
            ctx.ExitCode = RecordBuildCommand.Run(a.ToArray());
        });

        return cmd;
    }

    // ---------------------------------------------------------------------------
    // emit-pics  (forward-capture tooling: build-level pics-appinfo.json from a capture file)
    // ---------------------------------------------------------------------------
    private static Command BuildEmitPicsCommand()
    {
        var cmd = new Command(
            "emit-pics",
            "Write artifacts/<build>/pics-appinfo.json from an explicit PICS capture file (sidecar format).");

        var build = new Option<string?>("--build", "Build id to commit the capture under.");
        var capture = new Option<string?>("--capture", "The capture file to emit from.");
        var artifacts = new Option<string?>("--artifacts", "Artifacts root (default: artifacts).");

        cmd.AddOption(build);
        cmd.AddOption(capture);
        cmd.AddOption(artifacts);

        cmd.SetHandler(ctx =>
        {
            var p = ctx.ParseResult;
            var a = new ArgList();
            a.AddValue("--build", p.GetValueForOption(build));
            a.AddValue("--capture", p.GetValueForOption(capture));
            a.AddValue("--artifacts", p.GetValueForOption(artifacts));
            ctx.ExitCode = EmitPicsCommand.Run(a.ToArray());
        });

        return cmd;
    }

    // ---------------------------------------------------------------------------
    // merge-omissions  (forward-capture tooling: per-platform carrier merge)
    // ---------------------------------------------------------------------------
    private static Command BuildMergeOmissionsCommand()
    {
        var cmd = new Command(
            "merge-omissions",
            "Fold one platform's content-omission carrier from an external omissions.json into the build-level manifest.");

        var build = new Option<string?>("--build", "Build id whose manifest is merged into.");
        var platform = new Option<string?>("--platform", "The platform whose carrier is taken from --from.");
        var from = new Option<string?>("--from", "The source omissions.json (a leg upload). Absent = empty carrier.");
        var artifacts = new Option<string?>("--artifacts", "Artifacts root (default: artifacts).");

        cmd.AddOption(build);
        cmd.AddOption(platform);
        cmd.AddOption(from);
        cmd.AddOption(artifacts);

        cmd.SetHandler(ctx =>
        {
            var p = ctx.ParseResult;
            var a = new ArgList();
            a.AddValue("--build", p.GetValueForOption(build));
            a.AddValue("--platform", p.GetValueForOption(platform));
            a.AddValue("--from", p.GetValueForOption(from));
            a.AddValue("--artifacts", p.GetValueForOption(artifacts));
            ctx.ExitCode = MergeOmissionsCommand.Run(a.ToArray());
        });

        return cmd;
    }

    // ---------------------------------------------------------------------------
    // reconcile-changelog  (forward-capture tooling: from_build vs the true tip's predecessor)
    // ---------------------------------------------------------------------------
    private static Command BuildReconcileChangelogCommand()
    {
        var cmd = new Command(
            "reconcile-changelog",
            "Regenerate one set's changelog.json when its from_build disagrees with the tree's committed predecessor.");

        var build = new Option<string?>("--build", "Build id whose set is on disk.");
        var platform = new Option<string?>("--platform", "linux-x86_64 or windows-x86_64.");
        var artifacts = new Option<string?>("--artifacts", "Artifacts root (default: artifacts).");

        cmd.AddOption(build);
        cmd.AddOption(platform);
        cmd.AddOption(artifacts);

        cmd.SetHandler(ctx =>
        {
            var p = ctx.ParseResult;
            var a = new ArgList();
            a.AddValue("--build", p.GetValueForOption(build));
            a.AddValue("--platform", p.GetValueForOption(platform));
            a.AddValue("--artifacts", p.GetValueForOption(artifacts));
            ctx.ExitCode = ReconcileChangelogCommand.Run(a.ToArray());
        });

        return cmd;
    }

    // ---------------------------------------------------------------------------
    // dump-appinfo  (diagnostic — not part of the public surface)
    // ---------------------------------------------------------------------------
    private static Command BuildDumpAppInfoCommand()
    {
        var cmd = new Command("dump-appinfo", "Diagnostic: fetch an app's current PICS appinfo and write it to a file.");

        var app = new Option<string?>("--app", "App id (default: 730).");
        var format = new Option<string?>("--format", "Output rendering: json (default) or vdf.");
        var outOpt = new Option<string?>("--out", "Output path (default: a temp file).");

        cmd.AddOption(app);
        cmd.AddOption(format);
        cmd.AddOption(outOpt);

        cmd.SetHandler(ctx =>
        {
            var p = ctx.ParseResult;
            var a = new ArgList();
            a.AddValue("--app", p.GetValueForOption(app));
            a.AddValue("--format", p.GetValueForOption(format));
            a.AddValue("--out", p.GetValueForOption(outOpt));
            ctx.ExitCode = DumpAppInfoCommand.Run(a.ToArray());
        });

        return cmd;
    }

    // ---------------------------------------------------------------------------
    // capture-pics  (forward-capture tooling: NOT part of the documented CLI surface)
    //   Fetch the current PICS appinfo and write the capture sidecar into the (build, platform)
    //   binaries cache, so a following `extract --commit` promotes it to pics-appinfo.json.
    // ---------------------------------------------------------------------------
    private static Command BuildCapturePicsCommand()
    {
        var cmd = new Command(
            "capture-pics",
            "Fetch the current PICS appinfo and write the capture sidecar for a build/platform (promote via extract --commit).");

        var build = new Option<string?>("--build", "Build id naming the destination binaries-cache dir.");
        var platform = new Option<string?>("--platform", "Platform (e.g. windows-x86_64).");
        var app = new Option<string?>("--app", "App id (default: 730).");

        cmd.AddOption(build);
        cmd.AddOption(platform);
        cmd.AddOption(app);

        cmd.SetHandler(ctx =>
        {
            var p = ctx.ParseResult;
            var a = new ArgList();
            a.AddValue("--build", p.GetValueForOption(build));
            a.AddValue("--platform", p.GetValueForOption(platform));
            a.AddValue("--app", p.GetValueForOption(app));
            ctx.ExitCode = CapturePicsCommand.Run(a.ToArray());
        });

        return cmd;
    }

    /// <summary>
    /// Reconstructs the canonical <c>--name value</c> / bare-flag argument vector the existing
    /// command parsers (<see cref="Cli.CliArgs"/>) expect. Only options the user actually
    /// supplied are emitted, so the delegated command sees the same shape it would from a raw
    /// command line — preserving the existing per-command validation + test seams.
    /// </summary>
    private sealed class ArgList
    {
        private readonly List<string> _args = new();

        public void AddValue(string name, string? value)
        {
            if (value is null)
                return;   // option not supplied — omit entirely.
            _args.Add(name);
            _args.Add(value);
        }

        public void AddFlag(string name, bool present)
        {
            if (present)
                _args.Add(name);
        }

        public string[] ToArray() => _args.ToArray();
    }
}

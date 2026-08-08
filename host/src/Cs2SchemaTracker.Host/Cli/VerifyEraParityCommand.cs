// Host-owned cross-platform era-parity check (`verify-era-parity`).
//
// Replaces scripts/validate-linux-era.sh: after a bootstrapped linux-x86_64 era walker produces its
// raw WalkerOutput protobuf over a representative build, this command decodes it and compares its
// records (classes / enums / convars / commands / engine_constants) against the committed
// windows-x86_64 artifact for the same build.
//
// PLATFORM-AWARE MODEL (schema family 0.5.0): the two platforms no longer load the same module
// set. The windows depot additionally ships windows-only tool modules (resourcecompiler.dll,
// assetpreview.dll, navsystem, rendersystemdx11, toolframework2, propertyeditor, physicsbuilder,
// visbuilder, helpsystem, and the Workshop Tools editor DLLs — depot 2347779), none of which exist
// in the linux depot — AND those modules also register types into the shared "!GlobalTypes"
// pseudo-scope. A whole-artifact strict count equality therefore fails by construction. The
// comparison is instead:
//   - classes / enums / engine_constants are compared PER-MODULE over the MODULE INTERSECTION
//     (modules present on both platforms, identity via the same normalized key the modules.json
//     merge uses: "client.dll" == "libclient.so" == "client"). Within the intersection the counts
//     MUST match exactly (ABI-invariant).
//   - windows-only-module counts are reported separately as an INFORMATIONAL note, never a
//     failure. A linux-only module with registrations IS a failure (never expected — windows is
//     always the superset platform).
//   - "!GlobalTypes" classes carry their owning project in project_name; only the subset whose
//     project maps to a module present on BOTH platforms is compared (strict, per project). The
//     remainder (windows-only tool projects + lib projects with no module of their own) is
//     excluded symmetrically and reported informationally.
//   - "!GlobalTypes" enums and engine_constants carry NO project attribution (the proto has no
//     project_name there), so they cannot be filtered the same way: windows may carry a surplus
//     (the tool modules' global registrations — informational); linux exceeding windows is a
//     failure.
//   - convars strict and the commands dev-tolerance are UNCHANGED from the pre-0.5.0 rules.
//
// Exit codes: 0 parity holds · 1 a strict count differs (or commands out of tolerance, or a
// linux-only surplus) · 64 usage error · 65 missing input (walk .pb or committed windows set).

using Cs2SchemaTracker.Host.Modules;

using Google.Protobuf;

namespace Cs2SchemaTracker.Host.Cli;

/// <summary>
/// Per-platform tallies feeding the platform-aware parity comparison. Module / project keys are
/// normalized (<see cref="SchemaRegistrationCounter.NormalizeKey"/>) so windows and linux module
/// file names agree ("client.dll" == "libclient.so" == "client"). "!GlobalTypes" (and any
/// '!'-prefixed pseudo-scope) registrations are NOT in the per-module maps: classes go to
/// <see cref="GlobalClassesByProject"/> keyed by their normalized project_name; enums and
/// engine_constants (which carry no project attribution) go to the unattributed global counters.
/// </summary>
internal sealed record PlatformSchemaCounts(
    IReadOnlyDictionary<string, int> ClassesByModule,
    IReadOnlyDictionary<string, int> GlobalClassesByProject,
    IReadOnlyDictionary<string, int> EnumsByModule,
    int GlobalEnums,
    int ConVars,
    int Commands,
    IReadOnlyDictionary<string, int> EngineConstantsByModule,
    int GlobalEngineConstants);

/// <summary>One compared row: the metric, both platforms' counts, and the verdict.</summary>
internal sealed record ParityRow(string Metric, int Linux, int Windows, bool Ok, string Note);

/// <summary>The full parity verdict (passes iff every row is Ok).</summary>
internal sealed record ParityReport(IReadOnlyList<ParityRow> Rows)
{
    public bool Passed => Rows.All(r => r.Ok);
}

/// <summary>
/// Pure platform-aware parity comparison (see the file header for the model). convars are strict;
/// commands tolerate up to <see cref="CommandDevTolerance"/> windows-only development commands
/// (windows may carry a few the linux build does not, never the reverse).
/// </summary>
internal static class EraParity
{
    /// <summary>Max number of windows-only development commands tolerated on the commands row.</summary>
    public const int CommandDevTolerance = 3;

    /// <summary>Max diff entries spelled out in a row note before truncating to "+N more".</summary>
    private const int NoteDiffLimit = 3;

    public static ParityReport Compare(PlatformSchemaCounts linux, PlatformSchemaCounts windows)
    {
        var linuxModules = ModuleUniverse(linux);
        var windowsModules = ModuleUniverse(windows);
        var shared = linuxModules.Intersect(windowsModules, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).ToList();
        var windowsOnly = windowsModules.Except(linuxModules, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).ToList();
        var linuxOnly = linuxModules.Except(windowsModules, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).ToList();

        var rows = new List<ParityRow>
        {
            ModulesRow(linuxModules.Count, windowsModules.Count, windowsOnly, linuxOnly),
            SharedStrict("classes", shared, linux.ClassesByModule, windows.ClassesByModule,
                linux.GlobalClassesByProject, windows.GlobalClassesByProject),
            ExcludedInfo("classes(excl)",
                Total(linux.ClassesByModule) + Total(linux.GlobalClassesByProject),
                Total(windows.ClassesByModule) + Total(windows.GlobalClassesByProject),
                Compared(shared, linux.ClassesByModule, linux.GlobalClassesByProject),
                Compared(shared, windows.ClassesByModule, windows.GlobalClassesByProject)),
            SharedStrict("enums", shared, linux.EnumsByModule, windows.EnumsByModule),
            WindowsSuperset("enums(global)", linux.GlobalEnums, windows.GlobalEnums),
            ExcludedInfo("enums(excl)",
                Total(linux.EnumsByModule), Total(windows.EnumsByModule),
                Compared(shared, linux.EnumsByModule), Compared(shared, windows.EnumsByModule)),
            Strict("convars", linux.ConVars, windows.ConVars),
            CommandsRow(linux.Commands, windows.Commands),
            SharedStrict("engine_const", shared, linux.EngineConstantsByModule, windows.EngineConstantsByModule),
            WindowsSuperset("engine_const(global)", linux.GlobalEngineConstants, windows.GlobalEngineConstants),
            ExcludedInfo("engine_const(excl)",
                Total(linux.EngineConstantsByModule), Total(windows.EngineConstantsByModule),
                Compared(shared, linux.EngineConstantsByModule), Compared(shared, windows.EngineConstantsByModule)),
        };
        return new ParityReport(rows);
    }

    /// <summary>
    /// Tally proto records into <see cref="PlatformSchemaCounts"/>. The one attribution builder
    /// both sides (linux walk .pb, committed windows JSON) share, so the two platforms are keyed
    /// identically by construction.
    /// </summary>
    public static PlatformSchemaCounts BuildCounts(
        IEnumerable<Schemas.SchemaClass> classes,
        IEnumerable<Schemas.SchemaEnum> enums,
        int convars,
        int commands,
        IEnumerable<Schemas.EngineConstant> engineConstants)
    {
        var classesByModule = new Dictionary<string, int>(StringComparer.Ordinal);
        var globalClassesByProject = new Dictionary<string, int>(StringComparer.Ordinal);
        var enumsByModule = new Dictionary<string, int>(StringComparer.Ordinal);
        var constsByModule = new Dictionary<string, int>(StringComparer.Ordinal);
        int globalEnums = 0, globalConsts = 0;

        foreach (var c in classes)
        {
            if (IsPseudoOrEmpty(c.Module))
            {
                // Globally-registered class: the owning project (project_name) is the meaningful
                // grouping key ("" when untagged — never a shared module, so it lands excluded).
                Bump(globalClassesByProject, SchemaRegistrationCounter.NormalizeKey(c.ProjectName ?? string.Empty));
            }
            else
            {
                Bump(classesByModule, SchemaRegistrationCounter.NormalizeKey(c.Module));
            }
        }
        foreach (var e in enums)
        {
            // Enums now carry project_name (schema family 0.5.1), so a pseudo-scope enum IS
            // attributable — but this report still counts it in the flat global bucket. The
            // by-project split the class branch does above is deliberately not mirrored here:
            // it would change PlatformSchemaCounts and the report's row set, which is a parity-
            // report change rather than the artifact-field change this version makes.
            if (IsPseudoOrEmpty(e.Module))
            {
                globalEnums++;
            }
            else
            {
                Bump(enumsByModule, SchemaRegistrationCounter.NormalizeKey(e.Module));
            }
        }
        foreach (var k in engineConstants)
        {
            // Source form "schema_enum:<module>/<enum>" attributes a constant to its module;
            // a pseudo-scope or unparseable source falls into the unattributed global bucket.
            var module = ModuleFromConstantSource(k.Source);
            if (module is null)
            {
                globalConsts++;
            }
            else
            {
                Bump(constsByModule, SchemaRegistrationCounter.NormalizeKey(module));
            }
        }

        return new PlatformSchemaCounts(
            classesByModule, globalClassesByProject, enumsByModule, globalEnums,
            convars, commands, constsByModule, globalConsts);
    }

    // ---- rows -------------------------------------------------------------------------------

    private static ParityRow ModulesRow(
        int linuxCount, int windowsCount, List<string> windowsOnly, List<string> linuxOnly)
    {
        if (linuxOnly.Count > 0)
        {
            return new ParityRow("modules", linuxCount, windowsCount, false,
                $"**DIFF** (linux-only: {JoinTrunc(linuxOnly)} — never expected)");
        }
        var note = windowsOnly.Count == 0
            ? "OK"
            : $"OK ({windowsOnly.Count} windows-only tool module(s): {JoinTrunc(windowsOnly)} — informational)";
        return new ParityRow("modules", linuxCount, windowsCount, true, note);
    }

    /// <summary>
    /// Strict per-module equality over the shared-module set; when the global-class maps are
    /// given, the "!GlobalTypes" classes whose project maps to a shared module are compared
    /// strictly per project as well. Row counts are the compared sums.
    /// </summary>
    private static ParityRow SharedStrict(
        string metric, List<string> shared,
        IReadOnlyDictionary<string, int> linuxByModule, IReadOnlyDictionary<string, int> windowsByModule,
        IReadOnlyDictionary<string, int>? linuxGlobalByProject = null,
        IReadOnlyDictionary<string, int>? windowsGlobalByProject = null)
    {
        int linuxSum = 0, windowsSum = 0;
        var diffs = new List<string>();
        foreach (var m in shared)
        {
            var l = Get(linuxByModule, m);
            var w = Get(windowsByModule, m);
            linuxSum += l;
            windowsSum += w;
            if (l != w)
            {
                diffs.Add($"{m} {l}!={w}");
            }
        }
        if (linuxGlobalByProject is not null && windowsGlobalByProject is not null)
        {
            foreach (var p in shared)
            {
                var l = Get(linuxGlobalByProject, p);
                var w = Get(windowsGlobalByProject, p);
                linuxSum += l;
                windowsSum += w;
                if (l != w)
                {
                    diffs.Add($"!GlobalTypes/{p} {l}!={w}");
                }
            }
        }
        return diffs.Count == 0
            ? new ParityRow(metric, linuxSum, windowsSum, true, "OK")
            : new ParityRow(metric, linuxSum, windowsSum, false, $"**DIFF** ({JoinTrunc(diffs)})");
    }

    /// <summary>
    /// Unattributed "!GlobalTypes" registrations (no project_name in the proto): windows may
    /// carry a surplus (the windows-only tool modules' global registrations — informational);
    /// linux exceeding windows is never expected and fails.
    /// </summary>
    private static ParityRow WindowsSuperset(string metric, int linux, int windows)
    {
        if (linux == windows)
        {
            return new ParityRow(metric, linux, windows, true, "OK");
        }
        if (windows > linux)
        {
            return new ParityRow(metric, linux, windows, true,
                $"OK (-{windows - linux} windows-only tools, unattributed)");
        }
        return new ParityRow(metric, linux, windows, false, "**DIFF** (linux exceeds windows)");
    }

    /// <summary>
    /// Informational not-compared remainder (platform-only modules + unshared global projects).
    /// Never a failure by itself — the modules row already fails on any linux-only module.
    /// </summary>
    private static ParityRow ExcludedInfo(
        string metric, int linuxTotal, int windowsTotal, int linuxCompared, int windowsCompared) =>
        new(metric, linuxTotal - linuxCompared, windowsTotal - windowsCompared, true,
            "info: not compared (platform-only modules / unshared global projects)");

    private static ParityRow Strict(string metric, int linux, int windows) =>
        new(metric, linux, windows, linux == windows, linux == windows ? "OK" : "**DIFF**");

    private static ParityRow CommandsRow(int linux, int windows)
    {
        if (linux == windows)
            return new ParityRow("commands", linux, windows, true, "OK");
        var devDelta = windows - linux;   // windows carries the extra dev commands.
        if (devDelta >= 1 && devDelta <= CommandDevTolerance)
        {
            return new ParityRow("commands", linux, windows, true, $"OK (-{devDelta} windows-only dev)");
        }
        return new ParityRow("commands", linux, windows, false, "**DIFF**");
    }

    // ---- helpers ----------------------------------------------------------------------------

    /// <summary>Every module key the platform carries any registration for (never pseudo-scopes).</summary>
    private static List<string> ModuleUniverse(PlatformSchemaCounts p) =>
        p.ClassesByModule.Keys
            .Union(p.EnumsByModule.Keys, StringComparer.Ordinal)
            .Union(p.EngineConstantsByModule.Keys, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// The module a constant's source attributes it to ("schema_enum:server.dll/CFoo::Bar_t" ->
    /// "server.dll"), or null when unattributable (pseudo-scope / no module segment).
    /// </summary>
    internal static string? ModuleFromConstantSource(string? source)
    {
        if (string.IsNullOrEmpty(source))
        {
            return null;
        }
        var colon = source.IndexOf(':', StringComparison.Ordinal);
        if (colon < 0)
        {
            return null;
        }
        var slash = source.IndexOf('/', colon + 1);
        if (slash <= colon + 1)
        {
            return null;
        }
        var module = source[(colon + 1)..slash];
        return module[0] == '!' ? null : module;
    }

    private static bool IsPseudoOrEmpty(string? module) =>
        string.IsNullOrEmpty(module) || module[0] == '!';

    private static void Bump(Dictionary<string, int> counts, string key) =>
        counts[key] = counts.TryGetValue(key, out var n) ? n + 1 : 1;

    private static int Get(IReadOnlyDictionary<string, int> counts, string key) =>
        counts.TryGetValue(key, out var n) ? n : 0;

    private static int Total(IReadOnlyDictionary<string, int> counts) => counts.Values.Sum();

    private static int Compared(
        List<string> shared,
        IReadOnlyDictionary<string, int> byModule,
        IReadOnlyDictionary<string, int>? globalByProject = null)
    {
        var sum = shared.Sum(m => Get(byModule, m));
        if (globalByProject is not null)
        {
            sum += shared.Sum(p => Get(globalByProject, p));
        }
        return sum;
    }

    private static string JoinTrunc(List<string> items)
    {
        var shown = string.Join(", ", items.Take(NoteDiffLimit));
        return items.Count <= NoteDiffLimit ? shown : $"{shown}, +{items.Count - NoteDiffLimit} more";
    }
}

internal static class VerifyEraParityCommand
{
    private static readonly JsonParser JsonParser =
        new(JsonParser.Settings.Default.WithIgnoreUnknownFields(true));

    public static int Run(string[] args)
    {
        if (CliArgs.HasHelpFlag(args))
        {
            Console.WriteLine(@"cs2-schema-tracker verify-era-parity — compare a linux-x86_64 walk's records to the
committed windows-x86_64 artifact for the same build (platform-aware cross-platform parity).

Usage:
  cs2-schema-tracker verify-era-parity --walk <walker_output.pb> --build <id> [--artifacts <root>]

Arguments:
  --walk <path>      The raw WalkerOutput protobuf a linux-x86_64 walker produced (`walk --out`).
  --build <id>       Build id whose committed windows-x86_64 set is the reference (required).
  --artifacts <root> Artifacts root, repo-relative (default: artifacts).

behavior:
  classes / enums / engine_constants are compared PER-MODULE over the module intersection (strict
  equality there); windows-only tool modules (resourcecompiler, the Workshop Tools editor DLLs, ...)
  are reported informationally, never as a failure. ""!GlobalTypes"" classes are compared over the
  subset whose project_name maps to a module present on both platforms; unattributed global enums /
  engine constants tolerate a windows-only surplus. convars are strict; commands may differ by up to
  the few windows-only development commands. Prints a comparison table; non-zero on any real
  difference (including any linux-only surplus).

Exit codes: 0 parity holds · 1 count difference · 64 usage error · 65 missing input.");
            return 0;
        }

        var parsed = CliArgs.Parse(args);
        if (!parsed.TryGetValue("walk", out var walkPath) || string.IsNullOrEmpty(walkPath))
        {
            Console.Error.WriteLine("verify-era-parity: --walk <walker_output.pb> is required.");
            return 64;
        }
        if (!parsed.TryGetValue("build", out var build) || string.IsNullOrEmpty(build))
        {
            Console.Error.WriteLine("verify-era-parity: --build <id> is required.");
            return 64;
        }
        var artifactsRel = parsed.TryGetValue("artifacts", out var a) && !string.IsNullOrEmpty(a) ? a : "artifacts";
        var windowsDir = Path.Combine(Path.GetFullPath(artifactsRel), build, "windows-x86_64");

        if (!File.Exists(walkPath))
        {
            Console.Error.WriteLine($"verify-era-parity: walk output not found: '{walkPath}'.");
            return 65;
        }
        if (!Directory.Exists(windowsDir))
        {
            Console.Error.WriteLine($"verify-era-parity: committed windows set not found: '{windowsDir}'.");
            return 65;
        }

        PlatformSchemaCounts linux, windows;
        try
        {
            linux = ReadWalkCounts(walkPath);
            windows = ReadCommittedCounts(windowsDir);
        }
        catch (Exception ex) when (ex is IOException or InvalidProtocolBufferException or InvalidJsonException)
        {
            Console.Error.WriteLine($"verify-era-parity: could not read inputs: {ex.Message}");
            return 65;
        }

        var report = EraParity.Compare(linux, windows);

        Console.WriteLine($"verify-era-parity: build {build}  (linux walk vs committed windows-x86_64)");
        Console.WriteLine($"  {"metric",-22}{"linux",8}{"windows",9}   verdict");
        foreach (var r in report.Rows)
        {
            Console.WriteLine($"  {r.Metric,-22}{r.Linux,8}{r.Windows,9}   {r.Note}");
        }

        if (!report.Passed)
        {
            Console.Error.WriteLine("verify-era-parity: FAIL — a record count differs beyond tolerance (see **DIFF** above).");
            return 1;
        }
        Console.WriteLine("verify-era-parity: OK — cross-platform record counts are consistent.");
        return 0;
    }

    /// <summary>Platform tallies from the raw linux WalkerOutput protobuf.</summary>
    private static PlatformSchemaCounts ReadWalkCounts(string walkPath)
    {
        var walk = Schemas.WalkerOutput.Parser.ParseFrom(File.ReadAllBytes(walkPath));
        return EraParity.BuildCounts(
            classes: walk.EntitySchema?.Classes ?? Enumerable.Empty<Schemas.SchemaClass>(),
            enums: walk.EntitySchema?.Enums ?? Enumerable.Empty<Schemas.SchemaEnum>(),
            convars: walk.Convars?.Convars.Count ?? 0,
            commands: walk.Commands?.Commands.Count ?? 0,
            engineConstants: walk.EngineConstants?.Constants ?? Enumerable.Empty<Schemas.EngineConstant>());
    }

    /// <summary>Platform tallies from the committed windows-x86_64 artifact JSON (parsed via generated protos).</summary>
    private static PlatformSchemaCounts ReadCommittedCounts(string windowsDir)
    {
        var entity = JsonParser.Parse<Schemas.EntitySchema>(File.ReadAllText(Path.Combine(windowsDir, "entity_schema.json")));
        var convars = JsonParser.Parse<Schemas.ConVars>(File.ReadAllText(Path.Combine(windowsDir, "convars.json")));
        var commands = JsonParser.Parse<Schemas.Commands>(File.ReadAllText(Path.Combine(windowsDir, "commands.json")));
        var engine = JsonParser.Parse<Schemas.EngineConstants>(File.ReadAllText(Path.Combine(windowsDir, "engine_constants.json")));
        return EraParity.BuildCounts(
            classes: entity.Classes,
            enums: entity.Enums,
            convars: convars.Convars.Count,
            commands: commands.Commands_.Count,
            engineConstants: engine.Constants);
    }
}

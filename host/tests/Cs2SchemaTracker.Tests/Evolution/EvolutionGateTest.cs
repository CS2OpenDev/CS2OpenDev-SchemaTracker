// Repo-level check coverage for the fixed-path schema-evolution artifact
// (schema_evolution/<platform>.json), via ArtifactSetValidator.ValidateEvolution.
//
// The artifact lives once per platform (not per build), so this is a repo-level assertion, not a
// per-build gate. Cases:
//   - dormant: no artifact => no violation (pre-seed);
//   - well-formed + current => no violation;
//   - wrong latest_build (stale) => violation;
//   - stale transition count => violation;
//   - wrong baseline => violation;
//   - malformed JSON => violation.

using Cs2SchemaTracker.Host.Artifacts;
using Cs2SchemaTracker.Host.Serialization;

using Xunit;

using Schemas = Cs2SchemaTracker.Schemas;

namespace Cs2SchemaTracker.Tests.Evolution;

public sealed class EvolutionGateTest
{
    private const string Platform = "linux-x86_64";
    private const string Floor = "1000";
    private const string Latest = "1002";

    private static void InRoot(Action<string> body)
    {
        var work = Path.Combine(Path.GetTempPath(), "evo-gate-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(work, "artifacts");
        Directory.CreateDirectory(root);
        try
        { body(root); }
        finally { try { Directory.Delete(work, recursive: true); } catch { /* best effort */ } }
    }

    /// <summary>Write a bare (build,platform) dir carrying just entity_schema.json (enough to be a chain link).</summary>
    private static void MakeBuild(string root, string build)
    {
        var dir = Path.Combine(root, build, Platform);
        Directory.CreateDirectory(dir);
        AtomicWrite.WriteCanonical(
            new Schemas.EntitySchema { SchemaVersion = "0.5.0", BuildId = build, Platform = Platform },
            Path.Combine(dir, "entity_schema.json"));
    }

    private static void MakeChain(string root)
    {
        MakeBuild(root, Floor);
        MakeBuild(root, "1001");
        MakeBuild(root, Latest);
    }

    private static void WriteEvolution(string root, Schemas.SchemaEvolution evo)
        => AtomicWrite.WriteCanonical(evo, Path.Combine(root, ArtifactSet.SchemaEvolutionRelativePath(Platform)));

    /// <summary>A well-formed cumulative artifact for the 3-build chain (floor .. latest, 2 transitions).</summary>
    private static Schemas.SchemaEvolution WellFormed() => new()
    {
        SchemaVersion = "0.5.0",
        Platform = Platform,
        BaselineBuild = Floor,
        LatestBuild = Latest,
        Transitions =
        {
            new Schemas.Transition { FromBuild = Floor, ToBuild = "1001" },
            new Schemas.Transition { FromBuild = "1001", ToBuild = Latest },
        },
    };

    private static IEnumerable<string> EvoViolations(string root)
        => new ArtifactSetValidator(root).ValidateEvolution(Platform).Select(v => v.Message);

    [Fact]
    public void Dormant_when_no_artifact()
    {
        InRoot(root =>
        {
            MakeChain(root);
            Assert.Empty(EvoViolations(root));
        });
    }

    [Fact]
    public void Wellformed_current_artifact_passes()
    {
        InRoot(root =>
        {
            MakeChain(root);
            WriteEvolution(root, WellFormed());
            Assert.Empty(EvoViolations(root));
        });
    }

    [Fact]
    public void Stale_latest_build_is_a_violation()
    {
        InRoot(root =>
        {
            MakeChain(root);
            var stale = WellFormed();
            stale.LatestBuild = "1001";                    // a build landed after the artifact was written
            stale.Transitions.RemoveAt(1);                 // keep counts self-consistent for THIS assertion
            WriteEvolution(root, stale);
            Assert.Contains(EvoViolations(root), m => m.Contains("latest_build") && m.Contains("stale"));
        });
    }

    [Fact]
    public void Stale_transition_count_is_a_violation()
    {
        InRoot(root =>
        {
            MakeChain(root);
            var stale = WellFormed();
            stale.Transitions.RemoveAt(1);                 // 1 transition, but the chain has 3 builds (2 expected)
            WriteEvolution(root, stale);
            Assert.Contains(EvoViolations(root), m => m.Contains("transition(s)"));
        });
    }

    [Fact]
    public void Wrong_baseline_is_a_violation()
    {
        InRoot(root =>
        {
            MakeChain(root);
            var bad = WellFormed();
            bad.BaselineBuild = "999";
            WriteEvolution(root, bad);
            Assert.Contains(EvoViolations(root), m => m.Contains("baseline_build"));
        });
    }

    [Fact]
    public void Schema_evolution_dir_is_not_validated_as_a_build()
    {
        InRoot(root =>
        {
            MakeBuild(root, "1001");
            WriteEvolution(root, new Schemas.SchemaEvolution
            {
                SchemaVersion = "0.5.0",
                Platform = Platform,
                BaselineBuild = "1001",
                LatestBuild = "1001",
            });
            // The whole-tree scan must NOT treat the schema_evolution/ dir as a build set (it would
            // otherwise fail it as an incomplete "build"). It IS still validated by the repo-level
            // evolution check — but never enumerated as a build verdict.
            var report = new ArtifactSetValidator(root).ValidateAll();
            Assert.DoesNotContain(report.Builds, b => b.BuildId == ArtifactSet.SchemaEvolutionDirName);
        });
    }

    [Fact]
    public void Malformed_artifact_is_a_violation()
    {
        InRoot(root =>
        {
            MakeChain(root);
            var path = Path.Combine(root, ArtifactSet.SchemaEvolutionRelativePath(Platform));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "{ not json");
            Assert.Contains(EvoViolations(root), m => m.Contains("does not parse"));
        });
    }
}

// EraParity comparison tests — the pure platform-aware cross-platform parity logic behind
// verify-era-parity. classes/enums/engine_constants are strict PER-MODULE over the module
// intersection (windows-only tool modules are informational, linux-only modules fail);
// "!GlobalTypes" classes are compared over the shared-project subset; unattributed global
// enums/engine constants tolerate a windows-only surplus. convars are strict; commands tolerate up
// to EraParity.CommandDevTolerance windows-only development commands (windows ≥ linux only).

using Cs2SchemaTracker.Host.Cli;
using Cs2SchemaTracker.Schemas;

using Xunit;

namespace Cs2SchemaTracker.Tests.Cli;

public class EraParityTest
{
    // ---- fixture ---------------------------------------------------------------------------

    /// <summary>
    /// A representative platform tally: two shared modules plus shared-project globals. Override
    /// individual pieces per test; both platforms default to the SAME values (parity holds).
    /// </summary>
    private static PlatformSchemaCounts Counts(
        Dictionary<string, int>? classes = null,
        Dictionary<string, int>? globalClasses = null,
        Dictionary<string, int>? enums = null,
        int globalEnums = 5,
        int convars = 50,
        int commands = 30,
        Dictionary<string, int>? consts = null,
        int globalConsts = 4)
        => new(
            ClassesByModule: classes ?? new() { ["client"] = 100, ["server"] = 200 },
            GlobalClassesByProject: globalClasses ?? new() { ["client"] = 7, ["server"] = 9 },
            EnumsByModule: enums ?? new() { ["client"] = 10, ["server"] = 20 },
            GlobalEnums: globalEnums,
            ConVars: convars,
            Commands: commands,
            EngineConstantsByModule: consts ?? new() { ["server"] = 6 },
            GlobalEngineConstants: globalConsts);

    private static ParityRow Row(ParityReport report, string metric) =>
        report.Rows.Single(r => r.Metric == metric);

    // ---- whole-report verdicts -------------------------------------------------------------

    [Fact]
    public void Identical_Counts_Pass()
    {
        var report = EraParity.Compare(Counts(), Counts());
        Assert.True(report.Passed);
        Assert.All(report.Rows, r => Assert.True(r.Ok));
    }

    [Fact]
    public void Shared_Module_Class_Difference_Fails()
    {
        var windows = Counts(classes: new() { ["client"] = 101, ["server"] = 200 });
        var report = EraParity.Compare(Counts(), windows);
        Assert.False(report.Passed);
        var row = Row(report, "classes");
        Assert.False(row.Ok);
        Assert.Contains("client", row.Note);   // the diffing module is named.
    }

    [Fact]
    public void Shared_Module_Enum_Difference_Fails()
    {
        var windows = Counts(enums: new() { ["client"] = 10, ["server"] = 21 });
        var report = EraParity.Compare(Counts(), windows);
        Assert.False(report.Passed);
        Assert.False(Row(report, "enums").Ok);
    }

    [Fact]
    public void Shared_Module_EngineConst_Difference_Fails()
    {
        var windows = Counts(consts: new() { ["server"] = 7 });
        var report = EraParity.Compare(Counts(), windows);
        Assert.False(report.Passed);
        Assert.False(Row(report, "engine_const").Ok);
    }

    [Fact]
    public void ConVars_Difference_Fails()
    {
        var report = EraParity.Compare(Counts(), Counts(convars: 51));
        Assert.False(report.Passed);
        Assert.False(Row(report, "convars").Ok);
    }

    // ---- windows-only tool modules (informational, never a failure) --------------------------

    [Fact]
    public void WindowsOnly_Tool_Modules_Are_Informational_Not_A_Failure()
    {
        // Windows additionally carries a tool module's classes/enums/consts (0.5.0 tools depot);
        // the shared modules still match exactly -> parity PASSES, with the surplus reported
        // outside the compared rows.
        var windows = Counts(
            classes: new() { ["client"] = 100, ["server"] = 200, ["resourcecompiler"] = 450 },
            enums: new() { ["client"] = 10, ["server"] = 20, ["resourcecompiler"] = 12 },
            consts: new() { ["server"] = 6, ["resourcecompiler"] = 3 });
        var report = EraParity.Compare(Counts(), windows);
        Assert.True(report.Passed);

        var modules = Row(report, "modules");
        Assert.True(modules.Ok);
        Assert.Contains("windows-only", modules.Note);
        Assert.Contains("resourcecompiler", modules.Note);

        // Compared rows carry only the shared-module sums; the surplus lands in the (excl) rows.
        Assert.Equal(100 + 200 + 7 + 9, Row(report, "classes").Windows);
        Assert.Equal(450, Row(report, "classes(excl)").Windows);
        Assert.Equal(0, Row(report, "classes(excl)").Linux);
        Assert.Equal(12, Row(report, "enums(excl)").Windows);
        Assert.Equal(3, Row(report, "engine_const(excl)").Windows);
    }

    [Fact]
    public void LinuxOnly_Module_Fails()
    {
        // A module registering ONLY on linux is never expected (windows is the superset platform).
        var linux = Counts(classes: new() { ["client"] = 100, ["server"] = 200, ["mystery"] = 1 });
        var report = EraParity.Compare(linux, Counts());
        Assert.False(report.Passed);
        var modules = Row(report, "modules");
        Assert.False(modules.Ok);
        Assert.Contains("mystery", modules.Note);
    }

    // ---- "!GlobalTypes" handling -------------------------------------------------------------

    [Fact]
    public void Global_Classes_With_Shared_Project_Are_Strict()
    {
        var windows = Counts(globalClasses: new() { ["client"] = 8, ["server"] = 9 });
        var report = EraParity.Compare(Counts(), windows);
        Assert.False(report.Passed);
        var row = Row(report, "classes");
        Assert.False(row.Ok);
        Assert.Contains("!GlobalTypes/client", row.Note);
    }

    [Fact]
    public void Global_Classes_With_Unshared_Project_Are_Excluded_Not_Compared()
    {
        // Tool projects ("resourcecompiler") and lib projects with no module of their own
        // ("animlib") do not map to a shared module -> excluded from the strict subset (both
        // sides), reported in classes(excl).
        var linux = Counts(globalClasses: new() { ["client"] = 7, ["server"] = 9, ["animlib"] = 40 });
        var windows = Counts(globalClasses: new() { ["client"] = 7, ["server"] = 9, ["animlib"] = 40, ["resourcecompiler"] = 25 });
        var report = EraParity.Compare(linux, windows);
        Assert.True(report.Passed);
        Assert.Equal(40, Row(report, "classes(excl)").Linux);
        Assert.Equal(40 + 25, Row(report, "classes(excl)").Windows);
    }

    [Theory]
    [InlineData("enums(global)")]
    [InlineData("engine_const(global)")]
    public void Unattributed_Global_Windows_Surplus_Is_Informational(string metric)
    {
        // Global enums / engine constants carry no project attribution; a windows-only surplus
        // (the tool modules' global registrations) passes with a note.
        var windows = metric == "enums(global)" ? Counts(globalEnums: 5 + 570) : Counts(globalConsts: 4 + 30);
        var report = EraParity.Compare(Counts(), windows);
        Assert.True(report.Passed);
        var row = Row(report, metric);
        Assert.True(row.Ok);
        Assert.Contains("windows-only", row.Note);
    }

    [Theory]
    [InlineData("enums(global)")]
    [InlineData("engine_const(global)")]
    public void Unattributed_Global_Linux_Surplus_Fails(string metric)
    {
        var linux = metric == "enums(global)" ? Counts(globalEnums: 6) : Counts(globalConsts: 5);
        var report = EraParity.Compare(linux, Counts());
        Assert.False(report.Passed);
        Assert.False(Row(report, metric).Ok);
    }

    // ---- commands tolerance (unchanged rules) ------------------------------------------------

    [Fact]
    public void Commands_Within_Dev_Tolerance_Pass()
    {
        // windows carries EraParity.CommandDevTolerance extra windows-only dev commands.
        var report = EraParity.Compare(
            Counts(commands: 30),
            Counts(commands: 30 + EraParity.CommandDevTolerance));
        Assert.True(report.Passed);
        var cmd = Row(report, "commands");
        Assert.True(cmd.Ok);
        Assert.Contains("dev", cmd.Note);
    }

    [Fact]
    public void Commands_Beyond_Tolerance_Fails()
    {
        var report = EraParity.Compare(
            Counts(commands: 30),
            Counts(commands: 30 + EraParity.CommandDevTolerance + 1));
        Assert.False(report.Passed);
    }

    [Fact]
    public void Commands_Linux_Exceeding_Windows_Fails()
    {
        // The extra dev commands are windows-only; linux having MORE commands is never expected.
        var report = EraParity.Compare(Counts(commands: 33), Counts(commands: 30));
        Assert.False(report.Passed);
    }

    // ---- BuildCounts attribution -------------------------------------------------------------

    [Fact]
    public void BuildCounts_Normalizes_Module_Names_Across_Platforms()
    {
        // "client.dll" (windows) and "libclient.so" (linux) tally under the SAME key, so the
        // per-module comparison is naming-convention-proof.
        var windows = EraParity.BuildCounts(
            classes: new[] { new SchemaClass { Name = "C_A", Module = "client.dll" } },
            enums: new[] { new SchemaEnum { Name = "E_A", Module = "server.dll" } },
            convars: 0, commands: 0,
            engineConstants: Array.Empty<EngineConstant>());
        var linux = EraParity.BuildCounts(
            classes: new[] { new SchemaClass { Name = "C_A", Module = "libclient.so" } },
            enums: new[] { new SchemaEnum { Name = "E_A", Module = "libserver.so" } },
            convars: 0, commands: 0,
            engineConstants: Array.Empty<EngineConstant>());
        Assert.Equal(1, windows.ClassesByModule["client"]);
        Assert.Equal(1, linux.ClassesByModule["client"]);
        Assert.Equal(1, windows.EnumsByModule["server"]);
        Assert.Equal(1, linux.EnumsByModule["server"]);
    }

    [Fact]
    public void BuildCounts_Routes_GlobalTypes_By_Kind()
    {
        // A "!GlobalTypes" class is attributed by its project_name; a "!GlobalTypes" enum has no
        // project attribution and lands in the unattributed global counter.
        var counts = EraParity.BuildCounts(
            classes: new[]
            {
                new SchemaClass { Name = "CGlobal", Module = "!GlobalTypes", ProjectName = "animlib" },
                new SchemaClass { Name = "C_Client", Module = "client.dll", ProjectName = "client" },
            },
            enums: new[]
            {
                new SchemaEnum { Name = "EGlobal", Module = "!GlobalTypes" },
                new SchemaEnum { Name = "E_Server", Module = "server.dll" },
            },
            convars: 3, commands: 2,
            engineConstants: Array.Empty<EngineConstant>());

        Assert.Equal(1, counts.GlobalClassesByProject["animlib"]);
        Assert.Equal(1, counts.ClassesByModule["client"]);
        Assert.False(counts.ClassesByModule.ContainsKey("!GlobalTypes"));
        Assert.Equal(1, counts.GlobalEnums);
        Assert.Equal(1, counts.EnumsByModule["server"]);
        Assert.Equal(3, counts.ConVars);
        Assert.Equal(2, counts.Commands);
    }

    [Fact]
    public void BuildCounts_Attributes_EngineConstants_By_Source_Module()
    {
        var counts = EraParity.BuildCounts(
            classes: Array.Empty<SchemaClass>(),
            enums: Array.Empty<SchemaEnum>(),
            convars: 0, commands: 0,
            engineConstants: new[]
            {
                new EngineConstant { Name = "A", Source = "schema_enum:server.dll/CFuncMover::Move_t" },
                new EngineConstant { Name = "B", Source = "schema_enum:libserver.so/CFuncMover::Move_t" },
                new EngineConstant { Name = "C", Source = "schema_enum:!GlobalTypes/SomeEnum_t" },
                new EngineConstant { Name = "D", Source = "some-opaque-pool" },
            });
        // Both platform namings of the server module tally under "server"; the pseudo-scope and
        // the unparseable source both land unattributed.
        Assert.Equal(2, counts.EngineConstantsByModule["server"]);
        Assert.Equal(2, counts.GlobalEngineConstants);
    }
}

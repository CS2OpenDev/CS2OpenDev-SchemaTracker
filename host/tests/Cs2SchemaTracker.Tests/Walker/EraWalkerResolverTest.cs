// EraWalkerResolver resolution tests — over the single-source inventory + era catalog.
//
// Drives the host-side era -> walker-binary resolver over a FIXTURE data/cs2-assets-inventory.json
// in a throwaway repo-root temp dir (the resolver takes an explicit repoRoot, so no real repo /
// natives / CS2 binaries are touched). The resolver computes paths + metadata only; no process is
// launched, and it does NOT check the walker binary exists (that fail-loud is WalkerProcessRunner's
// at launch time).
//
// Coverage:
//   1. Known build (builds[].era) -> that exact era's binary + signature + band.
//   2. Unknown / fresh build (not in builds[]) -> the newest compile-pin era (eras[0]).
//   3. Runtime-variant build -> the ridden compile era's binary, but the variant's OWN signature/band.
//   4. CS2_WALKER_BIN override -> bypasses era->binary selection (returns the override path).
//   5. CS2_WALKER_ERAS_ROOT selects the natives root the binary path is built under.
//
// Mutates CS2_WALKER_BIN / CS2_WALKER_ERAS_ROOT, so the class joins the serialized "era-walker"
// collection and restores every env var it touches in a finally.

using Cs2SchemaTracker.Host.Walker;

using Xunit;

namespace Cs2SchemaTracker.Tests.Walker;

[Collection("era-walker")]
public sealed class EraWalkerResolverTest
{
    private const string Platform = "windows-x86_64";

    private const string CurrentSha = "b8dcaf14c603076300cab3861c99b44878d65db4";
    private const string CurrentSig = "hl2sdk-cs2/b8dcaf14c603076300cab3861c99b44878d65db4/v1/3d1200e346019c59";
    private const string Q1Sha = "0da05cff57162fe8f950192cf73d89e77ab9ee00";
    private const string Q1Sig = "hl2sdk-cs2/0da05cff57162fe8f950192cf73d89e77ab9ee00/v1/3e396404979881c9";
    private const string VariantSig = "re-2023lt/v1/69a8cb68432fca4f";

    // A self-contained fixture repo root carrying walker/CMakeLists.txt (the repo-root sentinel) +
    // data/cs2-assets-inventory.json (eras[] + builds[]).
    private sealed class FixtureRepo : IDisposable
    {
        public string Root { get; }

        // erasOverride replaces the default eras[] body verbatim (no surrounding brackets), for
        // tests that need era rows the default fixture does not carry — e.g. per-platform
        // classBands.
        public FixtureRepo(string buildsJson, string? erasOverride = null)
        {
            Root = Path.Combine(Path.GetTempPath(), "era-resolve-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(Root, "walker"));
            File.WriteAllText(Path.Combine(Root, "walker", "CMakeLists.txt"), "# fixture");
            var dataDir = Path.Combine(Root, "data");
            Directory.CreateDirectory(dataDir);
            var erasJson = erasOverride ?? $$"""
                { "era": "cs2-2026-04-21", "kind": "compile-pin", "hl2sdkSha": "{{CurrentSha}}",
                  "layoutSignatures": { "windows-x86_64": "{{CurrentSig}}" },
                  "minClasses": 1321, "maxClasses": 1461 },
                { "era": "cs2-2026-01-22", "kind": "compile-pin", "hl2sdkSha": "{{Q1Sha}}",
                  "layoutSignatures": { "windows-x86_64": "{{Q1Sig}}" },
                  "minClasses": 1296, "maxClasses": 1434 },
                { "era": "cs2-2023-03-22", "kind": "runtime-variant", "ridesCompilePin": "{{CurrentSha}}",
                  "variantSignature": "{{VariantSig}}", "minClasses": 980, "maxClasses": 1080 }
                """;
            File.WriteAllText(Path.Combine(dataDir, "cs2-assets-inventory.json"), $$"""
            {
              "app": { "app_id": 730 },
              "eras": [
                {{erasJson}}
              ],
              "depots": [],
              "builds": [{{buildsJson}}]
            }
            """);
        }

        public void Dispose()
        {
            try
            { Directory.Delete(Root, recursive: true); }
            catch { /* best effort */ }
        }
    }

    // Run a body with CS2_WALKER_BIN / CS2_WALKER_ERAS_ROOT cleared (then restored).
    private static void WithCleanEnv(Action body)
    {
        var oldBin = Environment.GetEnvironmentVariable(WalkerProcessRunner.BinaryPathEnvVar);
        var oldNatives = Environment.GetEnvironmentVariable(EraWalkerResolver.NativesRootEnvVar);
        Environment.SetEnvironmentVariable(WalkerProcessRunner.BinaryPathEnvVar, null);
        Environment.SetEnvironmentVariable(EraWalkerResolver.NativesRootEnvVar, null);
        try
        {
            body();
        }
        finally
        {
            Environment.SetEnvironmentVariable(WalkerProcessRunner.BinaryPathEnvVar, oldBin);
            Environment.SetEnvironmentVariable(EraWalkerResolver.NativesRootEnvVar, oldNatives);
        }
    }

    [Fact]
    public void Known_Build_Resolves_To_Its_Exact_Era()
    {
        WithCleanEnv(() =>
        {
            using var repo = new FixtureRepo(
                """{ "build_id": 11112222, "era": "cs2-2026-01-22", "content": "c", "binaries": {} }""");

            var r = new EraWalkerResolver(repo.Root).Resolve("11112222", Platform);

            Assert.Equal("cs2-2026-01-22", r.Era);
            Assert.Equal(Q1Sha, r.Pin);
            Assert.Equal(Q1Sig, r.ExpectedLayoutSignature);
            Assert.False(r.FromExplicitOverride);
            // The walker binary is <natives>/<platform>/<era>.exe — the era id is the compile-pin name.
            Assert.EndsWith(Path.Combine(Platform, "cs2-2026-01-22.exe"), r.WalkerBinaryPath);
        });
    }

    [Fact]
    public void Unknown_Fresh_Build_Defaults_To_Newest_Compile_Pin_Era()
    {
        WithCleanEnv(() =>
        {
            using var repo = new FixtureRepo("");   // build 99998888 absent from builds[].

            var r = new EraWalkerResolver(repo.Root).Resolve("99998888", Platform);

            // eras[0] is the newest compile-pin era.
            Assert.Equal("cs2-2026-04-21", r.Era);
            Assert.Equal(CurrentSha, r.Pin);
            Assert.Equal(CurrentSig, r.ExpectedLayoutSignature);
            Assert.EndsWith(Path.Combine(Platform, "cs2-2026-04-21.exe"), r.WalkerBinaryPath);
        });
    }

    [Fact]
    public void Runtime_Variant_Build_Rides_Compile_Era_Binary_And_Signature_With_Own_Band()
    {
        WithCleanEnv(() =>
        {
            using var repo = new FixtureRepo(
                """{ "build_id": 10832117, "era": "cs2-2023-03-22", "content": "c", "binaries": {} }""");

            var resolver = new EraWalkerResolver(repo.Root);
            var r = resolver.Resolve("10832117", Platform);

            // Walker binary = the RIDDEN compile era's binary (cs2-2026-04-21), pin = the ridden pin.
            Assert.Equal("cs2-2023-03-22", r.Era);
            Assert.Equal(CurrentSha, r.Pin);
            Assert.EndsWith(Path.Combine(Platform, "cs2-2026-04-21.exe"), r.WalkerBinaryPath);
            // The second gate compares the walker's EMITTED signature = the ridden compile era's
            // compile-time signature (NOT variantSignature). Only the class band is the variant's own.
            Assert.Equal(CurrentSig, r.ExpectedLayoutSignature);
            var band = resolver.DetermineEffectiveClassBand("10832117", Platform);
            Assert.Equal(980, band.MinClasses);
            Assert.Equal(1080, band.MaxClasses);
        });
    }

    // --- per-platform class bands -------------------------------------------------------
    //
    // The Workshop Tools depot ships windows-only, so a windows walk loads ~19 tool modules a
    // linux walk cannot and the two platforms land in clearly separated class-count ranges within
    // the SAME era (measured on the committed sets, e.g. cs2-2026-07-09: win 4967 vs linux 3326).
    // An era's `classBands` therefore decides the gate per platform; the flat
    // minClasses/maxClasses remain only as the fallback for eras not yet calibrated per platform.

    [Fact]
    public void Class_Band_Prefers_Per_Platform_Entry_Over_Flat_Fields()
    {
        WithCleanEnv(() =>
        {
            using var repo = new FixtureRepo(
                """{ "build_id": 24134959, "era": "cs2-2026-04-21", "content": "c", "binaries": {} }""",
                erasOverride: $$"""
                { "era": "cs2-2026-04-21", "kind": "compile-pin", "hl2sdkSha": "{{CurrentSha}}",
                  "layoutSignatures": { "windows-x86_64": "{{CurrentSig}}",
                                        "linux-x86_64": "{{CurrentSig}}" },
                  "minClasses": 1, "maxClasses": 2,
                  "classBands": { "windows-x86_64": { "min": 4470, "max": 5464 },
                                  "linux-x86_64":   { "min": 2993, "max": 3659 } } }
                """);

            var resolver = new EraWalkerResolver(repo.Root);

            var win = resolver.DetermineEffectiveClassBand("24134959", "windows-x86_64");
            Assert.Equal(4470, win.MinClasses);
            Assert.Equal(5464, win.MaxClasses);

            // Same era, same build — a different band, because the platform differs.
            var linux = resolver.DetermineEffectiveClassBand("24134959", "linux-x86_64");
            Assert.Equal(2993, linux.MinClasses);
            Assert.Equal(3659, linux.MaxClasses);
        });
    }

    [Fact]
    public void Class_Band_Falls_Back_To_Flat_Fields_When_Platform_Absent()
    {
        WithCleanEnv(() =>
        {
            using var repo = new FixtureRepo(
                """{ "build_id": 24134959, "era": "cs2-2026-04-21", "content": "c", "binaries": {} }""",
                erasOverride: $$"""
                { "era": "cs2-2026-04-21", "kind": "compile-pin", "hl2sdkSha": "{{CurrentSha}}",
                  "layoutSignatures": { "windows-x86_64": "{{CurrentSig}}",
                                        "linux-x86_64": "{{CurrentSig}}" },
                  "minClasses": 1321, "maxClasses": 1461,
                  "classBands": { "windows-x86_64": { "min": 4470, "max": 5464 } } }
                """);

            var resolver = new EraWalkerResolver(repo.Root);

            // windows has a per-platform entry -> it wins.
            var win = resolver.DetermineEffectiveClassBand("24134959", "windows-x86_64");
            Assert.Equal(4470, win.MinClasses);

            // linux has none -> the flat era band still applies (no silent "unbounded" gate).
            var linux = resolver.DetermineEffectiveClassBand("24134959", "linux-x86_64");
            Assert.Equal(1321, linux.MinClasses);
            Assert.Equal(1461, linux.MaxClasses);
        });
    }

    [Fact]
    public void Runtime_Variant_Uses_Its_Own_Per_Platform_Band()
    {
        WithCleanEnv(() =>
        {
            using var repo = new FixtureRepo(
                """{ "build_id": 10832117, "era": "cs2-2023-03-22", "content": "c", "binaries": {} }""",
                erasOverride: $$"""
                { "era": "cs2-2026-04-21", "kind": "compile-pin", "hl2sdkSha": "{{CurrentSha}}",
                  "layoutSignatures": { "windows-x86_64": "{{CurrentSig}}" },
                  "minClasses": 1321, "maxClasses": 1461,
                  "classBands": { "windows-x86_64": { "min": 4470, "max": 5464 } } },
                { "era": "cs2-2023-03-22", "kind": "runtime-variant", "ridesCompilePin": "{{CurrentSha}}",
                  "variantSignature": "{{VariantSig}}", "minClasses": 980, "maxClasses": 1080,
                  "classBands": { "windows-x86_64": { "min": 2457, "max": 3130 } } }
                """);

            // The variant rides the modern binary but must be gated against ITS OWN band, not the
            // ridden era's — a 2023 layout has far fewer classes.
            var band = new EraWalkerResolver(repo.Root)
                .DetermineEffectiveClassBand("10832117", "windows-x86_64");
            Assert.Equal(2457, band.MinClasses);
            Assert.Equal(3130, band.MaxClasses);
        });
    }

    [Fact]
    public void CS2_WALKER_BIN_Override_Bypasses_Era_Selection()
    {
        WithCleanEnv(() =>
        {
            using var repo = new FixtureRepo("");   // fresh -> cs2-2026-04-21 era.
            var overrideExe = Path.Combine(repo.Root, "my-dev-walker.exe");
            File.WriteAllText(overrideExe, "dev");
            Environment.SetEnvironmentVariable(WalkerProcessRunner.BinaryPathEnvVar, overrideExe);

            var r = new EraWalkerResolver(repo.Root).Resolve("99998888", Platform);

            Assert.True(r.FromExplicitOverride);
            Assert.Equal(Path.GetFullPath(overrideExe), r.WalkerBinaryPath);
            // The expected signature is still the resolved era's so the gate stays meaningful.
            Assert.Equal("cs2-2026-04-21", r.Era);
            Assert.Equal(CurrentSig, r.ExpectedLayoutSignature);
        });
    }

    [Fact]
    public void CS2_WALKER_ERAS_ROOT_Selects_The_Natives_Root()
    {
        WithCleanEnv(() =>
        {
            using var repo = new FixtureRepo("");
            var externalRoot = Path.Combine(Path.GetTempPath(), "natives-ext-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(externalRoot);
            Environment.SetEnvironmentVariable(EraWalkerResolver.NativesRootEnvVar, externalRoot);

            try
            {
                var r = new EraWalkerResolver(repo.Root).Resolve("99998888", Platform);
                // The binary path is built under the EXTERNAL natives root, not the default next-to-exe one.
                Assert.Equal(
                    Path.GetFullPath(Path.Combine(externalRoot, Platform, "cs2-2026-04-21.exe")),
                    r.WalkerBinaryPath);
            }
            finally
            {
                try
                { Directory.Delete(externalRoot, recursive: true); }
                catch { /* best effort */ }
            }
        });
    }

    [Fact]
    public void Build_Referencing_Unknown_Era_Fails_Loud()
    {
        WithCleanEnv(() =>
        {
            using var repo = new FixtureRepo(
                """{ "build_id": 12345678, "era": "cs2-does-not-exist", "content": "c", "binaries": {} }""");

            var resolver = new EraWalkerResolver(repo.Root);
            var ex = Assert.Throws<InvalidDataException>(() => resolver.Resolve("12345678", Platform));
            Assert.Contains("cs2-does-not-exist", ex.Message, StringComparison.Ordinal);
        });
    }
}

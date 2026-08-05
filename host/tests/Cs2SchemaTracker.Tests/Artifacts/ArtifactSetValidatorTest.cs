// host-port tests for the all-or-nothing artifact-set completeness validator.
//
// They cover the gameevents, item-definitions, and content-file gating, the core shape rules
// (single-platform vs full-build, omissions accounting, required files, malformed-manifest
// fail-loud), and the verify-artifacts CLI exit-code contract.
//
// Each test builds a throwaway temp artifacts/<build>/<platform>/ tree, exercises it via
// ArtifactSetValidator (the pure validator) or VerifyArtifactsCommand.Run (for exit codes),
// then deletes the temp tree in a finally. No cwd dependency (every path is absolute), no
// network, no real binaries — deterministic / the no-flake invariant.
//
// Provenance shapes:
//   binary-only  -> steam.depots = [{depotId:2347771}]            (content depot 2347770 ABSENT)
//   content      -> steam.depots = [{depotId:2347770},{2347771}]  (content depot 2347770 PRESENT)

using Cs2SchemaTracker.Host.Artifacts;
using Cs2SchemaTracker.Host.Cli;

using Xunit;

namespace Cs2SchemaTracker.Tests.Artifacts;

public sealed class ArtifactSetValidatorTest
{
    private const string LinuxPlatform = "linux-x86_64";
    private const string WindowsPlatform = "windows-x86_64";

    // The full content-depot-gated set (faithful mirror of ArtifactSet.ContentDepotGatedFiles).
    // localization.json is deliberately NOT here: it is build-on-demand (NOT committed) and is gated
    // by the provenance.localization FINGERPRINT, not by on-disk presence.
    private static readonly string[] AllContentFiles =
    {
        "gameevents.json",
        "item_definitions.json",
        "game_modes.json",
        "surface_properties.json",
        "prop_data.json",
        "map_overviews.json",
    };

    // A well-formed 64-hex sha256 for the build-on-demand localization.json fingerprint. Its exact
    // value is irrelevant to the gate (which only checks Sha256 is non-empty); the emit-localization
    // round-trip tests use a REAL computed hash instead.
    private const string LocSha256 =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    // provenance bodies.
    private const string ProvNoContent = """{"steam":{"depots":[{"depotId":2347771}]}}""";

    // Content depot acquired AND the build-on-demand localization.json fingerprint recorded — the
    // shape of a COMPLETE content set. (uint64 fields serialize as JSON strings.)
    private const string ProvWithContent =
        "{\"steam\":{\"depots\":[{\"depotId\":2347770},{\"depotId\":2347771}]}," +
        "\"localization\":{\"sha256\":\"" + LocSha256 + "\",\"sizeBytes\":\"1\",\"tokenCount\":\"1\"}}";

    // Content depot acquired but NO localization fingerprint — an INCOMPLETE content set unless
    // localization.json is recorded as a content omission for the era.
    private const string ProvWithContentNoLoc =
        """{"steam":{"depots":[{"depotId":2347770},{"depotId":2347771}]}}""";
    private const string ProvMalformed = """{"steam":{"depots":[{"depotId":2347770""";  // truncated

    private static readonly string[] UnknownOptionArgs = { "--frobnicate" };

    // ---- fixture builders ----

    /// <summary>A fresh throwaway artifacts root (final path segment "artifacts").</summary>
    private static string NewArtifactsRoot()
    {
        var work = Path.Combine(Path.GetTempPath(), "artifact-validator-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(work, "artifacts");
        Directory.CreateDirectory(root);
        return root;
    }

    /// <summary>The build dir + an omissions.json (empty list = clean build). A present empty
    /// omissions.json is still valid; absent = clean is covered by dedicated tests.</summary>
    private static string MakeBuild(string artifactsRoot, string buildId, string? omissionsBody = null)
    {
        var buildDir = Path.Combine(artifactsRoot, buildId);
        Directory.CreateDirectory(buildDir);
        var body = omissionsBody ?? $$"""{"build_id":"{{buildId}}","omissions":[]}""";
        File.WriteAllText(Path.Combine(buildDir, "omissions.json"), body);
        return buildDir;
    }

    /// <summary>The unconditionally-required files + a non-empty protos/ for one platform dir.</summary>
    private static string MakePlatform(string buildDir, string platform, string provenanceBody)
    {
        var dir = Path.Combine(buildDir, platform);
        Directory.CreateDirectory(Path.Combine(dir, "protos"));
        foreach (var f in new[]
                 {
                     "entity_schema.json", "convars.json", "commands.json",
                     "network_messages.json", "demo_messages.json", "engine_constants.json",
                     "string_pools.json", "registry_audit.json", "modules.json",
                 })
        {
            File.WriteAllText(Path.Combine(dir, f), "{}");
        }
        File.WriteAllText(Path.Combine(dir, "provenance.json"), provenanceBody);
        File.WriteAllText(Path.Combine(dir, "protos.descriptorset"), "");
        File.WriteAllText(Path.Combine(dir, "protos", "x.proto"), "message X {}\n");
        return dir;
    }

    /// <summary>Add every content-gated file EXCEPT the named one (null/"" = add them all).</summary>
    private static void AddContentFilesExcept(string platformDir, string? except)
    {
        foreach (var f in AllContentFiles)
        {
            if (f == except)
                continue;
            File.WriteAllText(Path.Combine(platformDir, f), "{}");
        }
    }

    /// <summary>Run <paramref name="body"/> against a fresh root, then delete the work tree.</summary>
    private static void InRoot(Action<string> body)
    {
        var root = NewArtifactsRoot();
        var work = Directory.GetParent(root)!.FullName;
        try
        {
            body(root);
        }
        finally
        {
            try
            { Directory.Delete(work, recursive: true); }
            catch { /* best effort */ }
        }
    }

    private static BuildVerdict Validate(string root, string buildId)
        => new ArtifactSetValidator(root).ValidateBuild(buildId);

    // =====================================================================================
    // 1. Core shape — single complete binary-only platform set PASSES.
    // =====================================================================================

    [Fact]
    public void BinaryOnly_SinglePlatform_Complete_Passes()
    {
        InRoot(root =>
        {
            var build = MakeBuild(root, "1001");
            MakePlatform(build, LinuxPlatform, ProvNoContent);

            var verdict = Validate(root, "1001");
            Assert.True(verdict.Passed, string.Join("; ", verdict.Violations.Select(x => x.Message)));
        });
    }

    // =====================================================================================
    // 2. gameevents gating.
    // =====================================================================================

    [Fact]
    public void Gameevents_BinaryOnly_Without_Passes()
    {
        InRoot(root =>
        {
            var build = MakeBuild(root, "1001");
            MakePlatform(build, LinuxPlatform, ProvNoContent);  // no gameevents, no content depot

            Assert.True(Validate(root, "1001").Passed);
        });
    }

    [Fact]
    public void Gameevents_ContentAcquired_Missing_Fails()
    {
        InRoot(root =>
        {
            var build = MakeBuild(root, "1002");
            var plat = MakePlatform(build, LinuxPlatform, ProvWithContent);
            AddContentFilesExcept(plat, "gameevents.json");  // all siblings present, gameevents missing

            var verdict = Validate(root, "1002");
            Assert.False(verdict.Passed);
            Assert.Contains(verdict.Violations, x => x.Message.Contains("gameevents.json"));
        });
    }

    [Fact]
    public void Gameevents_ContentAcquired_Present_Passes()
    {
        InRoot(root =>
        {
            var build = MakeBuild(root, "1003");
            var plat = MakePlatform(build, LinuxPlatform, ProvWithContent);
            AddContentFilesExcept(plat, null);  // every content file present

            Assert.True(Validate(root, "1003").Passed);
        });
    }

    // content-omission: a content artifact genuinely absent for the era (its source was
    // never shipped) is ACCEPTABLE when recorded as a content_omissions entry for that present
    // platform AND the content depot is in provenance — e.g. the 2023 baseline lacks localization.
    [Fact]
    public void ContentArtifact_GenuinelyAbsent_WithOmissionRecord_Passes()
    {
        InRoot(root =>
        {
            var omissions =
                """
                {"build_id":"1004","omissions":[
                  {"platform":"linux-x86_64","content_omissions":[
                    {"artifact":"localization.json","reason":"CONTENT_NOT_SHIPPED_THIS_ERA",
                     "notes":"resource/csgo_<lang>.txt absent"}]}]}
                """;
            var build = MakeBuild(root, "1004", omissions);
            var plat = MakePlatform(build, LinuxPlatform, ProvWithContent);
            AddContentFilesExcept(plat, "localization.json");  // localization legitimately absent

            var verdict = Validate(root, "1004");
            Assert.True(verdict.Passed, string.Join("; ", verdict.Violations.Select(x => x.Message)));
        });
    }

    // A content artifact absent with NO omission record is STILL a violation (the content depot was
    // acquired but a file is missing and unaccounted-for). prop_data.json is a genuine content-gated
    // committed file (unlike localization.json, which is build-on-demand and gated by fingerprint).
    [Fact]
    public void ContentArtifact_Absent_WithoutOmissionRecord_Fails()
    {
        InRoot(root =>
        {
            var build = MakeBuild(root, "1005");  // clean omissions:[]
            var plat = MakePlatform(build, LinuxPlatform, ProvWithContent);
            AddContentFilesExcept(plat, "prop_data.json");

            var verdict = Validate(root, "1005");
            Assert.False(verdict.Passed);
            Assert.Contains(verdict.Violations, x => x.Message.Contains("prop_data.json"));
        });
    }

    // A content-carrier (reason UNSPECIFIED + content_omissions) for a PRESENT platform must NOT
    // be treated as a wholesale platform omission (no "present yet listed in omissions" violation).
    [Fact]
    public void ContentCarrier_DoesNotTriggerWholesaleOmissionViolation()
    {
        InRoot(root =>
        {
            var omissions =
                """
                {"build_id":"1006","omissions":[
                  {"platform":"linux-x86_64","content_omissions":[
                    {"artifact":"prop_data.json","reason":"CONTENT_NOT_SHIPPED_THIS_ERA","notes":"x"}]}]}
                """;
            var build = MakeBuild(root, "1006", omissions);
            var plat = MakePlatform(build, LinuxPlatform, ProvWithContent);
            AddContentFilesExcept(plat, "prop_data.json");

            var verdict = Validate(root, "1006");
            Assert.True(verdict.Passed, string.Join("; ", verdict.Violations.Select(x => x.Message)));
            Assert.DoesNotContain(verdict.Violations, x => x.Message.Contains("present on disk yet also listed"));
        });
    }

    // localization.json fingerprint gate (build-on-demand artifact, gated by provenance.localization
    // NOT by on-disk presence).
    //
    // Negative: content depot acquired, every content-gated file present, but provenance carries NO
    // localization fingerprint and localization is NOT recorded as an omission ⇒ a completeness
    // violation (fail-loud — an on-demand emit-localization rebuild would not be byte-verifiable).
    [Fact]
    public void ContentAcquired_MissingLocalizationFingerprint_NotOmitted_Fails()
    {
        InRoot(root =>
        {
            var build = MakeBuild(root, "1007");  // clean omissions:[] (no localization omission)
            var plat = MakePlatform(build, LinuxPlatform, ProvWithContentNoLoc);
            AddContentFilesExcept(plat, null);  // every content-gated file present

            var verdict = Validate(root, "1007");
            Assert.False(verdict.Passed);
            Assert.Contains(verdict.Violations,
                x => x.Message.Contains("provenance.localization") && x.Message.Contains("localization.json"));
        });
    }

    // Accepted: content depot acquired, every content-gated file present, NO localization fingerprint,
    // but localization.json is recorded as a content omission for the era (the era genuinely shipped no
    // localization tables) ⇒ the set is COMPLETE without a fingerprint.
    [Fact]
    public void ContentAcquired_LocalizationOmitted_NoFingerprint_Passes()
    {
        InRoot(root =>
        {
            var omissions =
                """
                {"build_id":"1008","omissions":[
                  {"platform":"linux-x86_64","content_omissions":[
                    {"artifact":"localization.json","reason":"CONTENT_NOT_SHIPPED_THIS_ERA",
                     "notes":"resource/csgo_<lang>.txt absent this era"}]}]}
                """;
            var build = MakeBuild(root, "1008", omissions);
            var plat = MakePlatform(build, LinuxPlatform, ProvWithContentNoLoc);
            AddContentFilesExcept(plat, null);  // every content-gated file present; only the fingerprint is absent

            var verdict = Validate(root, "1008");
            Assert.True(verdict.Passed, string.Join("; ", verdict.Violations.Select(x => x.Message)));
        });
    }

    [Fact]
    public void Gameevents_BinaryOnly_WithFilePresent_Passes()
    {
        InRoot(root =>
        {
            var build = MakeBuild(root, "1004");
            var plat = MakePlatform(build, LinuxPlatform, ProvNoContent);
            File.WriteAllText(Path.Combine(plat, "gameevents.json"), "{}");  // present but not required

            Assert.True(Validate(root, "1004").Passed);
        });
    }

    // DEFECT (ArtifactSetValidator malformed-JSON fail-loud): the validator's
    // catch is `catch (InvalidProtocolBufferException)`, but Google.Protobuf 3.28.x's JsonParser
    // throws `InvalidJsonException`, which does NOT derive from InvalidProtocolBufferException.
    // So a malformed provenance.json / omissions.json CRASHES the validator instead of being
    // reported as an violation (the deleted shell gate reported it as a FAIL). The two
    // tests below PIN the current (defective) behavior so the suite is green and the regression is
    // visible; flip them to the "no-crash, reported violation" form once the host widens the
    // catch in ArtifactSetValidator.cs (lines ~159 and ~273) to also cover InvalidJsonException.
    [Fact]
    public void Malformed_Provenance_IsReportedViolation_NoThrow()
    {
        InRoot(root =>
        {
            var build = MakeBuild(root, "1005");
            MakePlatform(build, LinuxPlatform, ProvMalformed);

            // Malformed provenance.json -> reported violation, no crash (the catch covers
            // both InvalidProtocolBufferException and Google.Protobuf InvalidJsonException).
            var verdict = Validate(root, "1005");
            Assert.False(verdict.Passed);
            Assert.Contains(verdict.Violations, x => x.Message.Contains("provenance.json"));
        });
    }

    // =====================================================================================
    // 3. item_definitions gating.
    // =====================================================================================

    [Fact]
    public void ItemDefs_BinaryOnly_Without_Passes()
    {
        InRoot(root =>
        {
            var build = MakeBuild(root, "2001");
            MakePlatform(build, LinuxPlatform, ProvNoContent);

            Assert.True(Validate(root, "2001").Passed);
        });
    }

    [Fact]
    public void ItemDefs_ContentAcquired_Missing_Fails()
    {
        InRoot(root =>
        {
            var build = MakeBuild(root, "2002");
            var plat = MakePlatform(build, LinuxPlatform, ProvWithContent);
            AddContentFilesExcept(plat, "item_definitions.json");

            var verdict = Validate(root, "2002");
            Assert.False(verdict.Passed);
            Assert.Contains(verdict.Violations, x => x.Message.Contains("item_definitions.json"));
        });
    }

    [Fact]
    public void ItemDefs_ContentAcquired_Present_Passes()
    {
        InRoot(root =>
        {
            var build = MakeBuild(root, "2003");
            var plat = MakePlatform(build, LinuxPlatform, ProvWithContent);
            AddContentFilesExcept(plat, null);

            Assert.True(Validate(root, "2003").Passed);
        });
    }

    [Fact]
    public void ItemDefs_BinaryOnly_WithFilePresent_Passes()
    {
        InRoot(root =>
        {
            var build = MakeBuild(root, "2004");
            var plat = MakePlatform(build, LinuxPlatform, ProvNoContent);
            File.WriteAllText(Path.Combine(plat, "item_definitions.json"), "{}");

            Assert.True(Validate(root, "2004").Passed);
        });
    }

    // =====================================================================================
    // 4. Content-file gating — the content-gated files. Per-file matrix:
    //    content+missing -> FAIL; content+present -> PASS; binary-only -> PASS.
    //    localization.json is NOT here: it is build-on-demand (not committed) and gated by the
    //    provenance.localization fingerprint, not by on-disk presence — see the fingerprint-gate tests.
    // =====================================================================================

    public static IEnumerable<object[]> ContentGatedFiles() => new[]
    {
        new object[] { "game_modes.json" },
        new object[] { "surface_properties.json" },
        new object[] { "prop_data.json" },
        new object[] { "map_overviews.json" },
    };

    [Theory]
    [MemberData(nameof(ContentGatedFiles))]
    public void ContentGated_Missing_Fails(string file)
    {
        InRoot(root =>
        {
            var build = MakeBuild(root, "3001");
            var plat = MakePlatform(build, LinuxPlatform, ProvWithContent);
            AddContentFilesExcept(plat, file);  // every OTHER content file present

            var verdict = Validate(root, "3001");
            Assert.False(verdict.Passed);
            Assert.Contains(verdict.Violations, x => x.Message.Contains(file));
        });
    }

    [Theory]
    [MemberData(nameof(ContentGatedFiles))]
    public void ContentGated_Present_Passes(string file)
    {
        InRoot(root =>
        {
            var build = MakeBuild(root, "3002");
            var plat = MakePlatform(build, LinuxPlatform, ProvWithContent);
            AddContentFilesExcept(plat, except: null);  // every content file present
            Assert.True(File.Exists(Path.Combine(plat, file)), $"fixture should contain {file}");

            Assert.True(Validate(root, "3002").Passed);
        });
    }

    [Theory]
    [MemberData(nameof(ContentGatedFiles))]
    public void ContentGated_BinaryOnly_Passes(string file)
    {
        _ = file;  // the binary-only case is per-file identical: no content depot => nothing gated.
        InRoot(root =>
        {
            var build = MakeBuild(root, "3003");
            MakePlatform(build, LinuxPlatform, ProvNoContent);

            Assert.True(Validate(root, "3003").Passed);
        });
    }

    // =====================================================================================
    // 5. Missing unconditional required files / empty protos dir -> FAIL.
    // =====================================================================================

    [Fact]
    public void Missing_RequiredFile_EntitySchema_Fails()
    {
        InRoot(root =>
        {
            var build = MakeBuild(root, "4001");
            var plat = MakePlatform(build, LinuxPlatform, ProvNoContent);
            File.Delete(Path.Combine(plat, "entity_schema.json"));

            var verdict = Validate(root, "4001");
            Assert.False(verdict.Passed);
            Assert.Contains(verdict.Violations, x => x.Message.Contains("entity_schema.json"));
        });
    }

    [Fact]
    public void Missing_RequiredFile_ProtosDescriptorset_Fails()
    {
        InRoot(root =>
        {
            var build = MakeBuild(root, "4002");
            var plat = MakePlatform(build, LinuxPlatform, ProvNoContent);
            File.Delete(Path.Combine(plat, "protos.descriptorset"));

            var verdict = Validate(root, "4002");
            Assert.False(verdict.Passed);
            Assert.Contains(verdict.Violations, x => x.Message.Contains("protos.descriptorset"));
        });
    }

    [Fact]
    public void Empty_ProtosDir_Fails()
    {
        InRoot(root =>
        {
            var build = MakeBuild(root, "4003");
            var plat = MakePlatform(build, LinuxPlatform, ProvNoContent);
            File.Delete(Path.Combine(plat, "protos", "x.proto"));  // dir exists but is now empty

            var verdict = Validate(root, "4003");
            Assert.False(verdict.Passed);
            Assert.Contains(verdict.Violations, x => x.Message.Contains("protos") && x.Message.Contains("empty"));
        });
    }

    // =====================================================================================
    // 6. Legal shapes (A) single-platform; (B) full-build with omissions accounting.
    // =====================================================================================

    [Fact]
    public void ShapeA_SinglePlatform_EmptyOmissions_Passes()
    {
        InRoot(root =>
        {
            var build = MakeBuild(root, "5001");
            MakePlatform(build, WindowsPlatform, ProvNoContent);

            Assert.True(Validate(root, "5001").Passed);
        });
    }

    [Fact]
    public void ShapeB_FullBuild_BothPlatformsComplete_Passes()
    {
        InRoot(root =>
        {
            var build = MakeBuild(root, "5002");
            MakePlatform(build, LinuxPlatform, ProvNoContent);
            MakePlatform(build, WindowsPlatform, ProvNoContent);

            Assert.True(Validate(root, "5002").Passed);
        });
    }

    [Fact]
    public void ShapeB_FullBuild_OneOmitted_ValidReason_Passes()
    {
        InRoot(root =>
        {
            // One platform present + complete; the other accounted-for with a valid reason.
            var omissions =
                """{"build_id":"5003","omissions":[{"platform":"windows-x86_64","reason":"DEPOT_UNAVAILABLE","notes":"depot down"}]}""";
            var build = MakeBuild(root, "5003", omissions);
            MakePlatform(build, LinuxPlatform, ProvNoContent);

            var verdict = Validate(root, "5003");
            Assert.True(verdict.Passed, string.Join("; ", verdict.Violations.Select(x => x.Message)));
        });
    }

    [Fact]
    public void MissingPlatform_NoOmissionsEntry_Fails()
    {
        // A lone present platform + EMPTY omissions is the legal single-platform shape (A), so a
        // genuine "unaccounted-for hole" needs shape (B): a NON-empty omissions list that does NOT
        // cover a canonical platform. Here only linux is omitted and NO platform is present on disk,
        // so windows is unaccounted-for (and no platform present at all) -> a partial cross-platform
        // set, forbidden.
        InRoot(root =>
        {
            var omissions =
                """{"build_id":"5004","omissions":[{"platform":"linux-x86_64","reason":"OTHER","notes":"down"}]}""";
            MakeBuild(root, "5004", omissions);  // build dir + omissions, but NO platform dir present

            var verdict = Validate(root, "5004");
            Assert.False(verdict.Passed);
            Assert.Contains(verdict.Violations,
                x => x.Message.Contains("UNACCOUNTED-FOR") || x.Message.Contains("no platform directories"));
        });
    }

    [Fact]
    public void Omission_InvalidReason_Unspecified_Fails()
    {
        InRoot(root =>
        {
            // REASON_UNSPECIFIED (0) is not a valid omission reason.
            var omissions =
                """{"build_id":"5005","omissions":[{"platform":"windows-x86_64","reason":"REASON_UNSPECIFIED"}]}""";
            var build = MakeBuild(root, "5005", omissions);
            MakePlatform(build, LinuxPlatform, ProvNoContent);

            var verdict = Validate(root, "5005");
            Assert.False(verdict.Passed);
            Assert.Contains(verdict.Violations, x => x.Message.Contains("invalid/empty reason"));
        });
    }

    [Fact]
    public void Platform_Present_And_Omitted_Fails()
    {
        InRoot(root =>
        {
            // windows present-and-complete BUT also listed in omissions -> contradiction.
            var omissions =
                """{"build_id":"5006","omissions":[{"platform":"windows-x86_64","reason":"OTHER","notes":"x"}]}""";
            var build = MakeBuild(root, "5006", omissions);
            MakePlatform(build, LinuxPlatform, ProvNoContent);
            MakePlatform(build, WindowsPlatform, ProvNoContent);

            var verdict = Validate(root, "5006");
            Assert.False(verdict.Passed);
            Assert.Contains(verdict.Violations,
                x => x.Message.Contains("present on disk yet also listed in omissions"));
        });
    }

    // =====================================================================================
    // 7. Malformed omissions.json -> fail-loud (no crash). Missing omissions.json -> treated
    //    as clean (absent = omissions:[]) — the empty-file-for-clean-builds ceremony is dropped,
    //    but a genuine unaccounted hole still fails loud (see safety tests below).
    // =====================================================================================

    // Malformed omissions.json -> reported violation, no crash (same fix as Malformed_Provenance).
    [Fact]
    public void Malformed_Omissions_IsReportedViolation_NoThrow()
    {
        InRoot(root =>
        {
            var build = MakeBuild(root, "6001", """{"build_id":"6001","omissions":[""");  // truncated
            MakePlatform(build, LinuxPlatform, ProvNoContent);

            var verdict = Validate(root, "6001");
            Assert.False(verdict.Passed);
            Assert.Contains(verdict.Violations, x => x.Message.Contains("omissions.json"));
        });
    }

    // Policy: absent omissions.json == clean (omissions:[]). A complete single-platform build with
    // NO omissions.json and every required file present PASSES (the empty-file ceremony is dropped).
    [Fact]
    public void Missing_Omissions_TreatedAsClean_Passes()
    {
        InRoot(root =>
        {
            var build = Path.Combine(root, "6002");
            Directory.CreateDirectory(build);  // build dir but NO omissions.json
            MakePlatform(build, LinuxPlatform, ProvNoContent);

            var verdict = Validate(root, "6002");
            Assert.True(verdict.Passed, string.Join("; ", verdict.Violations.Select(x => x.Message)));
        });
    }

    // Safety property (preserved): content depot acquired in provenance.json + a
    // content-gated file missing + NO omissions.json ⇒ STILL a violation (unaccounted hole). The
    // absent omissions.json yields an empty contentOmitted set, so the content-gating check fires.
    [Fact]
    public void Missing_Omissions_ContentDepotHole_StillFails()
    {
        InRoot(root =>
        {
            var build = Path.Combine(root, "6004");
            Directory.CreateDirectory(build);  // build dir but NO omissions.json
            var plat = MakePlatform(build, LinuxPlatform, ProvWithContent);
            AddContentFilesExcept(plat, "prop_data.json");  // content acquired, a gated file missing

            var verdict = Validate(root, "6004");
            Assert.False(verdict.Passed);
            Assert.Contains(verdict.Violations, x => x.Message.Contains("prop_data.json"));
        });
    }

    [Fact]
    public void NonCanonical_PlatformDir_Fails()
    {
        InRoot(root =>
        {
            var build = MakeBuild(root, "6003");
            MakePlatform(build, "macos-arm64", ProvNoContent);  // not a canonical platform

            var verdict = Validate(root, "6003");
            Assert.False(verdict.Passed);
            Assert.Contains(verdict.Violations, x => x.Message.Contains("non-canonical platform directory"));
        });
    }

    // =====================================================================================
    // 8. VerifyArtifactsCommand.Run exit codes + ExtractBuildId.
    // =====================================================================================

    [Fact]
    public void Cli_CleanSet_Exit0()
    {
        InRoot(root =>
        {
            var build = MakeBuild(root, "7001");
            MakePlatform(build, LinuxPlatform, ProvNoContent);

            var code = VerifyArtifactsCommand.Run(new[] { "--artifacts", root, "--build", "7001" });
            Assert.Equal(0, code);
        });
    }

    [Fact]
    public void Cli_Violation_Exit1()
    {
        InRoot(root =>
        {
            var build = MakeBuild(root, "7002");
            var plat = MakePlatform(build, LinuxPlatform, ProvWithContent);
            AddContentFilesExcept(plat, "gameevents.json");  // content acquired, gameevents missing

            var code = VerifyArtifactsCommand.Run(new[] { "--artifacts", root, "--build", "7002" });
            Assert.Equal(1, code);
        });
    }

    [Fact]
    public void Cli_UsageError_Exit64()
    {
        // Unknown option is a usage error.
        var code = VerifyArtifactsCommand.Run(UnknownOptionArgs);
        Assert.Equal(64, code);
    }

    [Fact]
    public void Cli_ChangedPaths_ToolOnly_Exit0_NoOp()
    {
        InRoot(root =>
        {
            // A build exists but the changed paths touch only tool files -> nothing in scope.
            var build = MakeBuild(root, "7003");
            MakePlatform(build, LinuxPlatform, ProvNoContent);

            var code = VerifyArtifactsCommand.Run(new[]
            {
                "--artifacts", root,
                "--changed-paths", "host/src/Foo.cs\nschemas/x.proto\nREADME.md",
            });
            Assert.Equal(0, code);
        });
    }

    [Fact]
    public void Cli_ChangedPaths_ExtractsBuildId_Validates()
    {
        InRoot(root =>
        {
            // A genuinely broken build under artifacts/, referenced via a changed path.
            var build = MakeBuild(root, "7004");
            var plat = MakePlatform(build, LinuxPlatform, ProvWithContent);
            AddContentFilesExcept(plat, "gameevents.json");

            var code = VerifyArtifactsCommand.Run(new[]
            {
                "--artifacts", root,
                "--changed-paths", "artifacts/7004/linux-x86_64/entity_schema.json",
            });
            Assert.Equal(1, code);  // the in-scope build is a violation
        });
    }

    // =====================================================================================
    // 9. build-to-build changelog PREDECESSOR gate.
    //    - earliest build for a platform: changelog must be ABSENT (presence is a violation);
    //    - build with a committed predecessor: changelog REQUIRED, with from_build == predecessor
    //      and to_build == this build (stale from_build is a violation).
    // =====================================================================================

    /// <summary>Write a changelog.json with the given from/to into a (build,platform) dir.</summary>
    private static void WriteChangelog(string root, string buildId, string platform, string from, string to)
    {
        var body = $$"""{"schema_version":"0.4.0","platform":"{{platform}}","from_build":"{{from}}","to_build":"{{to}}","families":[]}""";
        File.WriteAllText(Path.Combine(root, buildId, platform, "changelog.json"), body);
    }

    [Fact]
    public void Changelog_EarliestBuild_Present_Fails()
    {
        InRoot(root =>
        {
            // A lone (earliest) committed build for the platform carrying a changelog -> violation.
            var build = MakeBuild(root, "8001");
            MakePlatform(build, LinuxPlatform, ProvNoContent);
            WriteChangelog(root, "8001", LinuxPlatform, "7999", "8001");

            var verdict = Validate(root, "8001");
            Assert.False(verdict.Passed);
            Assert.Contains(verdict.Violations,
                x => x.Message.Contains("changelog.json") && x.Message.Contains("EARLIEST"));
        });
    }

    [Fact]
    public void Changelog_EarliestBuild_Absent_Passes()
    {
        InRoot(root =>
        {
            var build = MakeBuild(root, "8002");
            MakePlatform(build, LinuxPlatform, ProvNoContent);   // no changelog — correct for earliest.

            Assert.True(Validate(root, "8002").Passed);
        });
    }

    [Fact]
    public void Changelog_WithPredecessor_Missing_Fails()
    {
        InRoot(root =>
        {
            // Two committed builds for the platform; the NEWER lacks a changelog -> violation.
            MakePlatform(MakeBuild(root, "8003"), LinuxPlatform, ProvNoContent);
            MakePlatform(MakeBuild(root, "8004"), LinuxPlatform, ProvNoContent);

            var verdict = Validate(root, "8004");
            Assert.False(verdict.Passed);
            Assert.Contains(verdict.Violations,
                x => x.Message.Contains("MISSING changelog.json"));
        });
    }

    [Fact]
    public void Changelog_WithPredecessor_Correct_Passes()
    {
        InRoot(root =>
        {
            MakePlatform(MakeBuild(root, "8005"), LinuxPlatform, ProvNoContent);
            MakePlatform(MakeBuild(root, "8006"), LinuxPlatform, ProvNoContent);
            WriteChangelog(root, "8006", LinuxPlatform, "8005", "8006");

            var verdict = Validate(root, "8006");
            Assert.True(verdict.Passed, string.Join("; ", verdict.Violations.Select(x => x.Message)));
            // And the earliest one (8005) must pass with NO changelog.
            Assert.True(Validate(root, "8005").Passed);
        });
    }

    [Fact]
    public void Changelog_WithPredecessor_StaleFromBuild_Fails()
    {
        InRoot(root =>
        {
            MakePlatform(MakeBuild(root, "8007"), LinuxPlatform, ProvNoContent);
            MakePlatform(MakeBuild(root, "8008"), LinuxPlatform, ProvNoContent);
            // from_build points at a non-predecessor (an out-of-order backfill that wasn't regenerated).
            WriteChangelog(root, "8008", LinuxPlatform, "7000", "8008");

            var verdict = Validate(root, "8008");
            Assert.False(verdict.Passed);
            Assert.Contains(verdict.Violations,
                x => x.Message.Contains("from_build") && x.Message.Contains("stale"));
        });
    }

    [Fact]
    public void Changelog_WithPredecessor_Malformed_IsReportedViolation_NoThrow()
    {
        InRoot(root =>
        {
            MakePlatform(MakeBuild(root, "8009"), LinuxPlatform, ProvNoContent);
            MakePlatform(MakeBuild(root, "8010"), LinuxPlatform, ProvNoContent);
            File.WriteAllText(
                Path.Combine(root, "8010", LinuxPlatform, "changelog.json"),
                """{"from_build":"8009""");   // truncated

            var verdict = Validate(root, "8010");
            Assert.False(verdict.Passed);
            Assert.Contains(verdict.Violations, x => x.Message.Contains("changelog.json"));
        });
    }

    [Theory]
    [InlineData("artifacts/12345/linux-x86_64/convars.json", "12345")]
    [InlineData("artifacts/12345/omissions.json", "12345")]
    [InlineData("artifacts/99999/windows-x86_64/protos/x.proto", "99999")]
    [InlineData("host/src/Foo.cs", null)]                 // not under artifacts/
    [InlineData("artifacts/README.md", null)]             // a file directly under root, no build dir
    [InlineData("artifacts/", null)]                      // root only
    [InlineData("", null)]                                // empty
    public void ExtractBuildId_MapsPathToBuildId(string path, string? expected)
    {
        Assert.Equal(expected, VerifyArtifactsCommand.ExtractBuildId(path, "artifacts"));
    }

    // =====================================================================================
    // pics-appinfo.json is FULLY OPTIONAL build-level: presence OK, absence OK, NEVER an
    // omissions entry, NEVER illegal.
    // =====================================================================================

    [Fact]
    public void PicsAppInfo_Absent_BuildLevel_Is_Not_A_Violation()
    {
        InRoot(root =>
        {
            var buildDir = MakeBuild(root, "5001");
            MakePlatform(buildDir, LinuxPlatform, ProvNoContent);
            // NO pics-appinfo.json at the build level.
            Assert.True(Validate(root, "5001").Passed);
        });
    }

    [Fact]
    public void PicsAppInfo_Present_BuildLevel_Is_Not_Illegal()
    {
        InRoot(root =>
        {
            var buildDir = MakeBuild(root, "5002");
            MakePlatform(buildDir, LinuxPlatform, ProvNoContent);
            // A build-level pics-appinfo.json file (NOT under <platform>/) must be ignored:
            // the validator enumerates platform DIRECTORIES, so a build-level FILE is neither
            // a non-canonical platform dir nor a missing required file.
            File.WriteAllText(
                Path.Combine(buildDir, "pics-appinfo.json"),
                """{"schemaVersion":"0.4.0","buildId":"5002","appId":730}""");
            var verdict = Validate(root, "5002");
            Assert.True(verdict.Passed);
            Assert.DoesNotContain(verdict.Violations, x => x.Message.Contains("pics-appinfo"));
        });
    }
}

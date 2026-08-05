// shared producible-artifact fixtures for the cross-artifact
// invariants suites.
//
// This is the single place that knows HOW to produce each artifact that is emittable
// TODAY (entity_schema.json, gameevents.json, modules.json) from a synthetic fixture
// (no real binaries, no walker run, no Steam). The (schema-validation round-trip),
// (determinism), and (fail-loud) suites all drive off this one table so that
// adding a future artifact (convars / commands / network_messages / provenance) is a
// ONE-LINE addition to ArtifactCases.All — define an EmitFn + the generated proto
// message type + (if the emitter routes through Google.Protobuf.JsonFormatter) the
// byte-identical-round-trip flag.

using System.Buffers.Binary;
using System.Text;

using Cs2SchemaTracker.Host;
using Cs2SchemaTracker.Host.Commands;
using Cs2SchemaTracker.Host.ConVars;
using Cs2SchemaTracker.Host.EngineConstants;
using Cs2SchemaTracker.Host.EntitySchema;
using Cs2SchemaTracker.Host.GameEvents;
using Cs2SchemaTracker.Host.GameModes;
using Cs2SchemaTracker.Host.Items;
using Cs2SchemaTracker.Host.Localization;
using Cs2SchemaTracker.Host.MapOverviews;
using Cs2SchemaTracker.Host.Modules;
using Cs2SchemaTracker.Host.NetworkMessages;
using Cs2SchemaTracker.Host.PropData;
using Cs2SchemaTracker.Host.Provenance;
using Cs2SchemaTracker.Host.StringPools;
using Cs2SchemaTracker.Host.SurfaceProperties;
using Cs2SchemaTracker.Host.Vpk;
using Cs2SchemaTracker.Schemas;

using Google.Protobuf;

namespace Cs2SchemaTracker.Tests.Invariants;

/// <summary>
/// One producible artifact, described for the cross-artifact invariants suites.
/// </summary>
internal sealed class ArtifactCase
{
    /// <summary>Stable display name (the on-disk artifact filename).</summary>
    public required string FileName { get; init; }

    /// <summary>
    /// Emit the artifact from a synthetic fixture to <paramref name="outputPath"/>.
    /// Produces a REAL artifact on disk (round-tripping real bytes, never a mock) —
    /// then loads exactly these bytes back.
    /// </summary>
    public required Action<string> Emit { get; init; }

    /// <summary>
    /// Parse the emitted JSON text into its generated proto3 message with STRICT settings
    /// (unknown fields are rejected). A successful parse is the "validates against its
    /// schema" assertion; the returned message feeds the canonical re-serialization.
    /// </summary>
    public required Func<string, IMessage> Parse { get; init; }

    /// <summary>
    /// True when the emitter already writes canonical proto3 JSON (via
    /// Google.Protobuf.JsonFormatter + CanonicalJson) and therefore a parse →
    /// canonical-reformat round-trip is byte-identical to the emitted file (the full
    /// round-trip contract). False for an emitter that writes a non-proto3-canonical shape (currently
    /// only modules.json, whose POCO emits uint64 as a JSON number rather than the proto3
    /// string mapping); for those the strict-parse half still proves schema validity and
    /// <see cref="RoundTripDefect"/> records the tracked reconciliation.
    /// </summary>
    public required bool ByteIdenticalRoundTrip { get; init; }

    /// <summary>
    /// Reference for a known, tracked reason the byte-identical round-trip does
    /// not yet hold. Null when <see cref="ByteIdenticalRoundTrip"/> is true.
    /// </summary>
    public string? RoundTripDefect { get; init; }
}

internal static class ArtifactCases
{
    private const string BuildId = "13371337";
    private const string Platform = "linux-x86_64";

    /// <summary>
    /// The producible-now artifacts. Add a future artifact here (one entry) and every
    /// invariants suite picks it up automatically.
    /// </summary>
    public static IReadOnlyList<ArtifactCase> All { get; } = new[]
    {
        new ArtifactCase
        {
            FileName = "entity_schema.json",
            Emit = outPath => new EntitySchemaEmitter(SchemaFamily.Version, BuildId, Platform, "987654")
                .Emit(BuildEntitySchemaWalk(), outPath),
            Parse = json => ParseStrict<Cs2SchemaTracker.Schemas.EntitySchema>(json),
            ByteIdenticalRoundTrip = true,
        },
        new ArtifactCase
        {
            FileName = "gameevents.json",
            Emit = outPath => new GameEventsEmitter(SchemaFamily.Version, BuildId, Platform)
                .Emit(BuildGameEventsArchive(), outPath),
            Parse = json => ParseStrict<Cs2SchemaTracker.Schemas.GameEvents>(json),
            ByteIdenticalRoundTrip = true,
        },
        new ArtifactCase
        {
            FileName = "modules.json",
            Emit = outPath => BuildModules(outPath),
            // Cs2SchemaTracker.Schemas.Modules is generated in the HOST assembly now (
            // migration); the emitter routes through Google.Protobuf.JsonFormatter so the
            // uint64 file_size uses the proto3 string mapping and the round-trip is
            // byte-identical.
            Parse = json => ParseStrict<Cs2SchemaTracker.Schemas.Modules>(json),
            ByteIdenticalRoundTrip = true,
        },
        new ArtifactCase
        {
            FileName = "convars.json",
            Emit = outPath => new ConVarsEmitter(SchemaFamily.Version, BuildId, Platform)
                .Emit(BuildConVarsWalk(), outPath),
            Parse = json => ParseStrict<Cs2SchemaTracker.Schemas.ConVars>(json),
            ByteIdenticalRoundTrip = true,
        },
        new ArtifactCase
        {
            FileName = "commands.json",
            Emit = outPath => new CommandsEmitter(SchemaFamily.Version, BuildId, Platform)
                .Emit(BuildCommandsWalk(), outPath),
            Parse = json => ParseStrict<Cs2SchemaTracker.Schemas.Commands>(json),
            ByteIdenticalRoundTrip = true,
        },
        new ArtifactCase
        {
            FileName = "network_messages.json",
            Emit = outPath => new NetworkMessagesEmitter(SchemaFamily.Version, BuildId, Platform)
                .Emit(BuildNetworkMessagesChannels(), outPath),
            Parse = json => ParseStrict<Cs2SchemaTracker.Schemas.NetworkMessages>(json),
            ByteIdenticalRoundTrip = true,
        },
        new ArtifactCase
        {
            FileName = "engine_constants.json",
            Emit = outPath => new EngineConstantsEmitter(SchemaFamily.Version, BuildId, Platform)
                .Emit(BuildEngineConstantsWalk(), outPath),
            Parse = json => ParseStrict<Cs2SchemaTracker.Schemas.EngineConstants>(json),
            ByteIdenticalRoundTrip = true,
        },
        new ArtifactCase
        {
            FileName = "string_pools.json",
            Emit = outPath => new StringPoolsEmitter(SchemaFamily.Version, BuildId, Platform)
                .Emit(BuildStringPoolsWalk(), outPath),
            Parse = json => ParseStrict<Cs2SchemaTracker.Schemas.StringPools>(json),
            ByteIdenticalRoundTrip = true,
        },
        new ArtifactCase
        {
            FileName = "provenance.json",
            Emit = outPath => BuildProvenance(outPath),
            Parse = json => ParseStrict<Cs2SchemaTracker.Schemas.Provenance>(json),
            ByteIdenticalRoundTrip = true,
        },
        new ArtifactCase
        {
            FileName = "item_definitions.json",
            Emit = outPath => new ItemDefinitionsEmitter(SchemaFamily.Version, BuildId, Platform)
                .Emit(BuildItemDefinitionsArchive(), outPath),
            Parse = json => ParseStrict<Cs2SchemaTracker.Schemas.ItemDefinitions>(json),
            ByteIdenticalRoundTrip = true,
        },
        new ArtifactCase
        {
            FileName = "game_modes.json",
            Emit = outPath => new GameModesEmitter(SchemaFamily.Version, BuildId, Platform)
                .Emit(BuildGameModesArchive(), outPath),
            Parse = json => ParseStrict<Cs2SchemaTracker.Schemas.GameModes>(json),
            ByteIdenticalRoundTrip = true,
        },
        new ArtifactCase
        {
            FileName = "localization.json",
            Emit = outPath => new LocalizationEmitter(SchemaFamily.Version, BuildId, Platform)
                .Emit(BuildLocalizationArchive(), outPath),
            Parse = json => ParseStrict<Cs2SchemaTracker.Schemas.Localization>(json),
            ByteIdenticalRoundTrip = true,
        },
        new ArtifactCase
        {
            FileName = "surface_properties.json",
            Emit = outPath => new SurfacePropertiesEmitter(SchemaFamily.Version, BuildId, Platform)
                .Emit(BuildSurfacePropertiesArchive(), outPath),
            Parse = json => ParseStrict<Cs2SchemaTracker.Schemas.SurfaceProperties>(json),
            ByteIdenticalRoundTrip = true,
        },
        new ArtifactCase
        {
            FileName = "prop_data.json",
            Emit = outPath => new PropDataEmitter(SchemaFamily.Version, BuildId, Platform)
                .Emit(BuildPropDataArchive(), outPath),
            Parse = json => ParseStrict<Cs2SchemaTracker.Schemas.PropData>(json),
            ByteIdenticalRoundTrip = true,
        },
        new ArtifactCase
        {
            FileName = "map_overviews.json",
            Emit = outPath => new MapOverviewsEmitter(SchemaFamily.Version, BuildId, Platform)
                .Emit(BuildMapOverviewsArchive(), outPath),
            Parse = json => ParseStrict<Cs2SchemaTracker.Schemas.MapOverviews>(json),
            ByteIdenticalRoundTrip = true,
        },
        // registry_audit.json is intentionally NOT in this table. Every case here
        // emits from a single in-memory walk fixture via an Action<outputPath> (one artifact, one
        // emit). The registry audit's input is a DIRECTORY of OTHER already-emitted artifacts, not
        // a single walk — so it does not fit the single-emit-fn shape without smuggling a whole
        // produced (build, platform) directory through `outputPath`. Forcing it in would distort
        // the table's contract. Its schema-validity (strict-parsing the emitted
        // registry_audit.json back to RegistryAudit), determinism, and fail-loud behavior are
        // proven directly in RegistryAudit/RegistryAuditEmitterTest.cs instead.
    };

    // ---- Strict proto3-JSON parse (the schema-validation primitive) -------------

    private static readonly JsonParser StrictParser =
        new(JsonParser.Settings.Default.WithIgnoreUnknownFields(false));

    private static T ParseStrict<T>(string json) where T : IMessage<T>, new()
        => StrictParser.Parse<T>(json);

    // ---- entity_schema.json fixture --------------------------------------------------

    // A class with a parent and fields exercising recursive SchemaType (template + pointer +
    // fixed-array + bitfield), plus an enum with a negative member and member metadata, plus
    // an MGetKV3ClassDefaults annotation (value_parsed). Mirrors the rich fixture.
    private static WalkerOutput BuildEntitySchemaWalk()
    {
        var en = new SchemaEnum { Name = "MoveType_t", Module = "server", Alignment = "uint8_t" };
        en.Members.Add(new SchemaEnumMember { Name = "MOVETYPE_NONE", Value = 0 });
        en.Members.Add(new SchemaEnumMember { Name = "MOVETYPE_WALK", Value = 2 });
        var invalid = new SchemaEnumMember { Name = "MOVETYPE_INVALID", Value = -1 };
        invalid.Metadata.Add(new SchemaMetadata { Name = "MPropertyDescription", Value = "sentinel" });
        en.Members.Add(invalid);

        var cls = new SchemaClass { Name = "C_BaseEntity", Module = "client", Size = 1416 };
        cls.Parents.Add(new SchemaClassParent { Name = "CEntityInstance", Module = "client" });
        cls.Fields.Add(new SchemaField
        {
            Name = "m_iHealth",
            Offset = 80,
            TypeModule = "",
            Type = new SchemaType { Category = SchemaType.Types.Category.Builtin, Name = "int32" },
        });
        cls.Fields.Add(new SchemaField
        {
            Name = "m_hChildren",
            Offset = 88,
            TypeModule = "client",
            Type = new SchemaType
            {
                Category = SchemaType.Types.Category.Atomic,
                Name = "CUtlVector",
                Inner = new SchemaType
                {
                    Category = SchemaType.Types.Category.Atomic,
                    Name = "CHandle",
                    Inner = new SchemaType
                    {
                        Category = SchemaType.Types.Category.DeclaredClass,
                        Name = "C_BaseEntity",
                        Module = "client",
                    },
                },
            },
        });
        cls.Fields.Add(new SchemaField
        {
            Name = "m_vecOrigin",
            Offset = 120,
            TypeModule = "",
            Type = new SchemaType
            {
                Category = SchemaType.Types.Category.FixedArray,
                Count = 3,
                Inner = new SchemaType { Category = SchemaType.Types.Category.Builtin, Name = "float32" },
            },
        });
        cls.Fields.Add(new SchemaField
        {
            Name = "m_nFlags",
            Offset = 0,
            TypeModule = "",
            Type = new SchemaType { Category = SchemaType.Types.Category.Bitfield, Count = 4 },
        });
        cls.Metadata.Add(new SchemaMetadata
        {
            Name = "MGetKV3ClassDefaults",
            Value = "{ m_iHealth = 100 m_flScale = 1.0 }",
        });
        cls.Metadata.Add(new SchemaMetadata { Name = "MPropertyFriendlyName", Value = "Base Entity" });

        // Second class tagged module="server" so the union model is exercised.
        var serverCls = new SchemaClass { Name = "CBaseEntity", Module = "server", Size = 1416 };
        serverCls.Fields.Add(new SchemaField
        {
            Name = "m_iMaxHealth",
            Offset = 84,
            TypeModule = "",
            Type = new SchemaType { Category = SchemaType.Types.Category.Builtin, Name = "int32" },
        });

        var walk = new EntitySchemaWalk();
        walk.Classes.Add(cls);
        walk.Classes.Add(serverCls);
        walk.Enums.Add(en);

        return new WalkerOutput
        {
            SchemaVersion = "ignored-by-host",
            WalkerVersion = "0.0.0-test",
            Platform = Platform,
            EntitySchema = walk,
            SchemaSystemLayoutSignature = "sig-test",
        };
    }

    // ---- gameevents.json fixture (an in-memory VPK carrying .gameevents KV1) ----------

    private const string CoreGameEvents =
        """
        "GameEvents"
        {
            "player_death"           // a player died
            {
                "local"    "0"
                "reliable" "1"
                "userid"   "short"   // user ID who died
                "attacker" "short"
                "weapon"   "string"
            }
            "round_start"
            {
                "reliable" "1"
                "timelimit" "long"
            }
        }
        """;

    private const string GameGameEvents =
        """
        "GameEvents"
        {
            "bomb_planted"
            {
                "userid" "short"
                "site"   "short"
            }
        }
        """;

    private static VpkArchive BuildGameEventsArchive()
    {
        var files = new List<FileSpec>
        {
            new("resource", "gameevents", "game", Encoding.UTF8.GetBytes(GameGameEvents)),
            new("resource", "gameevents", "core", Encoding.UTF8.GetBytes(CoreGameEvents)),
            new("scripts", "txt", "items", Encoding.ASCII.GetBytes("not an event file")),
        };
        return VpkArchive.Parse("pak01_dir.vpk", BuildEmbeddedVpk(2, files));
    }

    // ---- item_definitions.json fixture (an in-memory VPK carrying items_game.txt) -----

    private const string ItemsGameTxt =
        """
        "items_game"
        {
            "items"
            {
                "default" { "name" "default" "item_class" "default" }
                "7"       { "name" "ak47" "item_name" "#weapon_ak47" "prefab" "primary weapon" }
                "1"       { "name" "deagle" "item_name" "#weapon_deagle" }
            }
            "prefabs"
            {
                "weapon_base" { "item_class" "weapon" "item_slot" "rifle" }
            }
            "paint_kits"
            {
                "0" { "name" "default" "description_tag" "#none" }
            }
            "rarities"
            {
                "common" { "value" "1" "loc_key" "#rare_common" }
            }
            "qualities"
            {
                "normal" { "value" "0" }
            }
        }
        """;

    private static VpkArchive BuildItemDefinitionsArchive()
    {
        var files = new List<FileSpec>
        {
            new("scripts/items", "txt", "items_game", Encoding.UTF8.GetBytes(ItemsGameTxt)),
        };
        return VpkArchive.Parse("pak01_dir.vpk", BuildEmbeddedVpk(2, files));
    }

    // ---- game_modes.json fixture (an in-memory VPK carrying the loose gamemodes.txt) ----

    private const string GameModesTxt =
        """
        "GameModes++"
        {
            "gameTypes"
            {
                "classic"
                {
                    "index" "0"
                    "gameModes"
                    {
                        "competitive"
                        {
                            "nameID" "#SFUI_GameModeMatchmaking"
                            "displayName" "#SFUI_Competitive"
                            "maxplayers" "10"
                            "game_type" "0"
                            "game_mode" "1"
                            "mapgroupsMP" { "mg_active" "" }
                            "convars" { "mp_roundtime" "1.92" "bot_quota" "0" }
                        }
                        "casual"
                        {
                            "maxplayers" "20"
                            "game_mode" "0"
                        }
                    }
                }
            }
            "mapgroups"
            {
                "mg_active" { "displayname" "Active Duty" "maps" { "de_ancient" "" "de_anubis" "" } }
            }
        }
        """;

    private static VpkArchive BuildGameModesArchive()
    {
        var files = new List<FileSpec>
        {
            // The loose top-level gamemodes.txt: path " " (root), ext "txt".
            new(" ", "txt", "gamemodes", Encoding.UTF8.GetBytes(GameModesTxt)),
        };
        return VpkArchive.Parse("pak01_dir.vpk", BuildEmbeddedVpk(2, files));
    }

    // ---- localization.json fixture (in-memory VPK carrying resource/csgo_<lang>.txt) ----

    private const string CsgoEnglish =
        """
        "lang"
        {
            "Language" "English"
            "Tokens"
            {
                "weapon_ak47" "AK-47"
                "SFUI_WPNHUD_AK47" "AK-47"
            }
        }
        """;

    private const string CsgoGerman =
        """
        "lang"
        {
            "Language" "German"
            "Tokens"
            {
                "weapon_ak47" "AK-47 (de)"
            }
        }
        """;

    private static VpkArchive BuildLocalizationArchive()
    {
        var files = new List<FileSpec>
        {
            new("resource", "txt", "csgo_english", Encoding.UTF8.GetBytes(CsgoEnglish)),
            new("resource", "txt", "csgo_german", Encoding.UTF8.GetBytes(CsgoGerman)),
        };
        return VpkArchive.Parse("pak01_dir.vpk", BuildEmbeddedVpk(2, files));
    }

    // ---- surface_properties.json fixture (in-memory VPK; KV3-TEXT surfaceproperties_*.txt) ----

    private const string SurfaceGameKv3 =
        """
        <!-- kv3 encoding:text:version{e21c7f3c-8a33-41c5-9977-a76d3a32aa0d} format:generic:version{7412167c-06e9-4698-aff2-e63eb59037e7} -->
        {
            SurfacePropertiesList =
            [
                {
                    surfacePropertyName = "default"
                    gamematerial = "C"
                    jumpfactor = 1.0
                    climbable = false
                    bulletPenetrationDamageModifier = 0.5
                },
                {
                    surfacePropertyName = "metal"
                    gamematerial = "M"
                    bulletPenetrationDistanceModifier = 0.4
                },
            ]
        }
        """;

    // Exercises the typed-resource KV3 value form (resource:"…") + a per-material disjoint field set.
    private const string SurfaceImpactKv3 =
        """
        <!-- kv3 encoding:text:version{e21c7f3c-8a33-41c5-9977-a76d3a32aa0d} format:generic:version{7412167c-06e9-4698-aff2-e63eb59037e7} -->
        {
            SurfacePropertiesList =
            [
                {
                    surfacePropertyName = "default"
                    effect = resource:"particles/impact_fx/impact_concrete.vpcf"
                    impactDecalName = "Impact.Concrete"
                },
            ]
        }
        """;

    internal static VpkArchive BuildSurfacePropertiesArchive()
    {
        var files = new List<FileSpec>
        {
            new("scripts", "txt", "surfaceproperties_game", Encoding.UTF8.GetBytes(SurfaceGameKv3)),
            new("scripts", "txt", "surfaceproperties_impact_effects", Encoding.UTF8.GetBytes(SurfaceImpactKv3)),
        };
        return VpkArchive.Parse("pak01_dir.vpk", BuildEmbeddedVpk(2, files));
    }

    // ---- prop_data.json fixture (in-memory VPK; propdata.txt KV1 + collision_properties.txt KV3) ----

    private const string PropDataTxt =
        """
        "PropData.txt"
        {
            "Cloth.Small" { "base" "Cloth.Base" "health" "30" }
            "Door.Standard" { "dmg.bullets" "1.0" "health" "1000" }
            "BreakableModels"
            {
                "WoodChunks"
                {
                    "models/Gibs/wood_gib01b.vmdl" "1"
                    "models/Gibs/wood_gib01a.vmdl" "1"
                }
            }
        }
        """;

    private const string CollisionPropertiesKv3 =
        """
        <!-- kv3 encoding:text:version{e21c7f3c-8a33-41c5-9977-a76d3a32aa0d} format:generic:version{7412167c-06e9-4698-aff2-e63eb59037e7} -->
        {
            collision_properties =
            [
                {
                    name = "default"
                    description = "Default collision"
                    collision_group = "default"
                    interact_as = []
                    interact_with = []
                    interact_exclude = []
                },
                {
                    name = "window"
                    description = "Solid but does not block light"
                    collision_group = "ConditionallySolid"
                    interact_as = [ "window" ]
                    interact_with = []
                    interact_exclude = []
                },
            ]
        }
        """;

    internal static VpkArchive BuildPropDataArchive()
    {
        var files = new List<FileSpec>
        {
            new("scripts", "txt", "propdata", Encoding.UTF8.GetBytes(PropDataTxt)),
            new("scripts", "txt", "collision_properties", Encoding.UTF8.GetBytes(CollisionPropertiesKv3)),
        };
        return VpkArchive.Parse("pak01_dir.vpk", BuildEmbeddedVpk(2, files));
    }

    // ---- map_overviews.json fixture (in-memory VPK; resource/overviews/<map>.txt KV1) ----

    private const string OverviewDust2 =
        """
        "de_dust2"
        {
            "material" "overviews/de_dust2_v2"
            "pos_x" "-2476"
            "pos_y" "3239"
            "scale" "4.4"
            "rotate" "1"
            "zoom" "1.1"
            "inset_left" "0.0"
            "bombA_x" "0.80"
            "bombA_y" "0.16"
            "CTSpawn_x" "0.62"
            "CTSpawn_y" "0.21"
        }
        """;

    private const string OverviewMirage =
        """
        "de_mirage"
        {
            "material" "overviews/de_mirage"
            "pos_x" "-3230"
            "pos_y" "1713"
            "scale" "5.0"
        }
        """;

    internal static VpkArchive BuildMapOverviewsArchive()
    {
        var files = new List<FileSpec>
        {
            new("resource/overviews", "txt", "de_dust2", Encoding.UTF8.GetBytes(OverviewDust2)),
            new("resource/overviews", "txt", "de_mirage", Encoding.UTF8.GetBytes(OverviewMirage)),
        };
        return VpkArchive.Parse("pak01_dir.vpk", BuildEmbeddedVpk(2, files));
    }

    // ---- modules.json fixture (synthetic PE + ELF binaries) --------------------------

    public static void BuildModules(string outPath)
    {
        // Materialize two synthetic binaries (one PE, one ELF) into a STABLE, shared fixture
        // dir (NOT under the per-run output dir) so the emitter records identical input paths
        // across two runs — otherwise the recorded `path` field would vary by run and the
        // determinism assertion would fail on path, not on emitter behaviour. The bytes are
        // fixed, so writing them idempotently from any test is safe; the emitter inspects real
        // bytes (SHA-256 / size / export_count) over these actual files (unaffected).
        var binDir = Path.Combine(Path.GetTempPath(), "cs2-tr-modules-fixture-bins");
        Directory.CreateDirectory(binDir);
        var pe = Path.Combine(binDir, "client.dll");
        var elf = Path.Combine(binDir, "libserver.so");
        WriteIfAbsent(pe, BuildPe());
        WriteIfAbsent(elf, BuildElf());

        new ModuleManifestEmitter(SchemaFamily.Version, BuildId, Platform)
            .Emit(new[] { new ModuleInput(pe, 17), new ModuleInput(elf, 3) }, outPath);
    }

    private static void WriteIfAbsent(string path, byte[] bytes)
    {
        if (!File.Exists(path))
        {
            // Atomic-ish: write a unique temp then move into place, tolerating a concurrent
            // creator (the bytes are identical regardless of which writer wins).
            var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(tmp, bytes);
            try
            { File.Move(tmp, path, overwrite: false); }
            catch (IOException) { try { File.Delete(tmp); } catch { /* best effort */ } }
        }
    }

    private static byte[] BuildPe()
    {
        var mi = typeof(Cs2SchemaTracker.Tests.Modules.PortableExecutableInspectorTest).GetMethod(
            "BuildMinimalPe",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        return (byte[])mi.Invoke(null, new object[] { 1, false })!;
    }

    private static byte[] BuildElf()
    {
        var build = typeof(Cs2SchemaTracker.Tests.Modules.ElfInspectorTest).GetMethod(
            "BuildElf64",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        var symBuilder = typeof(Cs2SchemaTracker.Tests.Modules.ElfInspectorTest).GetMethod(
            "BuildSym",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        var sym = (byte[])symBuilder.Invoke(null, new object[] { (byte)1, (byte)2, (ushort)1 })!;
        return (byte[])build.Invoke(null, new object[] { new[] { sym }, true })!;
    }

    // ---- convars.json / commands.json / network_messages.json fixtures ----------------
    //
    // Each builds a WalkerOutput carrying the relevant walk sub-message (the emitter lifts it).
    // Deliberately UNSORTED input so the emitter's deterministic ordering is exercised.

    private static WalkerOutput BuildConVarsWalk()
    {
        var walk = new ConVarsWalk();
        var sv_cheats = new ConVar { Name = "sv_cheats", Default = "0", Description = "Allow cheats" };
        sv_cheats.Flags.Add("release");
        sv_cheats.Flags.Add("notify");
        var mp_round = new ConVar { Name = "mp_roundtime", Default = "1.92", Description = "" };
        mp_round.Flags.Add("gamedll");
        // Out-of-order on purpose (m before s) — the emitter must sort by name.
        walk.Convars.Add(mp_round);
        walk.Convars.Add(sv_cheats);

        return new WalkerOutput
        {
            SchemaVersion = "ignored-by-host",
            WalkerVersion = "0.0.0-test",
            Platform = Platform,
            Convars = walk,
            SchemaSystemLayoutSignature = "sig-test",
        };
    }

    private static WalkerOutput BuildCommandsWalk()
    {
        var walk = new CommandsWalk();
        var kill = new Command { Name = "kill", Description = "Commit suicide" };
        kill.Flags.Add("gamedll");
        var jump = new Command { Name = "+jump", Description = "" };
        jump.Flags.Add("clientdll");
        walk.Commands.Add(kill);
        walk.Commands.Add(jump);

        return new WalkerOutput
        {
            SchemaVersion = "ignored-by-host",
            WalkerVersion = "0.0.0-test",
            Platform = Platform,
            Commands = walk,
            SchemaSystemLayoutSignature = "sig-test",
        };
    }

    // source is now the host RTTI scan (NetworkMessageRttiScanner -> NetworkChannel[]); the
    // emitter takes channels directly. The unresolved id 99 (empty type) is something the RTTI scan
    // never produces, but the EMITTER still preserves it — kept here to exercise that path.
    private static NetworkChannel[] BuildNetworkMessagesChannels()
    {
        var net = new NetworkChannel { Name = "NetMessages" };
        net.Messages.Add(new NetworkMessageEntry { Id = 7, ProtoMessageType = "CNETMsg_Tick" });
        net.Messages.Add(new NetworkMessageEntry { Id = 4, ProtoMessageType = "CNETMsg_SignonState" });
        // An ID with no resolvable type — kept, not dropped.
        net.Messages.Add(new NetworkMessageEntry { Id = 99, ProtoMessageType = "" });

        var ge = new NetworkChannel { Name = "GameEvents" };
        ge.Messages.Add(new NetworkMessageEntry { Id = 1, ProtoMessageType = "CMsgSource1LegacyGameEvent" });

        // Channels out-of-order (NetMessages before GameEvents) — the emitter sorts by name.
        return new[] { net, ge };
    }

    private static WalkerOutput BuildEngineConstantsWalk()
    {
        var walk = new EngineConstantsWalk();
        // An int-valued constant and a string-valued constant; both name + source non-empty.
        // Source uses the REAL walker form "schema_enum:<module>/<EnumName>"
        // (engine_constants_walk.cpp is the only producer of engine-constant sources).
        walk.Constants.Add(new EngineConstant
        {
            Name = "MAX_PLAYERS",
            Source = "schema_enum:server.dll/CGameRules",
            IntValue = 64,
        });
        // Out-of-order on purpose (M < S) — the emitter must sort by name.
        walk.Constants.Insert(0, new EngineConstant
        {
            Name = "SOURCE_ENGINE_NAME",
            Source = "schema_enum:engine2.dll/SourceEngineBuild",
            StringValue = "Source2",
        });

        return new WalkerOutput
        {
            SchemaVersion = "ignored-by-host",
            WalkerVersion = "0.0.0-test",
            Platform = Platform,
            EngineConstants = walk,
            SchemaSystemLayoutSignature = "sig-test",
        };
    }

    private static WalkerOutput BuildStringPoolsWalk()
    {
        var walk = new StringPoolsWalk();
        var sym = new StringPool { Name = "CUtlSymbolLarge" };
        // Deliberately unsorted + duplicated — the emitter must dedupe and sort.
        sym.Entries.Add("m_iHealth");
        sym.Entries.Add("m_vecOrigin");
        sym.Entries.Add("m_iHealth");
        var fileNames = new StringPool { Name = "CUtlFilenameSymbolTable" };
        fileNames.Entries.Add("materials/dev/reflectivity_30.vmat");
        // Pools out-of-order (C... after C..., but sym before fileNames lexically reversed) —
        // the emitter sorts pools by name.
        walk.Pools.Add(sym);
        walk.Pools.Add(fileNames);

        return new WalkerOutput
        {
            SchemaVersion = "ignored-by-host",
            WalkerVersion = "0.0.0-test",
            Platform = Platform,
            StringPools = walk,
            SchemaSystemLayoutSignature = "sig-test",
        };
    }

    // ---- provenance.json fixture (synthetic input binaries + synthetic Steam identity) -

    public static void BuildProvenance(string outPath)
    {
        // Reuse the same stable synthetic binaries the modules fixture materializes, so the
        // recorded path/sha256/size are stable across runs (no per-run temp paths).
        var binDir = Path.Combine(Path.GetTempPath(), "cs2-tr-modules-fixture-bins");
        Directory.CreateDirectory(binDir);
        var pe = Path.Combine(binDir, "client.dll");
        var elf = Path.Combine(binDir, "libserver.so");
        WriteIfAbsent(pe, BuildPe());
        WriteIfAbsent(elf, BuildElf());

        var ctx = new ProvenanceContext
        {
            SchemaVersion = SchemaFamily.Version,
            BuildId = BuildId,
            Platform = Platform,
            GitCommit = "deadbeefdeadbeefdeadbeefdeadbeefdeadbeef",
            AppId = 730,
            ManifestCreatedUtc = "2026-06-10T12:00:00Z",
            SchemaRevision = "sig-test",
            // built_from_cl intentionally left default (content-depot-only).
            Depots = new[]
            {
                new ProvenanceDepot(2347773, "8287382081622299196"),
                new ProvenanceDepot(2347770, "5146470907583764090"),
            },
            Inputs = new[]
            {
                new ProvenanceInput("client.dll", pe, "2026-06-10T12:00:00Z"),
                new ProvenanceInput("libserver.so", elf, "2026-06-10T12:00:00Z"),
            },
        };

        ProvenanceEmitter.Emit(ctx, outPath);
    }

    // ---- VPK fixture builder (matches the synthetic layout) --------------

    public sealed record FileSpec(string Path, string Ext, string Name, byte[] Body);

    private const uint Signature = 0x55AA1234u;
    private const ushort Embedded = 0x7FFF;
    private const ushort Terminator = 0xFFFF;

    private static byte[] BuildEmbeddedVpk(int version, IReadOnlyList<FileSpec> files)
    {
        var tree = new MemoryStream();
        var dataSection = new MemoryStream();

        var offsets = new Dictionary<FileSpec, uint>();
        foreach (var f in files)
        {
            offsets[f] = (uint)dataSection.Length;
            dataSection.Write(f.Body);
        }

        foreach (var byExt in files.GroupBy(f => f.Ext))
        {
            WriteCString(tree, byExt.Key);
            foreach (var byPath in byExt.GroupBy(f => f.Path))
            {
                WriteCString(tree, byPath.Key);
                foreach (var f in byPath)
                {
                    WriteCString(tree, f.Name);
                    WriteU32(tree, Crc32(f.Body));
                    WriteU16(tree, 0);
                    WriteU16(tree, Embedded);
                    WriteU32(tree, offsets[f]);
                    WriteU32(tree, (uint)f.Body.Length);
                    WriteU16(tree, Terminator);
                }
                tree.WriteByte(0);
            }
            tree.WriteByte(0);
        }
        tree.WriteByte(0);

        byte[] treeBytes = tree.ToArray();
        byte[] dataBytes = dataSection.ToArray();

        var ms = new MemoryStream();
        WriteU32(ms, Signature);
        WriteU32(ms, (uint)version);
        WriteU32(ms, (uint)treeBytes.Length);
        if (version == 2)
        {
            WriteU32(ms, (uint)dataBytes.Length);
            WriteU32(ms, 0);
            WriteU32(ms, 0);
            WriteU32(ms, 0);
        }
        ms.Write(treeBytes);
        ms.Write(dataBytes);
        return ms.ToArray();
    }

    private static uint Crc32(byte[] data)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
            {
                crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
            }
        }
        return crc ^ 0xFFFFFFFFu;
    }

    private static void WriteCString(Stream s, string value)
    {
        s.Write(Encoding.UTF8.GetBytes(value));
        s.WriteByte(0);
    }

    private static void WriteU32(Stream s, uint v)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, v);
        s.Write(b);
    }

    private static void WriteU16(Stream s, ushort v)
    {
        Span<byte> b = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(b, v);
        s.Write(b);
    }
}

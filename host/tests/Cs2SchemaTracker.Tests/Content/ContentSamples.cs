// Representative content-family sample bodies for the content-store tests.
//
// The KV1/KV3 strings are lifted verbatim from the per-emitter test fixtures so every one of the
// 7 content emitters parses them WITHOUT throwing (they must, for the byte-identical trim proof to
// run all 7). The .gameevents body is the REAL shipped CS2 fixture (content depot 2347770) that
// VpkGameEventsRoundTripTest also uses.

using System.Text;

namespace Cs2SchemaTracker.Tests.Content;

internal static class ContentSamples
{
    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    private static byte[] RealGameEvents()
    {
        // Committed under Vpk/fixtures/resource/, copied next to the test binary by the csproj.
        var path = Path.Combine(AppContext.BaseDirectory, "Vpk", "fixtures", "resource", "game.gameevents");
        return File.ReadAllBytes(path);
    }

    private const string Kv3Header =
        "<!-- kv3 encoding:text:version{e21c7f3c-8a33-41c5-9977-a76d3a32aa0d} format:generic:version{7412167c-06e9-4698-aff2-e63eb59037e7} -->\n";

    private const string ItemsGame =
        """
        "items_game"
        {
            "items"
            {
                "7"       { "name" "ak47" "item_name" "#weapon_ak47" "item_description" "#desc_ak47" "prefab" "primary weapon" "item_type_name" "#type_rifle" "item_slot" "rifle" }
                "default" { "name" "default" "item_class" "default_class" }
                "1"       { "name" "deagle" "item_name" "#weapon_deagle" }
            }
            "prefabs"
            {
                "weapon_base"  { "item_class" "weapon" "item_slot" "rifle" "item_name" "#base" "item_type_name" "#t" }
            }
        }
        """;

    private const string GameModes =
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
                        "competitive" { "maxplayers" "10" "game_type" "0" "game_mode" "1" }
                        "casual" { "maxplayers" "20" }
                    }
                }
            }
            "mapgroups"
            {
                "mg_active" { "displayname" "Active" "maps" { "de_anubis" "" "de_ancient" "" } }
            }
        }
        """;

    private const string English =
        """
        "lang"
        {
            "Language" "English"
            "Tokens"
            {
                "weapon_ak47" "AK-47"
                "SFUI_only_english" "EnglishOnly"
            }
        }
        """;

    private const string German =
        """
        "lang"
        {
            "Language" "German"
            "Tokens"
            {
                "weapon_ak47" "AK-47 (de)"
                "weapon_german_only" "NurDeutsch"
            }
        }
        """;

    private static readonly string SurfaceGame = Kv3Header +
        """
        {
            SurfacePropertiesList =
            [
                { surfacePropertyName = "metal"  gamematerial = "M" jumpfactor = 1.0 climbable = false },
                { surfacePropertyName = "default" gamematerial = "C" bulletPenetrationDamageModifier = 0.5 },
            ]
        }
        """;

    private const string PropData =
        """
        "PropData.txt"
        {
            "Door.Standard" { "dmg.bullets" "1.0" "health" "1000" }
            "Cloth.Small" { "base" "Cloth.Base" "health" "30" }
        }
        """;

    private static readonly string CollisionKv3 = Kv3Header +
        """
        {
            collision_properties =
            [
                { name = "window" description = "win" collision_group = "ConditionallySolid" interact_as = [ "window" ] interact_with = [] interact_exclude = [] },
                { name = "default" description = "def" collision_group = "default" interact_as = [] interact_with = [] interact_exclude = [] },
            ]
        }
        """;

    private const string Dust2 =
        """
        "de_dust2"
        {
            "material" "overviews/de_dust2_v2"
            "pos_x" "-2476"
            "pos_y" "3239"
            "scale" "4.4"
        }
        """;

    /// <summary>
    /// The standard 7-family entry set (matching the spec: one .gameevents, items_game.txt, two
    /// csgo_&lt;lang&gt;.txt, a surfaceproperties file, propdata + collision, one overview) PLUS a
    /// couple of unrelated entries the emitters must ignore. gameevents + items are EXTERNAL (chunk 0)
    /// to exercise the external→trim remap; the rest are EMBEDDED in _dir.vpk.
    /// </summary>
    public static IReadOnlyList<ContentVpkFixture.Entry> StandardEntries() =>
    [
        new("resource", "gameevents", "game", RealGameEvents(), ArchiveIndex: 0),
        new("scripts/items", "txt", "items_game", Utf8(ItemsGame), ArchiveIndex: 0),
        new(" ", "txt", "gamemodes", Utf8(GameModes), ContentVpkFixture.Embedded),
        new("resource", "txt", "csgo_english", Utf8(English), ContentVpkFixture.Embedded),
        new("resource", "txt", "csgo_german", Utf8(German), ContentVpkFixture.Embedded),
        new("scripts", "txt", "surfaceproperties_game", Utf8(SurfaceGame), ContentVpkFixture.Embedded),
        new("scripts", "txt", "propdata", Utf8(PropData), ContentVpkFixture.Embedded),
        new("scripts", "txt", "collision_properties", Utf8(CollisionKv3), ContentVpkFixture.Embedded),
        new("resource/overviews", "txt", "de_dust2", Utf8(Dust2), ContentVpkFixture.Embedded),
        // Unrelated entries the required-set selector must EXCLUDE from the trim.
        new("scripts", "txt", "unrelated", Utf8("\"x\" { }"), ContentVpkFixture.Embedded),
        new("materials", "vmat", "noise", Utf8("not content we read"), ArchiveIndex: 0),
    ];
}

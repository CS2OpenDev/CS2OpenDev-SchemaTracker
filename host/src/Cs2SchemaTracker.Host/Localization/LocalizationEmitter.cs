// Localization token-table extraction (localization.json).
//
// Pipeline (clones ItemDefinitionsEmitter, but over a FAMILY of files): open a content-depot
// pak01_dir.vpk (VpkArchive) -> enumerate every "resource/csgo_<lang>.txt" entry -> for each,
// extract bytes (CRC-verified by the VPK layer) -> normalize encoding to UTF-8 (UCS-2/UTF-16LE
// source is normalized deterministically) -> parse the KV1 "lang" table -> harvest its Tokens block
// -> COMBINE all languages token-keyed -> serialize the canonical proto3 JSON -> atomic .tmp+rename.
//
// === DESIGN CHOICE: COMBINED token-keyed (lead-recommended; see localization.proto header) ===
// One artifact keyed by TOKEN, each token carrying a per-language value list. english is the
// canonical token universe; a token present only in a non-english file is still admitted (no
// source data dropped). english_value is duplicated onto each token for english-only consumers.
//
// === csgo_<lang>.txt KV1 shape ===
//   "lang" { "Language" "english"  "Tokens" { "#CSGO_..." "Display string" ... } }
// The wrapper block is located STRUCTURALLY (first/only top-level block). The language code
// is taken from the FILE SUFFIX (resource/csgo_<lang>.txt), NOT the in-file "Language" key
// (which is a display name like "English", not the file suffix the rest of CS2 keys on).
//
// === v1 mapping decisions (mirror item_definitions) ===
//   - Structured messages, NOT a generic KV1 mirror. Only the Tokens table is surfaced.
//   - RAW values: display strings VERBATIM (after KV1 unescaping); no %s1 interpretation.
//   - Within one language file a duplicate token is last-occurrence-wins (KV1 override).
//   - english is REQUIRED to be present (it defines the token universe). Its absence is fail-loud —
//     a localization artifact with no english table would be useless.
//
// Invariants:
//   Determinism: languages Ordinal-sorted; tokens Ordinal-sorted by token; each token's values
//     Ordinal-sorted by language. UTF-16 normalized to UTF-8 the same way every run. Canonical JSON,
//     LF, UTF-8 no BOM.
//   Fail-loud: missing vpk / zero csgo_<lang>.txt entries / missing english / CRC mismatch /
//     malformed KV1 / a lang file with no top-level block / a "Tokens" section that is a scalar —
//     all throw BEFORE any output bytes. No catch-and-continue.
//   All-or-nothing: build the full message in memory, then write to a sibling .tmp and atomically
//     rename.

using System.Text;
using System.Text.RegularExpressions;

using Cs2SchemaTracker.Host.GameEvents;   // Kv1 / Kv1Node (shared minimal KV1 parser)
using Cs2SchemaTracker.Host.Serialization;
using Cs2SchemaTracker.Host.Vpk;
using Cs2SchemaTracker.Schemas;

using Google.Protobuf;

namespace Cs2SchemaTracker.Host.Localization;

/// <summary>
/// Extracts the <c>resource/csgo_&lt;lang&gt;.txt</c> KV1 token tables from a content-depot VPK
/// and writes the canonical combined token-keyed localization.json. Host-only identity fields are
/// stamped by the constructor; display strings come verbatim from the parsed KV1.
/// </summary>
internal sealed class LocalizationEmitter
{
    /// <summary>The canonical language whose token set defines the artifact's token universe.</summary>
    public const string CanonicalLanguage = "english";

    // resource/csgo_<lang>.txt — <lang> is a lowercase ascii run (english, schinese, latam, ...).
    private static readonly Regex LangFileRegex =
        new("^resource/csgo_(?<lang>[a-z]+)\\.txt$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly string _schemaVersion;
    private readonly string _buildId;
    private readonly string _platform;

    public LocalizationEmitter(string schemaVersion, string buildId, string platform)
    {
        ArgumentException.ThrowIfNullOrEmpty(schemaVersion);
        ArgumentException.ThrowIfNullOrEmpty(buildId);
        ArgumentException.ThrowIfNullOrEmpty(platform);
        _schemaVersion = schemaVersion;
        _buildId = buildId;
        _platform = platform;
    }

    /// <summary>
    /// Open the <paramref name="vpkDirPath"/> (a <c>*_dir.vpk</c>), extract and parse every
    /// <c>resource/csgo_&lt;lang&gt;.txt</c>, and write localization.json to
    /// <paramref name="outputPath"/>. Fail-loud: throws before any output bytes if the VPK is
    /// missing, no lang file is present, english is absent, or any KV1 is malformed. Returns the
    /// token count written (see <see cref="Emit"/>).
    /// </summary>
    public int EmitFromVpk(string vpkDirPath, string outputPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(vpkDirPath);
        ArgumentException.ThrowIfNullOrEmpty(outputPath);

        var archive = VpkArchive.Open(vpkDirPath);
        return Emit(archive, outputPath);
    }

    /// <summary>
    /// True iff <paramref name="archive"/> ships at least one <c>resource/csgo_&lt;lang&gt;.txt</c>
    /// token table in its directory tree. The loose localization tables are GENUINELY ABSENT in the
    /// earliest CS2 builds (e.g. build 10832117, 2023-03 — the tables shipped elsewhere then), so
    /// their absence is a graceful omission, not a failure. A present-but-unreadable table (missing
    /// backing chunk, or langs present without english) is still fail-loud inside <see cref="Emit"/>.
    /// Directory-tree check only.
    /// </summary>
    public static bool HasSource(VpkArchive archive)
    {
        ArgumentNullException.ThrowIfNull(archive);
        foreach (var entry in archive.Entries)
        {
            if (LangFileRegex.IsMatch(entry.FullPath))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Map every <c>resource/csgo_&lt;lang&gt;.txt</c> in <paramref name="archive"/> into the
    /// public combined <see cref="Schemas.Localization"/> message and write the canonical
    /// localization.json. All validation + the full document build happen before any disk write.
    /// Returns the number of tokens written (the top-level entry count) so the caller can record the
    /// build-on-demand fingerprint (provenance.localization.token_count) without re-parsing the
    /// ~199 MB artifact.
    /// </summary>
    public int Emit(VpkArchive archive, string outputPath)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentException.ThrowIfNullOrEmpty(outputPath);

        // Discover the lang files. Iterate deterministically (Ordinal by language) so the
        // merge order is identical every run. Keyed by language code (file suffix).
        var langEntries = new SortedDictionary<string, VpkDirectoryEntry>(StringComparer.Ordinal);
        foreach (var entry in archive.Entries)
        {
            var m = LangFileRegex.Match(entry.FullPath);
            if (!m.Success)
            {
                continue;
            }
            // Lowercase the suffix so the key is stable regardless of manifest casing.
            string lang = m.Groups["lang"].Value.ToLowerInvariant();
            // First-wins on the (impossible-in-practice) duplicate; deterministic regardless.
            if (!langEntries.ContainsKey(lang))
            {
                langEntries[lang] = entry;
            }
        }

        if (langEntries.Count == 0)
        {
            throw new InvalidDataException(
                "LocalizationEmitter: no 'resource/csgo_<lang>.txt' entries in the VPK — refusing "
                + "to write localization.json. Was the correct content pak01_dir.vpk supplied?");
        }
        if (!langEntries.ContainsKey(CanonicalLanguage))
        {
            throw new InvalidDataException(
                $"LocalizationEmitter: the canonical '{CanonicalLanguage}' language "
                + $"(resource/csgo_{CanonicalLanguage}.txt) is absent — refusing to write a "
                + "localization.json with no english token universe.");
        }

        // token -> (language -> value). SortedDictionary keeps both axes Ordinal-sorted.
        var tokens = new SortedDictionary<string, SortedDictionary<string, string>>(StringComparer.Ordinal);

        foreach (var (lang, entry) in langEntries)
        {
            byte[] bytes = archive.ReadEntryBytes(entry); // CRC-verified; throws on mismatch.
            string text = DecodeText(bytes);
            IReadOnlyList<Kv1Node> roots = Kv1.Parse(text, entry.FullPath);
            Kv1Node wrapper = LocateWrapperBlock(roots, entry.FullPath);
            Kv1Node? tokenBlock = LocateTokensBlock(wrapper, entry.FullPath);
            if (tokenBlock is null)
            {
                // A lang file with no Tokens block contributes nothing (legitimately rare);
                // not an error — english presence + non-empty universe are the hard gates.
                continue;
            }

            foreach (var child in tokenBlock.Children!)
            {
                if (child.IsBlock)
                {
                    // A token whose value is a block is not a string binding; skip it (the
                    // Tokens table is conventionally flat token→string).
                    continue;
                }
                string value = child.Value ?? "";
                if (!tokens.TryGetValue(child.Key, out var byLang))
                {
                    byLang = new SortedDictionary<string, string>(StringComparer.Ordinal);
                    tokens[child.Key] = byLang;
                }
                byLang[lang] = value;   // last-occurrence-wins within one file (KV1 override).
            }
        }

        if (tokens.Count == 0)
        {
            throw new InvalidDataException(
                "LocalizationEmitter: parsed zero localization tokens across all language files — "
                + "refusing to write an empty localization.json.");
        }

        var document = new Schemas.Localization
        {
            SchemaVersion = _schemaVersion,
            BuildId = _buildId,
            Platform = _platform,
        };
        foreach (var lang in langEntries.Keys)
        {
            document.Languages.Add(lang);   // already Ordinal-sorted.
        }
        foreach (var (token, byLang) in tokens)
        {
            var ls = new LocalizedString
            {
                Token = token,
                EnglishValue = byLang.TryGetValue(CanonicalLanguage, out var en) ? en : "",
            };
            foreach (var (lang, value) in byLang)
            {
                ls.Values.Add(new LanguageValue { Language = lang, Value = value });
            }
            document.Tokens.Add(ls);
        }

        string json = SerializeCanonical(document);
        AtomicWrite(outputPath, json);
        return document.Tokens.Count;
    }

    // ---- Encoding normalization ------------------------------------------------------

    // CS2 csgo_<lang>.txt files are commonly UTF-16LE with a BOM; some are UTF-8. Decode
    // deterministically: honor a UTF-16 (LE/BE) or UTF-8 BOM; otherwise default to UTF-8. The decoded
    // string is what the KV1 parser sees, so the same bytes always decode the same way. A
    // heuristic-free, BOM-first rule keeps it deterministic and faithful.
    internal static string DecodeText(byte[] bytes)
    {
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return new UnicodeEncoding(bigEndian: false, byteOrderMark: false)
                .GetString(bytes, 2, bytes.Length - 2);
        }
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return new UnicodeEncoding(bigEndian: true, byteOrderMark: false)
                .GetString(bytes, 2, bytes.Length - 2);
        }
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }
        return Encoding.UTF8.GetString(bytes);
    }

    // ---- Wrapper + Tokens location ---------------------------------------------------

    private static Kv1Node LocateWrapperBlock(IReadOnlyList<Kv1Node> roots, string sourceName)
    {
        foreach (var root in roots)
        {
            if (root.IsBlock)
            {
                return root;
            }
        }
        throw new InvalidDataException(
            $"LocalizationEmitter: '{sourceName}' has no top-level block — expected the "
            + "lang wrapper.");
    }

    // Locate the "Tokens" child (case-insensitive on the key, since lang files have used both
    // "Tokens" and "tokens"). Returns null when absent; fail-loud if present but scalar.
    private static Kv1Node? LocateTokensBlock(Kv1Node wrapper, string sourceName)
    {
        Kv1Node? found = null;
        foreach (var child in wrapper.Children!)
        {
            if (!string.Equals(child.Key, "Tokens", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (!child.IsBlock)
            {
                throw new InvalidDataException(
                    $"LocalizationEmitter: 'Tokens' section in '{sourceName}' is a scalar, "
                    + "expected a block.");
            }
            found = child;   // last-occurrence-wins on the (rare) repeated Tokens block.
        }
        return found;
    }

    // ---- Atomic write + canonical proto3 JSON ----------------------------------------

    private static void AtomicWrite(string outputPath, string json)
    {
        var fullPath = Path.GetFullPath(outputPath);
        var parent = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }
        var tmpPath = fullPath + ".tmp";
        try
        {
            File.WriteAllBytes(tmpPath, Encoding.UTF8.GetBytes(json));
            File.Move(tmpPath, fullPath, overwrite: true);
        }
        catch
        {
            if (File.Exists(tmpPath))
            {
                try
                { File.Delete(tmpPath); }
                catch { /* best effort */ }
            }
            throw;
        }
    }

    private static readonly JsonFormatter Formatter = new(
        JsonFormatter.Settings.Default
            .WithFormatDefaultValues(true)
            .WithIndentation("  "));

    internal static string SerializeCanonical(IMessage message)
    {
        string formatted = Formatter.Format(message);
        return CanonicalJson.SerializeRawJson(formatted);
    }
}

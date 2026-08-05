// Registry audit aggregation (registry_audit.json).
//
// UNIVERSE-OF-RECORD: registry_audit.json's symbol universe IS the walker's observed-symbol
// universe (WalkerOutput.registry_universe, the FULL set of named registry symbols the walk
// observed in the loaded binaries — emitted or not). The audit has TWO production paths:
//
//   PATH A — extract-time (full audit): EmitFromUniverse(...).
//     The walker_output is in hand. For every ObservedRegistrySymbol in the universe:
//       - if that (symbol, module) lands in a produced artifact -> Extracted{artifact_filename};
//       - else -> Omitted{rationale}, derived from the symbol's category via CategoryRationale
//         (never empty — see that method).
//     This is the ONLY path that can MINT Omitted rows (minting requires a fresh walk's universe).
//     Cross-check: every symbol PRESENT in an artifact MUST appear in the universe — an extracted
//     symbol the walk never observed is a walker/host inconsistency (a bug) and fails loud.
//
//   PATH B — `audit --artifacts <dir>` regeneration (no walker_output): EmitForDirectory(...).
//     The universe is NOT available at audit-only time, so PATH B CANNOT INVENT Omitted rows. It
//     re-derives the Extracted rows from the artifacts present in the dir (the same logic PATH A's
//     extracted set uses) and CARRIES FORWARD the Omitted rows verbatim from the prior committed
//     registry_audit.json in that dir (the authoritative omitted set). Merge {fresh Extracted} ∪
//     {carried Omitted}, re-sort, write. Consequences:
//       - Idempotent: running PATH B twice over the same dir is byte-identical.
//       - Reproducible: running PATH B after PATH A reproduces PATH A's file byte-for-byte.
//       - If NO prior registry_audit.json exists, PATH B emits Extracted-only and logs a one-line
//         note that minting Omitted rows requires an extract-time (PATH A) run.
//
// Every RegistryEntry MUST have its disposition oneof set. Guaranteed by construction on both
// paths; asserted before write so a future change that introduces an unset disposition fails loud
// rather than committing a silent drop.
//
// Invariants:
//   Determinism: entries sorted by (symbol, module, disposition-detail) Ordinal — a stable TOTAL
//     order; canonical proto3 JSON via AtomicWrite; re-running is byte-identical.
//   Fail-loud: an empty symbol name, an identity disagreement across artifacts, an
//     extracted-but-unobserved symbol (PATH A cross-check), or an unreadable/corrupt artifact (the
//     strict JsonParser throws) throws BEFORE any output bytes are written.
//   All-or-nothing: sibling .tmp then atomic rename (via AtomicWrite).

using Cs2SchemaTracker.Host.Serialization;
using Cs2SchemaTracker.Schemas;

using Google.Protobuf;

namespace Cs2SchemaTracker.Host.RegistryAudit;

/// <summary>
/// Aggregates the produced artifacts in one (build_id, platform) directory into the canonical
/// registry_audit.json. See file header for the two production paths.
/// </summary>
public static class RegistryAuditEmitter
{
    public const string OutputFileName = "registry_audit.json";

    // Strict parse: unknown fields are rejected, so the audit reads exactly the committed shape and
    // a malformed/foreign artifact fails loud.
    private static readonly JsonParser StrictParser =
        new(JsonParser.Settings.Default.WithIgnoreUnknownFields(false));

    /// <summary>The identity triple (schema_version, build_id, platform) shared by all artifacts.</summary>
    private sealed record Identity(string SchemaVersion, string BuildId, string Platform);

    /// <summary>An Extracted observation: where a (symbol, module) landed.</summary>
    private sealed record ExtractedHit(string Symbol, string Module, string ArtifactFilename);

    // ----------------------------------------------------------------------------------------
    // PATH A — extract-time full audit. The walker universe is the universe-of-record.
    // ----------------------------------------------------------------------------------------

    /// <summary>
    /// Synthesize registry_audit.json for one (build, platform) from the walker's observed-symbol
    /// universe and the produced artifacts in <paramref name="artifactsDir"/>. Every observed
    /// symbol becomes Extracted (if it landed in an artifact) or Omitted (rationale from its
    /// category). Fail-loud if an artifact carries a symbol the universe never observed.
    /// </summary>
    public static void EmitFromUniverse(string artifactsDir, RegistryUniverse universe)
    {
        ArgumentException.ThrowIfNullOrEmpty(artifactsDir);
        ArgumentNullException.ThrowIfNull(universe);

        if (!Directory.Exists(artifactsDir))
        {
            throw new DirectoryNotFoundException(
                $"RegistryAuditEmitter: artifacts directory not found: '{artifactsDir}'.");
        }

        var hits = CollectExtractedHits(artifactsDir, out Identity? identity);

        if (identity is null)
        {
            throw new InvalidDataException(
                $"RegistryAuditEmitter: no auditable artifact found in '{artifactsDir}' — cannot "
                + "determine schema_version/build_id/platform.");
        }

        // Index the extracted hits by (symbol, module). A given (symbol, module) can in principle
        // land in more than one artifact; we keep the Ordinal-least filename for determinism.
        var hitByKey = new Dictionary<(string, string), string>();
        foreach (var hit in hits)
        {
            var key = (hit.Symbol, hit.Module);
            if (!hitByKey.TryGetValue(key, out var existing)
                || string.CompareOrdinal(hit.ArtifactFilename, existing) < 0)
            {
                hitByKey[key] = hit.ArtifactFilename;
            }
        }

        // Build the set of (symbol, module) the universe observed, so the cross-check below can
        // detect an artifact symbol the walk never saw.
        var observedKeys = new HashSet<(string, string)>();
        foreach (var obs in universe.Symbols)
        {
            if (string.IsNullOrEmpty(obs.Symbol))
            {
                throw new InvalidDataException(
                    "RegistryAuditEmitter: the walker universe carries a symbol with an empty name — "
                    + " enumerates only named registry symbols.");
            }
            observedKeys.Add((obs.Symbol, obs.Module ?? ""));
        }

        // Cross-check: every PRODUCED symbol MUST be in the universe. An extracted symbol the walk
        // never observed means walker and host disagree on what the binary contains — that is a bug,
        // not a silent merge.
        foreach (var key in hitByKey.Keys)
        {
            if (!observedKeys.Contains(key))
            {
                throw new InvalidDataException(
                    $"RegistryAuditEmitter: artifact symbol (symbol='{key.Item1}', module='{key.Item2}') "
                    + "was produced but is ABSENT from the walker's observed universe — an extracted "
                    + "symbol that was never observed is a walker/host inconsistency. "
                    + "Refusing to write registry_audit.json.");
            }
        }

        // One RegistryEntry per observed symbol: Extracted if it landed in an artifact, else
        // Omitted with a non-empty, category-derived rationale.
        var entries = new List<RegistryEntry>(universe.Symbols.Count);
        foreach (var obs in universe.Symbols)
        {
            var key = (obs.Symbol, obs.Module ?? "");
            if (hitByKey.TryGetValue(key, out var filename))
            {
                entries.Add(MakeExtracted(obs.Symbol, obs.Module ?? "", filename));
            }
            else
            {
                entries.Add(MakeOmitted(obs.Symbol, obs.Module ?? "", CategoryRationale(obs.Category)));
            }
        }

        WriteAudit(artifactsDir, identity, entries);
    }

    // ----------------------------------------------------------------------------------------
    // PATH B — audit-only regeneration. No universe; carry forward prior Omitted rows.
    // ----------------------------------------------------------------------------------------

    /// <summary>
    /// Regenerate registry_audit.json for one (build, platform) directory WITHOUT a walker
    /// universe (the `audit --artifacts` path). Re-derives the Extracted rows from the produced
    /// artifacts and CARRIES FORWARD the Omitted rows from the prior committed registry_audit.json
    /// (the universe-of-record at audit-only time). Deterministic + idempotent. Fail-loud.
    /// </summary>
    public static void EmitForDirectory(string artifactsDir)
    {
        ArgumentException.ThrowIfNullOrEmpty(artifactsDir);

        if (!Directory.Exists(artifactsDir))
        {
            throw new DirectoryNotFoundException(
                $"RegistryAuditEmitter: artifacts directory not found: '{artifactsDir}'.");
        }

        var hits = CollectExtractedHits(artifactsDir, out Identity? identity);

        if (identity is null)
        {
            throw new InvalidDataException(
                $"RegistryAuditEmitter: no auditable artifact found in '{artifactsDir}' — cannot "
                + "determine schema_version/build_id/platform or enumerate any registry symbol.");
        }

        var entries = new List<RegistryEntry>();

        // Freshly-derived Extracted rows (one per produced symbol). A (symbol, module) that lands
        // in multiple artifacts keeps each hit — the (symbol, module, filename) sort is total.
        foreach (var hit in hits)
        {
            entries.Add(MakeExtracted(hit.Symbol, hit.Module, hit.ArtifactFilename));
        }

        // Carry forward the prior Omitted rows. PATH B cannot INVENT omitted rows (it has no
        // universe), so the prior committed registry_audit.json IS the authoritative omitted set.
        // Re-parsing + re-sorting them through the same comparer makes a post-extract regenerate
        // reproduce PATH A's file byte-for-byte, and a second PATH B run idempotent.
        var priorPath = Path.Combine(artifactsDir, OutputFileName);
        int carried = 0;
        if (File.Exists(priorPath))
        {
            string priorJson = File.ReadAllText(priorPath);
            var prior = StrictParser.Parse<Schemas.RegistryAudit>(priorJson); // throws on corrupt
            foreach (var e in prior.Entries)
            {
                if (e.DispositionCase == RegistryEntry.DispositionOneofCase.Omitted)
                {
                    entries.Add(MakeOmitted(e.Symbol, e.Module ?? "", e.Omitted.Rationale));
                    carried++;
                }
            }
        }
        else
        {
            // No prior audit -> no universe-of-record for the omitted set. Emit Extracted-only
            // and tell the operator that minting omitted rows requires an extract-time (PATH A) run.
            Console.Error.WriteLine(
                "audit: no prior registry_audit.json in the directory — emitting Extracted rows only. "
                + "Omitted rows can only be minted at extract time (from the walker's observed universe); "
                + "run a full extract to populate them.");
        }

        WriteAudit(artifactsDir, identity, entries);
        Console.Error.WriteLine(
            $"audit: derived {hits.Count} extracted row(s); carried forward {carried} omitted row(s).");
    }

    // ----------------------------------------------------------------------------------------
    // Shared core: scan produced artifacts -> Extracted hits + unified identity.
    // ----------------------------------------------------------------------------------------

    /// <summary>
    /// Read every produced artifact present in <paramref name="artifactsDir"/>, strict-parse each
    /// through its generated proto3 message, and return one ExtractedHit per named registry symbol
    /// (the symbol, its module, and the artifact filename that received it). Unifies the identity
    /// triple across artifacts (out param). Absent artifacts are skipped (not a failure). Fail-loud
    /// on a corrupt artifact, an empty symbol, or an identity disagreement.
    /// </summary>
    private static List<ExtractedHit> CollectExtractedHits(string artifactsDir, out Identity? identity)
    {
        var hits = new List<ExtractedHit>();
        Identity? id = null;

        // entity_schema.json: each SchemaClass.name and each SchemaEnum.name is a registry symbol;
        // the module is the class/enum's own module field.
        ForEachArtifact<Schemas.EntitySchema>(artifactsDir, "entity_schema.json", doc =>
        {
            UnifyIdentity(ref id, doc.SchemaVersion, doc.BuildId, doc.Platform, "entity_schema.json");
            foreach (var cls in doc.Classes)
            {
                hits.Add(MakeHit(cls.Name, cls.Module, "entity_schema.json"));
            }
            foreach (var en in doc.Enums)
            {
                hits.Add(MakeHit(en.Name, en.Module, "entity_schema.json"));
            }
        });

        // convars.json: each ConVar.name. ConVar has no module field — module = "".
        ForEachArtifact<Schemas.ConVars>(artifactsDir, "convars.json", doc =>
        {
            UnifyIdentity(ref id, doc.SchemaVersion, doc.BuildId, doc.Platform, "convars.json");
            foreach (var cv in doc.Convars)
            {
                hits.Add(MakeHit(cv.Name, "", "convars.json"));
            }
        });

        // commands.json: each Command.name. Command has no module field — module = "".
        ForEachArtifact<Schemas.Commands>(artifactsDir, "commands.json", doc =>
        {
            UnifyIdentity(ref id, doc.SchemaVersion, doc.BuildId, doc.Platform, "commands.json");
            foreach (var cmd in doc.Commands_)
            {
                hits.Add(MakeHit(cmd.Name, "", "commands.json"));
            }
        });

        // network_messages.json: each NetworkMessageEntry. The registry symbol is the bound
        // proto_message_type when the binary resolved one; when the binary registered an ID with no
        // resolvable type (proto_message_type == ""), the channel-scoped numeric ID is the symbol so
        // the registry audit still accounts for it. The channel is the originating "module"
        // discriminator here.
        ForEachArtifact<Schemas.NetworkMessages>(artifactsDir, "network_messages.json", doc =>
        {
            UnifyIdentity(ref id, doc.SchemaVersion, doc.BuildId, doc.Platform, "network_messages.json");
            foreach (var channel in doc.Channels)
            {
                foreach (var msg in channel.Messages)
                {
                    string symbol = msg.ProtoMessageType.Length != 0
                        ? msg.ProtoMessageType
                        : $"{channel.Name}#{msg.Id}";
                    hits.Add(MakeHit(symbol, channel.Name, "network_messages.json"));
                }
            }
        });

        // engine_constants.json: each EngineConstant.name. module = the constant's source.
        ForEachArtifact<Schemas.EngineConstants>(artifactsDir, "engine_constants.json", doc =>
        {
            UnifyIdentity(ref id, doc.SchemaVersion, doc.BuildId, doc.Platform, "engine_constants.json");
            foreach (var c in doc.Constants)
            {
                // module = the ORIGINATING module parsed from the source ("schema_enum:<module>/
                // <EnumName>" -> "<module>"), NOT the raw source — MUST match the walker's
                // registry_universe_walk.ModuleFromConstantSource so the (symbol, module) cross-check
                // against the universe agrees.
                hits.Add(MakeHit(c.Name, ModuleFromConstantSource(c.Source), "engine_constants.json"));
            }
        });

        // string_pools.json: the POOL NAMES are the registry symbols (interned strings inside a
        // pool are DATA, not registry symbols). module = "".
        ForEachArtifact<Schemas.StringPools>(artifactsDir, "string_pools.json", doc =>
        {
            UnifyIdentity(ref id, doc.SchemaVersion, doc.BuildId, doc.Platform, "string_pools.json");
            foreach (var pool in doc.Pools)
            {
                hits.Add(MakeHit(pool.Name, "", "string_pools.json"));
            }
        });

        identity = id;
        return hits;
    }

    /// <summary>
    /// Recover the originating module from an engine-constant <c>source</c> string. The walker's
    /// engine_constants_walk emits source as <c>"schema_enum:&lt;module&gt;/&lt;EnumName&gt;"</c>;
    /// this returns <c>&lt;module&gt;</c>, or "" when the prefix is absent (future source kinds).
    /// MUST stay byte-for-byte equivalent to <c>registry_universe_walk.ModuleFromConstantSource</c>
    /// (C++) so the registry-audit (symbol, module) cross-check against the walker universe agrees.
    /// </summary>
    private static string ModuleFromConstantSource(string? source)
    {
        const string prefix = "schema_enum:";
        if (string.IsNullOrEmpty(source) || !source.StartsWith(prefix, StringComparison.Ordinal))
        {
            return "";
        }
        int start = prefix.Length;
        int slash = source.IndexOf('/', start);
        return slash < 0 ? source[start..] : source[start..slash];
    }

    // ----------------------------------------------------------------------------------------
    // Category -> rationale map (PATH A). Total: NEVER returns empty.
    // ----------------------------------------------------------------------------------------

    /// <summary>
    /// Map an ObservedRegistrySymbol.category to a non-empty omission rationale. Small, documented,
    /// and TOTAL — every input (including unknown/empty categories) maps to a non-empty string, so
    /// an Omitted row never violates the non-empty-rationale invariant.
    /// </summary>
    private static string CategoryRationale(string? category) => (category ?? "") switch
    {
        // Structurally deferred categories — observed in the registry but intentionally not
        // extracted into any artifact for the documented reason.
        "string_pool" =>
            "category deferred: no reflection-reachable interned-string pool without re-declaring "
            + "layout",
        "network_message" =>
            "category deferred: HL2SDK lacks generated network_connection descriptors",

        // Categories that SHOULD have been extracted but were not found in any artifact. An observed
        // symbol of one of these absent from every produced artifact is a genuine completeness gap
        // that must be surfaced, not hidden.
        "schema_class" or "schema_enum" or "convar" or "command" or "engine_constant" =>
            $"observed in {category} registry but absent from all produced artifacts",

        // Unknown / empty category — outside the current extraction scope but still enumerated.
        _ =>
            "observed in registry; outside current extraction scope",
    };

    // ----------------------------------------------------------------------------------------
    // Entry construction + write.
    // ----------------------------------------------------------------------------------------

    /// <summary>Construct an ExtractedHit, failing loud on an empty symbol.</summary>
    private static ExtractedHit MakeHit(string symbol, string module, string artifactFilename)
    {
        if (string.IsNullOrEmpty(symbol))
        {
            throw new InvalidDataException(
                $"RegistryAuditEmitter: an entry in '{artifactFilename}' has an empty symbol name — "
                + " enumerates only named registry symbols, never blanks.");
        }
        return new ExtractedHit(symbol, module ?? "", artifactFilename);
    }

    private static RegistryEntry MakeExtracted(string symbol, string module, string artifactFilename)
        => new()
        {
            Symbol = symbol,
            Module = module ?? "",
            Extracted = new Extracted { ArtifactFilename = artifactFilename },
        };

    private static RegistryEntry MakeOmitted(string symbol, string module, string rationale)
    {
        if (string.IsNullOrEmpty(rationale))
        {
            // Defensive: CategoryRationale is total and prior Omitted rows are non-empty by
            // construction, so this is unreachable — but an empty rationale is exactly the
            // violation we must never write.
            throw new InvalidDataException(
                $"RegistryAuditEmitter: Omitted row for symbol '{symbol}' has an empty rationale — "
                + " requires a non-empty written rationale.");
        }
        return new RegistryEntry
        {
            Symbol = symbol,
            Module = module ?? "",
            Omitted = new Omitted { Rationale = rationale },
        };
    }

    /// <summary>
    /// Sort, assert every disposition is set, and atomically write registry_audit.json. Shared by
    /// both paths so the on-disk shape + ordering is identical regardless of producer.
    /// </summary>
    private static void WriteAudit(string artifactsDir, Identity identity, List<RegistryEntry> entries)
    {
        // Stable total order: (symbol, module, disposition-detail) Ordinal. The disposition detail
        // is the Extracted filename or the Omitted rationale; using whichever is set keeps the order
        // total across mixed Extracted/Omitted rows.
        entries.Sort(static (a, b) =>
        {
            int c = string.CompareOrdinal(a.Symbol, b.Symbol);
            if (c != 0)
                return c;
            c = string.CompareOrdinal(a.Module, b.Module);
            if (c != 0)
                return c;
            return string.CompareOrdinal(DispositionDetail(a), DispositionDetail(b));
        });

        var document = new Schemas.RegistryAudit
        {
            SchemaVersion = identity.SchemaVersion,
            BuildId = identity.BuildId,
            Platform = identity.Platform,
        };
        document.Entries.AddRange(entries);

        // Every entry MUST have its disposition oneof set. Guaranteed by construction (every entry
        // is built via MakeExtracted/MakeOmitted) — assert it so a future change that introduces an
        // unset disposition fails loud rather than committing a silent drop.
        foreach (var e in document.Entries)
        {
            if (e.DispositionCase == RegistryEntry.DispositionOneofCase.None)
            {
                throw new InvalidDataException(
                    $"RegistryAuditEmitter: RegistryEntry for symbol '{e.Symbol}' has no disposition "
                    + "set — this is the 'neither extracted nor omitted' silent-drop state and "
                    + "must never be written.");
            }
        }

        var outputPath = Path.Combine(artifactsDir, OutputFileName);
        AtomicWrite.WriteCanonical(document, outputPath);
    }

    /// <summary>The disposition-specific sort key: Extracted filename or Omitted rationale.</summary>
    private static string DispositionDetail(RegistryEntry e) => e.DispositionCase switch
    {
        RegistryEntry.DispositionOneofCase.Extracted => e.Extracted.ArtifactFilename,
        RegistryEntry.DispositionOneofCase.Omitted => e.Omitted.Rationale,
        _ => "",
    };

    /// <summary>
    /// If <paramref name="fileName"/> is present in <paramref name="dir"/>, strict-parse it as
    /// <typeparamref name="T"/> and invoke <paramref name="consume"/>. Absence is NOT a failure
    /// (skip silently — a (build, platform) set may legitimately omit some via omissions.json). A
    /// corrupt/foreign file lets the strict parser throw (no catch-and-continue). registry_audit.json
    /// itself is NOT in the scanned set, so a prior audit never feeds back into the Extracted
    /// derivation.
    /// </summary>
    private static void ForEachArtifact<T>(string dir, string fileName, Action<T> consume)
        where T : IMessage<T>, new()
    {
        var path = Path.Combine(dir, fileName);
        if (!File.Exists(path))
        {
            return;
        }

        string json = File.ReadAllText(path);
        T doc = StrictParser.Parse<T>(json);   // throws on malformed/foreign JSON
        consume(doc);
    }

    /// <summary>
    /// Unify the identity triple across artifacts. The first artifact establishes it; every later
    /// artifact must match byte-for-byte or we fail loud — a (build, platform) directory whose
    /// artifacts disagree on which build/platform they describe is corrupt.
    /// </summary>
    private static void UnifyIdentity(
        ref Identity? identity, string schemaVersion, string buildId, string platform, string fileName)
    {
        var observed = new Identity(schemaVersion, buildId, platform);
        if (identity is null)
        {
            identity = observed;
            return;
        }

        if (identity != observed)
        {
            throw new InvalidDataException(
                $"RegistryAuditEmitter: artifact '{fileName}' declares identity "
                + $"(schema_version='{observed.SchemaVersion}', build_id='{observed.BuildId}', "
                + $"platform='{observed.Platform}') which disagrees with an earlier artifact's "
                + $"(schema_version='{identity.SchemaVersion}', build_id='{identity.BuildId}', "
                + $"platform='{identity.Platform}') — the (build, platform) set is inconsistent.");
        }
    }
}

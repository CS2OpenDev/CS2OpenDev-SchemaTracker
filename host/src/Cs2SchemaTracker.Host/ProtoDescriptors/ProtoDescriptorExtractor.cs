// Descriptor orchestrator: scan binaries, dedupe, canonicalize, emit `.proto` files + descriptor
// set.
//
// Invariants:
//   Determinism: deterministic dedupe (sorted by FDP.Name), LF endings, UTF-8 no BOM, sorted
//     FileDescriptorSet, byte-identical re-emission. The canonical copy chosen for a byte-differing
//     same-name collision is a PURE function of the input SET (Ordinal-min source path), independent
//     of scan or enumeration order.
//   Fail-loud where it matters: a descriptor whose bytes cannot be PARSED is input corruption and
//     bubbles up. Benign same-name-but-byte-differing dependency-vendoring collisions (many CS2 DLLs
//     statically link their own protobuf runtime and embed their own copy of well-known descriptors
//     such as google/protobuf/descriptor.proto, serialized by a different protoc / with/without
//     source_code_info) are NOT corruption — they are resolved deterministically and surfaced
//     LOUDLY on stderr, never silently masked.
//   All-or-nothing: writes go to a temp directory; the final atomic move replaces any pre-existing
//     protos/ directory. A throw mid-write deletes the temp dir so no partial output survives.
//
// Collision policy (per descriptor NAME):
//   1. Byte-identical same-name copies  -> deduped to one (no warning).
//   2. Byte-DIFFERING same-name copies are first compared MODULO source_code_info
//      (FileDescriptorProto field 9: debug/comment/line-table metadata that does not
//      change the schema's semantics). If they are equal after stripping it, they are
//      treated as the same logical descriptor; the canonical copy is the Ordinal-min
//      source's copy and NO warning is emitted (the divergence was purely cosmetic).
//   3. Copies that still differ after stripping source_code_info are a REAL byte
//      divergence. We do NOT abort: we pick the copy from the Ordinal-FIRST source
//      binary path (a pure function of the input set) and emit a WARNING to stderr
//      naming the descriptor, the chosen source, and ALL conflicting sources.

using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace Cs2SchemaTracker.Host.ProtoDescriptors;

public sealed class ProtoDescriptorExtractor
{
    /// <summary>
    /// Synthetic source label for a supplemental (SDK-sourced) descriptor candidate — the
    /// <see cref="DescriptorCandidate.Binary"/> value stands in for the real binary path a
    /// binary-derived FDP carries. Only ever the SOLE candidate for its name (supplemental files
    /// are merged only when no binary provided them), so it never competes in a collision.
    /// </summary>
    private const string SupplementalSourceLabel = "<hl2sdk:wire_descriptors.pb>";

    /// <summary>
    /// The one file pruned to its referenced closure before emission. Named rather than inferred:
    /// this is the only descriptor in CS2's set whose unreferenced bulk is large enough to matter
    /// (~74k lines of generated C# downstream, about a third of a demo-scope codegen), and pruning
    /// every file by default would be a much larger behavioural claim than the evidence supports.
    /// </summary>
    private const string GcClosureTargetFile = "cstrike15_gcmessages.proto";

    /// <summary>
    /// Leading comment prepended to the .proto text of an SDK-sourced wire descriptor, so the
    /// artifact is self-documenting about its non-binary provenance. Valid proto (a comment before
    /// the syntax line); ends in a blank line so the syntax line stays visually separated.
    /// </summary>
    private const string SupplementalHeaderComment =
        "// SDK-SOURCED wire descriptor. CS2 does not embed this message family's FileDescriptorProto\n"
        + "// in any shipped binary, so — unlike every other file here — it was NOT recovered from the\n"
        + "// binaries but compiled from the pinned hl2sdk submodule (scripts/gen-wire-descriptors.sh)\n"
        + "// and merged so network_messages.json / demo_messages.json wire-ID -> type joins resolve.\n"
        + "// Reference-quality: no comments / source_code_info, same as every reconstructed descriptor.\n"
        + "\n";

    /// <summary>
    /// Scan every binary in <paramref name="binaryPaths"/>, dedupe FDPs by name,
    /// then write:
    ///   <paramref name="outputDir"/>/protos/&lt;file&gt;.proto    — one per FDP (canonical text)
    ///   <paramref name="outputDir"/>/protos.descriptorset       — FileDescriptorSet binary
    /// </summary>
    /// <param name="requireNonEmpty">
    /// When true (the real extract set), recovering ZERO FileDescriptorProtos from the input set is a
    /// STRUCTURAL failure and throws before any bytes hit disk: a real CS2 binary set always embeds
    /// serialized FileDescriptorProtos (netmessages_*.proto, networkbasetypes.proto, etc.), so zero
    /// descriptors means the scan or the inputs are wrong — never a legitimately empty protos/. When
    /// false (the undocumented --binaries dev hook), zero descriptors is permitted so the hook can be
    /// pointed at arbitrary directories for diagnostics.
    /// </param>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static",
        Justification = "Instance method by design; future extractor options (filters, allowlist) attach to the instance.")]
    public void Extract(
        IReadOnlyList<string> binaryPaths,
        string outputDir,
        bool requireNonEmpty = false,
        TextWriter? warningSink = null,
        IReadOnlyList<FileDescriptorProto>? supplementalDescriptors = null)
    {
        ArgumentNullException.ThrowIfNull(binaryPaths);
        ArgumentException.ThrowIfNullOrEmpty(outputDir);

        // Collision warnings go to stderr by default (production), but tests inject a
        // private sink so they don't race the process-global Console.Error under xUnit
        // parallelism. Behavior (warn loudly, never abort) is identical either way.
        warningSink ??= Console.Error;

        // 1. Scan each binary (preserving input order), collecting (binary, fdp) candidates. A
        //    descriptor whose embedded bytes cannot be PARSED is input corruption and bubbles up
        //    from DescriptorScanner — that is handled there.
        var candidates = new List<DescriptorCandidate>();
        foreach (var path in binaryPaths)
        {
            // Input failure (missing/unreadable file) bubbles up.
            var fdps = DescriptorScanner.Scan(path);
            foreach (var fdp in fdps)
            {
                candidates.Add(new DescriptorCandidate(path, fdp, fdp.ToByteArray()));
            }
        }

        // 1a. Structural guard is on the BINARY scan specifically (before any supplemental merge). A
        //     real CS2 binary set ALWAYS embeds serialized FileDescriptorProtos; recovering none from
        //     the inputs the full extract set was handed means the scan or the inputs are broken.
        //     Fail loud BEFORE writing — never silently emit an empty protos/ directory. (Checked on
        //     the binary candidates, not the post-merge total, so a supplemental set can never mask a
        //     dead binary scan.)
        if (requireNonEmpty && candidates.Count == 0)
        {
            throw new InvalidDataException(
                $"ProtoDescriptorExtractor: scanned {binaryPaths.Count} binary file(s) but recovered "
                + "ZERO FileDescriptorProtos. A real CS2 binary set embeds serialized descriptors "
                + "(demo, cstrike15_gcmessages, ...); zero is a structural failure of the scan or "
                + "the inputs. Refusing to emit an empty protos/ directory.");
        }

        // 1b. Merge the SDK-sourced supplemental descriptors (the engine wire-message protos —
        //     netmessages, usermessages, gameevents, te, ... — which CS2 does NOT embed in any
        //     shipped binary; see data/wire_descriptors.pb / scripts/gen-wire-descriptors.sh). A
        //     supplemental file is added ONLY when the binaries did not already provide it, so the
        //     binary-derived copy is always canonical and this can never override a real embedded
        //     descriptor. These carry no source_code_info (reference-quality), same as every
        //     reconstructed descriptor. The names are recorded so the emit step can stamp their
        //     provenance and the log can name them.
        var binaryNames = new HashSet<string>(candidates.Select(c => c.Fdp.Name), StringComparer.Ordinal);
        var supplementalNames = new HashSet<string>(StringComparer.Ordinal);
        if (supplementalDescriptors is not null)
        {
            foreach (var fdp in supplementalDescriptors)
            {
                if (string.IsNullOrEmpty(fdp.Name) || binaryNames.Contains(fdp.Name)
                    || !supplementalNames.Add(fdp.Name))
                {
                    // Skip: empty name, already binary-derived (binary wins), or a duplicate
                    // supplemental. No warning — these are all benign-by-design.
                    continue;
                }
                candidates.Add(new DescriptorCandidate(SupplementalSourceLabel, fdp, fdp.ToByteArray()));
            }
            if (supplementalNames.Count > 0)
            {
                warningSink.WriteLine(
                    "ProtoDescriptorExtractor: merged " + supplementalNames.Count
                    + " SDK-sourced wire descriptor(s) the binaries do not embed (hl2sdk-derived, "
                    + "reference-quality): " + string.Join(", ", supplementalNames.OrderBy(n => n, StringComparer.Ordinal)) + ".");
            }
        }

        // 2. Group by FDP.Name and resolve each group to a single canonical copy. The choice is a
        //    PURE function of the input SET (Ordinal-min source path), so it is independent of
        //    scan/enumeration order. Genuine byte divergence (after stripping source_code_info) is
        //    surfaced LOUDLY but does not abort (visible, never silent).
        var ordered = candidates
            .GroupBy(c => c.Fdp.Name, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)   // sort canonical output by Name
            .Select(g => ResolveCollision(g.Key, g.ToList(), warningSink))
            .ToList();

        // 3. Prune cstrike15_gcmessages down to the closure the rest of the set actually references,
        //    and drop the imports that closure no longer needs (steammessages, engine_gcmessages,
        //    gcsdk_gcmessages — Steam matchmaking/inventory/item-schema traffic, none of it on the
        //    demo wire path). Derived on every run, never a committed stub: a hand-written stub is
        //    silently restored by the next refresh and the build still succeeds. See
        //    DependencyClosurePruner and CS2OpenDev-SchemaTracker#3.
        //
        //    Runs AFTER collision resolution so it prunes the canonical copy, and BEFORE emission so
        //    both protos/<file>.proto and protos.descriptorset carry the pruned form. A no-op
        //    (target absent, unreferenced, or already minimal) returns null and changes nothing.
        var prunable = ordered.Select(c => c.Fdp).ToList();
        var pruneOutcome = DependencyClosurePruner.Prune(prunable, GcClosureTargetFile);
        if (pruneOutcome is not null)
        {
            for (var i = 0; i < ordered.Count; i++)
            {
                if (!ReferenceEquals(ordered[i].Fdp, prunable[i]))
                {
                    ordered[i] = ordered[i] with { Fdp = prunable[i] };
                }
            }

            warningSink.WriteLine(
                "ProtoDescriptorExtractor: pruned " + pruneOutcome.File + " to the "
                + pruneOutcome.KeptTypes + " top-level type(s) the rest of the set references, "
                + "removing " + pruneOutcome.RemovedTypes
                + (pruneOutcome.DroppedDependencies.Count > 0
                    ? "; dropped now-unused import(s): "
                      + string.Join(", ", pruneOutcome.DroppedDependencies)
                    : "")
                + ".");
        }

        // 4. Build to a temp directory first, then atomically rename.
        var tempRoot = Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(outputDir)) ?? outputDir,
            ".protos." + Guid.NewGuid().ToString("N") + ".tmp");

        try
        {
            Directory.CreateDirectory(tempRoot);

            // 4a. Per-FDP .proto text files.
            foreach (var entry in ordered)
            {
                var fdpName = entry.Fdp.Name;
                var relPath = SanitizeRelativePath(fdpName);
                var outPath = Path.Combine(tempRoot, relPath);
                var outParent = Path.GetDirectoryName(outPath);
                if (!string.IsNullOrEmpty(outParent))
                {
                    Directory.CreateDirectory(outParent);
                }

                var text = ProtoTextEmitter.Emit(entry.Fdp);
                // Stamp SDK-sourced wire descriptors so a reader of protos/<file>.proto knows this
                // one was NOT recovered from the shipped binaries (it isn't embedded in any) but
                // compiled from the pinned hl2sdk SDK sources — see the header for why. Binary-derived
                // files are emitted verbatim (no header) exactly as before.
                if (supplementalNames.Contains(fdpName))
                {
                    text = SupplementalHeaderComment + text;
                }
                // UTF-8 no BOM, LF endings already in text per emitter contract.
                File.WriteAllBytes(outPath, System.Text.Encoding.UTF8.GetBytes(text));
            }

            // 4b. Combined descriptor set, sorted by FDP.Name.
            var set = new FileDescriptorSet();
            foreach (var entry in ordered)
            {
                set.File.Add(entry.Fdp);
            }
            // FileDescriptorSet has no map fields, so default proto serialization
            // is deterministic for a fixed library version.
            var dsetBytes = set.ToByteArray();

            // 4c. Move temp directory to final location (atomic-ish): delete pre-existing target
            //     then move. Between delete and move the filesystem is briefly missing the protos/
            //     directory, but within a single process a crash there leaves no half-written
            //     output — the contract.
            var protosDir = Path.Combine(outputDir, "protos");
            var descriptorSetPath = Path.Combine(outputDir, "protos.descriptorset");
            Directory.CreateDirectory(outputDir);

            if (Directory.Exists(protosDir))
            {
                Directory.Delete(protosDir, recursive: true);
            }
            Directory.Move(tempRoot, protosDir);
            // tempRoot is now consumed by the move; null it so the cleanup branch
            // doesn't try to delete the renamed directory.
            tempRoot = null!;

            // Descriptor set written AFTER the protos/ rename succeeds. If the
            // process dies between these two writes the protos/ tree exists but
            // protos.descriptorset doesn't — still detectable downstream, and
            // re-running the extractor will regenerate both atomically.
            File.WriteAllBytes(descriptorSetPath, dsetBytes);
        }
        catch
        {
            // Roll back the temp directory if anything went wrong. Don't swallow the exception.
            if (!string.IsNullOrEmpty(tempRoot) && Directory.Exists(tempRoot))
            {
                try
                { Directory.Delete(tempRoot, recursive: true); }
                catch { /* best effort; we're already failing loud */ }
            }
            throw;
        }
    }

    /// <summary>
    /// Convert an FDP `name` (e.g. "google/protobuf/descriptor.proto") into a
    /// safe relative path beneath the protos/ output directory. Rejects names
    /// that try to escape the directory or contain control characters.
    /// </summary>
    private static string SanitizeRelativePath(string fdpName)
    {
        if (string.IsNullOrEmpty(fdpName))
        {
            throw new InvalidDataException("ProtoDescriptorExtractor: FDP with empty Name encountered.");
        }
        if (fdpName.Contains('\\', StringComparison.Ordinal)
            || fdpName.StartsWith('/')
            || fdpName.Contains("..", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"ProtoDescriptorExtractor: FDP name '{fdpName}' contains path-escape characters; refusing to write.");
        }
        // FDP names use forward slashes; on Windows we replace with the OS sep
        // for filesystem operations.
        return fdpName.Replace('/', Path.DirectorySeparatorChar);
    }

    /// <summary>
    /// A single scanned FileDescriptorProto together with the binary it was found in
    /// and its serialized form (computed once).
    /// </summary>
    private sealed record DescriptorCandidate(string Binary, FileDescriptorProto Fdp, byte[] Bytes);

    /// <summary>
    /// Resolve all candidates sharing one descriptor <paramref name="name"/> to a single
    /// canonical copy. Deterministic and pure in the input SET:
    ///   - All byte-identical  -> dedupe to one, no warning.
    ///   - Differing bytes but identical MODULO source_code_info -> cosmetic divergence;
    ///     pick the Ordinal-min source's copy, no warning.
    ///   - Genuinely differing (even after stripping source_code_info) -> pick the
    ///     Ordinal-min source's copy and WARN to stderr naming every conflicting source.
    /// The winner is always the candidate from the Ordinal-FIRST source binary path, so the choice
    /// does not depend on scan/enumeration order.
    /// </summary>
    private static DescriptorCandidate ResolveCollision(
        string name, List<DescriptorCandidate> group, TextWriter warningSink)
    {
        // Pick the canonical winner deterministically: Ordinal-min by source binary path,
        // then (defensive tie-break, e.g. two copies in the SAME binary) Ordinal-min by
        // serialized bytes. This is a pure function of the input set.
        var winner = group
            .OrderBy(c => c.Binary, StringComparer.Ordinal)
            .ThenBy(c => Convert.ToHexString(c.Bytes), StringComparer.Ordinal)
            .First();

        // Fast path: everything is byte-identical -> silent dedupe.
        var allIdentical = group.All(c => c.Bytes.AsSpan().SequenceEqual(winner.Bytes));
        if (allIdentical)
        {
            return winner;
        }

        // Compare modulo source_code_info (FDP field 9: debug/comment/line-table metadata
        // that does not affect schema semantics). If every copy is equal once that field is
        // stripped, the divergence is purely cosmetic — resolve silently to the winner.
        var winnerStripped = StripSourceCodeInfo(winner.Fdp);
        var differingSources = group
            .Where(c => !ReferenceEquals(c, winner)
                        && !StripSourceCodeInfo(c.Fdp).AsSpan().SequenceEqual(winnerStripped))
            .Select(c => c.Binary)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(b => b, StringComparer.Ordinal)
            .ToList();

        if (differingSources.Count == 0)
        {
            // Only source_code_info differed: same logical schema. No warning.
            return winner;
        }

        // Genuine byte divergence. Do NOT abort (these are overwhelmingly benign dependency-vendoring
        // differences across statically-linked protobuf runtimes), but make it LOUD so a real schema
        // conflict is never invisible.
        warningSink.WriteLine(
            $"WARNING ProtoDescriptorExtractor: descriptor '{name}' has byte-differing copies "
            + "(differ even after stripping source_code_info). Deterministically keeping the copy "
            + $"from the Ordinal-first source '{winner.Binary}'. Conflicting source(s): "
            + string.Join(", ", differingSources) + ".");

        return winner;
    }

    /// <summary>
    /// Serialize <paramref name="fdp"/> after clearing its source_code_info (FDP field 9).
    /// Used only for collision comparison; the original FDP (with its source_code_info, if any)
    /// is what gets emitted. Deterministic: a deep clone is mutated so the caller's instance is
    /// untouched, and serialization of an FDP is stable for a fixed library version.
    /// </summary>
    private static byte[] StripSourceCodeInfo(FileDescriptorProto fdp)
    {
        var clone = fdp.Clone();
        clone.SourceCodeInfo = null;
        return clone.ToByteArray();
    }
}

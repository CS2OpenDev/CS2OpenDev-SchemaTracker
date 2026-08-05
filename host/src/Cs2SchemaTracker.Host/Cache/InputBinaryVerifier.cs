// (redesign) — provenance-driven input-binary verification.
//
// The CONTENT-ADDRESSED binary cache + `fetch-cached-binaries` command were
// removed (hosting/redistributing Valve's binaries is a legal + storage liability
// the project avoids — independence). Steam is the durable store; a committed
// provenance.json already pins everything needed to re-acquire deterministically
// (steam.depots[].manifest_id) and to VERIFY (inputs[].{path,sha256}). This type is
// the shared verification chokepoint behind the two surviving verification points:
//
//   A1  `acquire --from-provenance <p>`  — after re-acquiring the exact pinned
//       inputs, hash every acquired file and compare to the provenance sha256.
//   B   pre-walker at-use check          — immediately before launching the walker,
//       when a committed provenance.json exists for this (build, platform), hash
//       each RESOLVED input the walker will read and compare to the provenance,
//       so a binary modified/corrupted between acquisition and use is caught at the
// point of use, BEFORE any walk.
//
// Both reuse ProvenanceReader (inputs[].{path,sha256}) + Sha256Hex. Verification is
// deterministic and bounded: the provenance input list is finite, each file is
// streamed once (Sha256Hex.OfFile), and results are reported in a stable
// (path-ordinal) order.

namespace Cs2SchemaTracker.Host.Cache;

/// <summary>One per-file verification outcome (mismatch or missing).</summary>
internal sealed record InputVerificationFailure(string RelativePath, string Expected, string Actual)
{
    /// <summary>Sentinel <see cref="Actual"/> value used when the file is absent on disk.</summary>
    public const string MissingActual = "<missing>";

    public bool IsMissing => string.Equals(Actual, MissingActual, StringComparison.Ordinal);
}

/// <summary>Aggregate result of verifying a binaries dir against a provenance.json.</summary>
internal sealed record InputVerificationResult(
    int Verified,
    IReadOnlyList<InputVerificationFailure> Failures)
{
    public bool Ok => Failures.Count == 0;
}

internal static class InputBinaryVerifier
{
    /// <summary>
    /// Verify every binary listed in <paramref name="provenancePath"/> against the
    /// files actually present under <paramref name="binariesDir"/>: each input's
    /// on-disk SHA-256 must equal the recorded <c>inputs[].sha256</c>. A missing file
    /// OR a hash mismatch is a failure (collected, not thrown — the caller decides the
    /// exit code and prints the per-file report). Iterates in path-ordinal order so
    /// the report is deterministic. Throws (fail-loud) only if the provenance
    /// itself is missing / unparseable, or an input path escapes the binaries dir.
    /// </summary>
    public static InputVerificationResult Verify(string provenancePath, string binariesDir)
    {
        ArgumentException.ThrowIfNullOrEmpty(provenancePath);
        ArgumentException.ThrowIfNullOrEmpty(binariesDir);

        // A provenance with ZERO inputs has nothing to verify — a documented SKIP (the
        // caller treats Ok+0-verified as "no at-use check applicable"), NOT a fail.
        var refs = ProvenanceReader.ReadInputsAllowEmpty(provenancePath);

        int verified = 0;
        var failures = new List<InputVerificationFailure>();
        foreach (var r in refs.OrderBy(r => r.Path, StringComparer.Ordinal))
        {
            // ResolveLocal fail-loud rejects any path escaping the binaries dir.
            var local = ProvenanceReader.ResolveLocal(binariesDir, r.Path);
            if (!File.Exists(local))
            {
                failures.Add(new InputVerificationFailure(r.Path, r.Sha256, InputVerificationFailure.MissingActual));
                continue;
            }

            var actual = Sha256Hex.OfFile(local);
            if (!string.Equals(actual, r.Sha256, StringComparison.Ordinal))
            {
                failures.Add(new InputVerificationFailure(r.Path, r.Sha256, actual));
                continue;
            }
            verified++;
        }

        return new InputVerificationResult(verified, failures);
    }

    /// <summary>
    /// Write a per-file failure report to <paramref name="writer"/> (path, expected,
    /// actual). Stable order; never prints secrets. No-op when there are no failures.
    /// </summary>
    public static void WriteFailureReport(TextWriter writer, string context, InputVerificationResult result)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (result.Ok)
            return;

        writer.WriteLine($"{context}: {result.Failures.Count} input binary verification failure(s):");
        foreach (var f in result.Failures)
        {
            if (f.IsMissing)
            {
                writer.WriteLine($"  MISSING  {f.RelativePath}  expected={f.Expected}");
            }
            else
            {
                writer.WriteLine($"  MISMATCH {f.RelativePath}  expected={f.Expected} actual={f.Actual}");
            }
        }
    }
}

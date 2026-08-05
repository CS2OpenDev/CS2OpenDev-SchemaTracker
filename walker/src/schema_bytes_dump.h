// DIAGNOSTIC — raw byte-dump of the live CSchemaSystemTypeScope class-binding
// CONTAINER and a handful of pointed-to records, to STDERR.
//
// PURPOSE: the pre-2024 "V1" runtime layout (builds 12182426 2023-09-13 ..
// 13240071 2024-01-22) is a POOL/CONTAINER relayout — the variant-0
// (V0/10832117) container geometry (real_base = &m_ClassBindings-8, bucket array
// @ +160, lock 8, stride 24, pool-blob head @ +48, block_size 24) locates NEITHER
// pool nor buckets on V1 (an UNFILTERED bucket harvest recovers 0). Deriving
// V1's real container/record offsets requires first DUMPing the raw memory around a
// V1 scope's m_ClassBindings to locate the bucket-array pointer,
// the live count, the pool-blob head, and the first record pointers by pattern.
//
// This subcommand does NOT walk schema and writes NO output file. Everything goes
// to stderr. It is reachable ONLY via the `dump-schema-bytes` subcommand; the
// normal `walk` path never calls it, so all committed-era artifact output stays
// byte-identical.
//
// It boots the schema system the SAME way the normal walk does — LoadInProcess-
// Environment -> BootEngineForConVars (best-effort) -> RetrySchemaRegistration-
// IfEmpty ("SchemaSystem_001" post-boot handshake). On a V1 build that handshake
// SUCCEEDS (14 modules register) and THEN fails the runtime-layout variant gate
// (kUnknown); we deliberately IGNORE that verdict here — the schema records are
// populated regardless, and dumping their bytes is the whole point.
//
// Every memory read routes through the SEH-guarded readers in tshash_compat.h
// (SafeReadPtr2023 / SafeReadBytes2023 / SafeReadCString2023 / LooksLikePointer2023),
// so a wrong guess at where a pool/bucket/record lives degrades to a "(unreadable)"
// note and NEVER crashes the process.
#pragma once

#include <filesystem>
#include <string>

namespace cs2_schema_walker {

// Load the modules under `binaries_dir`, boot + register the schema system, and
// dump the raw class-binding container + sampled record bytes for every live type
// scope to stderr. Returns true if the load + enumeration succeeded, false + *err
// on a hard load/interface failure. Writes NO output file — diagnostic only.
bool DumpSchemaBytes(const std::filesystem::path& binaries_dir, std::string* err);

}  // namespace cs2_schema_walker

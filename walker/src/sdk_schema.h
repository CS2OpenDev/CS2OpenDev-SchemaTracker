// Single inclusion point for the HL2SDK schema-system headers.
//
// Repo invariant: use the pinned HL2SDK headers DIRECTLY for every Source 2
// struct layout and NEVER re-declare a layout. This header is the ONE place the
// heavy HL2SDK include chain is pulled in, so the rest of the walker includes a
// single well-defined surface and the build's HL2SDK coupling is auditable from
// one file.
//
// Why isolate it:
//   - The HL2SDK headers drag in a large tier0/tier1 transitive chain and a pile
//     of compiler/platform #defines (COMPILER_MSVC, _WIN32, _LINUX, ...). Keeping
//     that in one TU-facing header keeps the blast radius small.
//   - The schema walk reads Valve's POD-ish data structs (SchemaClassInfoData_t,
//     SchemaClassFieldData_t, CSchemaType + subclasses, SchemaEnumInfoData_t,
//     SchemaMetadataEntryData_t) by MEMBER ACCESS against these exact headers.
//     The compiler computes every offset from the pinned layout — that is the
//     whole point of depending on HL2SDK rather than hand-maintaining offsets.
//
// What we DO call at runtime:
//   - The pure-virtual ISchemaSystem / ISchemaSystemTypeScope interface methods.
//     Those dispatch through the live object's vtable into the loaded DLL's own
//     implementation, so there is no link-time dependency on Valve code.
//   - The header-inline, fully-defined CUtlTSHash<>::Count/GetElements/Element
//     templates (class/enum binding enumeration). These are instantiated into
//     our own object code from the header; no Valve symbol is referenced.
//
// What we MUST NOT call: any tier0/tier1 method marked DLL_CLASS_IMPORT /
// DLL_IMPORT (e.g. CUtlString::Get, CBufferString::ToGrowable). Those resolve to
// symbols exported by tier0.dll / tier1 and would create a link dependency we
// don't want. We read the raw char* / fixed-buffer members instead.
#pragma once

#include "schemasystem/schemasystem.h"
#include "schemasystem/schematypes.h"

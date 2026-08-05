// Partial Source 2 engine bootstrap for ConVar / ConCommand extraction.
//
// WHY THIS EXISTS
// ---------------
// CS2 ConVars/ConCommands are NOT static lists the walker can read from a DLL
// image. They are *registered at runtime*: each game module's IAppSystem::Init()
// runs the module's ConVar_Register() which flushes that module's convars into
// the live ICvar registry (VEngineCvar007). So to extract them, the walker must
// actually bring a slice of the engine up far enough that the game-config
// modules (client / server / host / matchmaking) reach Init().
//
// This is the proven offline-dumper technique (ValveResourceFormat/DumpSource2
// uses the same hl2sdk cs2 branch we vendor). We adopt the *technique* only —
// every call here is re-derived from the pinned hl2sdk headers
// (appframework/IAppSystem.h, appframework/IAppSystemGroup.h,
// interfaces/interfaces.h, icvar.h). No DumpSource2 source is copied.
//
// A NAIVE stub factory (return null for everything but ICvar/SchemaSystem)
// access-violates: engine2 and server Connect/Init dereference interfaces the
// stub returned null for. The fix here is an INCREMENTAL real-interface factory:
// every interface a later module asks for is answered with the REAL pointer of
// an earlier module that we already Connected, plus a minimal real ICvar,
// CSchemaSystem and IApplication. We minimize nulls.
//
// Fail-loud: if, after Init()'ing the game-config modules, the ICvar registry is
// still empty, BootEngineForConVars returns false with a precise error. The
// caller must NOT emit an empty convars.json silently.
#pragma once

#include <string>

namespace cs2_schema_walker {

class InProcessEnvironment;

// Perform the partial engine boot against the modules already loaded into `env`.
//
// Preconditions: `env` has every allow-listed module resident, env.schema_system()
// and env.cvar() are non-null (the loader obtained SchemaSystem_001 and
// VEngineCvar007 before calling this).
//
// On success the live ICvar registry (env.cvar()) is populated with the convars
// and concommands registered by the game-config modules' Init(), ready for the
// index-based enumeration in cvar_walk.cpp.
//
// Returns false + sets *err on any structural failure. Leaves rich tracing
// behind CS2_WALKER_TRACE=1, prefixed "[walker-boot]".
bool BootEngineForConVars(InProcessEnvironment& env, std::string* err);

}  // namespace cs2_schema_walker

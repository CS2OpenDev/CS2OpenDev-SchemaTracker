// posix_crash_guard.h — POSIX signal crash guard that mirrors the Windows SEH
// __try/__except leaves in engine_boot.cpp / loader.cpp.
//
// WHY THIS EXISTS
// ---------------
// The walker calls into loaded CS2 module code by raw vtable / function pointer:
// the data-subsystem IAppSystem::Connect+Init (engine_boot.cpp) and each module's
// InstallSchemaBindings export (loader.cpp). Those calls can fault (wrong ABI, a
// module that bails, a lazy-schema Init whose comparator dereferences uninitialised
// engine state — e.g. libparticles' Init -> V_qsort_s on current-era Linux). On
// WINDOWS a hard access violation is caught by an SEH __except leaf so the walker
// SKIPS the faulting call and the boot CONTINUES. The Itanium ABI (Linux) has NO
// SEH, so the same fault SIGSEGVs and kills the process before the schema/netmsg
// walk ever runs.
//
// This header provides the POSIX equivalent of that SEH leaf: a sigaction handler +
// sigsetjmp/siglongjmp that turns a fault inside a guarded, POD-only callback into a
// "faulted" return, then restores the previous handlers AND signal mask. The two
// call sites use it to reach the SAME kFaulted / *faulted path Windows already has.
//
// WINDOWS IS UNTOUCHED
// -------------------
// Everything below is under `#if !defined(_WIN32)`. On Windows this header expands
// to nothing, so including it changes no Windows translation unit and the Windows
// binary is byte-identical. The SEH leaves remain the sole Windows path.
//
// CONSTRAINTS (identical to the SEH leaves)
// -----------------------------------------
//   * POD-ONLY guarded frame: a siglongjmp may abandon the callback's frame, and
//     jumping past C++ destructors is UB. The callback must touch only POD locals +
//     raw vtable/fnptr calls (no std::string, no RAII). Both call sites pass a
//     trivial POD context struct + a leaf callback that only does raw calls.
//   * NON-REENTRANT PER THREAD: a given thread must not nest guarded calls (the
//     boot callers are sequential — load-time InstallSchemaBindings, then boot-time
//     Connect+Init — never nested). ACROSS threads it is safe: the jmp_buf/active
//     flag are thread_local, so the KV3-defaults recovery can run guarded accessor
//     calls on detached watchdog worker threads concurrently with the main thread.
//   * The SIGSEGV/SIGBUS/SIGABRT/SIGFPE handler is installed ONCE, persistently
//     (EnsureGuardHandlerInstalled) — never restored per call — so no process-wide
//     sigaction restore can race a still-running detached worker out of coverage.
//   * A fault OUTSIDE any active guard (GuardActive()==0 on the faulting thread)
//     restores the default disposition and re-raises, so a genuine walker bug still
//     crashes normally (never swallowed), even with the handler left installed.
#pragma once

#if !defined(_WIN32)

#include <setjmp.h>  // sigjmp_buf, sigsetjmp, siglongjmp (POSIX)
#include <signal.h>  // sigaction, sigemptyset, SIGSEGV/SIGBUS/SIGABRT/SIGFPE
#include <cstdio>    // std::fprintf(stderr, ...) — SIGABRT loudness in RunGuarded
#include <cstring>   // std::memset

namespace cs2_schema_walker {
namespace posix_crash_guard {

// Per-THREAD guard state (thread_local function-local statics). The KV3-defaults recovery runs its
// guarded accessor call on a WORKER THREAD under a watchdog (schema_walk.cpp), and an abandoned/hung
// worker may still be inside a guarded frame — so each thread must own its jmp_buf + active flag. A
// synchronous fault (SIGSEGV/SIGBUS/SIGFPE) is delivered to the FAULTING thread, so GuardSignalHandler
// reads that thread's own thread_local state and longjmps within its own stack. (Single-guarded-call-
// per-thread at a time is still the contract; thread_local just keeps concurrent threads independent.)
inline sigjmp_buf& GuardJmpBuf() {
  static thread_local sigjmp_buf buf;
  return buf;
}
inline volatile sig_atomic_t& GuardActive() {
  static thread_local volatile sig_atomic_t active = 0;
  return active;
}

// The leaf memory-probe guard (SafeProbeCopy, below) owns its OWN jmp_buf/flag but
// shares the SAME four signals as RunGuarded. Forward-declared here so the single
// unified handler can service both without either install racing the other's
// disposition (see WHY ONE HANDLER, below). Definitions are further down.
inline sigjmp_buf& ProbeJmpBuf();
inline volatile sig_atomic_t& ProbeActive();

// UNIFIED async-signal handler for BOTH guards (RunGuarded + SafeProbeCopy).
//
// WHY ONE HANDLER (this was a real, intermittent Linux crash — `extract exit 139`
// on 2023-era builds):
//   RunGuarded and SafeProbeCopy both guard SIGSEGV/SIGBUS, but each used to install
//   its OWN handler (GuardSignalHandler vs ProbeSignalHandler) keyed on its OWN active
//   flag, and sigaction() is LAST-WRITER-WINS with no reinstall. DetectSchemaVariant
//   interleaves the two WITHIN one loop: each scope runs the modern probe under
//   RunGuarded (GuardActive), then a 2023 pool-blob read that calls SafeProbeCopy —
//   whose EnsureProbeHandlerInstalled then OWNED SIGSEGV. The NEXT scope's RunGuarded
//   modern-probe fault was then delivered to the PROBE handler, which saw ProbeActive
//   == 0 (we were in a guard, not a probe), fell through to SIG_DFL + re-raise, and
//   SIGSEGV-killed the process at the fault PC. Intermittent because whether a scope's
//   2023 read reaches SafeProbeCopy depends on the (ASLR-varying) garbage it walks.
//
//   One handler that checks BOTH flags removes the conflict: whichever guard installed
//   last, the same handler services either an active RunGuarded OR an active
//   SafeProbeCopy, so a fault is never handed to the "other" guard's disabled handler.
//
// FLAG ORDER — GuardActive (thread_local) BEFORE ProbeActive (global): a synchronous
// fault is delivered to the FAULTING thread. If that thread is inside a RunGuarded its
// OWN thread_local GuardActive is set, so we must jump through its thread_local
// GuardJmpBuf (never the global ProbeJmpBuf, which another thread may own). The two
// never nest on one thread (a RunGuarded POD callback never calls SafeProbeCopy), so at
// most one flag is set per thread and the order only guards the cross-thread case.
inline void GuardSignalHandler(int sig) {
  if (GuardActive()) {
    GuardActive() = 0;
    siglongjmp(GuardJmpBuf(), sig);  // -> nonzero return from RunGuarded's sigsetjmp
  }
  if (ProbeActive()) {
    ProbeActive() = 0;
    siglongjmp(ProbeJmpBuf(), sig);  // -> nonzero return from SafeProbeCopy's sigsetjmp
  }
  // Fault outside ANY active guard on this thread: restore the default disposition and
  // re-raise so a genuine walker bug still crashes normally (never silently swallowed).
  ::signal(sig, SIG_DFL);
  ::raise(sig);
}

// Install the crash-guard handler for SIGSEGV/SIGBUS/SIGABRT/SIGFPE exactly once
// (thread-safe static init). PERSISTENT install — unlike the old per-call
// install/restore, we never hand the disposition back to SIG_DFL between calls.
//
// WHY PERSISTENT (this was a real Linux crash):
//   The KV3-defaults recovery (schema_walk.cpp) runs each guarded accessor on a
//   WORKER THREAD under a watchdog; a hung worker is DETACHED and keeps running.
//   sigaction() is PROCESS-WIDE, but the old RunGuarded saved/restored handlers
//   per call. A detached worker whose RunGuarded had captured old_actions =
//   SIG_DFL (it was the first guarded call, so nothing was installed yet) would,
//   when it finally FAULTED, longjmp out and then restore SIG_DFL — UNINSTALLING
//   the guard for every still-running detached worker. The next faulter then hit
//   SIG_DFL and SIGSEGV-killed the whole process (observed as `extract exit 139`
//   on older-era linux builds, whose layouts trip many not-yet-denylisted
//   faulting/hanging accessors; new-era linux and ALL windows builds, which use
//   the per-thread SEH leaf, were unaffected). A persistent install removes the
//   restore entirely, so the handler is ALWAYS present no matter how the detached
//   workers interleave. This mirrors EnsureProbeHandlerInstalled below, which
//   made the identical choice for the identical reason.
//
// Leaving the handler installed for the whole one-shot walk never swallows a real
// crash: GuardSignalHandler is a no-op passthrough (SIG_DFL + re-raise) whenever
// GuardActive() is 0, i.e. outside any guarded call.
inline void EnsureGuardHandlerInstalled() {
  static const bool installed = []() {
    static const int kSigs[4] = {SIGSEGV, SIGBUS, SIGABRT, SIGFPE};
    struct sigaction sa;
    std::memset(&sa, 0, sizeof(sa));  // POD init, no C++ ctor
    sa.sa_handler = &GuardSignalHandler;
    sigemptyset(&sa.sa_mask);
    // SA_ONSTACK: run the handler on the per-thread ALTERNATE signal stack
    // (EnsureAltStackInstalled) so a STACK-OVERFLOW SIGSEGV — a guarded accessor
    // that recurses without bound and exhausts the thread stack — can still be
    // delivered and caught. Without it the handler has no stack to run on and the
    // process dies uncatchably (observed: current-era linux, CBodyComponentBaseAnimGraph,
    // whose linux-.so accessor infinite-recurses where the windows-.dll one returns).
    // For threads that never registered an altstack, SA_ONSTACK is simply ignored
    // (the kernel falls back to the normal stack), so this never regresses the
    // ordinary in-bounds fault path.
    sa.sa_flags = SA_ONSTACK;  // classic handler; SA_SIGINFO not needed to jump
    for (int i = 0; i < 4; ++i) {
      sigaction(kSigs[i], &sa, nullptr);
    }
    return true;
  }();
  (void)installed;
}

// Register a per-THREAD alternate signal stack so the SA_ONSTACK handler above has a
// valid stack to run on even when THIS thread's normal stack is exhausted by a
// runaway-recursion accessor. Idempotent per thread (thread_local flag). The buffer
// is a thread_local static so it lives for the thread's lifetime; the handler only
// does a siglongjmp, so a modest fixed size well above MINSIGSTKSZ is ample (SIGSTKSZ
// is not a compile-time constant on modern glibc, so we can't size the array by it).
inline void EnsureAltStackInstalled() {
  static thread_local bool installed = false;
  if (installed) return;
  installed = true;
  static thread_local char alt_stack[64 * 1024];  // 64 KiB; handler just siglongjmps
  stack_t ss;
  std::memset(&ss, 0, sizeof(ss));  // POD init
  ss.ss_sp = alt_stack;
  ss.ss_size = sizeof(alt_stack);
  ss.ss_flags = 0;
  sigaltstack(&ss, nullptr);
}

// Run fn(ctx) guarded against SIGSEGV/SIGBUS/SIGABRT/SIGFPE. Returns true if fn ran
// to completion, false if a fault was caught (fn's POD frame is abandoned). The
// handler is installed persistently (see EnsureGuardHandlerInstalled) — safe under
// concurrent/detached guarded calls, since there is no per-call handler restore to
// race. Only the per-THREAD GuardActive()/GuardJmpBuf() gate this thread's call, so
// concurrent guarded calls on different threads stay independent. fn MUST be
// POD-frame-only (see the header note).
inline bool RunGuarded(void (*fn)(void*), void* ctx) {
  EnsureGuardHandlerInstalled();
  EnsureAltStackInstalled();  // this thread — so a stack-overflow fault is still catchable

  bool ran = false;
  // 2nd arg nonzero => the signal mask is saved here and restored on the longjmp
  // back, so the handler's masking is undone when a fault jumps out. The return
  // value doubles as which path we took: 0 on the direct (non-fault) call, and
  // the CAUGHT SIGNAL NUMBER on the longjmp-back path (GuardSignalHandler passes
  // `sig` as siglongjmp's 2nd arg; sigsetjmp hands that value back here verbatim).
  // Captured into a variable (rather than the bare `== 0` check this replaces) so
  // the fault branch below can distinguish SIGABRT from SIGSEGV/SIGBUS/SIGFPE.
  int caught_sig = sigsetjmp(GuardJmpBuf(), 1);
  if (caught_sig == 0) {
    GuardActive() = 1;
    fn(ctx);  // guarded POD-only call
    GuardActive() = 0;
    ran = true;
  } else {
    ran = false;  // returned via siglongjmp from the handler: fn faulted
    // SIGABRT LOUDNESS — WHY: Windows' SEH __except leaves (engine_boot.cpp /
    // loader.cpp) do NOT catch abort(): an engine abort() inside a guarded call
    // (e.g. a lazy-schema Init whose comparator dereferences uninitialised engine
    // state) simply TERMINATES the Windows walker process outright, which is
    // impossible to miss. This POSIX guard, by contrast, installs SIGABRT in the
    // very same kSigs[] array as SIGSEGV/SIGBUS/SIGFPE (see
    // EnsureGuardHandlerInstalled above), so on Linux the identical engine
    // abort() was silently folded into the ordinary "fn faulted, module skipped,
    // walk continues" path above — indistinguishable in the log from a mundane
    // garbage-pointer access violation. That asymmetry meant a Linux run could
    // swallow a real engine abort() with zero trace, while the same bug on
    // Windows would have been unmissable. This fprintf restores that visibility
    // — it does NOT change control flow (ran is false and the module is skipped
    // either way; this is pure logging). We are back on the normal stack
    // post-longjmp (sigsetjmp's saved context, not signal-handler context), so
    // ordinary buffered stdio is fully safe here.
    if (caught_sig == SIGABRT) {
      std::fprintf(stderr,
                   "walker: crash-guard caught SIGABRT (engine abort()) inside a guarded call - module skipped\n");
    }
  }
  return ran;
}

// ---------------------------------------------------------------------------------
// LEAF MEMORY-PROBE GUARD (distinct from RunGuarded above)
// ---------------------------------------------------------------------------------
// RunGuarded installs+restores four sigaction handlers PER CALL — right for the
// handful of boot calls, but far too heavy for the convar/command memory-mirror
// (cvar_walk.cpp RunConVarMirror / the ConCommand mirror), which PROBES thousands of
// candidate addresses across the CCvar object solving (table_base, stride) against
// convar canaries. Each of those reads goes through tshash_compat.h's SafeRead*2023,
// which on Windows is an SEH __try/__except leaf that turns a fault on an unmapped/
// garbage pointer into a `return false`. The Itanium ABI has no SEH, so the POSIX
// SafeRead* used to do a bare memcpy — a garbage candidate then SIGSEGVs and kills the
// walk (observed: 3525af99 iterates 1800 ConVarRefs, then the derive scan faults).
//
// This provides the POSIX equivalent for those LEAF reads: a persistent SIGSEGV/SIGBUS
// handler installed once, plus a cheap per-read sigsetjmp. SafeProbeCopy returns false
// (instead of crashing) when the source range is unmapped, so the adaptive, canary-
// self-validating mirror scan runs to completion on Linux exactly as it does under SEH
// on Windows. A fault while NO probe is active restores SIG_DFL and re-raises, so a
// genuine walker bug outside a guarded read still crashes normally (fail-loud).
//
// Distinct jmp_buf/flag from RunGuarded's so the two never alias. The two phases are
// sequential (boot uses RunGuarded; the mirror uses SafeProbeCopy afterwards), and the
// walk is single-threaded, so one shared jmp_buf/flag here is safe.
inline sigjmp_buf& ProbeJmpBuf() {
  static sigjmp_buf buf;
  return buf;
}
inline volatile sig_atomic_t& ProbeActive() {
  static volatile sig_atomic_t active = 0;
  return active;
}

// Install the crash-guard disposition for the probe path. This delegates to the SAME
// EnsureGuardHandlerInstalled used by RunGuarded so BOTH guards install the ONE unified
// GuardSignalHandler (which services either an active RunGuarded or an active
// SafeProbeCopy). Previously this installed a SEPARATE ProbeSignalHandler keyed only on
// ProbeActive, which — being last-writer-wins over the shared SIGSEGV/SIGBUS — silently
// disabled RunGuarded's catch and let interleaved guarded faults kill the process
// (see the WHY ONE HANDLER note on GuardSignalHandler). A single installer keeps the
// disposition consistent no matter the call order.
inline void EnsureProbeHandlerInstalled() {
  EnsureGuardHandlerInstalled();
}

// Copy [src, src+n) into dst under the probe guard. Returns true iff the whole range
// read without faulting; false (dst left partially written / indeterminate — caller
// ignores it on false) if any byte was unmapped. POD-only frame (a siglongjmp may
// abandon it): only the raw pointers/size are live across the jump.
inline bool SafeProbeCopy(void* dst, const void* src, std::size_t n) {
  EnsureProbeHandlerInstalled();
  // savemask=1: the handler masks the signal while running; restoring the saved mask
  // on the longjmp back re-enables catching the NEXT probe's fault.
  if (sigsetjmp(ProbeJmpBuf(), 1) == 0) {
    ProbeActive() = 1;
    // COMPILER BARRIERS — the copy MUST stay inside the ProbeActive()==1 window.
    // Nothing else orders the NON-volatile memcpy against the volatile flag
    // stores: GCC 13 -O2 was observed (era cs2-2025-07-31 linux walker,
    // disassembly of the shipped binary) to sink the probe load BELOW BOTH
    // stores — `ProbeActive=1; ProbeActive=0; <load>` — so a probe of an
    // unmapped candidate faulted with the flag already 0 and the handler
    // re-raised fatally (the exact "iterates ~1800 ConVarRefs then the derive
    // scan faults" symptom this guard exists to prevent). The "memory" clobber
    // forbids moving any memory access across each barrier.
    asm volatile("" ::: "memory");
    std::memcpy(dst, src, n);
    asm volatile("" ::: "memory");
    ProbeActive() = 0;
    return true;
  }
  ProbeActive() = 0;
  return false;
}

}  // namespace posix_crash_guard
}  // namespace cs2_schema_walker

#endif  // !defined(_WIN32)

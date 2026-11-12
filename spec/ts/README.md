# ax25sdl (TypeScript)

TypeScript consumers of the AX.25 SDL specification, generated from
the YAML transcriptions under `/spec-sdl/` by
`codegen/src/Packet.Sdl.CodeGen.Ts`.

## Layout

```
spec/ts/
  package.json
  tsconfig.json
  src/ax25sdl/
    types.ts     # hand-written runtime types — DO NOT regenerate
    index.ts     # GENERATED — re-exports every page + types
    *.g.ts       # GENERATED — one per *.sdl.yaml
    *.test.ts    # hand-written smoke tests
```

## Regenerate

From the repo root:

```sh
dotnet run --project codegen/src/Packet.Sdl.CodeGen -- \
  --in spec-sdl \
  --out spec/csharp \
  --tests spec/csharp/tests \
  --ts spec/ts/src/ax25sdl
```

The orchestrator emits C#, Go, and TypeScript in the same pass so all
three stay in lockstep. CI verifies the generated files are checked
in (idempotent regen + `tsc --noEmit` + `node --test`).

## Local development

```sh
cd spec/ts
npm ci                    # install devDependencies
npm run typecheck         # tsc --noEmit
npm test                  # node --test against src/**/*.test.ts
npm run build             # emit JS + .d.ts to dist/
```

Node 22+ is required (the test runner uses
`--experimental-strip-types` to load `.ts` directly).

## Saves

`StatePage.saves` lists the events a state saves (the figures' Save symbol, "Requeue"). A saved event has no transition in that state: hold it in arrival order, and each time the machine enters a state (the same state included) consume the first held event that state does not save. An explicit save beats the page's "all other" column. See `docs/golden-traces.md` ("Saves") in the repo.

## What's not here

This package is **specification data**, not a runtime. It exposes the
SDL transitions, subroutine bodies, predicates, and action verbs as
plain TS values. Building an actual AX.25 session on top requires
binding predicates and action verbs to behaviour (the TS equivalent of
`Packet.Ax25.Session.GuardEvaluator` / `ActionDispatcher` /
`DefaultSubroutineRegistry`) and wiring it to frame I/O (WebSerial,
AGWPE-over-WebSocket, etc.). That work is intentionally out of scope
for the Tier 1c codegen; the goal here is to prove the codegen IR
survives a third backend.

## Not yet transcribed

> For the **cross-runtime** view of what's implemented where (frame
> codec coverage, transports, lifecycle features, subroutine wiring,
> etc.), see
> [`docs/runtime-capability-matrix.md`](../docs/runtime-capability-matrix.md).
> The narrative below is the TypeScript-runtime-specific status of the
> SDL transcription pipeline; the matrix is the multi-runtime
> companion view.

The AX.25 v2.2 SDL chapter is figc4.1 through figc4.7. Each YAML
transcription's `coverage:` field declares whether it's `complete` or
`partial`. Current status:

| Figure | State / Page                          | Status                                                         |
| ------ | ------------------------------------- | -------------------------------------------------------------- |
| figc4.1 | Disconnected                         | ✅ complete                                                    |
| figc4.2 | AwaitingConnection (v2.0)            | ✅ complete                                                    |
| figc4.3 | AwaitingRelease                      | ✅ complete                                                    |
| figc4.4 | Connected                            | ✅ complete                                                    |
| figc4.5 | TimerRecovery                        | ❌ **not transcribed** — see "TimerRecovery" below            |
| figc4.6 | AwaitingConnection (v2.2)            | ✅ complete                                                    |
| figc4.7 | Subroutines                          | ⚠️ partial — see "Subroutine status" below                    |

### TimerRecovery state (figc4.5)

`DataLinkConnected` t38 (T1 expiry) and t39 (T3 expiry) both transition
to a `TimerRecovery` state that has no transcribed page in
`spec-sdl/v2.2-errata/data-link/`. The C# and TS runtimes register `TimerRecovery`
with an empty transition map so the state-target lint passes (see the
`StateTargetAllowList` entry in `codegen/src/Packet.Sdl.CodeGen/Program.cs`),
but any session that enters TimerRecovery currently has no exit path —
it'll silently stall until a peer-initiated DISC or a `TimerRecovery`
isn't transcribed.

Real consequence: long-running connected sessions where the peer goes
quiet for > T3 (default 30s in the runtime) will time out and re-poll
via the unmodelled state. Short-lived sessions never reach it.

### Subroutine status (figc4.7) — TS runtime

13 subroutines are declared in `spec-sdl/v2.2-errata/data-link/yaml/subroutines.sdl.yaml`.
The TS `SubroutineRegistry` (`web/ax25/src/sdl/subroutine-registry.ts`)
ships the following coverage. The C# runtime's `DefaultSubroutineRegistry`
in [`src/Packet.Ax25/Session/`](../src/Packet.Ax25/Session/) routes every
subroutine through a `Wire(...)` walker that consumes the generated
`SubroutineSpec.Paths` directly; see the
[runtime capability matrix](../docs/runtime-capability-matrix.md#figc47-subroutines)
for the side-by-side view.

| Subroutine                       | TS status                                           |
| -------------------------------- | --------------------------------------------------- |
| `Establish_Data_Link`            | ✅ inlined into the dispatcher                      |
| `Establish_Extended_Data_Link`   | ✅ inlined                                          |
| `Clear_Exception_Conditions`     | ✅ inlined                                          |
| `Check_I_Frame_Acknowledged`     | ✅ inlined                                          |
| `Select_T1_Value`                | ❌ no-op stub (dynamic T1V; figure transcribed)     |
| `Transmit_Enquiry`               | ❌ no-op stub                                       |
| `Invoke_Retransmission`          | ❌ no-op stub                                       |
| `N_r_Error_Recovery`             | ❌ no-op stub                                       |
| `Enquiry_Response`               | ❌ no-op stub                                       |
| `Check_Need_For_Response`        | ❌ no-op stub                                       |
| `UI_Check`                       | ❌ no-op stub                                       |
| `Set_Version_2_0`                | ❌ no-op stub                                       |
| `Set_Version_2_2`                | ❌ no-op stub                                       |

The figc4.7 YAML transcriptions are **complete** — every subroutine's
decision tree, predicate, and action chain is encoded. What's missing
in the TS runtime is the *walker* that consumes those bodies. The TS
dispatcher treats a `kind: subroutine` action as a name lookup against
a registry; nine of the thirteen names map to a no-op that logs a debug
message instead of executing the SDL-declared body.

Real consequence: REJ / SREJ recovery, T1V smoothing, frame
retransmission on T1 timeout, and v2.2 mode negotiation all degrade to
"do nothing" at runtime. SABM / UA / DISC / I-frame / RR happy-path
flows are unaffected.

### Other v2.2 spec gaps

These are spec features that have no SDL transitions in the canonical
figures — they live in spec prose only — and are therefore not modelled
in this package at all:

- **XID negotiation** (v2.2 §5.4, §6.6) — modulus + parameter exchange
  at link-establishment time.
- **TEST frame** handling (§4.3.3.10) — round-trip diagnostic.
- **Selective Reject (SREJ)** as a v2.2-only frame — generated /
  consumed only when the link is mod-128 *and* the `srej_enabled`
  predicate is true. Predicate currently returns false.
- **Mod-128 sequencing** as a runtime path — the SDL guards reference a
  `version_2_2` predicate, which the C# and TS bindings hard-wire to
  `false`. The figc4.6 (`AwaitingConnection22`) page transcribes the
  SABME handshake but the post-connect mod-128 transitions in figc4.4
  never get walked.

## How to keep this section honest

When transcribing a new SDL page or wiring a previously-stubbed
subroutine to its real walker, **update this README in the same PR**.
The maturity table is the only authoritative summary of which parts of
the spec are runnable end-to-end; the codegen lints will catch
predicate / action-verb gaps, but they will not surface "this whole
state has no transitions" or "this subroutine is a no-op". The README
is the human-readable backstop.

Suggested workflow when status changes:

1. Move a row from ❌ / ⚠️ to ✅ in this README.
2. Drop the corresponding `StateTargetAllowList` entry (if the state
   is now transcribed) or remove the no-op stub registration (if a
   subroutine is now wired).
3. Bump the version in `spec/ts/package.json` and `web/ax25/package.json`
   in lockstep so consumers pick up the broader behaviour with a
   semver hint. Even a no-op-stub → real-walker transition is worth a
   minor-version bump since previously-quiet code paths now produce
   wire traffic.

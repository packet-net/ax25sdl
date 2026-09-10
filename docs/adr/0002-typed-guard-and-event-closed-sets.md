# ADR-0002 — Typed `Ax25Guard` / `Ax25Event` closed sets (SP-010 for guards + events)

- **Status:** Accepted
- **Date:** 2026-06-03
- **Deciders:** Tom Fanning M0LTE; Claude (Opus 4.8) implementing

## Context

[SP-010](https://github.com/m0lte/packet.net) (packet.net#260) made action
**verbs** a typed closed set: `spec-sdl/actions.yaml` canonicalises the
figure-verbatim spellings, and the codegen emits a generated `Ax25ActionVerb`
(C# enum / TS string-literal union) so the runtime dispatcher can switch
exhaustively — a renamed or typo'd verb is a compile error, not an
"unknown SDL action" thrown at runtime (the failure mode behind the
packet.net UI-reception and DL-DATA-while-connecting bugs). See
`docs/sdl-verb-catalogue.md`.

Guards (decision predicates) and events (`on:` triggers) were still emitted as
**raw strings** and reconciled at runtime by hand-maintained alias tables:

- The C# consumer's `GuardEvaluator.PredicateAliases` (m0lte/packet.net) maps
  the post-walker spelling the package emits (`vs_eq_va`, `ack_pending`,
  `SREJ_enabled`) back to the historic hand-authored binding names
  (`V_s_eq_V_a`, `acknowledge_pending`, `srej_enabled`) at *evaluation* time.
- m0lte/ax25-ts re-implements the same reconciliation by hand in
  `session-bindings.ts`.

This is the same lint-invisible second layer SP-010 removed for verbs: a guard
atom that drifts or is typo'd isn't caught at compile time, and a binding gap
manifests as a silently-dropped frame when a background pump swallows the
"unbound identifier" throw.

### What the figures actually contain

- Each decision `predicate:` field in a `*.sdl.yaml` is a **single atom** —
  38 distinct atoms across 129 uses, zero compound `and`/`or` at the
  `predicate:` level. (Atoms whose *name* contains `_or_`, e.g.
  `F_eq_1_and_frame_eq_RR_or_frame_eq_RNR_or_frame_eq_I`, are single opaque
  atoms; the `or` is part of the name, not a composition operator.)
- A transition's (or subroutine path's) **`Guard`**, however, is composed by
  the `Resolver` into a **conjunction of optionally-negated atoms** —
  `not peer_receiver_busy and vs_eq_va_plus_k`. 86 of 116 distinct emitted
  guards are compound; 89 carry a `not`. So `Guard` is *not* a single atom.
- A `LoopRange.Predicate` is a single atom that may carry a leading `not`
  (the loop continue-condition).
- An `UndefinedSpecBranch.Predicate` is a single bare atom (no negation).
- Every `on:` event is a single atom already catalogued in
  `spec-sdl/events.yaml` (the codegen already refuses an uncatalogued event).

## Decision

Give guards and events the same treatment SP-010 gave verbs, generalised to
the shapes above.

### Catalog + canonicalisation (the guard analogue of `actions.yaml`)

- New `spec-sdl/predicates.yaml` — the canonical guard-atom catalog, structured
  exactly like `actions.yaml` (groups → `{ name, aliases }`, group key
  documentation-only since predicates have no kind). Canonical `name:` = the
  spelling the generated tables carry (and the spelling a consumer binds to);
  `aliases:` = alternate figure spellings that collapse to it. The historic
  packet.net binding names are recorded in `# legacy binding:` comments (they
  are not aliases — no YAML page spells a predicate that way) so Part B's
  re-binding has the mapping.
- Three genuine same-semantic figure-spelling collapses were seeded as aliases
  (each used by `timer_recovery`, which spells them inconsistently within one
  figure): `ACK_pending → ack_pending`, `own_receive_busy → own_receiver_busy`
  (SDL typo dropping the `r`), `peer_busy → peer_receiver_busy`.
- `Validation.NormaliseDecisionPredicates` rewrites alias atoms to canonical
  **before** validation / lints / resolution, so the guard-overlap disjointness
  lint and the stale-read resolver all observe the canonical atom — exactly the
  insertion point `NormaliseActionVerbs` uses for verbs.
- Two lints mirror the actions.yaml discipline: an **unused-predicate-alias**
  lint, and a **catalog-completeness** lint (repurposed from the old
  `LintPredicateBindings`) requiring every decision predicate to resolve to a
  `predicates.yaml` entry once the catalog is present. The original per-runtime
  binding-coverage check is retained (it silently skips when the consumer
  source isn't on disk, the normal case in this repo's CI).

### Typed emission (the guard/event analogue of `Ax25ActionVerb`)

- **`Ax25Guard`** (generated `Ax25Guard.g.cs` enum / `ax25-guard.g.ts` union) —
  the closed set of canonical guard atoms, **gathered from the resolved IR** so
  it includes atoms the `Resolver` synthesises that never appear as a raw
  `predicate:` (the stale-read substitution's `vs_eq_nr`; ax25sdl#53). 36
  members. Members are PascalCased with collision detection, ordered by
  canonical string, each doc-commented with its canonical text — same shape as
  `Ax25ActionVerb`.
- **`Ax25Event`** (generated `Ax25Event.g.cs` enum / `ax25-event.g.ts` union) —
  one member per `spec-sdl/events.yaml` entry. (Delivered in the follow-on
  events PR; same emission shape.)
- C# and TS only get the generated closed-set files + the tightened field
  types — exactly as SP-010 emitted `Ax25ActionVerb` for C#/TS only. Go / Rust
  / C / Python / JSON keep rendering the canonical guard string and event
  string (the IR fields stay `string`); their only churn is the three-alias
  canonicalisation.

### Representing the composed guard: `GuardTerm`

A single typed literal cannot represent a compound guard. We add a hand-written
`GuardTerm(Ax25Guard Atom, bool Negate)` (C# record / TS interface) and type:

- `TransitionSpec.Guard` : `IReadOnlyList<GuardTerm>?` — a conjunction; null /
  empty = unguarded.
- `SubroutinePath.Guard` : same.
- `LoopRange.Predicate` : a single `GuardTerm` (carries the loop's `not`).
- `UndefinedSpecBranch.Predicate` : `Ax25Guard` (bare atom).

The composed guard string the `Resolver` produces is parsed back into terms at
C#/TS emit time by `GuardExpression` (a language-neutral parser in the IR). It
accepts only a conjunction of optionally-negated atoms and **throws** on a
top-level `or` or a malformed shape — matching the codegen's "stop, don't
force it" discipline, so a future guard shape the typed model can't represent
faithfully fails codegen loudly rather than being silently mis-encoded.

## Alternatives considered

### 1. Leave `Guard` a string; emit only the `Ax25Guard` closed set

Rejected. The consumer would still parse a string and look up each atom; a
typo'd atom *inside* a guard string would not be a compile error. SP-010's goal
("bind exhaustively / compile-checked") is only met when the atom each consumer
looks up is the typed closed-set member.

### 2. Make `Guard` a single `Ax25Guard` (mirror the verb retype literally)

Rejected — impossible. 86/116 guards are compound conjunctions; a single
enum member cannot carry `not a and b`.

### 3. A full boolean-expression AST (and/or/not tree)

Rejected as over-engineering. No spec page emits a top-level `or` today, so a
flat conjunction of `(atom, negate)` terms is lossless. `GuardExpression.Parse`
throws if an `or` ever appears, which is the trigger to revisit this — a
deliberately small extension surface.

### 4. Emit the closed set in all 7 backends

Not done — SP-010 emitted `Ax25ActionVerb` for C#/TS only and explicitly kept
the other five backends string-typed with zero churn. We mirror that precisely;
extending to the other backends is a separate decision if/when those runtimes
want compile-checked binding.

## Consequences

### Positive

- A renamed/typo'd guard atom or event is a compile error in the C#/TS
  artefacts, not a runtime throw. Consumers can bind every atom exhaustively
  and delete their runtime alias layers (Part B).
- `predicates.yaml` is now the single source of truth for guard-atom spelling,
  catalog-completeness-linted exactly like verbs.
- The compound-guard shape is preserved losslessly (atom + negate + and), and
  an unsupported shape (top-level `or`) fails codegen rather than mis-encoding.

### Negative

- **Breaking change** to the generated C# `TransitionSpec.Guard` /
  `LoopRange.Predicate` / `UndefinedSpecBranch.Predicate` and TS equivalents:
  the runtime alias-layer deletion + re-binding against the typed atoms is
  Part B (packet.net, ax25-ts) and is intentionally out of scope here. Those
  consumers will not build against this change until rewired.
- One more generated file pair (`Ax25Guard.g.*`, `Ax25Event.g.*`) per typed
  backend, and a hand-written `GuardTerm` type to keep in sync across C#/TS —
  same discipline as the existing per-backend runtime type homes.

## Status

- 2026-06-03 — Accepted. Guards landed first (catalog + canonicalisation +
  `Ax25Guard` + `GuardTerm`); events (`Ax25Event`) follow in a stacked PR.
- 2026-09-10: Amended. The catalog-completeness lint stands. The per-runtime
  binding-coverage half described above was retired along with
  `spec-sdl/lint-targets.yaml`: it named paths in repos this repo's CI never
  checks out, and skipped silently when they were absent, so it had never
  failed on anything. Consumer binding coverage is now enforced by each
  consumer's own compiler against the typed closed set this ADR introduced,
  which is the outcome the ADR was aiming at. See
  `docs/sdl-guard-and-event-catalogue.md`, "Where the binding gate lives".

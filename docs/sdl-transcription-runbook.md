# SDL transcription runbook

> **Note (2026-07):** the SDL sources (`spec-sdl/`) moved to [`packethacking/ax25spec`](https://github.com/packethacking/ax25spec). The transcription workflow below still applies, but the graphml/yaml/citations PRs land in **that** repo; this repo then bumps its `ax25spec/` submodule pin and regenerates the backends.


This is the **step-by-step methodology** for taking one SDL figure from the
AX.25 spec all the way through to a fully validated, cross-referenced YAML
transcription in this repo. Refined through figc4.1 (Disconnected) and
figc4.2 (Awaiting Connection); should be reusable for every remaining figure.

If a step doesn't apply on your figure, say so in the PR description and
move on — the runbook is the default path, not a rigid contract.

## Pre-requisites (one-time, already done)

These are baked into the repo and don't repeat per figure:

- Lossless YAML schema (`spec-sdl/schema/sdl-machine.schema.json`) with
  `pinned_refs`, `decisions`, `path`, `references` fields.
- Codegen tool `codegen/src/Packet.Sdl.CodeGen` with lints (decision-branch
  completeness, guard overlap, references shape, and the semantic totality /
  determinism check over the guard-atom domain model, see
  [`lint-totality.md`](lint-totality.md)) and Roslyn parse-back.
- "Trust the figure" + "Read `d5`, not the shape direction" hard rules in
  [`CLAUDE.md`](../CLAUDE.md) and the agent's memory.
- Four reference codebases cloned locally (see Stage 4 below).

## Per-figure workflow

Two PRs per figure:

1. **PR 1 — transcription**: graphml → YAML + structural lints pass.
2. **PR 2 — validation**: orchestrator smoke test + spec_prose + four-codebase
   implementation references.

### Stage 0 — Tom draws the graphml

Tom transcribes the SDL figure into yEd using the agreed 13-shape palette
(`spec-sdl/ax25-sdl-all-shapes.graphml`) and pushes the result as
`spec-sdl/<machine>/DataLink_<StateName>.graphml` to `main`.

The agent (this one) waits for the file to be on main before starting.

### Stage 1 — Inspect the graphml (transcription PR)

```sh
git checkout main && git pull
git checkout -b feat/sdl-<state-name>
```

Run a node/edge extraction script (Python with `xml.etree.ElementTree` is
fine) to dump the structure:

- Shape inventory by `d5` description
- All nodes with their `<d5>` (= figc1.1 shape class) and label text
- All edges with source, target, and label (Yes/No on diamonds)
- Outgoing edges from the focal state (= input columns)

Verify:

- All shape classes used are recognised (see [`sdl-primer.md`](sdl-primer.md)
  for the 13-class palette).
- Note any **edge labels with annotations** like "(Note: assumed; missing
  from spec)" — these are Tom's `verification_pending:` flags.
- Count columns (= outgoing edges from focal state) and decisions
  (= `Test or decision` shapes). Multiply for the rough transition count.

### Stage 2 — Events catalogue

Check `spec-sdl/events.yaml`. Every event referenced in `on:` must appear
there. Add anything new — keep the grouping (`primitives_upper`,
`frames_received`, `internal`, `timers`, `catchalls`) sensible.

### Stage 3 — Write the YAML

`spec-sdl/<machine>/<state>.sdl.yaml`:

- **Header**: `machine`, `state`, `coverage`, `source.{spec, figure, url}`.
- **`save:`** for columns whose path ends in a Save parallelogram. The
  column becomes a page-level save directive, **not** a transition.
- **`decisions:`** — one entry per `Test or decision` shape in the figure.
  Each entry needs `id`, `question` (verbatim figure text including
  punctuation), `predicate` (canonical boolean form for the runtime).
- **`transitions:`** — one transition per (column × decision-branch
  combination):
  - Walk each column from the focal state to its terminating state.
  - Each `Test or decision` step contributes `{decision: …, branch:
    "Yes"|"No"}`.
  - Each action shape contributes `{action: …, kind: …}` where `kind` is
    one of `signal_upper`, `signal_lower`, `processing`, `subroutine`,
    `internal_out`.
  - **Multi-line processing boxes** split into one action per line.
  - For chained diamonds, enumerate every path (a chain of N binary
    diamonds → up to 2^N transitions).
  - Preserve `verification_pending` annotations in `notes:` with explicit
    "Tom flagged this in the graphml" wording.

Run codegen + tests:

```sh
dotnet run --project codegen/src/Packet.Sdl.CodeGen
dotnet build
dotnet test --filter "Category!=HardwareLoop&Category!=Interop"
```

The generated conformance tests + codegen lints catch most transcription
errors automatically. If anything fails, fix the YAML and re-run.

### Stage 4 — Ship PR 1 (transcription)

```sh
git add -A
git commit -m "sdl(<machine>/<state>): transcribe <figure> on lossless schema"
git push -u origin feat/sdl-<state-name>
gh pr create --title "sdl(<machine>/<state>): transcribe <figure> on lossless schema" --body "…"
```

Watch CI (6 checks: `docs/plan.md update discipline`, `SDL codegen
idempotent`, `build & test` on macos/ubuntu/windows, `interop matrix`).
Squash-merge on green.

### Stage 5 — Branch off updated main for validation

```sh
git checkout main && git pull
git checkout -b feat/sdl-<state-name>-validation
```

### Stage 6 — Orchestrator smoke test

Create `tests/Packet.Ax25.Tests/Session/DataLink<State>SmokeTests.cs`.
Follow the pattern in `DataLinkDisconnectedSmokeTests.cs` /
`DataLinkAwaitingConnectionSmokeTests.cs`:

- `RecordingActionDispatcher : IActionDispatcher` that appends `(verb, kind)`
  tuples to a list.
- `NewSession(...)` factory parameterised by every decision predicate this
  figure references (e.g. `pEq1`, `fEq1`, `vsEqVa`, `rcEqN2`,
  `layer3Initiated`).
- Custom guard bindings layered on top of `Ax25SessionBindings.CreateDefault`.
- `AssertTransitionFires(transitionId, evt, ...)` helper that posts the
  event and asserts `CurrentState == expected.Next` plus recorded actions
  equal `expected.Actions`.
- One `[Fact]` per transition (one per `transitions[]` entry in the YAML).

Run `dotnet test` — every smoke test must pass.

### Stage 7 — Spec prose cross-check

Pull the spec markdown:

```sh
curl -s 'https://raw.githubusercontent.com/packethacking/ax25spec/main/doc/ax.25.2.2.4_Oct_25.md' \
  | sed -n '/^### 6.3.X /,/^### 6.3.Y /p'
```

For each transition, add a `spec_prose` entry to its `references:` block:

```yaml
- source: spec_prose
  cite: "§6.3.5 ¶1"
  quote: "transmits a DM frame in response to a DISC command"
```

If a transition has no explicit prose backing (figure-only details like
DL-ERROR code letters), **skip the spec_prose entry** and note "figure-
authoritative" in the transition's `notes:`.

### Stage 8 — Implementation references via four subagents

Spawn **four parallel general-purpose subagents** via the Agent tool, one
per codebase, all `run_in_background: true`:

| Source | Codebase path | Key file(s) | Pinned commit |
|---|---|---|---|
| `linbpq` | `/home/tf/src/linbpq/` | `L2Code.c` (CRLF, use `grep -an`) | `88a68988b446187cc5f7a2dfdbe33d1cc3ebd46f` |
| `direwolf` | `/tmp/direwolf/` | `src/ax25_link.c` | `a231971a652bfb574a4bae9a5d875fbce53d2267` |
| `rax25` | `/tmp/rax25/` | `src/state.rs` (Rust) | `d97b7ab725620497d5444e7393969b8d6f9ae58d` |
| `linux_oot` | `/tmp/mod-orphan/` | `net/ax25/ax25_*.c`, `af_ax25.c` | `40188e901024be90f50fc37ba1ba3a84678dfe12` |

(Bump the pinned commits when one of the codebases gets a meaningful update;
audit existing line numbers for drift when you do.)

Brief each agent with:

- Pinned commit hash for that codebase.
- Key entry points already mapped from previous figures (functions, line
  numbers — save them rediscovering).
- Full transition table from your YAML (id + trigger + outcome).
- Output format: one YAML block per transition with `source`, `path`,
  `function`, `line`, `note`; or `omit: true, reason: …` if no equivalent
  exists.
- Constraint: "don't write files; produce YAML output only".

When all four return, merge their findings into per-transition `references:`
blocks in the YAML. Each transition typically ends up with 1–5 implementation
citations plus 0–1 spec_prose citation.

**Look for triangulation**: when multiple agents flag the same spec quirk
in source comments, that's a candidate upstream spec issue. Pull it up
into a NOTE block on the relevant transition's `notes:` and into the
amendment log.

### Stage 9 — Amendment log

Add an entry to `docs/plan.md` §17 covering:

- Test totals (was X, now Y).
- Triangulated upstream-spec findings (the gold).
- Per-codebase divergence patterns.

### Stage 10 — Ship PR 2 (validation)

```sh
git add -A
git commit -m "sdl(<state>): smoke test + spec_prose + 4-codebase references"
git push -u origin feat/sdl-<state-name>-validation
gh pr create --title "…" --body "…"
```

Same CI watch + squash-merge on green.

## Pattern observations from figc4.1 + figc4.2

- The codegen lints (`decision_branch_completeness`, `guard_overlap`, and
  the totality / determinism lint of [`lint-totality.md`](lint-totality.md),
  which checks the semantic space the first two only approximate) catch the
  kinds of transcription errors that used to take a build cycle to surface.
  Trust them. A totality finding on a real figure goes in
  `codegen/lint-known-findings.yaml` with an ax25spec issue URL, never in a
  "fixed" branch label.
- Subagent parallelism for implementation refs is **strongly preferred** —
  each codebase has its own structure that takes context to learn, and the
  agents run independently with no synthesis dependency until merge.
- Implementation **divergence is the rule, not the exception** — most
  transitions have at least one codebase that omits, collapses, or
  re-orders the spec's behaviour. Document each one in the citation note.
- Multiple implementers reaching the **same conclusion about a spec quirk**
  (in independent source comments) is the strongest evidence of an upstream
  spec issue. Capture verbatim.
- The four pinned implementations span a deliberate range: connected-mode
  C (LinBPQ), explicit SDL-like C (direwolf), modern Rust (rax25), Linux
  kernel (linux_oot). Each has its own blind spots; together they triangulate.

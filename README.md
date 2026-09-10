# ax25sdl

**Tooling + distribution for the AX.25 SDL state tables: a codegen pipeline that turns the normative SDL transcriptions into ready-to-consume libraries in seven languages.** The figc4.x state-machine figures are encoded *once*, with discipline, in [`packethacking/ax25spec`](https://github.com/packethacking/ax25spec) — the community home of everything normative about AX.25, prose and figures — and this repo emits that single source of truth as native idiomatic code for C#, Go, TypeScript, JSON, Rust, C, and Python. Downstream runtimes walk the generated tables — they don't have an opinion of their own about what AX.25 says.

> **Where changes go:** figure/spec changes (graphml, yaml transcriptions, citations, catalogues) are PRs against [`packethacking/ax25spec`](https://github.com/packethacking/ax25spec); a pin-bump PR here then regenerates the language backends. Only tooling (walker/emitters/lint), generated backends and packaging change in this repo.

## Inputs

The SDL sources live in the [`packethacking/ax25spec`](https://github.com/packethacking/ax25spec) **git submodule** at [`ax25spec/`](ax25spec), pinned to a specific commit (the same pin-bump cadence packet.net uses towards this repo). A `spec-sdl -> ax25spec/spec-sdl` symlink keeps every historical path working. Clone with `git clone --recurse-submodules` (or run `git submodule update --init` after a plain clone).

| Path | What |
| --- | --- |
| `spec-sdl/v2.2-errata/data-link/` | The figc4.1 – figc4.7 data-link state machine (v2.2 + errata), split by artefact kind: `sdl/` holds the canonical `*.graphml` yEd sources, `yaml/` holds the `*.sdl.yaml` transcriptions (derived) plus the `*.citations.yaml` human-curated sidecars, `mmd/` holds the `*.g.mmd` Mermaid renderings (derived). |
| `spec-sdl/v2.2/` | Clean published v2.2 (black-only, no errata). **Currently empty — backfill pending.** |
| `spec-sdl/schema/` | JSON Schema for `*.sdl.yaml` |
| `spec-sdl/actions.yaml` | Action-verb normalisation table (figure spellings → canonical verbs) |
| `spec-sdl/events.yaml` | Canonical event catalog |
| Upstream | The AX.25 v2.2 specification figures themselves — the source of truth for every transcription |

## Outputs

| Artefact | Where | Name | In-repo source |
| --- | --- | --- | --- |
| C# library | NuGet | [`Packet.Ax25.Sdl`](https://www.nuget.org/packages/Packet.Ax25.Sdl) | [`spec/csharp/`](spec/csharp/) |
| TypeScript library | npm | [`ax25sdl`](https://www.npmjs.com/package/ax25sdl) | [`spec/ts/`](spec/ts/) |
| Go module | git | `github.com/m0lte/ax25sdl/spec/go` | [`spec/go/`](spec/go/) |
| Rust crate | crates.io (publishable; not yet published) | `ax25sdl` (`no_std`-capable) | [`spec/rust/`](spec/rust/) |
| C / Python / JSON | _not externally packaged_ | codegen output for in-tree consumers | per-backend dirs |

Tagging `v*` on `main` fires [`.github/workflows/publish.yml`](.github/workflows/publish.yml) — NuGet + npm publish from the same tag, version taken from the tag stripped of its leading `v`.

## Discipline

The transcription rules and how to add a new figure live in:

- [`docs/sdl-primer.md`](docs/sdl-primer.md) — SDL shape reference
- [`docs/sdl-transcription-runbook.md`](docs/sdl-transcription-runbook.md) — end-to-end per-figure workflow
- [`docs/sdl-verb-catalogue.md`](docs/sdl-verb-catalogue.md) — action-verb normalisation
- [`docs/lint-totality.md`](docs/lint-totality.md) - the totality / determinism lint: every (state, event) arm and subroutine must decide every feasible guard-atom valuation exactly once, and the allow-list contract for known figure findings
- [`docs/adr/0001-sdl-dsl.md`](docs/adr/0001-sdl-dsl.md) — why YAML + codegen rather than hand-written tables

## Semantic validation — golden traces

Transcription fidelity (graphml → YAML → generated tables) is proved by the
drift jobs; **behavioural** fidelity is proved by the golden-trace suite. A
reference interpreter ([`codegen/src/Packet.Sdl.Interpreter/`](codegen/src/Packet.Sdl.Interpreter/))
executes the emitted JSON tables directly — events in, abstract effects
(frames, DL primitives, internal-queue posts) out — and each scenario in
[`traces/`](traces/) replays a protocol exchange against it with
expectations derived from the AX.25 v2.2 **prose**, cited section by section.

```sh
dotnet test codegen/tests/Packet.Sdl.Interpreter.Tests
```

Where a trace fails because the *figure itself* is defective, it is not bent
to match: it carries `expected_failure: <upstream-issue-url>` and runs as a
strict xfail (it must keep failing until the upstream fix lands, then the
suite demands the marker's removal). How to add a trace, the file format,
and the interpreter's execution policies are documented in
[`docs/golden-traces.md`](docs/golden-traces.md).

Beyond single-machine traces, the two-station explorer (`codegen/src/Packet.Sdl.Explorer`) composes two interpreter instances over a modelled lossy channel and searches every interleaving for cross-station invariant violations (delivery safety, reject coherence, acknowledgement coherence, quiescence). Its calibration suite must rediscover the catalogued figure defects on pre-fix table fixtures before any novel finding is believed; see [`docs/explorer.md`](docs/explorer.md).

The SREJ sender-side scenarios (`traces/srej-*.trace.yaml`) were authored clean-room from the prose and direwolf alone and committed before they were ever executed, as an independence control on the SREJ table fix; the protocol and its outcome are recorded in [packet-net/ax25sdl#76](https://github.com/packet-net/ax25sdl/pull/76).

## Provenance

Extracted from `packet.net` (then `m0lte/packet.net`) on 2026-05-17 — the transcriptions and codegen previously lived alongside the .NET runtime in that monorepo. The SDL transcriptions moved on again to [`packethacking/ax25spec`](https://github.com/packethacking/ax25spec) in 2026-07, making that repo the single normative home (prose + figures) and this one tooling + distribution. History preserved via `git filter-repo` at both steps.

## Sibling repos

| Repo | Relationship |
| --- | --- |
| [`packethacking/ax25spec`](https://github.com/packethacking/ax25spec) | **upstream** — normative SDL sources, consumed here as a pinned submodule |
| [`packet-net/packet.net`](https://github.com/packet-net/packet.net) | consumes `Packet.Ax25.Sdl` (NuGet) |
| [`packet-net/ax25-ts`](https://github.com/packet-net/ax25-ts) | consumes `ax25sdl` (npm) |
| [`packet-net/packet-term-tui`](https://github.com/packet-net/packet-term-tui) | transitive: `Packet.Ax25` → `Packet.Ax25.Sdl` |
| [`packet-net/packet-term-web`](https://github.com/packet-net/packet-term-web) | transitive: `@packet-net/ax25` → `ax25sdl` |

## License

[MIT](LICENSE). Spec text and figures derive from the AX.25 v2.2 specification; the transcription discipline that turns figures into machine-checkable YAML is documented in [`docs/sdl-transcription-runbook.md`](docs/sdl-transcription-runbook.md).

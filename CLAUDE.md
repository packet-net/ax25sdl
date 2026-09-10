# CLAUDE.md

Operating notes for Claude Code (and other agents) working in `packet-net/ax25sdl`.

## What this repo is

**Tooling + distribution.** The codegen pipeline (transcribe walker, emitters, lints, generated backends, packaging/publish) that turns the normative AX.25 SDL transcriptions into language-specific libraries. Downstream consumers (e.g. `packet-net/packet.net`, `packet-net/ax25-ts`) pull the published artefacts.

**The normative sources live in [`packethacking/ax25spec`](https://github.com/packethacking/ax25spec)** — the single home of everything normative about AX.25, prose and figures. This repo consumes it as a git **submodule** at `ax25spec/`, pinned to a specific commit; a `spec-sdl -> ax25spec/spec-sdl` symlink keeps all historical paths working. Run `git submodule update --init` after cloning.

**Figure/spec changes (graphml, `*.sdl.yaml`, `*.citations.yaml`, catalogues, SVG renders) are PRs against `packethacking/ax25spec`, never here.** The flow for a spec change is: land it in ax25spec (its CI drift-locks graphml → yaml and graphml → SVG) → open a pin-bump PR here that advances the submodule and regenerates the backends. Only tooling, generated backends and packaging change in this repo.

Provenance: extracted from `m0lte/packet.net` on 2026-05-17; the SDL transcriptions moved on to `packethacking/ax25spec` in 2026-07 (history preserved via `git filter-repo` both times).

## Read first

- [`docs/sdl-primer.md`](docs/sdl-primer.md) — SDL shape reference. Mandatory before touching `/spec-sdl/`.
- [`docs/sdl-transcription-runbook.md`](docs/sdl-transcription-runbook.md) — end-to-end per-figure workflow (graphml → transcription PR → validation PR). Read this when starting a new SDL page.
- [`docs/sdl-verb-catalogue.md`](docs/sdl-verb-catalogue.md) — how `spec-sdl/actions.yaml` normalises figure-verbatim action spellings to canonical verbs at codegen time.
- [`docs/sdl-guard-and-event-catalogue.md`](docs/sdl-guard-and-event-catalogue.md) — the guard/event counterpart: `spec-sdl/predicates.yaml` → typed `Ax25Guard`, and `spec-sdl/events.yaml` → typed `Ax25Event`.
- [`docs/sdl-rendering.md`](docs/sdl-rendering.md) — the graphml → SVG figure renders (`spec-sdl/**/svg/`). The renderer (`tools/render/`) moved to `packethacking/ax25spec` with the figures; any PR **there** that touches a graphml must regenerate them (that repo's CI drift-locks it) — they are how humans review figure changes.
- [`docs/adr/0001-sdl-dsl.md`](docs/adr/0001-sdl-dsl.md) — why the SDL YAML DSL + codegen exists.

## Hard rules

### Trust the figure

The AX.25 SDL figures are the source of truth. When a figure surprises you, the surprise is yours. **Do not** "fix" a branch label, swap a Yes/No, or substitute "correct-looking" actions on the basis that the figure looks wrong to you. If you're uncertain, flag for human review with a `verification_pending:` note — never silently deviate.

### Encode-then-verify

Every transition in `/spec-sdl/` must come from an **explicit human-authored transcription** of the figure. You may *encode* paths that Tom has described in plain text; you may not *infer* paths by reading the PNG yourself.

### Pin implementation evidence

When transcribing any SDL transition whose semantics are non-obvious, cross-reference how at least one of the canonical implementations handles it. Drop the citation into the transition's `notes:` field.

### Reading SDL graphml: `d5` is the authoritative shape class

Each node in a `spec-sdl/**/*.graphml` file carries a `<data key="d5">` description (e.g. "Signal reception from lower layer", "Signal generation to upper layer", "Processing description", "Test or decision"). That `d5` text is the **only** authoritative source for the node's shape class.

The `d5` value and the visual direction of the parallelogram (left-notch vs right-notch) are consistent: left-notch = upper layer, right-notch = lower layer.

When the same label appears under two different `d5` values, those are **two distinct events** in the catalogue. Disambiguate with a `__from_<shape-class>` suffix on the event id.

### SDL revision provenance

The AX.25 v2.2 SDL figures use colour-coded version control: black is original published v2.2, red and green are errata that don't yet form part of the released spec. We don't distinguish red from green — they're both "errata".

Transcriptions are filed under a revision directory:

- `spec-sdl/v2.2/` — clean published v2.2 (black-only). **Currently empty** — Tom will backfill once the errata variant is complete. Until then, the `Published` revision is unavailable to consumers.
- `spec-sdl/v2.2-errata/` — v2.2 with all errata applied (black + red + green). This is what every existing `*.sdl.yaml` in the repo encodes.

A page with no errata is transcribed identically into both directories. A future CI lint will enforce byte-identity for those pages unless the errata variant explicitly declares an errata is applied.

**Package versioning** for `Packet.Ax25.Sdl` (and equivalents):

- `MAJOR.MINOR` = WG-published spec revision (`2.2`, `2.3`, `3.0`). One package per spec revision; parallel lines possible (like Linux LTS).
- `PATCH` = errata batch accumulation within that spec revision.
- Each `PATCH` artefact is **complete** — ships both clean spec tables and errata-applied tables.
- Consumers pick at runtime via `SdlRevision.{Published, WithErrata}`, defaulting to `Published`.

The codegen + `SdlRevision` enum + namespace split are **Phase 2** work, deferred until the `v2.2/` baseline is transcribed. Until then, codegen recursively walks `spec-sdl/**` and emits a single set of tables (sourced from `v2.2-errata/`).

## Common commands

```sh
# One-time after clone: fetch the ax25spec submodule (SDL sources)
git submodule update --init

# Build everything
dotnet build

# Run codegen tests
dotnet test

# Regenerate all backends
dotnet run --project codegen/src/Packet.Sdl.CodeGen

# Regenerate one backend
dotnet run --project codegen/src/Packet.Sdl.CodeGen -- --csharp
dotnet run --project codegen/src/Packet.Sdl.CodeGen -- --go
dotnet run --project codegen/src/Packet.Sdl.CodeGen -- --ts
# etc.

# Verify generated Go compiles + tests + gofmt clean
cd spec/go && go build ./... && go vet ./... && go test ./... && gofmt -l .

# Verify generated TS typechecks + tests pass
cd spec/ts && npm ci && npm run typecheck && npm test

# Verify generated Rust compiles (std + no_std) + tests + fmt clean
cd spec/rust && cargo build && cargo build --no-default-features && cargo test && cargo fmt --check

# Verify generated C compiles + tests (cmake + ctest)
cd spec/c && cmake -B build -S . && cmake --build build && ctest --test-dir build --output-on-failure

# Verify generated Python imports + tests + lint
cd spec/python && python3 -m pytest --import-mode=importlib && ruff check .
```

## Things to avoid

- Don't hand-edit the generated files: `spec/csharp/*.g.cs`, `spec/go/ax25sdl/*.g.go`, `spec/ts/src/ax25sdl/*.g.ts`, `spec/rust/src/*.g.rs` (including `lib.rs` and the typed closed-set files `ax25_action_verb.g.rs` / `ax25_guard.g.rs` / `ax25_event.g.rs`), `spec/c/src/*.g.{c,h}`, `spec/python/ax25sdl/*.g.py` (+ `*_g_test.py`). Fix the figure/yaml in `packethacking/ax25spec`, bump the submodule pin, and rerun the codegen (or fix the emitter here). Never edit anything under `ax25spec/` (the submodule) from this repo — the SDL sources, their yaml and the SVG renders all change via ax25spec PRs. The per-backend **runtime type homes are hand-written** and must stay in sync with the C# types in `spec/csharp/`: `spec/go/ax25sdl/types.go`, `spec/ts/src/ax25sdl/types.ts` (+ `*.test.ts`), `spec/rust/src/types.rs`, `spec/c/src/ax25sdl.h`, `spec/python/ax25sdl/types.py`. The per-backend build files are also hand-written: `spec/rust/Cargo.toml`, `spec/c/CMakeLists.txt`. All six backends are built + tested in CI (not just drift-checked) — see the verify commands above.
- **The Rust crate is `no_std`-capable** (`#![no_std]` unless the default-on `std` feature is set) and **publishable** (real crates.io metadata). Keep the core data/type path `no_std`-clean — `&'static` data + `Copy` types + the closed-set enums only; no `String` / `Vec` / `std::` / allocator on it. The typed closed sets (`Ax25ActionVerb` / `Ax25Guard` / `Ax25Event` + `GuardTerm`) match the C#/TS backends — see [`docs/adr/0003-rust-typed-closed-sets-and-no-std.md`](docs/adr/0003-rust-typed-closed-sets-and-no-std.md). CI runs `cargo build --no-default-features` to guard the `no_std` path.
- Don't add `[Version=...]` on `<PackageReference>` items — CPM enforces a central version table.
- Don't infer protocol semantics from the spec PNGs. See "Encode-then-verify" above.
- **Don't add new GitHub Actions jobs with `runs-on: ubuntu-latest`** (or any other GitHub-hosted runner label). This project has no Actions minutes budget for hosted runners; every workflow job MUST target `[self-hosted, Linux, X64]`. Same rule as `packet-net/packet.net`. **One standing exception, and only one:** `publish.yml`'s `publish-npm` job runs on `ubuntu-latest` because npm refuses a provenance attestation built anywhere else (`Only "github-hosted" runners are supported when publishing with provenance`), and trusted publishing generates provenance by default. It is a small job that runs on release tags only. Do not read it as licence to move drift or build jobs, which are what would actually cost minutes.

## When in doubt

Ask Tom. Spec-side surprises are best resolved by reference to the figure with human verification.

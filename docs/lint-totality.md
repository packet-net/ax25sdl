# Totality and determinism lint

The codegen's static check that every SDL figure decides every case exactly once. It is step 1 of the search campaign described in ax25spec's `docs/fuzzing-the-figures.md`: interrogate the figures directly, so that a counterexample is a figure defect rather than a bug report against one runtime.

It runs on every codegen invocation (so every CI drift job exercises it), fails the run on a finding, and lives in `codegen/src/Packet.Sdl.IR/Totality/`.

## What it checks

For every state page and every `on:` event on it, take the set of transitions for that (state, event) arm and the union of guard atoms they mention: the arm's **live atoms**. Typically one to a dozen, never all 37. Enumerate the valuations of the live atoms and, for each one, count how many of the arm's composed guards hold. A transition that does not mention an atom is indifferent to it.

- Zero matches is a **hole**: the figure has no branch for a case that can occur.
- Two or more matches is **nondeterminism**: the runtime would silently take the first, and the figure has not said which.

Subroutines get the same treatment over their paths: no event, but the paths must be total and exclusive over their live atoms.

A transition that crosses an `Undefined` spec branch (the figure's "both edges labelled undefined" marker, carried in the IR as `UndefinedBranches` and emitted as a runtime throw) is the spec deliberately leaving behaviour open. Crossing one counts as covering a valuation for totality, so it is not reported as a hole, but it never counts toward nondeterminism, and the summary line says how many valuations only an Undefined branch covers.

The two older lints are syntactic: `LintDecisionBranchCompleteness` checks that each decision has a Yes and a No somewhere on the page, and `LintGuardOverlap` checks pairwise literal contradiction. Neither looks at the semantic space. A page can pass both with a corner missing (a decision used with both branches, but not both branches under every prefix) and both can flag a pair of guards that no protocol state could ever satisfy together. This lint checks the space itself.

The lint runs on the resolved IR, after the ax25sdl#53 stale-read substitution, so it sees exactly the guards the backends emit (including the synthesised `vs_eq_nr`).

## Feasibility: the atom domain model

The atoms are not independent booleans. `ns_eq_vr`, `vr_lt_ns_lt_vr_plus_k` and `ns_gt_vr_plus_1` all talk about N(s) against V(r); `nr_eq_va`, `nr_eq_vs` and `va_le_nr_le_vs` about N(r) against V(a) and V(s); `command_and_P_eq_1` and `response_and_F_eq_1` read one P/F bit and one command/response role; `mod_8` and `mod_128` are the two values of one modulus; `T1_running` and `T1_expired` two states of one timer. A hole at a valuation no protocol state can produce is not a figure defect.

So `AtomDomain.cs` defines the underlying variables and every one of the 37 atoms as a function of them, and the lint obtains the feasible valuations by **enumerating the variables concretely**: never by hand-written pairwise exclusion rules. The variables:

| Variable | Domain | Source |
| --- | --- | --- |
| V(s), V(a), V(r), N(s), N(r), X | sequence numbers modulo 8 | §4.2.2.2 to §4.2.2.6; X is figc4.7's saved copy of V(s) |
| k | 1..7 | §6.7.2.3 |
| P/F | one bit | §4.2.1, §6.2 |
| role | command or response | §6.1.2 |
| frame | I, RR, RNR, other | §4.3; fixed by the `on:` event on a state page, free in a subroutine |
| modulus | 8 or 128 | §4.3.3.1, §4.3.3.2 |
| T1 | stopped, running, expired | §6.7.1.1, §6.4.1, §6.4.6, §4.4.5.1 |
| RC, N2, NM201 | 0..3 | §6.7.2.2; the spec puts no lower bound on N2, so 0 is kept |
| the flags | free booleans | see the file |

Each atom's definition sits next to a one-line spec citation in the file. The rule for what may go in the model: **everything must be a definition the spec gives, never a behaviour some runtime chose.** A model that quietly encoded packet.net's choices would be testing packet.net again and calling the result a spec finding. Where the spec does not settle an atom's relationship to anything else (the session flags, the exception conditions, `version_2_2`, `SREJ_enabled`, `able_to_establish`, the frame-content test) the atom is a free boolean and the file says so.

Modular comparisons use the spec's own reading (§4.2.4, §4.4.2): an offset ahead of a reference variable, wrapping. `vr_lt_ns_lt_vr_plus_k` is the open interval `1 <= (N(s) - V(r)) mod M <= k - 1` exactly as the `predicates.yaml` comment states it; `va_le_nr_le_vs` is `(N(r) - V(a)) mod M <= (V(s) - V(a)) mod M`, §6.4.11's "from the last N(R) received to the last N(S) sent plus one".

### Why modulo 8 is enough

Sequence numbers are enumerated modulo 8 with k in 1..7 regardless of the `mod_8` / `mod_128` selection. Every atom that reads a sequence number compares an offset, and the qualitative configurations of those offsets (equal, inside the window, just past the window, wrapped) are all present at modulo 8 with k ranging over 1..7. Modulo 128 adds no new relationship between any pair of atoms, only more room. The modulus variable is kept for the two atoms that read it directly.

### Independence makes it fast

Atoms that share no variable, directly or transitively, are independent, so the enumeration works per connected component of the atom/variable graph and takes the Cartesian product of the components' feasible sets. The biggest real arm (figc4.4 `I_received`, twelve live atoms) comes to a few thousand feasible valuations instead of tens of millions, and the whole run over the real figures takes well under a second.

### Limits

- **State-space invariants are not encoded.** That the number of outstanding I frames never exceeds k, or that a station with SREJ disabled has no SREJ exception, are reachability facts, not definitions. Leaving them out can only make the lint over-report, which is the visible failure mode; a reported hole at an unreachable valuation is for a human to classify, and the allow-list is where that classification is recorded.
- **`ns_gt_vr_plus_1` outside the window.** The figure only asks "N(s) > V(r)+1?" inside the receive window, where the offset reading is the only one. The model uses the same offset convention everywhere. If a future figure reads it outside the window, revisit.
- **N2 = 0 is kept feasible.** The spec gives N2 no lower bound, so `RC_eq_0` and `RC_eq_N2` may coincide. No current arm reads both.
- **Mod-8 with a large k** is the protocol's own unsoundness (selective repeat needs k of at most half the modulus; that is what `Ax25Spec13` is about). It is a two-station-model concern, not a totality one, and the lint does not constrain k below 7.
- **Unknown atoms** (an atom in a fixture page, or a new catalogue atom without a definition here) are treated as independent booleans and listed on stdout. A unit test (`AtomDomainTests`) fails if `spec-sdl/predicates.yaml` gains a canonical the model does not define, so that gap cannot stay silent.

## What the report looks like

Findings are reported as minimal cubes rather than one line per valuation: a hole at `ns_eq_vr=false, vr_lt_ns_lt_vr_plus_k=false` regardless of the other live atoms is one finding, not sixty-four. Each names the page, state and event (or subroutine), the cube, the live atoms it does not depend on, a concrete witness for the underlying variables, and for nondeterminism every transition id that matches. This is what the lint printed when the out-of-window `P_eq_1=false` leaf was deleted from figc4.4 `I_received` as a mutation test:

```
::error::spec-sdl/v2.2-errata/data-link/yaml/connected.sdl.yaml: totality: state `Connected`, event `I_received`: no transition matches when command=true, info_field_length_le_N1_and_content_is_octet_aligned=true, va_le_nr_le_vs=true, own_receiver_busy=false, ns_eq_vr=false, P_eq_1=false, vr_lt_ns_lt_vr_plus_k=false (version_2_2, ack_pending, reject_exception, SREJ_enabled, sreject_exception_gt_0, ns_gt_vr_plus_1: either); witness V(s)=0, V(a)=0, V(r)=1, N(s)=0, N(r)=0, k=4, P/F=0, role=command, own_receiver_busy=false, info_field_length_le_N1_and_content_is_octet_aligned=true. The figure has no branch for this case. Fix the figure in packethacking/ax25spec (or mark the branch Undefined if the spec really leaves it open), or record it in codegen/lint-known-findings.yaml with an issue URL.
```

The summary line on stdout shows the model did work:

```
  lint  totality/determinism: 120 (state, event) arm(s) and 13 subroutine(s) checked over 8051 feasible atom valuation(s): 0 hole(s), 0 overlap(s); 0 syntactic hole(s) and 0 syntactic overlap(s) were infeasible under the atom domain model.
```

"Syntactic" here means over the raw 2^n valuations of the live atoms, ignoring feasibility; the count is how many of those the domain model classified out.

## The allow-list: `codegen/lint-known-findings.yaml`

A real finding on the real figures would turn every CI job red until an ax25spec figure fix lands and the pin is bumped. The allow-list downgrades a **known, filed** finding to a printed `::warning::` so the pipeline keeps moving. The contract is strict, the same discipline the golden-trace suite in ax25sdl#75 uses for `expected_failure`:

- Every entry needs an `issue:` URL.
- An entry identifies exactly one finding: `kind`, `machine`, `state` + `event` (or `subroutine`), the `when:` cube exactly as reported, and for nondeterminism the `transitions:` set.
- An entry that matches nothing **fails the run**. The list can never go stale silently: when the figure is fixed and the pin bumped, the entry must go.
- Two entries for one finding fail the run.

The path defaults to `codegen/lint-known-findings.yaml` relative to the working directory (the repo root in CI and in `dotnet run` from the root) and an absent default file is an empty list. `--known-findings <path>` names another file; an explicit path that does not exist is an error, since a typo would otherwise silently disable every entry.

## Calibration

The brief says to run any checker against the figures as they stood before each known erratum and see whether it rediscovers the defect. For ax25spec#40 (the out-of-sequence `I_received` arm lacked a receive-window guard) the honest answer is that this lint would not have caught it, and cannot: that arm was total. Every N(s) that failed `ns_eq_vr` fell into a drawn branch; the defect was that the branch did the wrong thing for out-of-window frames, not that no branch existed. That is a behavioural property, and it needs the two-station model (step 3 of the brief). The pre-#40 run is recorded in the PR that added this lint.

What the lint does catch, proven by mutation: remove any leaf from a real decision tree (for example the out-of-window `P_eq_1=false` leaf of figc4.4 `I_received`) and it fails the run naming the cube and a witness. The transcriptions are decision trees, so today's figures are total by construction; the lint's value is as a gate on future figure edits, transcription slips and hand-written arms, and as the base for the domain model the later steps need.

## Running it

```sh
# Every codegen run includes the lint.
dotnet run --project codegen/src/Packet.Sdl.CodeGen -- --json --json-out /tmp/out

# Against another checkout of the figures, with no allow-list.
: > /tmp/empty.yaml
dotnet run --project codegen/src/Packet.Sdl.CodeGen -- --in /path/to/spec-sdl --json --json-out /tmp/out --known-findings /tmp/empty.yaml
```

Tests: `codegen/tests/Packet.Sdl.CodeGen.Tests/TotalityLintTests.cs` (black-box, through the CLI: a hole, an overlap, a hole only at an infeasible valuation that must not fire, a non-exhaustive subroutine, an Undefined-covered valuation, the allow-list suppressing a finding, a stale entry failing the run) and `AtomDomainTests.cs` (the modular window atoms, the shared P/F bit and role, the timer, the catalogue sync check).

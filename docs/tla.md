# TLA+ model and the TLC cross-check

The Data Link tables are rendered as a TLA+ module and model-checked with TLC, as a second checker independent of the two-station explorer (`docs/explorer.md`). It is the first step of Phase 2 of packethacking/ax25spec#116: a model generated from the emitted tables, never written by hand, so it cannot drift from the figures, and two tools that must agree on the calibration set.

Run it:

```sh
dotnet run --project codegen/src/Packet.Sdl.CodeGen -- --tla          # regenerate spec/tla/Ax25DataLinkTables.tla
export TLA2TOOLS_JAR=/path/to/tla2tools.jar                            # TLC 2.19 (tlaplus v1.7.4)
dotnet run --project codegen/src/Packet.Sdl.TlaCheck -- --parallel 6   # the calibration suite, explorer vs TLC
dotnet run --project codegen/src/Packet.Sdl.TlaCheck -- --grid --parallel 8   # the explorer's 576-cell grid
dotnet run --project codegen/src/Packet.Sdl.TlaCheck -- --help
```

`TlaCheck` prints one row per scenario and exits 0 only if every row agrees. Each model directory (under `--work`, default `<temp>/ax25sdl-tlacheck`) keeps the rendered tables, the configs and TLC's full output, so any row can be rerun by hand with `java -cp tla2tools.jar tlc2.TLC -config MC_Safety.cfg TwoStation.tla`.

## The pieces

| File | Written by | What it is |
| --- | --- | --- |
| `spec/tla/Ax25DataLinkTables.tla` | generated (`--tla`) | The tables as constant TLA+ data: `Pages[state][event]` is that page's transitions on that event in file order, `Subroutines[name]` its paths. Each has its guard (`Is(atom)` / `Not(atom)` conjuncts), loop regions, action chain (`Box(kind, verb)`) and next state. |
| `spec/tla/Ax25Semantics.tla` | hand | What each verb and guard atom means, and a runner for the table data. A port of the reference interpreter (`Machine.cs`). |
| `codegen/src/Packet.Sdl.TlaCheck/TwoStation.tla` | hand | The harness: two stations, the channel, the v2.0 stub peer, the moves, the invariants. A port of the explorer's `Stepper.cs`. |
| `codegen/src/Packet.Sdl.TlaCheck/` | hand | Runs the explorer and TLC on the same scenario and compares them. |
| `codegen/src/Packet.Sdl.TlaCheck/fixtures/` | script, documented | Two deliberately broken table sets that fail acknowledgement coherence and quiescence, which no real defect does (see its README). |
| `codegen/tests/Packet.Sdl.CodeGen.Tests/TlaEmitterTests.cs` | hand | Fails if the committed module is not exactly a fresh render of `spec/json`. TlaCheck renders afresh from JSON each run, so this test is what ties the committed module to what was checked. |

**Why the input is the JSON.** `spec/json` is exactly what the interpreter and the explorer execute, so the two checkers read the same bytes. The explorer's pre-fix fixtures (`tables-pre38`, `tables-pre40`) exist only as JSON. `--tla-tables DIR` renders any table directory.

**Why the tables are data, not operators.** The other six backends emit the tables as data for a hand-written runtime; this one does the same. Emitting each transition as a TLA+ operator was tried first. TLC's start-up level analysis (`SpecProcessor.processConstantDefns`) unfolds every operator application site separately, and with a subroutine call reachable from every action box it spent about 15 seconds per run before exploring a single state. As data, TLC starts in about a second.

**What is hand-written, and why.** The catalogues (`actions.yaml`, `predicates.yaml`, `events.yaml`) name verbs, atoms and events but do not say what they do; the only definition is the interpreter's switch. So `Ax25Semantics.tla` restates it, verb by verb and atom by atom, with the same policies (pinned semantics 1, 3 and 4 of `docs/explorer.md`). Nothing protocol-specific is in the generated module.

**How independent the two checkers are.** Less than "two tools" suggests, and on purpose, so that a disagreement means a bug rather than a different reading. Shared or mirrored between them:

- the input: the same JSON tables (TlaCheck renders the TLA+ from the scenario's JSON afresh for each run rather than reading the committed `spec/tla` module; the drift test is what ties the committed module to that same render);
- the meaning of every verb and atom: `Ax25Semantics.tla` is a port of `Machine.cs`, written by reading it;
- the harness: `TwoStation.tla` is a port of `Stepper.cs`, written by reading it;
- the move set and the move order, chosen to match so both searches meet the same first violation;
- every invariant definition, including the per-step checks and their order;
- the v2.0 stub peer's rules (`V20Peer.cs`);
- the seeds;
- the quiescence definition;
- the scenarios: `Scenarios.cs` hand-copies `CalibrationTests.cs`, and the one depth-bounded row is marked by hand;
- the depth-bound handling (a TLC state constraint chosen to cut where the explorer's bound cuts).

Truly independent: the exploration engine (TLC's breadth-first search, state hashing, deadlock detection and liveness checking, against the explorer's own), and the code that runs the tables and the verbs, which is a separate implementation in a separate language though still a port of the same reading. So the cross-check finds bugs in either implementation or either search; it cannot find a shared misreading of a verb or a shared mistake in an invariant's definition.

## How TLC reproduces the explorer

The harness follows `Stepper.cs` move for move: the same seeds (connected, disconnected, awaiting-v22), the same enabled moves in the same order (per station: deliver, drop, duplicate, reorder, pop, seize-confirm, flow-on/off; then T1 at A, at B), the same quiescent-timeout rule for T1, the same "a pop is a move only when it changes something", the same event routing and catch-alls, the same stub peer rules. Because both searches are breadth-first over the same moves in the same order, with one TLC worker they meet the same first violation: the cross-check compares not just the verdict but the length and the transition fired at every step.

Three places needed a different formulation in TLA+:

1. **Per-step invariants are a state variable.** The explorer checks reject coherence, delivery order, flow-off delivery, busy-tracks-flow and machine errors on each edge, from the step's effects. The harness computes the same checks inside the step and records the first that fires in `viol`; the invariant is `viol = NoViol`. A violating successor is always a new state (its `viol` differs), so every edge is checked, as in the explorer. Defined state, sequence sanity and acknowledgement coherence are ordinary TLC state invariants; the explorer checks them only on the station that moved, which is equivalent because the other station did not change. The step record `last` (move, station, frame, transition id) is left out of the `VIEW`, so TLC's distinct-state count is the explorer's.
2. **Deadlock** is TLC's own deadlock check. A quiescent state has a stuttering `Rest` step, so a TLC deadlock is exactly the explorer's (a terminal state that is not quiescent). It runs after the safety run, in its own TLC run, as the explorer checks deadlock only after the whole search.
3. **Liveness.** The explorer checks "from every reachable state a fault-free path reaches quiescence" on its graph (AG EF over the non-fault edges; timer-free progress also leaves out T1 edges). TLC checks LTL, so it is stated as `[]<>Quiescent` under weak fairness of the disjunction of the non-fault moves. This is sound in one direction: if the explorer's property fails, some fair behaviour circles in a region with no path to quiescence, so TLC fails too. A clean TLC verdict therefore implies the explorer's. The converse does not hold: a TLC counterexample could be a fair cycle that ignores a move which would have led out. So TLC's liveness counterexamples are compared with the explorer's verdict, not trusted alone. Two TLC costs forced this shape. Strong fairness per move (which would be tighter) is expanded by TLC into 2^n cases and did not finish on 42 states in ten minutes. And `WF_vars(A)` needs TLC's `ENABLED`, which evaluates without caching and did not finish on 3 states. So the fairness is written out: enabledness is the moves' own guards (`CanDeliver` and so on), and "a progress move was taken" is read off the step record. The argument needs every progress move to be a real (non-stuttering) step: a move that changed nothing would count as stuttering, and the fairness could be discharged falsely, so TLC could report clean where the explorer reports a violation (an independent review found this gap). The step record therefore carries a tick that flips on every move, so no move is ever a stutter.

## Decisions beyond the explorer's pinned semantics

The explorer's register (`docs/explorer.md`, "Pinned delegated semantics") applies unchanged. These are the choices the TLA+ model had to make that the explorer does not:

- **Unmodelled write-only state.** N1 and the symbolic timer registers (SRT, T1V, T2, Next T1) are not in the TLA+ state, as they are not in the explorer's state fingerprint; their verbs change nothing. No guard reads them.
- **Maps as sets of pairs.** The receive buffer and the retained send history are sets of `<<N(s), label>>` pairs, so equal maps are equal TLA+ values whatever order built them.
- **Subroutine call depth.** The interpreter refuses a call chain deeper than 16; the TLA+ runner does not count depth. The tables have no recursive subroutine call, so the limit is never reached in either.
- **Liveness fairness**, as above: weak fairness on the disjunction of non-fault moves, stated without `ENABLED`. Sound for a clean verdict; a counterexample is compared with the explorer's.
- **Depth bound.** The one calibration row that relies on the explorer's depth bound (`d42-both-off`, the #42 runaway, marked `DepthBound` in `Scenarios.cs`) uses a TLC state constraint `TLCGet("level") <= MaxDepth`; the distinct-state counts match, which confirms the two bounds cut the graph at the same place.
- **Error text.** Interpreter error messages are reproduced closely but not byte for byte; the cross-check compares the error's kind and station, not its text.

## Calibration: explorer against TLC

Every row of the explorer's calibration suite (`CalibrationTests.cs`), plus its controls. "Agree" means: the same verdict and invariant; for a safety or deadlock counterexample, the same station, the same length and the same transition at every step; for a clean or depth-bounded run, the same number of distinct states, and for a clean run also the same set of table transitions fired across the whole search; for a liveness verdict, the same verdict (TLC's counterexample is a lasso, not the explorer's stuck state). TLC 2.19, one worker per run.

| Scenario | Defect | Tables | Explorer | TLC | Agree |
| --- | --- | --- | --- | --- | --- |
| d40-pre40-srej | #40 | pre-40 | RejectCoherence at A, 4 steps | RejectCoherence at A, 4 steps (5.3 s) | yes |
| d40-pre40-nosrej | #40 | pre-40 | RejectCoherence at A, 4 steps | RejectCoherence at A, 4 steps (5.3 s) | yes |
| d40-current-srej | #40 fixed | current | clean, 47 states | clean, 47 states (10.3 s) | yes |
| d40-current-nosrej | #40 fixed | current | clean, 47 states | clean, 47 states (10.4 s) | yes |
| d40-pre40-rej-eq-vs | #40 | pre-40 | MachineError at A, 5 steps | MachineError at A, 5 steps (5.1 s) | yes |
| d38-pre38 | #38 | pre-38 | MachineError at A, 15 steps | MachineError at A, 15 steps (8.1 s) | yes |
| d38-pre40 | #38 | pre-40 | MachineError at A, 15 steps | MachineError at A, 15 steps (7.6 s) | yes |
| d42 | #42 | current | RejectCoherence at B, 6 steps | RejectCoherence at B, 6 steps (4.6 s) | yes |
| d42-rc-off | #42 | current | SequenceSanity at B, 16 steps | SequenceSanity at B, 16 steps (6.5 s) | yes |
| d42-both-off | #42 | current | depth bound hit, 967 states | depth bound hit, 967 states (8.8 s) | yes |
| d47-rc-off | #47 | current | Delivery at B, 20 steps | Delivery at B, 20 steps (12.2 s) | yes |
| d47-k2 | #47 | current | Delivery at A, 24 steps | Delivery at A, 24 steps (12.8 s) | yes |
| d47-rc-on | #47/#42 | current | RejectCoherence at B, 16 steps | RejectCoherence at B, 16 steps (10.9 s) | yes |
| d9 | #9 | current | Deadlock, 30 steps | Deadlock, 30 steps (10.5 s) | yes |
| d13-k7 | #13 | current | RejectCoherence at A, 13 steps | RejectCoherence at A, 13 steps (5.9 s) | yes |
| d13-k4 | #13 control | current | clean, 1144 states | clean, 1144 states (29.1 s) | yes |
| d44-48-cold | #44/#48 | current | Deadlock, 2 steps | Deadlock, 2 steps (7.1 s) | yes |
| d44-48-cold-mod8 | #44/#48 control | current | clean, 7 states | clean, 7 states (9.2 s) | yes |
| d43 | #43 | current | BusyTracksFlow at B, 3 steps | BusyTracksFlow at B, 3 steps (4.9 s) | yes |
| d43-busy-off | #43 | current | FlowOffDelivery at B, 5 steps | FlowOffDelivery at B, 5 steps (4.5 s) | yes |
| d43-both-off | #43 | current | clean, 42 states | clean, 42 states (11.1 s) | yes |
| d45-cold-timer-free | #44 masks #45 | current | SelectiveProgress, 2 steps | SelectiveProgress, lasso of 4 states (10.0 s) | yes |
| d45-cold | #44 masks #45 | current | clean, 6 states | clean, 6 states (8.0 s) | yes |
| d45-v22-timer-free | #45 | current | SelectiveProgress, 4 steps | SelectiveProgress, lasso of 6 states (10.5 s) | yes |
| d45-v22 | #45 | current | clean, 8 states | clean, 8 states (7.2 s) | yes |
| d48-v22-dm | #48 | current | Deadlock, 2 steps | Deadlock, 2 steps (6.4 s) | yes |
| d48-v22-tables | #48 | current | Deadlock, 2 steps | Deadlock, 2 steps (6.5 s) | yes |
| d48-cold-dm | #48 | current | Deadlock, 2 steps | Deadlock, 2 steps (5.9 s) | yes |
| v20-mod8-frmr | #45/#48 control | current | clean, 3 states | clean, 3 states (11.1 s) | yes |
| v20-mod8-dm | #45/#48 control | current | clean, 3 states | clean, 3 states (11.1 s) | yes |
| v22-peer-ua | #45/#48 control | current | clean, 3 states | clean, 3 states (9.7 s) | yes |
| h1 | H1 | current | Deadlock, 19 steps | Deadlock, 19 steps (6.7 s) | yes |
| h1-tr-quiescent | H1 control | current | clean, 806 states | clean, 806 states (7.6 s) | yes |
| seeded-ack | seeded (AckCoherence) | seeded ack-broken | AckCoherence at A, 4 steps | AckCoherence at A, 4 steps (1.9 s) | yes |
| seeded-livelock | seeded (Quiescence) | seeded livelock | Quiescence, 0 steps | Quiescence, lasso of 14 states (3.7 s) | yes |

Times are TLC's own (all phases of the row); the whole suite (35 rows, with the coverage reruns) takes about 70 s wall time at `--parallel 6`. Run on 23 October 2026, after rebasing onto ax25sdl#94 (ax25spec pin 3027400). Reading the rows against `docs/explorer.md`'s calibration table:

- **Fires where the explorer fires:** #40 on the pre-40 fixture (both SREJ settings, and the phantom retransmission with the REJ = V(s) exemption), #42 (and its runaway behind reject coherence, and the depth-bound witness), #47 (all three variants), #9, #13 at k = 7, #44/#48 from a cold connect, #43 (both symptoms), #45 (from figc4.6 and masked by #44 from a cold connect, both through the timer-free check), #48 (stub and table-driven peer, figc4.6 and figc4.2), and the H1 deadlock.
- **Clean where the explorer is clean:** #40 on the current tables (the fixed side of the only pre/post pair), #13 at k = 4, the #43 scenario behind both flow checks, the lenient #45 runs, every connect-phase control, and H1 with TimerRecovery counted as quiescent.
- **#38** is the explorer's documented limit and stays one: with reject coherence off, both fixtures end in the same machine error (a retransmission of I(ns=3), a frame never sent: the H2 do-while) at the same 15 steps, in both tools.
- **Liveness rows:** TLC's lasso for `d45-v22-timer-free` is FRMR, `t14_frmr_received` (which resends SABME), FRMR, `t06_all_other_primitives__from_lower_layer`, then stuck: the explorer's four-step #45 signature.
- **Seeded rows:** `seeded-ack` and `seeded-livelock` run on the deliberately broken fixtures and exist only to show that acknowledgement coherence and quiescence can fire at all; nothing in the real tables makes them fire.
- **Not in the table:** #54 and #41 (outside the explorer's model too: traces and numeric timers), and the interpreter-level H3 note, which the explorer pins as a unit test rather than an exploration.

**Do the comparisons have teeth? Partly, and the coverage says where not.** Three kinds of evidence:

- *Coverage.* Across the 28 rows on the current tables, the searches fire only part of the tables. TlaCheck reports it at the end of every run:

```
Arm coverage over the current tables, across the 28 scenario(s) run on them: 63 of 279 rows fired (23%).
  transitions: TLC 46 of 247, explorer 46 of 247 (the same set)
  subroutine paths: TLC 17 of 32 (the explorer does not report paths)
```

  The two tools fire exactly the same set of transitions, and on every clean row the same set row by row. The remaining 77% of rows (201 transitions, 15 subroutine paths) are never reached by any scenario, so neither tool says anything about them: a reviewer deleted two arms (`Connected/t21_rr_received_no_no`, `TimerRecovery/t22_i_received_yes_yes_yes_no_no_yes_no_yes_yes`) and swapped a guard between the two `t19_rnr_received_yes_yes_*` arms, and neither tool noticed, because no scenario reaches them. All three are in TlaCheck's never-fired list. Most of the unfired rows are inputs the explorer never injects (layer-3 primitives other than data and flow control, UI frames, the error events, DISC and DM in the data phase, T3) and the busy and peer-busy columns behind #43. Closing that gap is its own piece of verification work, not part of this change.
- *Seeded table defects.* Acknowledgement coherence and quiescence fire on no real defect, so a reviewer could set either to TRUE and nothing failed. The two seeded rows now fail in that case; this was checked by setting both to TRUE and running them (`seeded-ack` became a TLC deadlock, `seeded-livelock` clean, both reported as disagreements).
- *A seeded semantic slip.* Making the semantics' `V(r) := V(r) - 1` a no-op (silently "fixing" #47 in TLA+ only) was reported by `d47-rc-off` as a disagreement at step 16 of the transition sequence; `d47-k2` and `d13-k4` still agreed. Verdicts and state counts alone would not have caught it. One mutation is a spot check.

## Wider: the explorer's grid

`--grid` runs the explorer's data-phase grid (`docs/explorer.md`, "Grid"): frames 0..3 each way, budget 0..2, k in {2, 4}, SREJ off and on, faults drop, dup and drop+dup, all invariants on: 576 cells, each compared exactly as a calibration row.

All 576 agree. Rerun on 23 October 2026 after the review fixes and the rebase onto ax25sdl#94, with `--grid --parallel 8 --workers 1 --heap 1500m`; the first pass ran out of memory in the runner after 542 cells (reading a 1.2 GB coverage dump whole, since fixed), and the other 34 were rerun with `--parallel 4 --heap 3g`. About 2.7 hours of wall time in all, most of it the coverage reruns of the largest cells. The verdicts are the explorer document's own: 460 clean, 66 deadlocks (all H1), 45 reject-coherence hits (all the #42 signature), 5 delivery duplicates (all #47 at k = 2). Every clean cell has the same number of distinct states and the same table transitions fired in both tools (the largest clean cell is 66 641 states), and every violating cell the same station, length and transition sequence. The largest cell (3+3 frames, budget 2, k = 4, drop+dup) is 602 754 distinct states; TLC takes about ten minutes per run on it with one worker, against the explorer's 20 seconds, and finds the same 26-step H1 deadlock. (A first run on 21 October, before the review fixes, also agreed on all 576.)

The grid does not change the coverage picture: it varies frame counts, faults, k and SREJ, not the inputs, so it reaches the same kinds of arm.

Not yet run: the flow-control grid (`--grid-flow`) and the v2.0 stub-peer grid.

## Not covered yet

- 77% of the table rows: see the coverage above.
- The Management Data Link (C5) and the other Annex C machines: the emitter renders only machine `data_link`, and the harness drives only Data Link stations.
- Anything the explorer's envelope leaves out (docs/explorer.md): numeric timers, T3, data arriving after recovery has begun, premature T1, modulo 128 data phase. The TLA+ model shares the envelope, by design, so that the two can be compared; widening it is the next step for both.
- Liveness under strong (per-move) fairness, which would make TLC's liveness verdict exact rather than sound in one direction, is out of reach of TLC at these sizes.
- The cross-check runs locally; it is not in CI, because the runners' Java and `tla2tools.jar` have not been checked. The drift test is in `dotnet test`.

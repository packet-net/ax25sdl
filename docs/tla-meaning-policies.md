# The default meaning's own choices (withheld from the independent author)

**Do not give this file to the author of an independent meaning module** (ax25spec#116 Phase 2, Step B). It records what `spec/tla/Ax25Meaning.tla`, the port of the reference interpreter (`codegen/src/Packet.Sdl.Interpreter/Machine.cs`), chose where the interface (`spec/tla/MeaningInterface.md`) leaves the choice to the meaning. It is the checklist for Step D: when the lockstep check of two meanings disagrees, look here first for the choice behind it, then decide which reading the spec text supports.

The pinned-semantics numbers refer to `docs/explorer.md`, "Pinned delegated semantics"; `docs/golden-traces.md`, "Interpretation policies", states the interpreter's readings the same way. Both are the interpreter's written-up readings, so they are withheld too.

## Policies

1. **Retransmission by pre-emption** (pinned 1). The meaning keeps a copy of every I frame it sends (`ret`, keyed by N(s)), pruned to `[V(a), V(s))` after every dispatch (`AfterTransition`, and inside `Preempt`). "Push Old I Frame onto Queue" re-queues the retained copy of I(V(s)) (the rewound V(s) inside `Invoke_Retransmission`'s loop); "Push Old I Frame N(r) on Queue" that of I(N(r)). Such an entry popping in `Connected` or `TimerRecovery` while Peer Receiver Busy is clear is handled by `Preempt`, not the table: the I frame is re-sent with its original N(s), N(r) = V(r), P = 0, Acknowledge Pending clears, and T1 starts (T3 stops) if T1 is not running; `tid` is `pinned:retransmit_old_i_frame`. A re-queued entry with no retained copy is an error, not a skip. While the peer is busy the entry takes the table's own pop arm, which pushes it back.
2. **Pending frame registers** (pinned 3). P, F, N(r), N(s) are per-dispatch registers (`pP pF pNr pNs`), reset by `Begin` (P and F to 0, N(r) and N(s) unset). A frame verb that needs an unset N(r) or N(s) is an error: the table omitted the staging box. Bare "UA", "DM", "Expedited UA", "Expedited DM", "RR", "RNR", "REJ", "SREJ" take F from the pending F; bare "SABM", "SABME", "UI Command", "XID_command", "RR Command", "RNR Command", "I Command" take P from the pending P; spellings that name the bit ("DM (F = 1)", "SABM (P == 1)", "RR Command (P = 0)", ...) use it.
3. **The queue** (pinned 3). The I frame queue is a FIFO of entries `[lbl, old, data]`. A push during `DL_DATA_request` joins the tail; every other push lands at the head, in the dispatch's own order (`qi`). A popped entry pushed back (no event data, an entry popped) returns unchanged, keeping its original N(s). A push with no label anywhere gets the label `"frame"`, and so does an I frame with no popped or event label.
4. **The pop** is the meaning's `Arrive`: `I_frame_pops_off_queue` takes the head into `pop` (`hasPop`) before the table runs; an empty queue pops nothing and the table runs anyway.
5. **The receive buffer** (pinned 3). "Save Contents of I Frame" stores the frame keyed by N(s) (label: the frame's, else `I(ns=n)`); "Discard Contents of I Frame" is a no-op, so on the SREJ-enabled REJ arm that draws Save then Discard the copy stays. "Retrieve Stored V(r) I Frame" removes the V(r) entry and makes its label the one the next `DL_DATA_indication` carries; retrieving a missing entry is an error.
6. **Delivery labels** (pinned 4, 16). `DL_DATA_indication` carries `dd`, which `Begin` sets to the event's data and Retrieve overwrites. `DL_UNIT_DATA_indication` carries the event's data; "UI Command" carries the DL-UNIT-DATA Request's label.
7. **Environment atoms** (pinned 3). `able_to_establish` and the N1 atom are read from the event (default TRUE).
8. **Timers** (pinned 8). Symbolic: Start/Stop set running/stopped; `Arrive` marks a timer expired on its expiry event, and an expiry of a timer that is not running is an error. `T1_expired` and `T1_running` read the state.
9. **Version and modulus** (pinned 9). "Set Version 2.2" and "set_version_2_0" set the flag only; the modulus changes only on "Modulo := 8" and "Modulo := 128" (the pages never assign it, pinned 9).
10. **Window atoms.** `vr_lt_ns_lt_vr_plus_k` is the open interval V(r) < N(s) < V(r) + k in modular arithmetic with k unclamped (golden traces, ax25spec#40). `ns_gt_vr_plus_1` and `va_le_nr_le_vs` are modular distances. `vs_eq_va_plus_k` is `vs = (va + k) % mod`.
11. **Not modelled.** N1 and the timer registers SRT, T1V, T2 and Next T1: their verbs change nothing, and so do "Discard I Frame", "Discard Primitive", "Backtrack" and "MDL-NEGOTIATE Request".

## Smaller readings worth checking in triage

- **Role of a frame when the event does not say**: SABM, SABME, DISC and I are commands, UA, DM and FRMR responses (`Cmd`). Frames always carry `cmd`, so this fallback is not exercised.
- **`vs_eq_nr`** is the Resolver's stale-read substitution, V(s) = the received N(r).
- **`F_eq_1_and_frame_eq_RR_or_frame_eq_RNR_or_frame_eq_I`** (Enquiry Response's head decision) reads the pending F the runner staged and the received frame's type.
- **RC** is unbounded ("RC := RC + 1" never wraps); `RC_eq_N2` compares with `n2`.
- **Atom errors**: an atom that needs `pf`, `cmd`, `nr` or `ns` the event does not set is an error, with the interpreter's short-circuits (the compound role atoms consult P/F only when the role matches); `vs_eq_X` before "X := V(s)" is an error; `RC_eq_NM201` belongs to the management machine.
- **`V(r) := V(r) - 1`** wraps modulo the modulus.

## This module's own fields and scratch

Station record, beyond the fixed fields: `hd` (half duplex), `peer` (Peer Receiver Busy), `rej` (Reject Exception), `ret` (the retained send history, a set of `<<N(s), label>>` pairs).

Per-dispatch scratch: `pP pF pNr pNs` (pending P, F, N(r), N(s)), `x` (figc4.7's X), `qi` (where the next head push goes), `dd` (the label the next `DL_DATA_indication` carries), `hasPop` and `pop` (the popped queue head).

## How the operators are used here

- `Arrive`: timer expiry, then the pop.
- `Preempts` / `Preempt`: retransmission of a re-queued old frame (policy 1).
- `AfterTransition`: pruning the send history.

## For Step C and Step D

- **Do not compare `tid`.** `Preempt` names its own transition, and two meanings that pre-empt differently, or not at all, differ there trivially. Compare effects (`fx`) and the fixed station fields.
- **Local busy is the harness's.** `TwoStation.tla`'s `LocalBusy` (mirroring the explorer's `Stepper.LocalBusy`) sets Own Receiver Busy, Acknowledge Pending and the timers and puts the RNR or RR on the air itself; no table row and no meaning operator runs. A lockstep check cannot see a difference between meanings there, so the entry and exit of a local busy condition are outside Step D's scope; only what the tables do afterwards is compared.
- **Extra fields** in either meaning show as different distinct-state counts on clean rows against the explorer; that alone is not a disagreement.

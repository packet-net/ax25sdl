# Where the two meanings differ (Step C)

The existing meaning (`spec/tla/Ax25Meaning.tla`, A, a port of the reference interpreter) and the independent one (`spec/tla/Ax25MeaningIndependent.tla`, B, written from the spec text alone) were run side by side on every calibration row with `TlaCheck --lockstep` (docs/tla.md, "Lockstep"). A drives the system and B shadows it; after every dispatch the lockstep compares the error flag, the effects (frames with all their fields, primitives to layer 3 with their labels, LM requests), the fixed station fields and the change in queue length. This file lists every distinct difference found, with a preliminary class:

- **(a)** the existing meaning misreads the spec;
- **(b)** the independent meaning misreads the spec;
- **(c)** the spec is genuinely ambiguous or silent, or its figure and prose disagree;
- **(d)** an artefact of the harness or the interface.

The classes are preliminary. Step D (a second reader against the 2025 figures, the 1998 edition, the GraphML and the prose) decides. Silent-reading ids (S1 ...) and conflict ids (K1 ...) are the independent author's, in `silent.md` and `conflicts.md`; policy numbers are those of `docs/tla-meaning-policies.md`.

## Counts

- Rows run in lockstep: 188 (the whole calibration suite, CI run of commit f63a315).
- Rows on which the two meanings never differed: 45.
- Rows with at least one difference: 141 (two of them, `d54-cold-v22-wrap` and `d54-cold-v20-wrap`, then time out; see D6).
- Distinct signatures found: 38, which come down to six distinct differences, D1 to D6 below.
- Explorer against TLC on the independent meaning (the plain cross-check): 112 rows agree, 76 do not (listed at the end): on 54 the verdict changes, on 22 it is the same verdict with a different counterexample length, trace or state count (a state count alone can come from the meanings' different private fields, which is not a behavioural difference).

## How the list was found

Lockstep stops at the first difference per row. Each row was run up to eight times, each run setting aside the differences already found (by signature: the station's state, the event and the components that differ) and resetting B's fixed fields to A's after each one, so that the search could go on. Two families dominate: every row that brings a link up runs into D1, and every row that retransmits runs into D2 or D3, so eight runs were not enough to see past them on most rows. To look behind them, a throwaway branch (not part of this PR, deleted after) set both families aside wholesale (any difference on an `I_frame_pops_off_queue` dispatch, and differences confined to `mod k n2 srej` on SABM, SABME, FRMR or UA receipt) and ran every row again with up to fourteen runs: 174 of 188 rows then showed no difference at all, and the remaining 14 signatures are D4, D5 and the queue-length artefacts of D6. So, as far as the calibration suite reaches, there is nothing else behind D1 to D3, except possibly on pops (which that run did not compare) and on the two d54 rows, which time out (see D6).

## The differences

| | Difference | Rows (committed run) | Preliminary class |
| --- | --- | --- | --- |
| D1 | "Set Version 2.0/2.2" boxes: B also sets modulus, k, N2 and the reject mode (figc4.7b's expansion); A sets only the version flag | 88 | (c) |
| D2 | Retransmission: B renumbers retransmitted I frames through the table's fresh-frame path; A re-sends them with their original N(s), and does so even when the window is full | 75 | (c) |
| D3 | A popped I frame pushed back (peer busy, window full): B appends it at the tail of the queue, A returns it to the head | 26 | (c) |
| D4 | DL-FLOW-OFF / DL-FLOW-ON Request with no "N(r) := V(r)" box (H3): A fails the dispatch, B sends with N(r) = V(r) | 1 | (c) |
| D5 | REJ whose N(r) names a frame never sent: B fails on the REJ, A one dispatch later on the pop | 4 | (d) |
| D6 | Queue-length differences after a difference was set aside | 7 (and the two d54 timeouts) | (d) |

### D1. What "Set Version 2.0" and "Set Version 2.2" set

- **First counterexample.** Row `d44-48-cold-mod8`, 1 step: station B, Disconnected, receives A's SABM (P = 1, command) and takes `t13_sabm_received_yes` in both meanings. A: k 4, N2 4 (the harness's seed, kept). B: k 8, N2 10. On a SABME (row `cold-v22-data`, 1 step, `t14_sabme_received_yes`): A mod 8, k 4, N2 4, SREJ off; B mod 128, k 32, N2 10, SREJ on. A modulo-128 station receiving SABM (row `reconnect-cold-mod128`, 4 steps, Connected, `t14_sabm_received_yes`): A keeps mod 128, B goes to mod 8. An FRMR in AwaitingV22Connection (row `d45-v22-timer-free`, 2 steps, `t14_frmr_received`, the v2.0 fallback): A mod 128, k 4, N2 4; B mod 8, k 8, N2 10.
- **Signatures.** Every `... / SABM_received / k, n2[, srej][, mod]`, `... / SABME_received / mod, k, n2[, srej]` and `... / FRMR_received / [mod, ]k, n2` in Disconnected, Connected, TimerRecovery and AwaitingV22Connection.
- **Verbs.** `set_version_2_0`, `Set Version 2.2` (processing boxes on the pages).
- **Independent.** S11: the boxes mean figc4.7b's subroutines of the same names, so they set the version flag, implicit or selective reject, the modulus, k and N2 (and the unmodelled N1, T2, duplex), overwriting the harness's seed. K2: figc4.7b's Set Version 2.0 draws k := 8 with modulo 8, against 6.3.2 and 6.7.2.3 (seven outstanding), and the figure was followed. Interface concern 4 flagged the seed/expansion clash.
- **Existing.** Policy 9 (pinned 9): the boxes set the flag only; the modulus changes only on "Modulo := 8" / "Modulo := 128", which the pages never draw.
- **Preliminary class: (c).** The pages draw the name of a figc4.7b subroutine as a plain processing box; the spec does not say whether that is a call. If it is, A misreads (a) by not setting the modulus, k and N2; but B's literal reading then carries K2's k := 8 into modulo 8, and with S8 (V(s) = V(a) + k modulo 8 holds whenever V(s) = V(a)) a v2.0 link can never send an I frame: the plain cross-check on B shows it (`cold-v20-data`: Deadlock, 11 steps, against clean). So figc4.7b's k := 8 is itself a figure defect worth a look in Step D.

### D2. How a retransmitted I frame is numbered

- **First counterexample.** Row `inj-srej-tr`, 4 steps: 1. pop at A (`t03_i_frame_pops_off_queue_no_no_no`, I(ns=0) sent), 2. the harness injects an SREJ command at B, 3. A receives it (`t24_srej_received_yes_no`, Push Old I Frame N(r) on Queue), 4. A pops the re-queued frame in Connected. A: I command N(s) 0, N(r) 0, label a0, V(s) stays 1 (pre-emption, `pinned:retransmit_old_i_frame`). B: I command N(s) 1, label a0, V(s) 2 (`t03_i_frame_pops_off_queue_no_no_yes`, "N(s) := V(s)").
- **With the window full** (row `d47-k2`, 6 steps, after an SREJ in Connected; and `ro-k1`, 6 steps, TimerRecovery): A re-sends the old frame anyway and clears Acknowledge Pending; B takes the window-full arm (`t03_i_frame_pops_off_queue_no_yes`) and pushes the frame back, sending nothing. Signatures `... / I_frame_pops_off_queue / fx, q length` and `fx, ack, q length`.
- **Signatures.** `Connected / I_frame_pops_off_queue / fx, vs`, `TimerRecovery / I_frame_pops_off_queue / fx, vs` (with `q length` when D3 also applies), and the window-full ones above.
- **Verbs.** "Push Old I Frame onto Queue" (figc4.7a Invoke Retransmission), "Push Old I Frame N(r) on Queue" (figc4.4c, figc4.5 SREJ), and the pop page's "N(s) := V(s)" / "I Command".
- **Independent.** K4: the figure rewinds V(s) to N(r), pushes the old frames and steps V(s) back up to X; the pop path then sends each with "N(s) <- V(s)", so retransmitted data goes out under new numbers; followed the figure against 6.4.7 and 4.4.3 ("retransmits starting with the frame numbered N(r)"). S3 (pushes append at the tail) and S4 (a push of a number never sent fails) shape the rest.
- **Existing.** Policy 1 (pinned 1): a re-queued old frame popping in Connected or TimerRecovery with the peer not busy is pre-empted: re-sent with its original N(s), N(r) = V(r), P = 0, Acknowledge Pending cleared, T1 started; no window check. The `Preempts`/`Preempt` escape hatch exists for this.
- **Preliminary class: (c)**, figure against prose. The prose (6.4.7, 6.4.8, 4.4.3) asks for the old frame under its own number; the figures, read literally, renumber. B's reading makes a retransmission after REJ or SREJ arrive as a new frame the receiver cannot place, and with the window full it cannot retransmit at all: the plain cross-check on B fails many retransmitting rows (for example `ip-tr-inseq`: Deadlock, 23 steps, against clean). A's reading needs a mechanism the figures do not draw.

### D3. Where a popped frame pushed back goes

- **First counterexample.** Row `busy-b`, 6 steps: 1. B enters local busy, 2. A receives RNR (`t22_rnr_received_yes`), 3. A pops a0 and, the peer busy, pushes it back (`t03_i_frame_pops_off_queue_yes`), 4. B leaves local busy, 5. A receives RR (`t21_rr_received_yes`), 6. A pops in Connected (`t03_i_frame_pops_off_queue_no_no_no` in both). A: I N(s) 0 label a0. B: I N(s) 0 label a1: B's step 3 put a0 behind a1.
- **Signatures.** `Connected / I_frame_pops_off_queue / fx`, `TimerRecovery / I_frame_pops_off_queue / fx` (same arm in both meanings, a different label).
- **Verbs.** "Push on I Frame Queue", "Push I Frame on I Queue" (internal_out) on the pop pages' peer-busy and window-full arms.
- **Independent.** S3: C4.3 says every queue is first-in first-out, and no text says a returned item goes to the front, so every push appends; the author flagged this as the reading most likely to differ.
- **Existing.** Policy 3 (pinned 3): pushes during DL-DATA Request join the tail; every other push lands at the head, in the dispatch's order; a popped entry pushed back returns unchanged.
- **Preliminary class: (c)**, silent. Consequence of B's reading: layer 3 data is reordered whenever a pop is pushed back with more than one frame queued, and the plain cross-check on B reports it as a delivery failure (`busy-b`: Delivery at B, 7 steps, against clean). Step D should weigh that against C4.3's literal FIFO.

### D4. DL-FLOW-OFF and DL-FLOW-ON with no N(r) staging (H3)

- **First counterexample.** Row `h3-busy-flow-off`, 4 steps: 1. pop at A, 2. B receives the I frame, 3. B enters local busy, 4. B's layer 3 issues DL-FLOW-OFF Request in Connected. A fails: "'RNR Response' emitted without a preceding N(r) staging assignment: the table omits 'N(r) := ...' on this path". B takes `t05_dl_flow_off_request_yes` and sends RNR with N(r) = V(r). Then (5 steps) DL-FLOW-ON Request: A fails on "RR Command", B takes `t06_dl_flow_on_request_yes_no`.
- **Verbs.** "RNR Response", "RR Command" with no "N(r) := V(r)" box before them on figc4.4's flow-control arms.
- **Independent.** S16: an unassigned outgoing N(R) is V(r) at the moment of sending (4.2.2.5).
- **Existing.** Policy 2 (pinned 3): an S frame with no staged N(r) is an error (the table omitted the box). The row exists to show that defect (H3 in docs/explorer.md).
- **Preliminary class: (c).** The figure omits the box; 4.2.2.5 supplies the value. A errs on purpose to surface the omission, B fills it from the prose; neither misreads, but they disagree on what an omission means.

### D5. A REJ naming a frame never sent

- **First counterexample.** Row `d40-pre40-rej-eq-vs` (pre-#40 tables), 4 steps: 1. A pops a0, 2. the channel duplicates it to B, 3. B, with a0 already received, sends REJ N(r) 1, 4. A receives REJ N(r) = 1 = V(s) in Connected. B fails: "Push Old I Frame: no sent I frame with N(s) = 1". A takes `t25_rej_received_yes`, queues an entry with no copy, and fails on the next pop (5 steps, `Connected / I_frame_pops_off_queue / error`): "retransmission of I(ns=1) requested, but this station holds no copy of that frame".
- **Rows.** `d40-pre40-rej-eq-vs`, `d38-pre38`, `d38-pre40`, `busy-b-ro` (the last on the current tables, through reordering).
- **Independent.** S4. **Existing.** Policy 1 ("a re-queued entry with no retained copy is an error, not a skip").
- **Preliminary class: (d).** Both meanings refuse the same thing (a REJ that "V(a) <= N(r) <= V(s)" lets through for N(r) = V(s)); they fail at different dispatches only because one checks when pushing and the other when popping. The spec question behind it (#40, REJ N(r) = V(s)) is already known.

### D6. Queue-length artefacts

- Seen once D2 or D3 has been set aside: B's queue keeps entries A's has already sent, and a later dispatch that empties the queue (DISC, DM, DL-DISCONNECT Request, a SABM reset, an N(r) error) removes a different number of entries in each. Signatures such as `Connected / DISC_received / q length` and `TimerRecovery / RR_received / q length` ("A 0 to 0, B 1 to 0").
- The same divergence makes B's queue grow without bound on `d54-cold-v22-wrap` and `d54-cold-v20-wrap`, which then time out after D1 and D2 are found.
- **Preliminary class: (d).** Consequences of setting a difference aside, not differences of their own.

## Explorer against TLC on the independent meaning

The plain cross-check with `--meaning Ax25MeaningIndependent` compares the explorer (the reference interpreter, A's semantics) with TLC on B, so every row whose verdict moves is a behavioural consequence of D1 to D5. Examples traced by hand: `cold-v20-data` (D1 with K2 and S8: no I frame can be sent), `ip-tr-inseq` (D2: the receiver cannot place a renumbered retransmission and the sender gives up), `busy-b` (D3: data delivered out of order). Attributing every row is Step D's work.

| Scenario | Explorer (the interpreter, A's semantics) | TLC on the independent meaning |
| --- | --- | --- |
| d40-pre40-rej-eq-vs | MachineError at A, 5 steps | MachineError at A, 4 steps |
| d38-pre38 | MachineError at A, 15 steps | Quiescence, lasso of 27 states |
| d38-pre40 | MachineError at A, 15 steps | Deadlock, 31 steps |
| d42-rc-off | SequenceSanity at B, 16 steps | Deadlock, 31 steps |
| d42-both-off | depth bound hit, 967 states | Deadlock, 31 steps |
| d47-rc-off | Delivery at B, 20 steps | Deadlock, 28 steps |
| d47-k2 | Delivery at A, 24 steps | Deadlock, 25 steps |
| d47-rc-on | RejectCoherence at B, 16 steps | RejectCoherence at B, 7 steps |
| d9 | Deadlock, 30 steps | Quiescence, lasso of 12 states |
| d13-k7 | RejectCoherence at A, 13 steps | Deadlock, 34 steps |
| d13-k4 | clean, 1144 states | Deadlock, 28 steps |
| cold-v20-data | clean, 210 states | Deadlock, 11 steps |
| d54-cold-v22-wrap | Deadlock, 45 steps | Delivery at B, 12 steps |
| d54-cold-v20-wrap | clean, 192 states | Delivery at B, 12 steps |
| h1 | Deadlock, 19 steps | Deadlock, 27 steps |
| h1-tr-quiescent | clean, 806 states | Deadlock, 27 steps |
| d114-cold-dup-ua | Deadlock, 7 steps | RejectCoherence at A, 7 steps |
| d44-48-cold-faults | Deadlock, 2 steps | Deadlock, 2 steps |
| h1-k1 | Deadlock, 15 steps | Deadlock, 18 steps |
| h1-k2-ab3 | Deadlock, 17 steps | Deadlock, 25 steps |
| release-a-tr | clean, 2890 states | Deadlock, 18 steps |
| gu-loss | GiveUpLoss, 6 steps | GiveUpLoss, 6 steps |
| d9-give-up | Deadlock, 30 steps | Quiescence, lasso of 19 states |
| d9-n1-release | Deadlock, 11 steps | Deadlock, 14 steps |
| k7-srej-rc-off | clean, 149 states | Deadlock, 35 steps |
| ro-k1 | clean, 245 states | Deadlock, 14 steps |
| ro-k1-srej | clean, 245 states | Deadlock, 14 steps |
| ro-srej-rc-off | clean, 60 states | Delivery at B, 10 steps |
| ro-stale-i | Deadlock, 9 steps | RejectCoherence at B, 13 steps |
| ro-reset-dup | Delivery at B, 18 steps | Deadlock, 11 steps |
| ro-d42 | SequenceSanity at B, 16 steps | Delivery at B, 10 steps |
| l3-down-mod128 | Deadlock, 7 steps | RejectCoherence at A, 9 steps |
| l3-down-mod128-b3 | Deadlock, 7 steps | RejectCoherence at A, 9 steps |
| reconnect-both-cold | clean, 1139 states | clean, 1332 states |
| reconnect-tr | Deadlock, 12 steps | Deadlock, 12 steps |
| reconnect-cold-mod128 | Deadlock, 10 steps | Deadlock, 8 steps |
| seeded-livelock | Quiescence, 0 steps | timeout: no verdict within 600 s |
| seeded-dm-ignored | Deadlock, 6 steps | Deadlock, 6 steps |
| busy-b | clean, 1069 states | Delivery at B, 7 steps |
| busy-a-ba2 | clean, 2369 states | Delivery at A, 7 steps |
| busy-both-trq | clean, 20221 states | Delivery at B, 13 steps |
| busy-b-b1 | Deadlock, 21 steps | Delivery at B, 7 steps |
| busy-b-long | Deadlock, 18 steps | Delivery at B, 7 steps |
| busy-b-ro | MachineError at A, 16 steps | Delivery at B, 7 steps |
| h3-busy-flow-off | MachineError at B, 4 steps | Delivery at B, 7 steps |
| d43-tr | clean, 1170 states | Deadlock, 28 steps |
| seeded-busy-says-rr | BusyIndication at B, 4 steps | BusyIndication at B, 6 steps |
| busy-a-enq | clean, 222 states | Deadlock, 23 steps |
| t3-data | clean, 758 states | Deadlock, 28 steps |
| t3-h1-k1 | Quiescence, 8 steps | Deadlock, 18 steps |
| ui-tr | clean, 214 states | Deadlock, 27 steps |
| ui-poll-tr | clean, 2865 states | Deadlock, 32 steps |
| fe-a1 | ResetLoss, 4 steps | ResetLoss, 4 steps |
| fe-a1-srej | ResetLoss, 4 steps | RejectCoherence at B, 10 steps |
| fe-iresp | clean, 16 states | Deadlock, 23 steps |
| fe-tr-b3 | Deadlock, 4 steps | Deadlock, 4 steps |
| fe-iresp-trq | clean, 1281 states | Deadlock, 24 steps |
| busy-a-tr2 | Deadlock, 14 steps | Delivery at A, 13 steps |
| tr-rej | Deadlock, 17 steps | Deadlock, 17 steps |
| tr-srej-rc-off | SequenceSanity at A, 24 steps | Deadlock, 26 steps |
| inj-rej-tr | clean, 67 states | Deadlock, 23 steps |
| inj-srej-tr | clean, 83 states | Delivery at B, 6 steps |
| inj-srejp-tr | clean, 101 states | Delivery at B, 7 steps |
| inj-frmr-t3 | clean, 25 states | clean, 35 states |
| ip-rejx | clean, 159 states | Deadlock, 23 steps |
| ip-tr-inseq | clean, 79 states | Deadlock, 23 steps |
| ip-tr-stale | Deadlock, 12 steps | Deadlock, 23 steps |
| ip-tr-busy | clean, 1244 states | Deadlock, 23 steps |
| ip-tr-rejx | clean, 444 states | Deadlock, 26 steps |
| ip-nr | clean, 333 states | clean, 338 states |
| ip-nr-v22 | clean, 500 states | clean, 476 states |
| ip-nr-rnr | clean, 1374 states | clean, 1592 states |
| ip-nr-rnr-v22 | clean, 2233 states | clean, 2431 states |
| rnv-rej | clean, 75 states | Deadlock, 23 steps |
| rnv-srejp | Deadlock, 11 steps | Delivery at B, 7 steps |
| ip-both | clean, 3531 states | Deadlock, 22 steps |


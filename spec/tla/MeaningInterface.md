# The meaning interface

The TLA+ model of the AX.25 v2.2 Data Link runs the generated tables (`Ax25DataLinkTables.tla`, rendered from the SDL figures) with two hand-written parts: a runner (`Ax25Runner.tla`), which says how a table is run, and a meaning module, which says what each action verb, guard atom and signal does. This file is the contract a meaning module implements. It is written so that a meaning can be built from it, `Ax25Values.tla`, `Ax25Runner.tla`, the tables, the catalogues (`spec-sdl/actions.yaml`, `predicates.yaml`, `events.yaml`) and the AX.25 v2.2 text, and nothing else.

`Ax25MeaningTemplate.tla` is a skeleton that parses: every operator is defined, the fixed fields are present, and every verb and atom is still unbound. Copy it, rename the module, and fill it in.

## Shape of a meaning module

- It `EXTENDS Ax25Values` (which brings `Integers`, `Sequences`, `FiniteSets` and `TLC`) and may extend other standard modules.
- It defines every operator listed under "Operators", with the arity shown. SANY rejects a meaning that misses one ("Substitution missing for symbol ...").
- It does not define any reserved name (below).
- It is selected with `TlaCheck --meaning PATH.tla`, whose file name must not be the name of a module in `spec/tla`. `Ax25Semantics.tla` instantiates the runner with it, and the two-station harness (`TwoStation.tla`) extends that.

## Inputs

### The event record

Built by the harness with `Ax25Values.Event` and fields set by `EXCEPT`. One per dispatch.

| Field | Meaning |
| --- | --- |
| `ev` | The event name, as the tables' page columns spell it: the `*_received` frame events (`I_received`, `RR_received`, `RNR_received`, `REJ_received`, `SREJ_received`, `UA_received`, `DM_received`, `DISC_received`, `SABM_received`, `SABME_received`, `FRMR_received`, `UI_received`, `XID_response_received`), the layer 3 primitives (`DL_DATA_request`, `DL_CONNECT_request`, `DL_DISCONNECT_request`, `DL_UNIT_DATA_request`, `DL_FLOW_OFF_request`, `DL_FLOW_ON_request`), `LM_SEIZE_confirm`, `T1_expiry`, `T3_expiry`, `I_frame_pops_off_queue`, the frame-error inputs (`control_field_error`, `info_not_permitted_in_frame`, `u_or_s_frame_length_error`) and the catch-all columns (`all_other_commands`, `i_or_s_command_received`, `all_other_primitives__from_lower_layer`, `all_other_primitives__from_upper_layer`). |
| `pf` | For a received frame, its P/F bit: always 1 or 0. `None` for any stimulus that is not a received frame. |
| `cmd` | For a received frame, 1 for a command, 0 for a response: always set. `None` otherwise. |
| `nr`, `ns` | The received frame's N(r) and N(s); `None` when the frame carries none or the stimulus is not a frame. |
| `data` | A payload label: the received I or UI frame's, or the DL-DATA or DL-UNIT-DATA Request's. `""` for none. |
| `able` | The environment's answer to `able_to_establish`. TRUE unless the scenario says otherwise. |
| `info` | The environment's answer to `info_field_length_le_N1_and_content_is_octet_aligned`. TRUE unless the scenario says otherwise. |
| `ftype`, `fe` | For the harness's own checks. A meaning must not read them. |

A received frame is dispatched as its own `*_received` event when the current page has that column, else as the page's catch-all (the harness routes it); a catch-all event raised by a frame carries the frame's fields. The frame-error inputs, `LM_SEIZE_confirm`, timer expiries, `I_frame_pops_off_queue` and layer 3 primitives carry `None` in `pf`, `cmd`, `nr` and `ns`.

### Verb and atom spellings

The tables spell each action as `Box(kind, verb)` (kind one of `processing`, `signal_upper`, `signal_lower`, `internal_out`, `subroutine`) and each guard term as `Is(atom)` or `Not(atom)`. The catalogues list the verbs and atoms. A meaning must `Fail` on a verb or atom it does not bind rather than leave the CASE without an arm (an unmatched TLA+ CASE is a TLC error, not a model verdict).

## The station record

What persists between dispatches, and so is part of the model's state. `NewMachine` builds it, `Begin` copies it into a dispatch context, `Persist` copies it back.

### Fixed fields

The harness reads, seeds or writes these, so their names, types and meanings are fixed. The list is `FixedFields` in `Ax25Values.tla`, and the harness checks it at start-up: a meaning whose `NewMachine` or `Persist` lacks one of them stops TLC before any state is explored, with "meaning interface: NewMachine lacks the fixed station field(s) ..." naming the missing fields.

| Field | AX.25 name | Type |
| --- | --- | --- |
| `st` | the Data Link state | a page name: `Disconnected`, `AwaitingConnection`, `AwaitingV22Connection`, `Connected`, `TimerRecovery`, `AwaitingRelease` |
| `vs` | V(S), send state variable | integer in `0 .. mod - 1` |
| `vr` | V(R), receive state variable | integer in `0 .. mod - 1` |
| `va` | V(A), acknowledge state variable | integer in `0 .. mod - 1` |
| `rc` | RC, retry count | integer |
| `mod` | the modulus | 8 or 128 |
| `k` | k, window size | integer |
| `n2` | N2, maximum number of retries | integer |
| `v22` | the version 2.2 flag | boolean |
| `srej` | SREJ enabled | boolean |
| `l3` | Layer 3 Initiated | boolean |
| `own` | Own Receiver Busy | boolean |
| `ack` | Acknowledge Pending | boolean |
| `srejx` | Selective Reject Exception | a count, 0 when clear |
| `t1`, `t3` | timers T1 and T3 | `"stopped"`, `"running"` or `"expired"` |
| `q` | the I frame queue | a sequence; empty exactly when no I frame waits to be sent; its entries are the meaning's own |
| `saved` | the receive buffer of out-of-sequence I frames | a set of `<<N(s), label>>` pairs, at most one per N(s) (use `Has`/`Get`/`Put`/`Del`) |

By who touches them:

- **Read by the harness:** `st vs vr va rc mod k own srejx saved q t1 t3`.
- **Seeded by the harness** (`NewMachine(st)` then `EXCEPT`): `mod k n2 srej v22 rc l3 t1 t3`.
- **Written by the harness directly**, with no table row running: `own ack t1 t3`. The harness's local busy condition enters with own TRUE and ack FALSE, and leaves with own FALSE, ack FALSE, and T1 running (T3 stopped if T1 was not running). The RNR and RR it sends then are the harness's, not the meaning's.

The runner moves `st`; a meaning only reads it.

### Harness checks that bind a meaning

The harness judges the system through these fields, so a meaning must keep them meaningful:

- Sequence sanity: `vs`, `va`, `vr` in `0 .. mod - 1`; `(vs - va) % mod <= k`; `srejx <= k`.
- Reject coherence: a received REJ or SREJ's N(r) must lie in `[va, vs)` at the receiver; a REJ or SREJ sent must carry N(r) = `vr`, or one inside `(vr, vr + k)` that is not `Has(saved, N(r))`.
- Busy indication: while `own` is TRUE the station sends no RR, REJ or SREJ.
- Rest (quiescence): a station is at rest only in `Connected` with `q = <<>>` and `vs = va` (and, when T3 is modelled, `t1` or `t3` running).
- Moves: the T1 move is enabled only when `t1 = "running"` and the page has `T1_expiry`; the T3 move the same with `t3`. The pop move is enabled only when `q # <<>>`, the page has `I_frame_pops_off_queue`, and dispatching it would fail, emit a frame, or give a `Persist` result different from the station record; a pop that changes nothing is not a move. The harness's busy hold reads `rc`, `own` and `st`.
- Delivery and link reports are read from `fx` (below).

### Extra fields

A meaning may add fields of its own to the station record. Every field is state, so on a clean row extra or differently represented fields change the distinct-state count TLC reports against the explorer without changing any verdict. The lockstep comparison of two meanings compares an observable projection (effects and the fixed fields), not whole records.

`Persist` must be canonical: two dispatches that leave the station the same must give equal records, because the harness compares them.

## The dispatch context

`Begin(m, e)` returns a record: the station record's fields, the meaning's own per-dispatch scratch, and the fields below.

### Owned by the runner

Initialise them in `Begin` as shown; no meaning operator writes them, except that `Preempt` may set `tid`.

| Field | Initial | Meaning |
| --- | --- | --- |
| `err` | `""` | The error the reference interpreter would have thrown. Set only through `Fail`, so the first error wins. |
| `tid` | `""` | The transition that fired. |
| `arms` | `{}` | The table rows the dispatch ran, for coverage. |

The runner calls no meaning operator on a context whose `err` is set, so operators may assume `err = ""` on entry.

### `fx`, read by the harness

`fx` is the dispatch's effects in emission order, a sequence built only with the `Ax25Values` constructors, starting `<<>>`:

- `FrameFx(Frm(type, cmd, pf, nr, ns, data))` for every frame put on the air: `type` one of `I RR RNR REJ SREJ SABM SABME DISC UA DM FRMR UI XID`; `cmd` TRUE for a command; `pf` TRUE when P or F is 1; `nr`, `ns` the numbers, `None` when absent; `data` the payload label, `""` for none.
- `UpperFx(verb, detail)` for every signal to layer 3, `verb` exactly as the table spells it (`DL_CONNECT_confirm`, `DL-ERROR Indication (K)`, ...).
- `LmFx(verb)` for every request to the link multiplexer (`LM_seize_request`, ...). The harness answers `LM_seize_request` with `LM_SEIZE_confirm`; the others need no answer.

The label contract: the harness checks delivery by payload label. An I frame carries the label of the data it sends (the label layer 3 gave that data in its DL-DATA Request, or a received I frame's label passed back). A `DL_DATA_indication`'s detail is the label of the data it delivers to layer 3; a `DL_UNIT_DATA_indication`'s the label of the received UI frame. A UI frame sent for a DL-UNIT-DATA Request carries that request's label. Other details are `""`.

The harness also reads, from `fx`: the link reports (`DL_CONNECT_confirm`, `DL_CONNECT_indication`, `DL_DISCONNECT_indication`, `DL_DISCONNECT_confirm`), the `DL-ERROR Indication (X)` codes, and whether a dispatch from `Connected` or `TimerRecovery` sent SABM or SABME (a link reset).

## Operators

Every operator is total over the inputs the runner and harness give it; `c` and the result are dispatch contexts unless said otherwise.

For the harness:

| Operator | Contract |
| --- | --- |
| `NewMachine(st)` | A station record in state `st` with the fixed fields present and AX.25's initial values; the harness then sets `mod k n2 srej v22 rc l3 t1 t3` itself. |
| `Persist(c)` | The station record of a context. Canonical, as above. |

For the runner (the `CONSTANTS` of `Ax25Runner.tla`):

| Operator | When the runner calls it | Contract |
| --- | --- | --- |
| `Begin(m, e)` | first, on every dispatch | A context for event `e` on station record `m`, with the runner's fields initialised. |
| `Arrive(c, e)` | right after `Begin`, before any table row | What the event itself does to the context before the table runs, if anything. Return `c` unchanged if nothing. |
| `AtomError(c, e, a)` | for each guard term, before `AtomValue` | `""` when atom `a` can be answered now, else a string saying why not (the term then fails the dispatch, if it is reached). |
| `AtomValue(c, e, a)` | only when `AtomError(c, e, a) = ""` | The atom's truth value. |
| `Processing(c, e, verb)` | for each `processing` box | The box's effect. |
| `SignalUpper(c, e, verb)` | for each `signal_upper` box | A signal to layer 3. |
| `SignalLower(c, e, verb)` | for each `signal_lower` box | A frame to the peer or a request to the link multiplexer. |
| `InternalOut(c, e, verb)` | for each `internal_out` box | An internal signal. |
| `Preempts(c, e)` | after `Arrive`, if no error | TRUE when the meaning handles this dispatch itself instead of the table. A meaning that never does defines it `FALSE`. |
| `Preempt(c, e)` | instead of the table, when `Preempts` holds | That handling. It may set `tid` to a name of its own. An escape hatch, not a requirement. |
| `AfterTransition(c)` | after a transition that did not fail | Any bookkeeping after the table has run. Return `c` if none. |

Guards are evaluated against the context as it is at that moment (see the runner policies for when that is).

## Runner policies

Fixed by `Ax25Runner.tla`; a meaning cannot change them.

- A transition's guard is evaluated against the context before any of its actions run; subroutine path guards and loop predicates are evaluated live, against the context at that point of the chain.
- Exactly one transition of the page's column, and exactly one path of a called subroutine, must hold. Every guard is evaluated, and an error in any is an error. A guard's terms are evaluated left to right and stop at the first false term, so an atom that cannot be answered is an error only when it is reached. None or several holding is an error.
- Loop regions are `while` (test first) or `do-while` (test last), capped at 4096 iterations.
- Call-site labels bind to subroutine tables through an explicit alias map (`SubroutineBindings`); a label not in it binds to the table of the same name.
- The call sites `Enquiry_Response_F_1` and `Enquiry Response (F = 0)` carry an F parameter. Before the call the runner applies `Processing(c, e, "F := 1")` or `Processing(c, e, "F := 0")` respectively, so the parameter means whatever that processing box means.
- A `processing` box whose verb is `Clear Exception Conditions` is a call of that subroutine.
- No subroutine call-depth limit.
- The first error wins and nothing runs after it; `Settle` skips `AfterTransition` on a failed dispatch.
- `Saves[state]` (the figures' Save symbol) is table data the runner does not consume yet; when it does, holding and releasing a saved input will be runner policy.

## Reserved names

A meaning must not define any of these (the instantiation would not parse):

- From `Ax25Values.tla`: `None Frm NoFrm FrameFx UpperFx LmFx Event Has Get Put Del FixedFields Fail`, and every operator of `Integers`, `Sequences`, `FiniteSets` and `TLC`.
- From `Ax25Runner.tla`: `MaxLoopIterations EvalTerms SelectGuard SubroutineBindings SubroutineFor BeforeCall ProcessingCallsSubroutine DataLinkStates Handles Invoke Act RunRange RunLoop RunActions CallSubroutine Goto DispatchState Prepare Settle Dispatch`.
- From `Ax25DataLinkTables.tla`: `Box Is Not Loop Pages Saves Subroutines` and every `Page_*` name.
- From the harness `TwoStation.tla`, which extends the result: any name it defines (its constants, variables and operators). A clash there is reported by SANY when the harness is parsed.

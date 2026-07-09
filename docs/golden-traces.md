# Golden traces — executable semantic validation of the SDL tables

Everything else in CI proves *transcription* fidelity (graphml → YAML →
generated tables, byte-for-byte). Nothing there can tell you that the
Connected page actually completes a SABM handshake. The golden-trace suite
closes that gap: a **reference interpreter** (`codegen/src/Packet.Sdl.Interpreter`)
executes the emitted JSON tables (`spec/json/*.g.json`) directly, and each
trace in `traces/*.trace.yaml` replays a protocol scenario against it,
asserting the emitted frames/primitives, the state changes and the SDL
variables step by step.

Run it locally:

```sh
dotnet test codegen/tests/Packet.Sdl.Interpreter.Tests
```

## Design rules

- **The interpreter consumes `spec/json/` only** — never a backend's
  generated source. It is the machine-neutral executable semantics of the
  tables, and can later be replayed against each generated backend as a
  cross-backend oracle.
- Guard strings are parsed by `Packet.Sdl.IR.GuardExpression` — the same
  conjunction-of-optionally-negated-atoms semantics the typed backends get.
  No second guard grammar exists.
- **Expected sequences come from the spec prose**, not from reading the
  tables (packethacking/ax25spec `doc/ax.25.2.2.4_Oct_25.md`). Every trace
  carries `citations:` — a trace without citations fails the suite.
- **Traces are never fudged to match a defective figure.** If a trace fails
  because the figure itself is wrong, it stays prose-true and is marked
  `expected_failure: <upstream-issue-url>` (see below).

## Trace file format

One scenario per `traces/<name>.trace.yaml`:

```yaml
scenario: mod8-connect-initiator       # required, unique
description: >                         # optional prose
  ...
citations:                             # required: where the expectations come from
  - "ax.25.2.2.4_Oct_25.md §6.3.1 (AX.25 Link Connection Establishment)"
  - "figc4.1 t03"
# expected_failure: https://github.com/packethacking/ax25spec/issues/43   # xfail (see below)
machine: data_link                     # optional; data_link is the default (and the only machine modelled today)
initial:
  state: Disconnected                  # required
  variables: { modulo: 8, k: 4 }       # optional seeds; see vocabulary below
  timers: { t1: stopped, t3: running } # optional; timers default to stopped
steps:
  - name: layer 3 asks for a connection   # optional label for failure messages
    inject:
      event: DL_CONNECT_request           # required: a member of the tables' `on:` vocabulary
      # received-frame fields, when the event is a frame:
      #   pf: true          P/F bit (P on commands, F on responses)
      #   command: true     command vs response (defaults exist where the
      #                     role is fixed: SABM/SABME/DISC/I are commands,
      #                     UA/DM/FRMR are responses)
      #   nr: 3             received N(R)
      #   ns: 0             received N(S)
      #   data: hello       opaque payload label (queue bookkeeping)
      # environment-supplied guard atoms (only these two exist):
      #   atoms: { able_to_establish: true,
      #            info_field_length_le_N1_and_content_is_octet_aligned: true }
    expect:
      transition: t03_dl_connect_request  # optional: exact transition id
      state: AwaitingConnection           # optional: state after the step
      effects:                            # optional: exhaustive, in order.
        - { frame: SABM, command: true, pf: true }
      variables: { rc: 1, layer_3_initiated: true }   # optional subset
      timers: { t1: running, t3: stopped }            # optional subset
      queue: []                           # optional: full queue contents, head first
```

Rules of thumb:

- **`effects` is exhaustive and ordered** when present: the machine must
  emit exactly that list. `effects: []` asserts silence; omitting the key
  skips effect checking for the step.
- Effect entries name exactly one of `frame:` / `dl:` / `lm:` / `internal:`.
  On a `frame:` the optional `command` / `pf` / `nr` / `ns` / `expedited`
  refinements are checked only when present. `dl:` matches the verbatim
  canonical verb (`DL_CONNECT_confirm`, `DL-ERROR Indication (F)`, …);
  `internal:` matches the internal_out verb, with optional `detail:` for the
  queued label.
- A guard or action that consults a frame field the step didn't supply
  (e.g. `F_eq_1` with no `pf:`) fails the trace with a message saying which
  field is missing — supply the field rather than guessing defaults.
- Injecting `T1_expiry` / `T3_expiry` requires that timer to be running.

### Variable vocabulary

`initial.variables` and `expect.variables` share one snake_case namespace:

| key | meaning | default |
|---|---|---|
| `vs`, `vr`, `va` | V(s) / V(r) / V(a) sequence variables | 0 |
| `rc` | retry counter RC | 0 |
| `modulo` | sequence modulus (8 or 128) | 8 |
| `k` | window size | 4 |
| `n1`, `n2` | max I-field length / max retries | 256 / 10 |
| `version_2_2` | "Set Version 2.2" flag | false |
| `srej_enabled` | selective-reject enabled | false |
| `half_duplex` | "Set Half Duplex" flag | false |
| `layer_3_initiated` | Layer 3 Initiated flag | false |
| `own_receiver_busy`, `peer_receiver_busy` | busy conditions | false |
| `reject_exception` | REJ sent, uncleared | false |
| `sreject_exception` | SREJ exception count (integer) | 0 |
| `ack_pending` | Acknowledge Pending flag | false |

Timers `t1` / `t3` take `stopped | running | expired`. SRT / T1V / T2 are
modelled symbolically (the tables only ever assign formulas to them) and are
not assertable.

## `expected_failure:` — the strict-xfail mechanism

Some figures are simply wrong, and the tables faithfully transcribe the
wrongness (trust-the-figure). Such traces keep their prose-true
expectations and declare:

```yaml
expected_failure: https://github.com/packethacking/ax25spec/issues/43
```

The runner then *requires* the trace to fail: it reports as **skipped** with
the upstream issue URL and the actual divergence, and the moment an upstream
fix lands in the figures/tables, the now-passing trace **fails the suite**
until the marker is removed. The xfail list is therefore a live index of
known figure defects, and it can never go stale silently.

## Interpretation policies (read before writing subtle traces)

The JSON tables leave a few execution details unstated. The interpreter
resolves them as follows — if a policy here surprises you, that's a finding;
raise it rather than encoding around it:

- **Guard timing.** A transition's composed guard is evaluated once against
  the pre-transition state + event (the tables are built select-then-execute;
  that's what the Resolver's stale-read substitution `vs_eq_nr` exists for).
  Subroutine path guards and loop predicates are evaluated **live** against
  the mutating state, because the figures place those decisions mid-chain
  (e.g. `set_peer_receiver_busy` runs before `Check_I_Frame_Acknowledged`
  branches on `peer_receiver_busy`). Exactly one transition (and exactly one
  subroutine path) must match — anything else is a table-determinism error.
- **Pending TX registers.** P / F / N(s) / N(r) staging registers reset at
  each dispatch (P/F to 0 per §6.2, N(s)/N(r) to unset). A frame emission
  that needs an unset N(r)/N(s) is an error — the table omitted the staging
  assignment. `Enquiry_Response_F_1` / `Enquiry Response (F = 0)` stage the
  subroutine's F parameter into the pending F register.
- **Queue model.** The I-frame queue is a FIFO. `DL_DATA_request` pushes
  join the tail; a frame pushed back after popping (peer busy / window
  full), and `Invoke_Retransmission`'s old-frame batch, re-enter at the head
  preserving their own order. `I_frame_pops_off_queue` consumes the queue
  head when the queue is non-empty (and cross-checks the step's `data:`
  label); popping against an empty queue is allowed so traces may treat the
  queue abstractly.
- **Retransmission is asserted as re-queueing.** `Invoke_Retransmission`
  restores V(s) to its pre-rewind value after pushing the old frames, so a
  subsequent `I_frame_pops_off_queue` would renumber them from the restored
  V(s) — the tables cannot express "retransmit with the original N(s)"
  end-to-end. Assert the re-queue effects/queue contents, not the re-emitted
  frames (see `rej-round-trip.trace.yaml`).
- **Subroutine binding.** Call-site verbs bind to subroutine tables via an
  explicit alias map in `Machine.cs` (`"N(r) Recovery"` →
  `N_r_Error_Recovery`, `"Select_T1_Value"` → `Select_T1`, `"Transmit
  Enquery"` → `Transmit_Enquiry`, …) — the JSON carries the figures'
  verbatim call labels, which don't always match the subroutine page's
  declared name.

## Adding a trace

1. Pick the scenario and find the prose that specifies the expected
   behaviour (`packethacking/ax25spec` `doc/ax.25.2.2.4_Oct_25.md`). Write
   the expectations **from the prose** — before reading the table paths.
2. Create `traces/<name>.trace.yaml` with `citations:` pointing at the
   sections (and the figure/transition ids once you've identified them).
3. `dotnet test codegen/tests/Packet.Sdl.Interpreter.Tests`.
4. If the trace fails, decide which of the three it is:
   - your expectation misreads the prose → fix the trace;
   - the interpreter lacks/mis-models a verb or atom → fix the interpreter
     (and say so in the PR — interpreter semantics are shared);
   - the figure itself is defective → file (or find) the upstream
     packethacking/ax25spec issue and add
     `expected_failure: <issue-url>`. Do **not** bend the expectations to
     match the tables.

## Reserved scenarios

`traces/srej-round-trip.RESERVED.md` — the SREJ round trip is reserved for
clean-room authoring as an independence control; see the file and
packet-net/ax25sdl#74.

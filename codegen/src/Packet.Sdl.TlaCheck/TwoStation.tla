---------------------------- MODULE TwoStation ----------------------------
(***************************************************************************)
(* Hand-written harness. Two Data Link machines running the generated     *)
(* tables (Ax25DataLinkTables.tla, through Ax25Semantics.tla) over a       *)
(* modelled channel, with the invariants                                   *)
(* of the two-station explorer stated in TLA+. It reproduces the explorer  *)
(* (codegen/src/Packet.Sdl.Explorer, docs/explorer.md) move for move: the  *)
(* same seeds, the same enabled moves in the same order, the same pinned   *)
(* delegated semantics, so that TLC and the explorer explore the same      *)
(* state graph and a disagreement is a bug in one of them. docs/tla.md     *)
(* records the few places TLC needs a different formulation.               *)
(***************************************************************************)
EXTENDS Ax25Semantics

CONSTANTS
    FramesAB,          \* I frames A submits for B (labels a0, a1, ...), queued in the seed
    FramesBA,          \* I frames B submits for A (b0, b1, ...)
    K,                 \* window size, both stations
    Srej,              \* SREJ negotiated (and so version 2.2) on both stations
    N2,                \* retry limit
    Budget,            \* channel faults allowed along any one path
    Faults,            \* subset of {"drop", "dup", "reorder"}
    InteriorDropsOnly, \* a drop may only hit an I frame that is not the last its sender submitted
    Seed,              \* "connected", "disconnected" or "awaiting-v22"
    Mod128A,           \* disconnected seed: A starts at modulo 128, so it sends SABME
    DataAfterConfirm,  \* disconnected seed: layer 3 submits its data only once its link is up
    PeerDeclinesSabme, \* table-driven B refuses SABME (able_to_establish false) but accepts SABM
    Peer,              \* what plays B: "tables", "v20-frmr" or "v20-dm"
    FlowAt,            \* stations whose layer 3 issues DL_FLOW_OFF / DL_FLOW_ON, subset of {"A", "B"}
    ReleaseAt,         \* stations whose layer 3 releases the link once everything is delivered, subset of {"A", "B"}
    ReleaseEarly,      \* the release may also come while the link is being set up (Awaiting*Connection)
    AbandonAt,         \* stations whose layer 3 may abandon its own connect while it is being set up, subset of {"A", "B"}
    ReconnectAt,       \* stations whose layer 3 issues DL_CONNECT_request once on a live or waiting link, subset of {"A", "B"}
    DataBeforeConfirm, \* with DataAfterConfirm: A's layer 3 submits from the seed on, not waiting for its confirm
    AcceptGiveUp,      \* the give-up rule: an honest N2 give-up that has settled is quiescent (see GiveUpSettled)
    BusyAt,            \* stations that enter a local busy condition once (own receiver busy, RNR), subset of {"A", "B"}
    LongBusy,          \* with BusyAt: the busy period may outlast the peer's retries (no T1 hold)
    T3Moves,           \* model T3 expiry (idle keepalive), the keepalive rest rule and the stricter give-up settle
    T3At,              \* with T3Moves: the stations whose T3 may expire, subset of {"A", "B"}
    DataAfterReconnect, \* with ReconnectAt, connected seed: a reconnecting layer 3 submits one more frame once its reconnect is answered and the link reported up
    UnitDataAt,        \* stations whose layer 3 sends one UI datagram, once, in any state, subset of {"A", "B"}
    UiForm,            \* the datagram's form: "plain", "poll" (P = 1), "response" or "long" (fails the N1 check)
    FrameErrors,       \* corrupted forms a frame may arrive in, subset of {"control", "info", "length", "n1", "iresp"}
    InjectAt,          \* stations that put one injected frame on the air, once, subset of {"A", "B"}
    InjectForm,        \* its form: "rej-poll", "srej-command", "srej-poll", "ua-f0", "frmr", "i-frame", "i-poll",
                       \* "i-poll-ahead", "i-poll-stale" (or "none")
    InjectThen,        \* a second injected frame the same station puts on the air after the first (or "none")
    FlowRounds,        \* FLOW_OFF-then-FLOW_ON rounds per flow-controlling station
    FlowOffAfter,      \* frames a station must have delivered before its FLOW_OFF
    StepChecks,        \* the per-step invariants switched on (see below)
    TimerRecoveryQuiescent, \* triage aid: count TimerRecovery as quiescent
    RejMayEqualVs,     \* receiver-side reject coherence accepts REJ N(r) = V(s)
    MaxDepth           \* depth bound, used only through the WithinDepth constraint

VARIABLES
    sys,   \* the composed system state (the explorer's SystemState)
    viol,  \* the per-step invariant that fired on the step into this state
    last   \* the move into this state: for reading counterexamples, for coverage,
           \* and for the liveness check; outside VIEW
vars == <<sys, viol, last>>

\* One layer 3 issues at most one DL-DISCONNECT Request: abandon and release
\* are never combined (Stepper refuses the same).
ASSUME AbandonAt = {} \/ ReleaseAt = {}
View == <<sys, viol>>

Stations == {"A", "B"}
Other(s) == IF s = "A" THEN "B" ELSE "A"

\* The frames each station's layer 3 submits at the seed or by its first
\* moves, and with DataAfterReconnect the one more a reconnecting station
\* submits once its reconnect is answered and the link reported up
\* (Stepper.SeedFrames).
SeedFrames(s) == IF s = "A" THEN FramesAB ELSE FramesBA
AfterReconnect(s) == IF DataAfterReconnect /\ s \in ReconnectAt THEN 1 ELSE 0

\* What each station's layer 3 submitted, in order.
Submitted(s) ==
    IF s = "A" THEN [i \in 1..(FramesAB + AfterReconnect("A")) |-> "a" \o ToString(i - 1)]
    ELSE [i \in 1..(FramesBA + AfterReconnect("B")) |-> "b" \o ToString(i - 1)]

\* The label of each station's UI datagram (Stepper.UiLabel).
UiLabel(s) == IF s = "A" THEN "ua0" ELSE "ub0"

\* The payload label of a station's injected I frame number n, 0 the first
\* (Stepper.InjectedLabel), and the set of them (Stepper.IsInjectedLabel).
InjLabel(s, n) == (IF s = "A" THEN "xa" ELSE "xb") \o ToString(n)
InjLabels == {"xa0", "xa1", "xb0", "xb1"}

StubB == Peer # "tables"
DataStates == {"Connected", "TimerRecovery"}
\* A live or waiting link: established, or being set up.
LinkStates == {"AwaitingConnection", "AwaitingV22Connection", "Connected", "TimerRecovery"}
KnownStates == {"Disconnected", "AwaitingConnection", "AwaitingV22Connection", "Connected", "AwaitingRelease", "TimerRecovery"}

\* The meaning's station record must have every fixed field (Ax25Values
\* FixedFields), as NewMachine builds it and as Persist gives it back: the
\* harness seeds and writes some of them with EXCEPT, which TLC would
\* otherwise accept on a missing field and run on silently.
ASSUME \A st \in KnownStates :
    LET made == DOMAIN NewMachine(st)
        kept == DOMAIN Persist(Begin(NewMachine(st), Event("I_frame_pops_off_queue")))
    IN  /\ Assert(FixedFields \subseteq made,
                  "meaning interface: NewMachine lacks the fixed station field(s) " \o ToString(FixedFields \ made))
        /\ Assert(FixedFields \subseteq kept,
                  "meaning interface: Persist(Begin(...)) lacks the fixed station field(s) " \o ToString(FixedFields \ kept))

NoViol == [kind |-> "none", at |-> "", msg |-> ""]
Viol(kind, s, msg) == [kind |-> kind, at |-> s, msg |-> msg]
NoStep == [move |-> "", at |-> "", frame |-> NoFrm, tid |-> "", arms |-> {}, tick |-> 0, lossy |-> ""]

N(num) == ToString(num)
FrameText(f) ==
    f.type \o (IF f.cmd THEN " cmd P=" ELSE " rsp F=") \o (IF f.pf THEN "1" ELSE "0")
    \o (IF f.ns # None THEN " ns=" \o N(f.ns) ELSE "") \o (IF f.nr # None THEN " nr=" \o N(f.nr) ELSE "")
    \o (IF f.data # "" THEN " [" \o f.data \o "]" ELSE "")

(***************************************************************************)
(* Event routing (Stepper.FrameEvent, SeizeConfirmEvent, FrameInput)       *)
(***************************************************************************)

\* The catch-all a received frame of this type raises on page st (pinned
\* semantic 5); "" when the page has none.
FrameCatchAll(st, type, cmd) ==
    IF cmd /\ Handles(st, "all_other_commands") THEN "all_other_commands"
    ELSE IF cmd /\ type \in {"I", "RR", "RNR", "REJ", "SREJ"} /\ Handles(st, "i_or_s_command_received") THEN "i_or_s_command_received"
    ELSE IF Handles(st, "all_other_primitives__from_lower_layer") THEN "all_other_primitives__from_lower_layer"
    ELSE ""

\* A received frame raises its own *_received arm if the page has one or
\* saves it (an explicit save beats the catch-all, pinned semantic 19), else
\* the page's catch-all (pinned semantic 5); "" when the page has neither.
FrameEvent(m, f) ==
    LET direct == f.type \o "_received"
    IN  IF Handles(m.st, direct) \/ SavedIn(m.st, direct) THEN direct
        ELSE FrameCatchAll(m.st, f.type, f.cmd)

\* An upper-layer primitive raises its own arm if the page has one or saves
\* it, else the page's upper-layer catch-all (pinned semantic 5); with
\* neither, itself, which Dispatch then reports as a missing arm.
UpperEvent(m, p) ==
    IF Handles(m.st, p) \/ SavedIn(m.st, p) \/ ~Handles(m.st, "all_other_primitives__from_upper_layer") THEN p
    ELSE "all_other_primitives__from_upper_layer"

UpperPrimitives == {"DL_DATA_request", "DL_CONNECT_request", "DL_DISCONNECT_request", "DL_UNIT_DATA_request",
                    "DL_FLOW_OFF_request", "DL_FLOW_ON_request"}

\* A held input released into state st is routed exactly as a fresh one
\* would be (pinned semantic 19(c), Stepper.RouteReleased): its own arm, else
\* the catch-all for its kind (a received frame's, an upper-layer
\* primitive's, else the lower-layer one); with none, itself.
RouteReleased(st, e) ==
    LET r == IF Handles(st, e.ev) THEN e.ev
             ELSE IF e.ftype # "" THEN FrameCatchAll(st, e.ftype, e.cmd = 1)
             ELSE IF e.ev \in UpperPrimitives
                  THEN IF Handles(st, "all_other_primitives__from_upper_layer") THEN "all_other_primitives__from_upper_layer" ELSE ""
             ELSE IF Handles(st, "all_other_primitives__from_lower_layer") THEN "all_other_primitives__from_lower_layer"
             ELSE ""
    IN  IF r = "" THEN e ELSE [e EXCEPT !.ev = r]

\* Only a long UI datagram fails the N1 check (UiForm "long").
FrameInput(m, f, into) ==
    [ev |-> FrameEvent(m, f), pf |-> IF f.pf THEN 1 ELSE 0, cmd |-> IF f.cmd THEN 1 ELSE 0,
     nr |-> f.nr, ns |-> f.ns, data |-> f.data,
     able |-> ~(PeerDeclinesSabme /\ into = "B" /\ f.type = "SABME"), info |-> ~(UiForm = "long" /\ f.type = "UI"),
     ftype |-> f.type, fe |-> ""]

\* The link multiplexer's confirm (pinned semantic 2); a page without the arm
\* takes it through its lower-layer catch-all.
SeizeEvent(m) ==
    IF Handles(m.st, "LM_SEIZE_confirm") THEN "LM_SEIZE_confirm"
    ELSE IF Handles(m.st, "all_other_primitives__from_lower_layer") THEN "all_other_primitives__from_lower_layer"
    ELSE ""

(***************************************************************************)
(* Per-step invariants (checked on every edge, as the explorer does)       *)
(***************************************************************************)

\* Receiver side: the frame a REJ/SREJ names must be outstanding here.
ReceiverReject(s, m, e) ==
    LET x == e.nr
        out == (m.vs - m.va) % m.mod
        kind == IF e.ev = "REJ_received" THEN "REJ" ELSE "SREJ"
    IN  IF (x - m.va) % m.mod < out THEN NoViol
        ELSE IF RejMayEqualVs /\ e.ev = "REJ_received" /\ x = m.vs THEN NoViol
        ELSE Viol("RejectCoherence", s, "reject coherence (receiver): station " \o s \o " received " \o kind \o " nr=" \o N(x)
                  \o " but its outstanding frames are [V(a)=" \o N(m.va) \o ", V(s)=" \o N(m.vs) \o ")")

\* Emitter side: a REJ/SREJ must name V(r) or a frame inside the receive
\* window that is not already held.
EmitterReject(s, m, f) ==
    LET x == f.nr
        d == (x - m.vr) % m.mod
        held == Has(m.saved, x)
    IN  IF x = m.vr \/ (d > 0 /\ d < m.k /\ ~held) THEN NoViol
        ELSE Viol("RejectCoherence", s, "reject coherence (emitter): station " \o s \o " sent " \o FrameText(f) \o " with V(r)=" \o N(m.vr)
                  \o (IF held THEN ", but it already holds I(ns=" \o N(x) \o ")" ELSE ", but N(r) is outside its receive window"))

\* A station whose own receiver is busy sends RNR, not RR, REJ or SREJ
\* (Stepper.BusyIndicationCheck).
BusyIndication(s, m, f) ==
    IF m.own /\ f.type \in {"RR", "REJ", "SREJ"}
    THEN Viol("BusyIndication", s, "station " \o s \o " sent " \o FrameText(f) \o " while its own receiver is busy")
    ELSE NoViol

\* A received UI frame's handling (Stepper.UnitDataCheck). Delivery, as UI
\* Check draws it: a command that passes the N1 check is delivered upward
\* once with its label; a response is not delivered and draws DL-ERROR (Q);
\* a command that fails the N1 check is not delivered and draws DL-ERROR (K).
\* The answer, as section 4.3.3.6 says (the tables' reading of
\* ax25spec#154): a command with P = 1 gets exactly one response with F = 1,
\* DM outside the information transfer state and RR or RNR in it; a command
\* with P = 0 gets none. A response's answer is not judged.
UnitDataCheck(s, pre, e, fx) ==
    LET cmd == e.cmd = 1
        poll == cmd /\ e.pf = 1
        inds == {i \in 1..Len(fx) : fx[i].kind = "dl" /\ fx[i].prim = "DL_UNIT_DATA_indication"}
        errs == {fx[i].prim : i \in {j \in 1..Len(fx) : fx[j].kind = "dl"}}
        frs == SelectSeq(fx, LAMBDA x : x.kind = "frame")
        \* Too long is the scenario's (UiForm), not read back from e.info.
        deliv == IF cmd /\ UiForm # "long"
                 \* The label is the peer's datagram's, not read back from the frame.
                 THEN Cardinality(inds) = 1 /\ \A i \in inds : fx[i].detail = UiLabel(Other(s))
                 ELSE inds = {} /\ (IF cmd THEN "DL-ERROR Indication (K)" ELSE "DL-ERROR Indication (Q)") \in errs
        ans == IF ~cmd THEN TRUE
               ELSE IF ~poll THEN Len(frs) = 0
               ELSE /\ Len(frs) = 1 /\ ~frs[1].frame.cmd /\ frs[1].frame.pf
                    /\ IF pre.st \in DataStates THEN frs[1].frame.type \in {"RR", "RNR"} ELSE frs[1].frame.type = "DM"
    IN  IF ~deliv THEN Viol("UnitData", s, "station " \o s \o " in " \o pre.st \o " mishandled the UI frame [" \o e.data \o "]: delivery or refusal is not what UI Check draws")
        ELSE IF ~ans THEN Viol("UnitData", s, "station " \o s \o " in " \o pre.st \o " answered the UI frame [" \o e.data \o "] other than as section 4.3.3.6 says")
        ELSE NoViol

\* The frame-error input each corrupted form raises, and the DL-ERROR the
\* figures report it with (Stepper.FrameErrorInput).
ErrEvent(form) ==
    CASE form = "control" -> "control_field_error"
      [] form = "info" -> "info_not_permitted_in_frame"
      [] OTHER -> "u_or_s_frame_length_error"
ErrCode(form) ==
    CASE form = "control" -> "DL-ERROR Indication (L)"
      [] form = "info" -> "DL-ERROR Indication (M)"
      [] form = "length" -> "DL-ERROR Indication (N)"
      [] OTHER -> "DL-ERROR Indication (O)"

\* A corrupted frame's handling (Stepper.FrameErrorCheck): reported with the
\* DL-ERROR the figures draw for its form, and not delivered upward.
FrameErrorCheck(s, pre, form, fx) ==
    LET prims == {fx[i].prim : i \in {j \in 1..Len(fx) : fx[j].kind = "dl"}}
    IN  IF prims \cap {"DL_DATA_indication", "DL_UNIT_DATA_indication"} # {}
        THEN Viol("FrameError", s, "station " \o s \o " in " \o pre.st \o " delivered a corrupted frame (" \o form \o ") upward")
        ELSE IF ErrCode(form) \notin prims
        THEN Viol("FrameError", s, "station " \o s \o " in " \o pre.st \o " received a corrupted frame (" \o form \o ") and did not report " \o ErrCode(form))
        ELSE NoViol

\* Route one dispatch's effects in order: frames to the outgoing channel
\* (checking reject coherence), deliveries to the delivery count (checking
\* order and flow-off), a seize request to the owed flag, a connect confirm
\* or indication to the link-up flag (with DataAfterConfirm) and to conn
\* (progress, for the give-up rule), a disconnect indication or confirm to
\* the link-up flag (layer 3 stops submitting until the link is reported up
\* again), and a connect confirm or disconnect indication to pend (the answer
\* a reconnecting layer 3 waits for).
RECURSIVE Route(_, _, _, _, _, _)
Route(s, m, fx, acc, flowOff, i) ==
    IF i > Len(fx) THEN acc
    ELSE LET fe == fx[i]
             sub == Submitted(Other(s))
             nxt ==
                IF fe.kind = "frame" THEN
                    LET vr == IF acc.v = NoViol /\ fe.frame.type \in {"REJ", "SREJ"} /\ "RejectCoherence" \in StepChecks
                              THEN EmitterReject(s, m, fe.frame) ELSE acc.v
                    IN  [acc EXCEPT !.out = Append(@, fe.frame),
                                    !.v = IF vr = NoViol /\ BusyAt # {} /\ "BusyIndication" \in StepChecks
                                          THEN BusyIndication(s, m, fe.frame) ELSE vr]
                \* An injected I frame's payload: once, by the dispatch of that
                \* frame received in sequence (acc.inj), and no delivery count.
                ELSE IF fe.kind = "dl" /\ fe.prim = "DL_DATA_indication" /\ fe.detail \in InjLabels THEN
                    LET vf == IF acc.v = NoViol /\ "FlowOffDelivery" \in StepChecks /\ flowOff
                              THEN Viol("FlowOffDelivery", s, "station " \o s \o " delivered '" \o fe.detail \o "' upward while its layer 3 has flow off")
                              ELSE acc.v
                        ok == fe.detail = acc.inj
                    IN  [acc EXCEPT !.inj = IF ok THEN "" ELSE @,
                                    !.v = IF ~ok /\ vf = NoViol /\ "Delivery" \in StepChecks
                                          THEN Viol("Delivery", s, "station " \o s \o " delivered the injected payload '" \o fe.detail \o "' upward other than once, in the dispatch that accepts that I frame in sequence")
                                          ELSE vf]
                ELSE IF fe.kind = "dl" /\ fe.prim = "DL_DATA_indication" THEN
                    LET vf == IF acc.v = NoViol /\ "FlowOffDelivery" \in StepChecks /\ flowOff
                              THEN Viol("FlowOffDelivery", s, "station " \o s \o " delivered '" \o fe.detail \o "' upward while its layer 3 has flow off")
                              ELSE acc.v
                        expected == IF acc.deliv < Len(sub) THEN sub[acc.deliv + 1] ELSE ""
                        inOrder == expected # "" /\ fe.detail = expected
                    IN  [acc EXCEPT !.deliv = IF inOrder THEN @ + 1 ELSE @,
                                    !.v = IF ~inOrder /\ vf = NoViol /\ "Delivery" \in StepChecks
                                          THEN Viol("Delivery", s, "station " \o s \o " delivered '" \o fe.detail \o "' upward where "
                                                    \o (IF expected = "" THEN "everything was already delivered: duplicate or spurious delivery"
                                                        ELSE "'" \o expected \o "' is next: out-of-order, duplicate or gapped delivery"))
                                          ELSE vf]
                ELSE IF fe.kind = "dl" /\ fe.prim \in {"DL_CONNECT_confirm", "DL_CONNECT_indication"}
                     THEN [acc EXCEPT !.up = IF DataAfterConfirm THEN TRUE ELSE @, !.conn = TRUE,
                                      !.pend = IF fe.prim = "DL_CONNECT_confirm" THEN FALSE ELSE @, !.rep = 1,
                                      !.told = IF @ >= 1 THEN 2 ELSE @]
                ELSE IF fe.kind = "dl" /\ fe.prim \in {"DL_DISCONNECT_indication", "DL_DISCONNECT_confirm"}
                     THEN [acc EXCEPT !.up = IF DataAfterConfirm THEN FALSE ELSE @,
                                      !.pend = IF fe.prim = "DL_DISCONNECT_indication" THEN FALSE ELSE @, !.rep = 2]
                ELSE IF fe.kind = "lm" /\ fe.prim = "LM_seize_request" THEN [acc EXCEPT !.owed = TRUE]
                \* The reset rule: layer 3 has been told of the error behind a
                \* reset (acc.tell: the corrupted frame's own code, or (F) "data
                \* link reset"), and of the reset once a connect report follows.
                ELSE IF fe.kind = "dl" /\ fe.prim \in acc.tell THEN [acc EXCEPT !.told = 1]
                ELSE acc
         IN  Route(s, m, fx, nxt, flowOff, i + 1)

(***************************************************************************)
(* One stimulus at one station (Stepper.Inject)                            *)
(***************************************************************************)

Max(x, y) == IF x >= y THEN x ELSE y

\* Retry-loop attempts on a page, the losses an honest give-up there needs
\* (Stepper.GiveUpThreshold): Establish_Data_Link starts RC at 1, so the
\* connect loops give up after N2 transmissions; DL-DISCONNECT starts it at
\* 0, and Connected's T1 expiry sets it to 1 only after a first loss.
\* With T3Moves, a Timer Recovery loop entered by a T3 expiry (RC := 1, then
\* the poll, no loss before it) gives up after N2 polls.
GiveUpThreshold(page, t3loop) ==
    IF page \in {"AwaitingConnection", "AwaitingV22Connection"} \/ (page = "TimerRecovery" /\ t3loop) THEN N2 ELSE N2 + 1

\* Saves (pinned semantic 19). A dispatch whose effects answer a layer 3's
\* DL-DISCONNECT Request.
AnswersDisconnect(fx) == \E i \in 1..Len(fx) : fx[i].kind = "dl" /\ fx[i].prim \in {"DL_DISCONNECT_confirm", "DL_DISCONNECT_indication"}

\* A consumed DL-DISCONNECT Request must take the station to one of these
\* (DisconnectKept); anything else loses it.
DisconnectedOrReleasing == {"Disconnected", "AwaitingRelease"}

\* After a transition has entered a state: consume the held inputs that
\* state does not save, head first, each routed and dispatched as a fresh
\* input, until every input still held is saved (Machine.ReplayHeld). a
\* carries the station record (m), the held inputs, the effects and rows of
\* the whole dispatch so far, the error, the first lost disconnect (lost)
\* and the disconnect answer rule's pending flag (dp).
RECURSIVE Replay(_)
Replay(a) ==
    LET i == ReleaseIndex(a.m.st, a.held)
    IN  IF a.err # "" \/ i = 0 THEN a
        ELSE LET h == a.held[i]
                 c == Dispatch(a.m, RouteReleased(a.m.st, h))
                 req == h.ev = "DL_DISCONNECT_request"
             IN  IF c.err # "" THEN [a EXCEPT !.err = "replaying saved input '" \o h.ev \o "' on entering " \o a.m.st \o ": " \o c.err]
                 ELSE LET m2 == Persist(c)
                      IN  Replay([a EXCEPT !.held = Unheld(@, i), !.m = m2, !.fx = @ \o c.fx, !.arms = @ \cup c.arms,
                                           !.lost = IF @ = "" /\ req /\ m2.st \notin DisconnectedOrReleasing
                                                    THEN a.m.st \o "/" \o c.tid \o " (released from a save)" ELSE @,
                                           !.dp = IF AnswersDisconnect(c.fx) THEN FALSE ELSE IF req THEN TRUE ELSE @])

\* Dispatch e at station s of system S; newIn becomes s's incoming channel.
\* p names the layer 3 primitive the move issued ("" for none), so that a
\* DL-DISCONNECT Request routed to a catch-all is still known as one.
\* Returns the next system state, the per-step violation and the transition.
\* An input the state saves is held instead (tid "save"); after the
\* transition, held inputs the new state does not save are replayed
\* (Replay), and their effects follow the transition's own.
InjectAs(S, s, e, p, newIn, newBudget, clearOwed) ==
    LET pre == S.m[s]
        v0 == IF e.ev = "" THEN Viol("MachineError", s, "station " \o s \o " in state " \o pre.st \o " has no arm (direct or catch-all) for the received frame")
              ELSE IF "RejectCoherence" \in StepChecks /\ e.ev \in {"REJ_received", "SREJ_received"} /\ pre.st \in DataStates
                   THEN ReceiverReject(s, pre, e)
              ELSE NoViol
        ran == v0 = NoViol
        hold == SavedIn(pre.st, e.ev)
        c == IF hold THEN [err |-> "", fx |-> <<>>, tid |-> "save", arms |-> {}] ELSE Dispatch(pre, e)
        isReq == (IF p # "" THEN p ELSE e.ev) = "DL_DISCONNECT_request"
        own == IF hold \/ c.err # "" THEN pre ELSE Persist(c)
        a0 == [m |-> own, held |-> IF hold THEN Append(S.held[s], e) ELSE S.held[s],
               fx |-> IF c.err = "" THEN c.fx ELSE <<>>, arms |-> IF c.err = "" THEN c.arms ELSE {}, err |-> c.err,
               lost |-> IF ~hold /\ c.err = "" /\ isReq /\ own.st \notin DisconnectedOrReleasing THEN pre.st \o "/" \o c.tid ELSE "",
               dp |-> IF hold \/ c.err # "" THEN S.dp[s]
                      ELSE IF AnswersDisconnect(c.fx) THEN FALSE ELSE IF isReq THEN TRUE ELSE S.dp[s]]
        \* A held input changes nothing, so nothing can be released after it
        \* (Machine.Dispatch returns at once on a hold).
        rp == IF ran /\ ~hold THEN Replay(a0) ELSE a0
        ok == ran /\ rp.err = ""
        v1 == IF ran /\ rp.err # "" THEN Viol("MachineError", s, "interpreter error at station " \o s \o ": " \o rp.err)
              ELSE IF ok /\ rp.lost # "" /\ "DisconnectKept" \in StepChecks
                   THEN Viol("DisconnectKept", s, "station " \o s \o "'s layer 3 DL_DISCONNECT_request was consumed by " \o rp.lost \o ": the disconnect is lost")
              ELSE v0
        m == IF ran THEN rp.m ELSE pre
        \* With a local busy condition modelled, a station whose own receiver
        \* was busy delivers nothing either (the flow-off delivery check).
        injIn == IF e.ev = "I_received" /\ e.cmd = 1 /\ e.data \in InjLabels /\ e.ns = pre.vr THEN e.data ELSE ""
        r == Route(s, m, IF ok THEN rp.fx ELSE <<>>,
                   [out |-> S.ch[Other(s)], owed |-> S.owed[s] /\ ~clearOwed, deliv |-> S.deliv[s], up |-> S.up[s], conn |-> FALSE,
                    pend |-> S.pend[s], rep |-> S.rep[s], told |-> S.told[s],
                    tell |-> {"DL-ERROR Indication (F)"} \cup (IF e.fe # "" THEN {ErrCode(e.fe)} ELSE {}), v |-> v1,
                    \* The injected payload this dispatch may deliver: its own,
                    \* received in sequence (Stepper: injectedDeliverable).
                    inj |-> injIn],
                   S.off[s] \/ (BusyAt # {} /\ pre.own), 1)
        \* A link (re)establishment zeroes the variables without acknowledging anything.
        reset == e.ev \in {"SABM_received", "SABME_received", "UA_received"} /\ m.vs = 0 /\ m.va = 0 /\ m.vr = 0
        acked == IF ok /\ ~reset THEN S.acked[s] + ((m.va - pre.va) % m.mod) ELSE S.acked[s]
        \* The give-up rule (AcceptGiveUp): progress clears the station's loss
        \* count; a T1 expiry into Disconnected is a give-up, honest (1) when
        \* the losses since the last progress reach the page's retry-loop
        \* attempts, else unjustified (2, sticky).
        loss == IF AcceptGiveUp /\ ok /\ (r.conn \/ acked > S.acked[s]) THEN 0 ELSE S.loss[s]
        gave == IF AcceptGiveUp /\ ok /\ e.ev = "T1_expiry" /\ m.st = "Disconnected"
                THEN Max(S.gave[s], IF loss >= GiveUpThreshold(pre.st, S.t3loop[s]) THEN 1 ELSE 2)
                ELSE S.gave[s]
        \* A Timer Recovery loop entered by T3 lasts until the station leaves
        \* Timer Recovery.
        t3loop == IF ok THEN m.st = "TimerRecovery" /\ (S.t3loop[s] \/ e.ev = "T3_expiry") ELSE S.t3loop[s]
        flowOffNow == ok /\ e.ev = "DL_FLOW_OFF_request"
        flowOnNow == ok /\ e.ev = "DL_FLOW_ON_request"
        \* Keyed on the frame being UI (ftype), not on the event it is
        \* dispatched as, so a page that lost its UI arm is caught.
        uiv == IF ok /\ e.ftype = "UI" /\ UnitDataAt # {} /\ "UnitData" \in StepChecks
               THEN UnitDataCheck(s, pre, e, c.fx) ELSE NoViol
        \* Keyed on the corruption (fe), not on the event it is dispatched as.
        fev == IF ok /\ e.fe # "" /\ "FrameError" \in StepChecks
               THEN FrameErrorCheck(s, pre, e.fe, c.fx) ELSE NoViol
        \* The reset rule (FrameErrors): a station that sends SABM or SABME from
        \* Connected or TimerRecovery has reset the link, honestly (1) only on
        \* the corrupted frame itself, else unjustified (2, sticky). A reset
        \* layer 3 asked for (DL-CONNECT Request) is not the data link's.
        rst == IF FrameErrors # {} /\ ok /\ pre.st \in DataStates /\ e.ev # "DL_CONNECT_request"
                  /\ \E i \in 1..Len(c.fx) : c.fx[i].kind = "frame" /\ c.fx[i].frame.type \in {"SABM", "SABME"}
               THEN Max(S.rs[s], IF e.fe # "" THEN 1 ELSE 2) ELSE S.rs[s]
        \* The injected payload is delivered exactly when the frame is
        \* accepted (V(r) moves past its N(s)) in this dispatch.
        injv == IF ok /\ injIn # "" /\ "Delivery" \in StepChecks /\ (m.vr # pre.vr) # (r.inj = "")
                THEN Viol("Delivery", s, "station " \o s \o " handled the injected I frame '" \o injIn \o "' in sequence, but did not deliver exactly what it accepted")
                ELSE NoViol
        v2 == IF r.v # NoViol THEN r.v
              ELSE IF injv # NoViol THEN injv
              ELSE IF uiv # NoViol THEN uiv
              ELSE IF fev # NoViol THEN fev
              ELSE IF flowOffNow /\ "BusyTracksFlow" \in StepChecks /\ ~m.own
                   THEN Viol("BusyTracksFlow", s, "station " \o s \o " took " \o c.tid \o " on DL_FLOW_OFF_request but its own-receiver-busy condition is still clear")
              ELSE IF flowOnNow /\ "BusyTracksFlow" \in StepChecks /\ m.own
                   THEN Viol("BusyTracksFlow", s, "station " \o s \o " took " \o c.tid \o " on DL_FLOW_ON_request but its own-receiver-busy condition is still set")
              ELSE NoViol
    IN  [sys |-> [S EXCEPT !.m[s] = m, !.held[s] = IF ok THEN rp.held ELSE @, !.dp[s] = IF ok THEN rp.dp ELSE @,
                           !.ch[s] = newIn, !.ch[Other(s)] = r.out, !.owed[s] = r.owed,
                           !.budget = newBudget, !.deliv[s] = r.deliv, !.acked[s] = acked, !.up[s] = r.up, !.pend[s] = r.pend, !.rep[s] = r.rep,
                           !.loss[s] = loss, !.gave[s] = gave, !.t3loop[s] = t3loop,
                           !.rs[s] = rst, !.told[s] = IF FrameErrors = {} THEN 0 ELSE r.told,
                           !.off[s] = IF flowOffNow THEN TRUE ELSE IF flowOnNow THEN FALSE ELSE @,
                           !.rounds[s] = IF flowOnNow THEN @ + 1 ELSE @],
         viol |-> v2,
         tid |-> IF ok THEN c.tid ELSE "",
         arms |-> IF ok THEN rp.arms ELSE {}]

Inject(S, s, e, newIn, newBudget, clearOwed) == InjectAs(S, s, e, "", newIn, newBudget, clearOwed)

(***************************************************************************)
(* The v2.0 stub peer as station B (V20Peer.cs, pinned semantic 10)        *)
(***************************************************************************)

StubReceive(S, f, newIn, newBudget) ==
    LET up == S.stub.up
        dm1 == Frm("DM", FALSE, TRUE, None, None, "")
        say(u, frames, rule) == [up |-> u, out |-> frames, rule |-> rule, err |-> ""]
        r == IF ~f.cmd THEN say(up, <<>>, "v20:response_ignored")
             ELSE CASE f.type = "SABME" ->
                          IF S.stub.kind = "v20-dm" THEN say(up, <<dm1>>, "v20:sabme_is_not_sabm_dm_f1")
                          ELSE say(up, <<Frm("FRMR", FALSE, f.pf, None, None, "")>>, "v20:sabme_unknown_control_field_frmr")
                    [] f.type = "SABM" -> say(TRUE, <<Frm("UA", FALSE, f.pf, None, None, "")>>, "v20:sabm_accepted_ua")
                    [] f.type = "DISC" ->
                          IF up THEN say(FALSE, <<Frm("UA", FALSE, f.pf, None, None, "")>>, "v20:disc_ua")
                          ELSE say(up, <<dm1>>, "v20:disc_while_disconnected_dm_f1")
                    [] f.type = "UI" -> say(up, <<>>, "v20:ui_ignored")
                    [] f.type = "I" -> [up |-> up, out |-> <<>>, rule |-> "", err |-> "the v2.0 stub peer has no data phase: it received an I frame"]
                    [] f.type \in {"RR", "RNR", "REJ"} ->
                          IF ~up THEN say(up, <<dm1>>, "v20:command_while_disconnected_dm_f1")
                          ELSE IF f.pf THEN say(up, <<Frm("RR", FALSE, TRUE, 0, None, "")>>, "v20:poll_answered_rr_f1")
                          ELSE say(up, <<>>, "v20:supervisory_p0_ignored")
                    [] OTHER ->
                          IF up THEN say(up, <<Frm("FRMR", FALSE, f.pf, None, None, "")>>, "v20:unknown_control_field_frmr")
                          ELSE say(up, <<dm1>>, "v20:command_while_disconnected_dm_f1")
    IN  [sys |-> [S EXCEPT !.stub.up = r.up, !.ch["B"] = newIn, !.ch["A"] = @ \o r.out, !.budget = newBudget],
         viol |-> IF r.err # "" THEN Viol("MachineError", "B", "v2.0 stub peer at station B: " \o r.err) ELSE NoViol,
         tid |-> r.rule,
         arms |-> {}]

(***************************************************************************)
(* Seed (Stepper.Seed)                                                     *)
(***************************************************************************)

SeedMachine(s) ==
    LET st == IF Seed = "connected" THEN "Connected" ELSE "Disconnected"
    IN  [NewMachine(st) EXCEPT !.mod = IF Mod128A /\ s = "A" THEN 128 ELSE 8, !.k = K, !.n2 = N2,
                                !.srej = Srej, !.v22 = Srej, !.t3 = IF st = "Connected" THEN "running" ELSE "stopped"]

\* figc4.6's own entry state, as the golden trace frmr-fallback-downgrades-to-sabm
\* seeds it, plus the SABME that put A there, still on the air.
AwaitingV22A ==
    [NewMachine("AwaitingV22Connection") EXCEPT !.mod = 128, !.k = K, !.n2 = N2, !.v22 = TRUE, !.srej = Srej,
                                                !.rc = 1, !.l3 = TRUE, !.t1 = "running"]

S0 ==
    [m |-> [s \in Stations |-> SeedMachine(s)], ch |-> [s \in Stations |-> <<>>],
     owed |-> [s \in Stations |-> FALSE], budget |-> 0,
     deliv |-> [s \in Stations |-> 0], acked |-> [s \in Stations |-> 0],
     off |-> [s \in Stations |-> FALSE], rounds |-> [s \in Stations |-> 0],
     up |-> [s \in Stations |-> FALSE], offered |-> [s \in Stations |-> 0],
     rel |-> [s \in Stations |-> FALSE],
     loss |-> [s \in Stations |-> 0], gave |-> [s \in Stations |-> 0], heard |-> [s \in Stations |-> FALSE],
     rcn |-> [s \in Stations |-> FALSE], pend |-> [s \in Stations |-> FALSE], rep |-> [s \in Stations |-> 0],
     busy |-> [s \in Stations |-> FALSE], t3loop |-> [s \in Stations |-> FALSE], hb |-> [s \in Stations |-> FALSE],
     uis |-> [s \in Stations |-> FALSE], uid |-> [s \in Stations |-> FALSE],
     rs |-> [s \in Stations |-> 0], told |-> [s \in Stations |-> 0], inj |-> [s \in Stations |-> 0],
     held |-> [s \in Stations |-> <<>>], ab |-> [s \in Stations |-> FALSE], dp |-> [s \in Stations |-> FALSE],
     stub |-> [kind |-> Peer, up |-> FALSE]]

Seeded(r) == IF r.viol = NoViol THEN r.sys ELSE Assert(FALSE, "seeding failed: " \o r.viol.msg)

S1 ==
    CASE Seed = "disconnected" -> Seeded(Inject(S0, "A", Event(UpperEvent(S0.m["A"], "DL_CONNECT_request")), <<>>, 0, FALSE))
      [] Seed = "awaiting-v22" -> [S0 EXCEPT !.m["A"] = AwaitingV22A, !.ch["B"] = <<Frm("SABME", TRUE, TRUE, None, None, "")>>]
      [] OTHER -> S0

\* All data is submitted up front: one DL_DATA_request per label, A first.
\* With DataAfterConfirm nothing is: layer 3 submits by SubmitData moves.
RECURSIVE Submit(_, _, _)
Submit(S, s, i) ==
    IF i > SeedFrames(s) THEN S
    ELSE Submit(Seeded(Inject(S, s, [Event(UpperEvent(S.m[s], "DL_DATA_request")) EXCEPT !.data = Submitted(s)[i]], S.ch[s], S.budget, FALSE)), s, i + 1)

\* With DataBeforeConfirm, A's layer 3 treats the link as usable as soon as it
\* has asked for it.
SeedState == [IF DataAfterConfirm THEN S1 ELSE Submit(Submit(S1, "A", 1), "B", 1)
              EXCEPT !.budget = Budget, !.acked = [s \in Stations |-> 0],
                     !.up = [s \in Stations |-> @[s] \/ (s = "A" /\ DataAfterConfirm /\ DataBeforeConfirm)]]

(***************************************************************************)
(* The give-up rule's settled state (Stepper.GiveUpSettled)                *)
(***************************************************************************)

AtRest(st) == st = "Connected" \/ (TimerRecoveryQuiescent /\ st = "TimerRecovery")

\* With T3Moves, the keepalive rule (Stepper.KeepsAlive): at rest a Connected
\* station has T1 or T3 running.
KeepsAlive(m) == ~T3Moves \/ m.st # "Connected" \/ m.t1 = "running" \/ m.t3 = "running"

\* At rest each table-driven station's last link report to its layer 3
\* (rep: 0 none, 1 up, 2 down) matches its state (Stepper.ReportsMatch).
ReportsMatch(S) ==
    LET Match(r, st) == (r = 1 => st \in DataStates) /\ (r = 2 => st = "Disconnected")
    IN  Match(S.rep["A"], S.m["A"].st) /\ (StubB \/ Match(S.rep["B"], S.m["B"].st))

\* The give-up rule's settled state, data aside (Stepper.GiveUpSettled): some
\* station gave up honestly and none without justification, each table-driven
\* station Disconnected or at rest as plain quiescence has it and deaf to its
\* peer since the peer gave up (the stub always is), nothing in flight, nothing
\* owed.
GiveUpSettled(S) ==
    \* With T3Moves no station still up is settled: its T3 finds the peer gone.
    LET Settled(m, heard) == m.st = "Disconnected" \/ (~T3Moves /\ ~heard /\ AtRest(m.st) /\ m.q = <<>> /\ m.vs = m.va)
    IN  /\ (S.gave["A"] = 1 \/ S.gave["B"] = 1)
        /\ S.gave["A"] # 2 /\ S.gave["B"] # 2
        /\ ~S.pend["A"] /\ ~S.pend["B"] /\ ~S.dp["A"] /\ ~S.dp["B"]
        /\ ReportsMatch(S)
        /\ Settled(S.m["A"], S.heard["A"]) /\ (StubB \/ Settled(S.m["B"], S.heard["B"]))
        /\ S.ch["A"] = <<>> /\ S.ch["B"] = <<>>
        /\ ~S.owed["A"] /\ ~S.owed["B"]

AllDelivered(S) == S.deliv["B"] = Len(Submitted("A")) /\ S.deliv["A"] = Len(Submitted("B"))

\* A settled honest give-up that lost data (Stepper.IsGiveUpLoss): not rest, so
\* a deadlock there is reported as the loss it is (see last.lossy).
GiveUpLoss(S) == AcceptGiveUp /\ GiveUpSettled(S) /\ ~AllDelivered(S)

(***************************************************************************)
(* The reset rule's settled loss (Stepper.IsResetLoss)                     *)
(***************************************************************************)

\* At rest every UI datagram was sent and then reached its peer or was
\* dropped (Stepper.UnitDataAccounted).
UiAccounted(S) == \A s \in UnitDataAt : S.uis[s] /\ S.uid[s]

\* Plain quiescence, everything but the delivery count (Stepper.PlainRest).
PlainRest(S) ==
    LET a == S.m["A"]
        b == S.m["B"]
    IN  \* A station that abandoned its connect rests as a released one; a v2.0
        \* stub as B is then at rest once it is disconnected.
        /\ IF ReleaseAt # {} \/ S.ab["A"] \/ S.ab["B"]
           THEN /\ S.rel["A"] \/ S.rel["B"] \/ S.ab["A"] \/ S.ab["B"]
                /\ a.st = "Disconnected" /\ b.st = "Disconnected" /\ ~(StubB /\ S.stub.up)
                /\ a.q = <<>> /\ b.q = <<>> /\ ~S.owed["A"] /\ ~S.owed["B"]
           ELSE /\ AtRest(a.st) /\ a.q = <<>> /\ a.vs = a.va /\ ~S.owed["A"] /\ KeepsAlive(a)
                /\ IF StubB THEN S.stub.up ELSE AtRest(b.st) /\ b.q = <<>> /\ b.vs = b.va /\ ~S.owed["B"] /\ KeepsAlive(b)
        /\ S.ch["A"] = <<>> /\ S.ch["B"] = <<>>

\* With FrameErrors: a settled honest reset that lost data, the layer 3 of
\* every station whose data was lost told (a DL-ERROR, then a DL-CONNECT
\* confirm or indication: told = 2). Not rest; a deadlock there is reported
\* as the loss it is (see last.lossy).
ResetLoss(S) ==
    /\ FrameErrors # {} /\ ~AllDelivered(S)
    /\ \E s \in Stations : S.rs[s] = 1
    /\ \A s \in Stations : S.rs[s] # 2
    /\ ~S.pend["A"] /\ ~S.pend["B"] /\ ~S.dp["A"] /\ ~S.dp["B"] /\ ReportsMatch(S) /\ UiAccounted(S)
    /\ S.deliv["B"] < Len(Submitted("A")) => S.told["A"] = 2
    /\ S.deliv["A"] < Len(Submitted("B")) => S.told["B"] = 2
    /\ PlainRest(S)

(***************************************************************************)
(* Moves (Stepper.EnabledMoves / Apply)                                    *)
(***************************************************************************)

Take(r, move, s, f) ==
    /\ sys' = r.sys
    /\ viol' = r.viol
    \* tick flips on every move, so no move is ever a stuttering step: a move
    \* that changes nothing else (a self-loop) still counts as taken for the
    \* liveness fairness, and cannot discharge it by stuttering.
    \* lossy marks a settled honest give-up that lost data ("yes"), or a
    \* settled honest reset that lost data ("reset"), so TlaCheck can report a
    \* deadlock there as GiveUpLoss or ResetLoss, as the explorer does.
    /\ last' = [move |-> move, at |-> s, frame |-> f, tid |-> r.tid, arms |-> r.arms, tick |-> 1 - last.tick,
                 lossy |-> IF GiveUpLoss(r.sys) THEN "yes" ELSE IF ResetLoss(r.sys) THEN "reset" ELSE ""]

TakeChannel(S2, move, s, f) == Take([sys |-> S2, viol |-> NoViol, tid |-> "", arms |-> {}], move, s, f)

Table(s) == ~(s = "B" /\ StubB)

\* A pop is a move only when it changes something: a window-full or peer-busy
\* pop pushes the frame straight back, a self-loop.
PopChanges(S, s) ==
    LET m == S.m[s]
    IN  /\ Table(s)
        /\ m.q # <<>>
        /\ Handles(m.st, "I_frame_pops_off_queue")
        /\ LET c == Dispatch(m, Event("I_frame_pops_off_queue"))
           IN  c.err # "" \/ (\E i \in 1..Len(c.fx) : c.fx[i].kind = "frame") \/ Persist(c) # m

\* Layer 3 holds data and its link is up (DataAfterConfirm), or its reset
\* was answered, its last link report is up, and it has its one more frame
\* (DataAfterReconnect).
SubmitReady(S, s) ==
    \/ DataAfterConfirm /\ Table(s) /\ S.up[s] /\ S.offered[s] < Len(Submitted(s))
    \/ /\ DataAfterReconnect /\ Table(s) /\ S.offered[s] = 0 /\ SeedFrames(s) < Len(Submitted(s))
       /\ S.rcn[s] /\ ~S.pend[s] /\ S.rep[s] = 1 /\ S.m[s].st \in DataStates

\* Layer 3 releases an established link once everything either side
\* submitted has been delivered (ReleaseAt), once per station.
ReleaseReady(S, s) ==
    /\ s \in ReleaseAt /\ Table(s) /\ ~S.rel[s] /\ S.m[s].st \in (IF ReleaseEarly THEN LinkStates ELSE DataStates)
    /\ AllDelivered(S)

\* Layer 3 abandons its own connect (AbandonAt), once per station, while it
\* is being set up, and only while it has asked to connect and not heard
\* back: A from a connect-phase seed until its first link report, any
\* station after a reconnect until that is answered (Stepper.AbandonReady).
AbandonReady(S, s) ==
    /\ s \in AbandonAt /\ Table(s) /\ ~S.ab[s] /\ S.m[s].st \in {"AwaitingConnection", "AwaitingV22Connection"}
    /\ S.pend[s] \/ (s = "A" /\ Seed # "connected" /\ S.rep["A"] = 0)

\* Layer 3 resets a live or waiting link under the same gate (ReconnectAt),
\* once per station, and then waits for its answer (pend). The gate counts
\* only the frames submitted before it (not a DataAfterReconnect frame).
ReconnectReady(S, s) ==
    /\ s \in ReconnectAt /\ Table(s) /\ ~S.rcn[s] /\ S.m[s].st \in LinkStates
    /\ S.deliv["B"] = FramesAB /\ S.deliv["A"] = FramesBA

\* The local busy condition (BusyAt): entered once on an established link,
\* left whenever it still holds.
BusyOnReady(S, s) == s \in BusyAt /\ Table(s) /\ ~S.busy[s] /\ S.m[s].st \in DataStates /\ ~S.m[s].own
BusyOffReady(S, s) == s \in BusyAt /\ Table(s) /\ S.busy[s] /\ S.m[s].st \in DataStates /\ S.m[s].own

\* A short busy period leaves the peer one retry for the poll that finds it
\* cleared: while the station is busy its peer's T1 does not fire in Timer
\* Recovery once RC has reached N2 - 1 (Stepper.BusyHoldsT1), and only while
\* the busy station is in a data state (own receiver busy can outlive them,
\* and then the busy period can never end).
BusyHoldsT1(S, s) ==
    /\ BusyAt # {} /\ ~LongBusy
    /\ Other(s) \in BusyAt /\ S.m[Other(s)].own /\ S.m[Other(s)].st \in DataStates
    /\ S.m[s].st = "TimerRecovery" /\ S.m[s].rc >= N2 - 1

\* Entering the condition does what the figures draw for DL-FLOW-OFF Request
\* with #43 and H3 fixed: Set Own Receiver Busy, N(r) := V(r), RNR response
\* F = 0, Clear Acknowledge Pending. Leaving it does what they draw for
\* DL-FLOW-ON Request with H3's box: Clear Own Receiver Busy, N(r) := V(r),
\* RR command P = 0, Clear Acknowledge Pending, and when T1 is not running
\* Stop T3, Start T1 (Stepper.LocalBusy). No table row runs.
LocalBusy(S, s, enter) ==
    LET m == S.m[s]
        m2 == IF enter THEN [m EXCEPT !.own = TRUE, !.ack = FALSE]
              ELSE [m EXCEPT !.own = FALSE, !.ack = FALSE,
                             !.t3 = IF m.t1 # "running" THEN "stopped" ELSE @, !.t1 = "running"]
        f == IF enter THEN Frm("RNR", FALSE, FALSE, m.vr, None, "") ELSE Frm("RR", TRUE, FALSE, m.vr, None, "")
    IN  [sys |-> [S EXCEPT !.m[s] = m2, !.ch[Other(s)] = Append(@, f), !.busy[s] = @ \/ enter],
         viol |-> NoViol,
         tid |-> IF enter THEN "pinned:local_busy_enter" ELSE "pinned:local_busy_leave",
         arms |-> {}]

\* The station's layer 3 still has its UI datagram to send (UnitDataAt).
UnitDataReady(S, s) == s \in UnitDataAt /\ Table(s) /\ ~S.uis[s]

\* Layer 3 sends its UI datagram: a plain or long one through the page's
\* DL_UNIT_DATA_request arm, a poll (UI command, P = 1) or a response (UI
\* response, F = 0) put on the air by the harness (Stepper.SendUnitData).
SendUnitData(S, s) ==
    IF UiForm \in {"plain", "long"}
    THEN LET r == Inject(S, s, [Event(UpperEvent(S.m[s], "DL_UNIT_DATA_request")) EXCEPT !.data = UiLabel(s)], S.ch[s], S.budget, FALSE)
         IN  [r EXCEPT !.sys.uis[s] = TRUE]
    ELSE LET poll == UiForm = "poll"
         IN  [sys |-> [S EXCEPT !.ch[Other(s)] = Append(@, Frm("UI", poll, poll, None, None, UiLabel(s))), !.uis[s] = TRUE],
              viol |-> NoViol,
              tid |-> IF poll THEN "pinned:ui_poll" ELSE "pinned:ui_response",
              arms |-> {}]


\* The injected frame's gate (Stepper.InjectReady): a REJ poll only while the
\* receiver is in TimerRecovery, an SREJ command (P = 0 or 1) while it is in
\* Connected or TimerRecovery, each from a sender in one of those and naming a
\* frame outstanding at the receiver (with RejMayEqualVs also its V(s), while
\* it is in TimerRecovery with both channels empty, and for an SREJ with
\* nothing outstanding); a UA with F = 0 while the receiver waits on a connect
\* or release page; an FRMR while the receiver is in Connected or
\* TimerRecovery. The injected I frames: both stations in Connected or
\* TimerRecovery and no I frame on the channel to the receiver; in sequence
\* only with the receiver's V(r) the sender's V(s), the sender's queue empty
\* and everything delivered; ahead of sequence not naming the sender's V(s)
\* unless the sender is in TimerRecovery with both channels empty.
InjForm(n) == IF n = 0 THEN InjectForm ELSE InjectThen
InjCount == IF InjectThen = "none" THEN 1 ELSE 2
IForms == {"i-frame", "i-poll", "i-poll-ahead", "i-poll-stale"}

InjectReady(S, s) ==
    /\ s \in InjectAt /\ Table(s) /\ S.inj[s] < InjCount
    /\ LET m == S.m[s]
           r == S.m[Other(s)]
           form == InjForm(S.inj[s])
           empty == S.ch["A"] = <<>> /\ S.ch["B"] = <<>>
       IN  CASE form \in {"rej-poll", "srej-command", "srej-poll"} ->
                    /\ m.st \in DataStates
                    /\ IF form = "rej-poll" THEN r.st = "TimerRecovery" ELSE r.st \in DataStates
                    /\ \/ (m.vr - r.va) % r.mod < (r.vs - r.va) % r.mod
                       \/ /\ RejMayEqualVs /\ m.vr = r.vs /\ r.st = "TimerRecovery" /\ empty
                          /\ (form = "rej-poll" \/ r.va = r.vs)
             [] form = "ua-f0" -> r.st \in {"AwaitingConnection", "AwaitingV22Connection", "AwaitingRelease"}
             [] form = "frmr" -> r.st \in DataStates
             [] form \in IForms ->
                    /\ m.st \in DataStates /\ r.st \in DataStates
                    /\ \A i \in 1..Len(S.ch[Other(s)]) : S.ch[Other(s)][i].type # "I"
                    /\ CASE form \in {"i-frame", "i-poll"} -> r.vr = m.vs /\ m.q = <<>> /\ AllDelivered(S)
                         [] form = "i-poll-ahead" -> r.vr # m.vs \/ (m.st = "TimerRecovery" /\ empty)
                         [] OTHER -> TRUE
             [] OTHER -> FALSE

\* The harness puts the station's next injected frame on the air
\* (Stepper.InjectFrame), as from another implementation.
InjectFrame(S, s) ==
    LET m == S.m[s]
        r == S.m[Other(s)]
        n == S.inj[s]
        form == InjForm(n)
        f == CASE form = "rej-poll" -> Frm("REJ", TRUE, TRUE, m.vr, None, "")
               [] form = "srej-command" -> Frm("SREJ", TRUE, FALSE, m.vr, None, "")
               [] form = "srej-poll" -> Frm("SREJ", TRUE, TRUE, m.vr, None, "")
               [] form = "ua-f0" -> Frm("UA", FALSE, FALSE, None, None, "")
               [] form = "i-frame" -> Frm("I", TRUE, FALSE, m.vr, r.vr, InjLabel(s, n))
               [] form = "i-poll" -> Frm("I", TRUE, TRUE, m.vr, r.vr, InjLabel(s, n))
               [] form = "i-poll-ahead" -> Frm("I", TRUE, TRUE, m.vr, (r.vr + 1) % r.mod, InjLabel(s, n))
               [] form = "i-poll-stale" -> Frm("I", TRUE, TRUE, m.vr, (r.vr + r.mod - 1) % r.mod, InjLabel(s, n))
               [] OTHER -> Frm("FRMR", FALSE, FALSE, None, None, "")
    IN  [sys |-> [S EXCEPT !.ch[Other(s)] = Append(@, f), !.inj[s] = n + 1],
         viol |-> NoViol,
         tid |-> CASE form = "rej-poll" -> "pinned:inject_rej_poll"
                   [] form = "srej-command" -> "pinned:inject_srej_command"
                   [] form = "srej-poll" -> "pinned:inject_srej_poll"
                   [] form = "ua-f0" -> "pinned:inject_ua_f0"
                   [] form = "i-frame" -> "pinned:inject_i_frame"
                   [] form = "i-poll" -> "pinned:inject_i_poll"
                   [] form = "i-poll-ahead" -> "pinned:inject_i_poll_ahead"
                   [] form = "i-poll-stale" -> "pinned:inject_i_poll_stale"
                   [] OTHER -> "pinned:inject_frmr",
         arms |-> {}]

\* The quiescent-timeout abstraction: a timer fires only when nothing is in
\* flight, owed, ready to pop or waiting to be submitted anywhere. A pending
\* release does not hold T1 back: it is a layer-3 decision, as flow control is.
Quiet(S) ==
    /\ S.ch["A"] = <<>> /\ S.ch["B"] = <<>>
    /\ ~S.owed["A"] /\ ~S.owed["B"]
    /\ ~PopChanges(S, "A") /\ ~PopChanges(S, "B")
    /\ ~SubmitReady(S, "A") /\ ~SubmitReady(S, "B")

DropAllowed(f, into) ==
    \/ ~InteriorDropsOnly
    \/ LET sub == Submitted(Other(into))
       IN  f.type = "I" /\ f.data # "" /\ Len(sub) > 0 /\ f.data # sub[Len(sub)]

\* The non-fault moves' guards, named so the liveness check can say when a
\* move is enabled without TLC's ENABLED (see Liveness).
CanDeliver(s) == viol = NoViol /\ sys.ch[s] # <<>>
CanPop(s) == viol = NoViol /\ PopChanges(sys, s)
CanSeize(s) == viol = NoViol /\ Table(s) /\ sys.owed[s] /\ SeizeEvent(sys.m[s]) # ""
CanFlowOn(s) == viol = NoViol /\ Table(s) /\ s \in FlowAt /\ sys.off[s] /\ Handles(sys.m[s].st, "DL_FLOW_ON_request")
CanFlowOff(s) ==
    /\ viol = NoViol /\ Table(s) /\ s \in FlowAt /\ ~sys.off[s]
    /\ sys.rounds[s] < FlowRounds /\ sys.deliv[s] >= FlowOffAfter
    /\ Handles(sys.m[s].st, "DL_FLOW_OFF_request")
CanSubmit(s) == viol = NoViol /\ SubmitReady(sys, s)
CanRelease(s) == viol = NoViol /\ ReleaseReady(sys, s)
CanReconnect(s) == viol = NoViol /\ ReconnectReady(sys, s)
CanAbandon(s) == viol = NoViol /\ AbandonReady(sys, s)
CanT1(s) == viol = NoViol /\ Quiet(sys) /\ sys.m[s].t1 = "running" /\ Handles(sys.m[s].st, "T1_expiry") /\ ~BusyHoldsT1(sys, s)
\* T3 (the idle keepalive) fires under the same rule and only while no T1 runs
\* at either station (the figures stop T3 whenever they start T1, and T3 is
\* longer than T1, section 6.7.1.3), on a page with a T3 arm.
CanT3(s) ==
    /\ viol = NoViol /\ T3Moves /\ Table(s) /\ Quiet(sys)
    /\ sys.m["A"].t1 # "running" /\ (StubB \/ sys.m["B"].t1 # "running")
    /\ sys.m[s].t3 = "running" /\ Handles(sys.m[s].st, "T3_expiry") /\ s \in T3At
CanBusyOn(s) == viol = NoViol /\ BusyOnReady(sys, s)
CanBusyOff(s) == viol = NoViol /\ BusyOffReady(sys, s)
CanUnitData(s) == viol = NoViol /\ UnitDataReady(sys, s)

\* The give-up rule: a frame reaching s after its peer gave up was sent at or
\* after the give-up (T1 only fires with both channels empty), so s heard it.
Heard(r, s) == [r EXCEPT !.sys.heard[s] = @ \/ (AcceptGiveUp /\ sys.gave[Other(s)] # 0)]

\* The peer-busy hold (Stepper.HeardBusyAfter): an RNR reaching a table-driven
\* station raises it; RR, REJ, SREJ and the frames that reset or end the link
\* lower it.
HeardBusyAfter(h, t) ==
    IF t = "RNR" THEN TRUE
    ELSE IF t \in {"RR", "REJ", "SREJ", "SABM", "SABME", "UA", "DM", "DISC", "FRMR"} THEN FALSE
    ELSE h
HeardBusy(r, s, f) == IF Table(s) THEN [r EXCEPT !.sys.hb[s] = HeardBusyAfter(sys.hb[s], f.type)] ELSE r

\* A UI datagram is accounted for once it reaches its peer; the only UI
\* frames are datagrams.
UiDone(r, s, f) == IF UnitDataAt # {} /\ f.type = "UI" THEN [r EXCEPT !.sys.uid[Other(s)] = TRUE] ELSE r

Deliver(s) ==
    /\ CanDeliver(s)
    /\ LET f == Head(sys.ch[s])
       IN  Take(UiDone(HeardBusy(Heard(IF Table(s) THEN Inject(sys, s, FrameInput(sys.m[s], f, s), Tail(sys.ch[s]), sys.budget, FALSE)
                      ELSE StubReceive(sys, f, Tail(sys.ch[s]), sys.budget), s), s, f), s, f), "deliver", s, f)

Drop(s) ==
    /\ viol = NoViol
    /\ sys.ch[s] # <<>> /\ sys.budget > 0 /\ "drop" \in Faults
    /\ DropAllowed(Head(sys.ch[s]), s)
    \* The give-up rule counts every drop as a loss for both stations; a
    \* dropped UI datagram is accounted for (honestly lost).
    /\ TakeChannel([sys EXCEPT !.ch[s] = Tail(@), !.budget = @ - 1,
                               !.loss = IF AcceptGiveUp THEN [x \in Stations |-> @[x] + 1] ELSE @,
                               !.uid[Other(s)] = @ \/ (UnitDataAt # {} /\ Head(sys.ch[s]).type = "UI")], "drop", s, Head(sys.ch[s]))

Dup(s) ==
    /\ viol = NoViol
    /\ sys.ch[s] # <<>> /\ sys.budget > 0 /\ "dup" \in Faults
    /\ LET f == Head(sys.ch[s])
       IN  Take(UiDone(HeardBusy(Heard(IF Table(s) THEN Inject(sys, s, FrameInput(sys.m[s], f, s), sys.ch[s], sys.budget - 1, FALSE)
                      ELSE StubReceive(sys, f, sys.ch[s], sys.budget - 1), s), s, f), s, f), "duplicate", s, f)

Reorder(s) ==
    /\ viol = NoViol
    /\ Len(sys.ch[s]) >= 2 /\ sys.budget > 0 /\ "reorder" \in Faults
    /\ LET q == sys.ch[s]
       IN  TakeChannel([sys EXCEPT !.ch[s] = <<q[2], q[1]>> \o SubSeq(q, 3, Len(q)), !.budget = @ - 1], "reorder", s, q[1])

\* Frame errors (FrameErrors, Stepper.CorruptApplies): a channel fault that
\* delivers the head frame corrupted. A control field error can hit any frame;
\* an information field where none is permitted an S or U frame other than UI
\* and FRMR; a length error an S or U frame other than UI; the N1 failure and
\* the I response an I frame, and only at a station in Connected or
\* TimerRecovery (the pages with an I frame input).
CorruptApplies(form, f, m) ==
    CASE form = "control" -> TRUE
      [] form = "info" -> f.type \notin {"I", "UI", "FRMR"}
      [] form = "length" -> f.type \notin {"I", "UI"}
      [] OTHER -> f.type = "I" /\ m.st \in DataStates

\* The input the receiver sees: the frame-error event (the page's lower-layer
\* catch-all if it has no arm), or the I frame with the N1 atom false or the
\* command bit lost. The frame is not received as itself (ftype "").
CorruptInput(m, f, s, form) ==
    IF form \in {"n1", "iresp"}
    THEN [FrameInput(m, IF form = "iresp" THEN [f EXCEPT !.cmd = FALSE] ELSE f, s)
             EXCEPT !.info = IF form = "n1" THEN FALSE ELSE @, !.ftype = "", !.fe = form]
    ELSE [Event(IF Handles(m.st, ErrEvent(form)) THEN ErrEvent(form)
                ELSE IF Handles(m.st, "all_other_primitives__from_lower_layer") THEN "all_other_primitives__from_lower_layer"
                ELSE "") EXCEPT !.fe = form]

\* The give-up rule counts a corrupted frame as a loss, as a drop.
CountLoss(r) == IF AcceptGiveUp THEN [r EXCEPT !.sys.loss = [x \in Stations |-> @[x] + 1]] ELSE r

Corrupt(s, form) ==
    /\ viol = NoViol
    /\ Table(s) /\ form \in FrameErrors
    /\ sys.ch[s] # <<>> /\ sys.budget > 0
    /\ CorruptApplies(form, Head(sys.ch[s]), sys.m[s])
    /\ LET f == Head(sys.ch[s])
       IN  Take(UiDone(Heard(CountLoss(Inject(sys, s, CorruptInput(sys.m[s], f, s, form), Tail(sys.ch[s]), sys.budget - 1, FALSE)), s), s, f),
                "corrupt-" \o form, s, f)

\* A pop is the only move that sends I frames: one sent while the peer-busy
\* hold is up is a violation (PeerBusyHold).
Pop(s) ==
    /\ CanPop(s)
    /\ LET r == Inject(sys, s, Event("I_frame_pops_off_queue"), sys.ch[s], sys.budget, FALSE)
           out == r.sys.ch[Other(s)]
           sentI == \E i \in (Len(sys.ch[Other(s)]) + 1)..Len(out) : out[i].type = "I"
       IN  Take(IF r.viol = NoViol /\ "PeerBusyHold" \in StepChecks /\ sys.hb[s] /\ sentI
                THEN [r EXCEPT !.viol = Viol("PeerBusyHold", s, "station " \o s \o " sent an I frame although its peer's RNR has not been cleared")]
                ELSE r, "pop", s, NoFrm)

Seize(s) ==
    /\ CanSeize(s)
    /\ Take(Inject(sys, s, Event(SeizeEvent(sys.m[s])), sys.ch[s], sys.budget, TRUE), "seize-confirm", s, NoFrm)

FlowOn(s) ==
    /\ CanFlowOn(s)
    /\ Take(Inject(sys, s, Event("DL_FLOW_ON_request"), sys.ch[s], sys.budget, FALSE), "flow-on", s, NoFrm)

FlowOff(s) ==
    /\ CanFlowOff(s)
    /\ Take(Inject(sys, s, Event("DL_FLOW_OFF_request"), sys.ch[s], sys.budget, FALSE), "flow-off", s, NoFrm)

\* Layer 3 submits its next frame (DataAfterConfirm, or DataAfterReconnect,
\* whose offered count starts after the seed's frames).
SubmitData(s) ==
    /\ CanSubmit(s)
    /\ LET r == Inject(sys, s, [Event(UpperEvent(sys.m[s], "DL_DATA_request")) EXCEPT !.data = Submitted(s)[(IF DataAfterReconnect THEN SeedFrames(s) ELSE 0) + sys.offered[s] + 1]], sys.ch[s], sys.budget, FALSE)
       IN  Take([r EXCEPT !.sys.offered[s] = @ + 1], "submit", s, NoFrm)

\* Layer 3 releases the link (ReleaseAt).
Release(s) ==
    /\ CanRelease(s)
    /\ LET r == InjectAs(sys, s, Event(UpperEvent(sys.m[s], "DL_DISCONNECT_request")), "DL_DISCONNECT_request", sys.ch[s], sys.budget, FALSE)
       IN  Take([r EXCEPT !.sys.rel[s] = TRUE], "release", s, NoFrm)

\* Layer 3 abandons its connect (AbandonAt).
Abandon(s) ==
    /\ CanAbandon(s)
    /\ LET r == InjectAs(sys, s, Event(UpperEvent(sys.m[s], "DL_DISCONNECT_request")), "DL_DISCONNECT_request", sys.ch[s], sys.budget, FALSE)
       IN  Take([r EXCEPT !.sys.ab[s] = TRUE], "abandon", s, NoFrm)

\* Layer 3 resets the link (ReconnectAt).
Reconnect(s) ==
    /\ CanReconnect(s)
    /\ LET r == Inject(sys, s, Event(UpperEvent(sys.m[s], "DL_CONNECT_request")), sys.ch[s], sys.budget, FALSE)
           \* pend is set after the dispatch, so an arm that answered the
           \* request within the same dispatch would be overwritten and the
           \* request left pending. No current arm does: each sends SABM(E)
           \* or nothing.
       IN  Take([r EXCEPT !.sys.rcn[s] = TRUE, !.sys.pend[s] = TRUE], "reconnect", s, NoFrm)

T1(s) ==
    /\ CanT1(s)
    /\ Take(Inject(sys, s, Event("T1_expiry"), sys.ch[s], sys.budget, FALSE), "T1-expiry", s, NoFrm)

T3(s) ==
    /\ CanT3(s)
    /\ Take(Inject(sys, s, Event("T3_expiry"), sys.ch[s], sys.budget, FALSE), "T3-expiry", s, NoFrm)

\* The local busy condition (BusyAt).
BusyOn(s) ==
    /\ CanBusyOn(s)
    /\ Take(LocalBusy(sys, s, TRUE), "busy-on", s, NoFrm)

BusyOff(s) ==
    /\ CanBusyOff(s)
    /\ Take(LocalBusy(sys, s, FALSE), "busy-off", s, NoFrm)

\* Layer 3 sends its UI datagram (UnitDataAt).
UnitData(s) ==
    /\ CanUnitData(s)
    /\ Take(SendUnitData(sys, s), "unit-data", s, NoFrm)

\* The harness's injected frame (InjectAt). Like a channel fault it is not a
\* progress move for the liveness checks.
InjectMove(s) ==
    /\ viol = NoViol /\ InjectReady(sys, s)
    /\ Take(InjectFrame(sys, s), "inject", s, NoFrm)

(***************************************************************************)
(* Quiescence                                                              *)
(***************************************************************************)

\* Both stations at rest, nothing in flight or queued, everything sent
\* acknowledged and everything submitted delivered, nothing owed. A v2.0
\* stub is at rest once it is connected. With ReleaseAt, at rest means
\* released: both Disconnected after a layer 3 issued its release.
PlainQuiescent ==
    /\ PlainRest(sys)
    /\ AllDelivered(sys)

\* With AcceptGiveUp, a settled honest give-up with everything delivered is at
\* rest too, and nothing is once a station has given up without justification.
\* A layer 3 still waiting for the answer to its reconnect is never at rest.
Quiescent ==
    /\ ~sys.pend["A"] /\ ~sys.pend["B"]
    \* Nor one whose DL_DISCONNECT_request was consumed and not answered.
    /\ ~sys.dp["A"] /\ ~sys.dp["B"]
    \* The reset rule: no rest after a reset the injected error does not justify.
    /\ ~(FrameErrors # {} /\ \E s \in Stations : sys.rs[s] = 2)
    /\ ReportsMatch(sys)
    /\ UiAccounted(sys)
    /\ \/ AcceptGiveUp /\ GiveUpSettled(sys) /\ AllDelivered(sys)
       \/ /\ ~(AcceptGiveUp /\ (sys.gave["A"] = 2 \/ sys.gave["B"] = 2))
          /\ PlainQuiescent

\* A quiescent state stutters, so TLC's deadlock check reports exactly the
\* explorer's deadlocks: terminal states that are not quiescent.
Rest == viol = NoViol /\ Quiescent /\ UNCHANGED vars

(***************************************************************************)
(* The specification                                                       *)
(***************************************************************************)

Init == sys = SeedState /\ viol = NoViol /\ last = NoStep

\* The explorer's move order, so that both breadth-first searches meet the
\* same first violation.
Next ==
    \/ Deliver("A") \/ Drop("A") \/ Dup("A") \/ Reorder("A")
       \/ Corrupt("A", "control") \/ Corrupt("A", "info") \/ Corrupt("A", "length") \/ Corrupt("A", "n1") \/ Corrupt("A", "iresp")
       \/ Pop("A") \/ Seize("A") \/ FlowOn("A") \/ FlowOff("A") \/ SubmitData("A") \/ Release("A") \/ Reconnect("A") \/ Abandon("A") \/ BusyOn("A") \/ BusyOff("A") \/ UnitData("A") \/ InjectMove("A")
    \/ Deliver("B") \/ Drop("B") \/ Dup("B") \/ Reorder("B")
       \/ Corrupt("B", "control") \/ Corrupt("B", "info") \/ Corrupt("B", "length") \/ Corrupt("B", "n1") \/ Corrupt("B", "iresp")
       \/ Pop("B") \/ Seize("B") \/ FlowOn("B") \/ FlowOff("B") \/ SubmitData("B") \/ Release("B") \/ Reconnect("B") \/ Abandon("B") \/ BusyOn("B") \/ BusyOff("B") \/ UnitData("B") \/ InjectMove("B")
    \/ T1("A") \/ T1("B")
    \/ T3("A") \/ T3("B")
    \/ Rest

Spec == Init /\ [][Next]_vars

(***************************************************************************)
(* Invariants                                                              *)
(***************************************************************************)

\* The per-step invariants (StepChecks: RejectCoherence, Delivery,
\* FlowOffDelivery, BusyTracksFlow) and MachineError, which is always on.
NoStepViolation == viol = NoViol

\* Both stations are in a state the tables define.
DefinedState == \A s \in Stations : sys.m[s].st \in KnownStates

\* V(s), V(a), V(r) in range; outstanding frames within k; SREJ exceptions
\* within k (each names a distinct gap in the window).
SequenceSanity ==
    \A s \in Stations :
        LET m == sys.m[s]
        IN  /\ m.vs \in 0..(m.mod - 1) /\ m.va \in 0..(m.mod - 1) /\ m.vr \in 0..(m.mod - 1)
            /\ (m.vs - m.va) % m.mod <= m.k
            /\ m.srejx <= m.k

\* A station never sees acknowledged more of its frames than the peer delivered.
AckCoherence == \A s \in Stations : sys.acked[s] <= sys.deliv[Other(s)]

(***************************************************************************)
(* Bounds                                                                  *)
(***************************************************************************)

\* The explorer's depth bound, for the one calibration row that needs it.
WithinDepth == TLCGet("level") <= MaxDepth

(***************************************************************************)
(* Liveness                                                                *)
(***************************************************************************)

\* The explorer checks "from every reachable state a fault-free path reaches
\* quiescence" on its state graph (AG EF over the non-fault edges). TLC
\* checks LTL, so it is stated as: if some non-fault move keeps being taken
\* whenever one is enabled (weak fairness on their disjunction), quiescence
\* recurs. That is sound in the direction that matters: a state the explorer
\* would report lets a fair behaviour circle in a part of the graph with no
\* way to quiescence, so a clean TLC verdict implies the explorer's. A TLC
\* counterexample can be spurious (a fair cycle that ignores a move which
\* would have led out), so each one is compared with the explorer's verdict.
\*
\* Two TLC costs shape the formulation (docs/tla.md). Strong fairness per
\* move would be tighter, but TLC expands n strong-fairness conjuncts into
\* 2^n cases and did not finish on 42 states in ten minutes. And WF_vars(A)
\* needs ENABLED <<A>>_vars, which TLC evaluates without its usual caching
\* and did not finish on 3 states. So the fairness is written out: the
\* enabledness is the moves' own guards, and "a progress move was taken" is
\* read off the step's record in last.
\*
\* The argument needs every progress move to be a real (non-stuttering) step,
\* or a move that changes nothing could be "taken" without TLC seeing it and
\* the fairness would be discharged falsely. The tick in last guarantees it.
ProgressKinds == {"deliver", "pop", "seize-confirm", "flow-on", "flow-off", "submit", "release", "reconnect", "abandon", "busy-on", "busy-off", "unit-data"}

CanProgress == \E s \in Stations : CanDeliver(s) \/ CanPop(s) \/ CanSeize(s) \/ CanFlowOn(s) \/ CanFlowOff(s) \/ CanSubmit(s) \/ CanRelease(s) \/ CanReconnect(s) \/ CanAbandon(s) \/ CanBusyOn(s) \/ CanBusyOff(s) \/ CanUnitData(s)

\* The timers: T1, and T3 with T3Moves.
CanProgressOrT1 == CanProgress \/ \E s \in Stations : CanT1(s) \/ CanT3(s)
TookProgress == last'.move \in ProgressKinds
TookProgressOrT1 == last'.move \in ProgressKinds \cup {"T1-expiry", "T3-expiry"}

\* The explorer's Quiescence: T1 expiries count as progress.
QuiescenceLive == (<>[]CanProgressOrT1 => []<><<TookProgressOrT1>>_vars) => []<>Quiescent

\* The explorer's SelectiveProgress (timer-free): T1 is not progress, so a
\* path that completes only through a T1 expiry does not count.
TimerFreeLive == (<>[]CanProgress => []<><<TookProgress>>_vars) => []<>Quiescent

\* With T3Moves or BusyAt (used instead of the two above): the keepalive, or
\* a peer polling a busy station that answers RNR, gives a cycle of moves
\* (poll, answer, back to Connected) that the disjunctive fairness accepts
\* for ever while a one-shot layer 3 move (a release, a reconnect, leaving
\* the busy condition, a submission) waits, so TLC would report a lasso
\* where the explorer's AG EF finds a path out. Each such move gets its own
\* weak fairness too. Still sound in the same direction: from a region with
\* no path to quiescence a round-robin schedule meets every fairness
\* conjunct without leaving it. Kept apart from the two above so that rows
\* without these options check exactly the formula they always did.
Took(kind, s) == last'.move = kind /\ last'.at = s
OneShotFair ==
    \A s \in Stations :
        /\ (<>[]CanRelease(s) => []<><<Took("release", s)>>_vars)
        /\ (<>[]CanReconnect(s) => []<><<Took("reconnect", s)>>_vars)
        /\ (<>[]CanAbandon(s) => []<><<Took("abandon", s)>>_vars)
        /\ (<>[]CanBusyOff(s) => []<><<Took("busy-off", s)>>_vars)
        /\ (<>[]CanSubmit(s) => []<><<Took("submit", s)>>_vars)
        /\ (<>[]CanUnitData(s) => []<><<Took("unit-data", s)>>_vars)
QuiescenceLiveL3 == ((<>[]CanProgressOrT1 => []<><<TookProgressOrT1>>_vars) /\ OneShotFair) => []<>Quiescent
TimerFreeLiveL3 == ((<>[]CanProgress => []<><<TookProgress>>_vars) /\ OneShotFair) => []<>Quiescent

=============================================================================

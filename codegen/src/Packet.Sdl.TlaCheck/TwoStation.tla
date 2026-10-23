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
    PeerDeclinesSabme, \* table-driven B refuses SABME (able_to_establish false) but accepts SABM
    Peer,              \* what plays B: "tables", "v20-frmr" or "v20-dm"
    FlowAt,            \* stations whose layer 3 issues DL_FLOW_OFF / DL_FLOW_ON, subset of {"A", "B"}
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
View == <<sys, viol>>

Stations == {"A", "B"}
Other(s) == IF s = "A" THEN "B" ELSE "A"

\* What each station's layer 3 submitted, in order.
Submitted(s) ==
    IF s = "A" THEN [i \in 1..FramesAB |-> "a" \o ToString(i - 1)]
    ELSE [i \in 1..FramesBA |-> "b" \o ToString(i - 1)]

StubB == Peer # "tables"
DataStates == {"Connected", "TimerRecovery"}
KnownStates == {"Disconnected", "AwaitingConnection", "AwaitingV22Connection", "Connected", "AwaitingRelease", "TimerRecovery"}

NoViol == [kind |-> "none", at |-> "", msg |-> ""]
Viol(kind, s, msg) == [kind |-> kind, at |-> s, msg |-> msg]
NoStep == [move |-> "", at |-> "", frame |-> NoFrm, tid |-> "", arms |-> {}, tick |-> 0]

N(num) == ToString(num)
FrameText(f) ==
    f.type \o (IF f.cmd THEN " cmd P=" ELSE " rsp F=") \o (IF f.pf THEN "1" ELSE "0")
    \o (IF f.ns # None THEN " ns=" \o N(f.ns) ELSE "") \o (IF f.nr # None THEN " nr=" \o N(f.nr) ELSE "")
    \o (IF f.data # "" THEN " [" \o f.data \o "]" ELSE "")

(***************************************************************************)
(* Event routing (Stepper.FrameEvent, SeizeConfirmEvent, FrameInput)       *)
(***************************************************************************)

\* A received frame raises its own *_received arm if the page has one, else
\* the page's catch-all (pinned semantic 5); "" when the page has neither.
FrameEvent(m, f) ==
    LET direct == f.type \o "_received"
    IN  IF Handles(m.st, direct) THEN direct
        ELSE IF f.cmd /\ Handles(m.st, "all_other_commands") THEN "all_other_commands"
        ELSE IF f.cmd /\ f.type \in {"I", "RR", "RNR", "REJ", "SREJ"} /\ Handles(m.st, "i_or_s_command_received") THEN "i_or_s_command_received"
        ELSE IF Handles(m.st, "all_other_primitives__from_lower_layer") THEN "all_other_primitives__from_lower_layer"
        ELSE ""

FrameInput(m, f, into) ==
    [ev |-> FrameEvent(m, f), pf |-> IF f.pf THEN 1 ELSE 0, cmd |-> IF f.cmd THEN 1 ELSE 0,
     nr |-> f.nr, ns |-> f.ns, data |-> f.data,
     able |-> ~(PeerDeclinesSabme /\ into = "B" /\ f.type = "SABME"), info |-> TRUE]

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

\* Route one dispatch's effects in order: frames to the outgoing channel
\* (checking reject coherence), deliveries to the delivery count (checking
\* order and flow-off), a seize request to the owed flag.
RECURSIVE Route(_, _, _, _, _, _)
Route(s, m, fx, acc, flowOff, i) ==
    IF i > Len(fx) THEN acc
    ELSE LET fe == fx[i]
             sub == Submitted(Other(s))
             nxt ==
                IF fe.kind = "frame" THEN
                    [acc EXCEPT !.out = Append(@, fe.frame),
                                !.v = IF @ = NoViol /\ fe.frame.type \in {"REJ", "SREJ"} /\ "RejectCoherence" \in StepChecks
                                      THEN EmitterReject(s, m, fe.frame) ELSE @]
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
                ELSE IF fe.kind = "lm" /\ fe.prim = "LM_seize_request" THEN [acc EXCEPT !.owed = TRUE]
                ELSE acc
         IN  Route(s, m, fx, nxt, flowOff, i + 1)

(***************************************************************************)
(* One stimulus at one station (Stepper.Inject)                            *)
(***************************************************************************)

\* Dispatch e at station s of system S; newIn becomes s's incoming channel.
\* Returns the next system state, the per-step violation and the transition.
Inject(S, s, e, newIn, newBudget, clearOwed) ==
    LET pre == S.m[s]
        v0 == IF e.ev = "" THEN Viol("MachineError", s, "station " \o s \o " in state " \o pre.st \o " has no arm (direct or catch-all) for the received frame")
              ELSE IF "RejectCoherence" \in StepChecks /\ e.ev \in {"REJ_received", "SREJ_received"} /\ pre.st \in DataStates
                   THEN ReceiverReject(s, pre, e)
              ELSE NoViol
        ran == v0 = NoViol
        c == Dispatch(pre, e)
        ok == ran /\ c.err = ""
        v1 == IF ran /\ c.err # "" THEN Viol("MachineError", s, "interpreter error at station " \o s \o ": " \o c.err) ELSE v0
        m == IF ran THEN Persist(c) ELSE pre
        r == Route(s, m, IF ok THEN c.fx ELSE <<>>,
                   [out |-> S.ch[Other(s)], owed |-> S.owed[s] /\ ~clearOwed, deliv |-> S.deliv[s], v |-> v1],
                   S.off[s], 1)
        \* A link (re)establishment zeroes the variables without acknowledging anything.
        reset == e.ev \in {"SABM_received", "SABME_received", "UA_received"} /\ m.vs = 0 /\ m.va = 0 /\ m.vr = 0
        acked == IF ok /\ ~reset THEN S.acked[s] + ((m.va - pre.va) % m.mod) ELSE S.acked[s]
        flowOffNow == ok /\ e.ev = "DL_FLOW_OFF_request"
        flowOnNow == ok /\ e.ev = "DL_FLOW_ON_request"
        v2 == IF r.v # NoViol THEN r.v
              ELSE IF flowOffNow /\ "BusyTracksFlow" \in StepChecks /\ ~m.own
                   THEN Viol("BusyTracksFlow", s, "station " \o s \o " took " \o c.tid \o " on DL_FLOW_OFF_request but its own-receiver-busy condition is still clear")
              ELSE IF flowOnNow /\ "BusyTracksFlow" \in StepChecks /\ m.own
                   THEN Viol("BusyTracksFlow", s, "station " \o s \o " took " \o c.tid \o " on DL_FLOW_ON_request but its own-receiver-busy condition is still set")
              ELSE NoViol
    IN  [sys |-> [S EXCEPT !.m[s] = m, !.ch[s] = newIn, !.ch[Other(s)] = r.out, !.owed[s] = r.owed,
                           !.budget = newBudget, !.deliv[s] = r.deliv, !.acked[s] = acked,
                           !.off[s] = IF flowOffNow THEN TRUE ELSE IF flowOnNow THEN FALSE ELSE @,
                           !.rounds[s] = IF flowOnNow THEN @ + 1 ELSE @],
         viol |-> v2,
         tid |-> IF ok THEN c.tid ELSE "",
         arms |-> IF ok THEN c.arms ELSE {}]

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
     stub |-> [kind |-> Peer, up |-> FALSE]]

Seeded(r) == IF r.viol = NoViol THEN r.sys ELSE Assert(FALSE, "seeding failed: " \o r.viol.msg)

S1 ==
    CASE Seed = "disconnected" -> Seeded(Inject(S0, "A", Event("DL_CONNECT_request"), <<>>, 0, FALSE))
      [] Seed = "awaiting-v22" -> [S0 EXCEPT !.m["A"] = AwaitingV22A, !.ch["B"] = <<Frm("SABME", TRUE, TRUE, None, None, "")>>]
      [] OTHER -> S0

\* All data is submitted up front: one DL_DATA_request per label, A first.
RECURSIVE Submit(_, _, _)
Submit(S, s, i) ==
    IF i > Len(Submitted(s)) THEN S
    ELSE Submit(Seeded(Inject(S, s, [Event("DL_DATA_request") EXCEPT !.data = Submitted(s)[i]], S.ch[s], S.budget, FALSE)), s, i + 1)

SeedState == [Submit(Submit(S1, "A", 1), "B", 1) EXCEPT !.budget = Budget, !.acked = [s \in Stations |-> 0]]

(***************************************************************************)
(* Moves (Stepper.EnabledMoves / Apply)                                    *)
(***************************************************************************)

Take(r, move, s, f) ==
    /\ sys' = r.sys
    /\ viol' = r.viol
    \* tick flips on every move, so no move is ever a stuttering step: a move
    \* that changes nothing else (a self-loop) still counts as taken for the
    \* liveness fairness, and cannot discharge it by stuttering.
    /\ last' = [move |-> move, at |-> s, frame |-> f, tid |-> r.tid, arms |-> r.arms, tick |-> 1 - last.tick]

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

\* The quiescent-timeout abstraction: a timer fires only when nothing is in
\* flight, owed or ready to pop anywhere.
Quiet(S) ==
    /\ S.ch["A"] = <<>> /\ S.ch["B"] = <<>>
    /\ ~S.owed["A"] /\ ~S.owed["B"]
    /\ ~PopChanges(S, "A") /\ ~PopChanges(S, "B")

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
CanT1(s) == viol = NoViol /\ Quiet(sys) /\ sys.m[s].t1 = "running" /\ Handles(sys.m[s].st, "T1_expiry")

Deliver(s) ==
    /\ CanDeliver(s)
    /\ LET f == Head(sys.ch[s])
       IN  Take(IF Table(s) THEN Inject(sys, s, FrameInput(sys.m[s], f, s), Tail(sys.ch[s]), sys.budget, FALSE)
                ELSE StubReceive(sys, f, Tail(sys.ch[s]), sys.budget), "deliver", s, f)

Drop(s) ==
    /\ viol = NoViol
    /\ sys.ch[s] # <<>> /\ sys.budget > 0 /\ "drop" \in Faults
    /\ DropAllowed(Head(sys.ch[s]), s)
    /\ TakeChannel([sys EXCEPT !.ch[s] = Tail(@), !.budget = @ - 1], "drop", s, Head(sys.ch[s]))

Dup(s) ==
    /\ viol = NoViol
    /\ sys.ch[s] # <<>> /\ sys.budget > 0 /\ "dup" \in Faults
    /\ LET f == Head(sys.ch[s])
       IN  Take(IF Table(s) THEN Inject(sys, s, FrameInput(sys.m[s], f, s), sys.ch[s], sys.budget - 1, FALSE)
                ELSE StubReceive(sys, f, sys.ch[s], sys.budget - 1), "duplicate", s, f)

Reorder(s) ==
    /\ viol = NoViol
    /\ Len(sys.ch[s]) >= 2 /\ sys.budget > 0 /\ "reorder" \in Faults
    /\ LET q == sys.ch[s]
       IN  TakeChannel([sys EXCEPT !.ch[s] = <<q[2], q[1]>> \o SubSeq(q, 3, Len(q)), !.budget = @ - 1], "reorder", s, q[1])

Pop(s) ==
    /\ CanPop(s)
    /\ Take(Inject(sys, s, Event("I_frame_pops_off_queue"), sys.ch[s], sys.budget, FALSE), "pop", s, NoFrm)

Seize(s) ==
    /\ CanSeize(s)
    /\ Take(Inject(sys, s, Event(SeizeEvent(sys.m[s])), sys.ch[s], sys.budget, TRUE), "seize-confirm", s, NoFrm)

FlowOn(s) ==
    /\ CanFlowOn(s)
    /\ Take(Inject(sys, s, Event("DL_FLOW_ON_request"), sys.ch[s], sys.budget, FALSE), "flow-on", s, NoFrm)

FlowOff(s) ==
    /\ CanFlowOff(s)
    /\ Take(Inject(sys, s, Event("DL_FLOW_OFF_request"), sys.ch[s], sys.budget, FALSE), "flow-off", s, NoFrm)

T1(s) ==
    /\ CanT1(s)
    /\ Take(Inject(sys, s, Event("T1_expiry"), sys.ch[s], sys.budget, FALSE), "T1-expiry", s, NoFrm)

(***************************************************************************)
(* Quiescence                                                              *)
(***************************************************************************)

AtRest(st) == st = "Connected" \/ (TimerRecoveryQuiescent /\ st = "TimerRecovery")

\* Both stations at rest, nothing in flight or queued, everything sent
\* acknowledged and everything submitted delivered, nothing owed. A v2.0
\* stub is at rest once it is connected.
Quiescent ==
    LET a == sys.m["A"]
        b == sys.m["B"]
    IN  /\ AtRest(a.st) /\ a.q = <<>> /\ a.vs = a.va /\ ~sys.owed["A"]
        /\ IF StubB THEN sys.stub.up ELSE AtRest(b.st) /\ b.q = <<>> /\ b.vs = b.va /\ ~sys.owed["B"]
        /\ sys.ch["A"] = <<>> /\ sys.ch["B"] = <<>>
        /\ sys.deliv["B"] = FramesAB /\ sys.deliv["A"] = FramesBA

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
    \/ Deliver("A") \/ Drop("A") \/ Dup("A") \/ Reorder("A") \/ Pop("A") \/ Seize("A") \/ FlowOn("A") \/ FlowOff("A")
    \/ Deliver("B") \/ Drop("B") \/ Dup("B") \/ Reorder("B") \/ Pop("B") \/ Seize("B") \/ FlowOn("B") \/ FlowOff("B")
    \/ T1("A") \/ T1("B")
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
ProgressKinds == {"deliver", "pop", "seize-confirm", "flow-on", "flow-off"}

CanProgress == \E s \in Stations : CanDeliver(s) \/ CanPop(s) \/ CanSeize(s) \/ CanFlowOn(s) \/ CanFlowOff(s)

CanProgressOrT1 == CanProgress \/ \E s \in Stations : CanT1(s)
TookProgress == last'.move \in ProgressKinds
TookProgressOrT1 == last'.move \in ProgressKinds \cup {"T1-expiry"}

\* The explorer's Quiescence: T1 expiries count as progress.
QuiescenceLive == (<>[]CanProgressOrT1 => []<><<TookProgressOrT1>>_vars) => []<>Quiescent

\* The explorer's SelectiveProgress (timer-free): T1 is not progress, so a
\* path that completes only through a T1 expiry does not count.
TimerFreeLive == (<>[]CanProgress => []<><<TookProgress>>_vars) => []<>Quiescent

=============================================================================

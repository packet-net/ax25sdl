---------------------- MODULE Ax25MeaningIndependent ----------------------
(***************************************************************************)
(* Hand-written. An independent meaning for the AX.25 v2.2 Data Link       *)
(* tables (Ax25DataLinkTables.tla), built only from MeaningInterface.md,   *)
(* Ax25Values.tla, Ax25Runner.tla, the tables, the sdl catalogues and the  *)
(* AX.25 v2.2 text (2025 Markdown and figures, 1998/2017 PDFs).           *)
(*                                                                         *)
(* Citations: "4.2.2.5" etc. are clauses of the AX.25 v2.2 text; "figc4.x" *)
(* are the Data Link SDL figures; "C1.2" and "C4.3" are Appendix C prose.  *)
(* Readings the text does not decide are listed in notes/silent.md, and    *)
(* figure-versus-prose conflicts in notes/conflicts.md.                    *)
(*                                                                         *)
(* Station record: the fixed fields plus                                   *)
(*   peer  Peer Receiver Busy (C4.3 Flags)                                 *)
(*   rej   Reject Exception (C4.3 Flags, 4.3.2.3)                          *)
(*   sent  the I frames sent and not yet known acknowledged, as a set of   *)
(*         <<N(s), label>> pairs, so that "Push Old I Frame" (figc4.4,     *)
(*         figc4.5, figc4.7a) has something to push (4.3.2.3, 6.4.7).      *)
(* Time values (SRT, T1V, Next T1), N1, T2 and duplex are not kept: the    *)
(* model has no time and nothing in the tables reads them (silent.md S9).  *)
(*                                                                         *)
(* Per-dispatch scratch (never persisted):                                 *)
(*   pf    the P/F bit of the next frame sent ("F := ...", "P := ...")     *)
(*   txnr  N(r) of the next I or S frame sent, None until assigned         *)
(*   txns  N(s) of the next I frame sent, None until assigned              *)
(*   cur   the label of the I frame in hand (DL-DATA Request, or the one  *)
(*         that popped off the I frame queue)                              *)
(*   ind   the label the next DL-DATA Indication delivers                  *)
(*   x     the subroutine variable X of Invoke Retransmission (figc4.7a)   *)
(***************************************************************************)
EXTENDS Ax25Values

(***************************************************************************)
(* Helpers                                                                 *)
(***************************************************************************)

\* Sequence arithmetic is modulo the modulus (4.2.2.1).
MIInc(c, v) == (v + 1) % c.mod
MIDec(c, v) == (v - 1) % c.mod
\* Distance from a to b going forward round the sequence space.
MIFwd(c, a, b) == (b - a) % c.mod

\* Is this event a received frame (it carries P/F and C/R)? (interface)
MIIsFrame(e) == e.pf # None /\ e.cmd # None

MIEmit(c, f) == [c EXCEPT !.fx = Append(@, FrameFx(f))]
MIUp(c, v, d) == [c EXCEPT !.fx = Append(@, UpperFx(v, d))]
MILm(c, v) == [c EXCEPT !.fx = Append(@, LmFx(v))]

\* N(r) of an I or S frame about to be sent: the value the figure assigned
\* in this dispatch, else V(r) (4.2.2.5: "Prior to sending an I or S frame,
\* this variable is updated to equal that of the received state variable").
MITxNr(c) == IF c.txnr = None THEN c.vr ELSE c.txnr
\* N(s) of an I frame about to be sent: as assigned, else V(s) (4.2.2.3).
MITxNs(c) == IF c.txns = None THEN c.vs ELSE c.txns

\* A supervisory frame (4.3.2): type, command?, P/F.
MISFrame(c, t, cmd, pf) == MIEmit(c, Frm(t, cmd, pf, MITxNr(c), None, ""))
\* An unnumbered frame (4.3.3): no N(r), N(s) or information.
MIUFrame(c, t, cmd, pf) == MIEmit(c, Frm(t, cmd, pf, None, None, ""))

\* Keep only the sent I frames inside the outstanding window [V(a), V(s))
\* (4.2.2.6: V(a)-1 is the last acknowledged frame).
MIOutstanding(c) == {pr \in c.sent : MIFwd(c, c.va, pr[1]) < MIFwd(c, c.va, c.vs)}

\* Push an old (already sent) I frame numbered n onto the I frame queue.
\* All queues are first-in, first-out (C4.3), so a push appends at the tail
\* (silent.md S3). A frame that was never sent cannot be pushed (S4).
MIPushOld(c, n) ==
    IF Has(c.sent, n) THEN [c EXCEPT !.q = Append(@, Get(c.sent, n))]
    ELSE Fail(c, "Push Old I Frame: no sent I frame with N(s) = " \o ToString(n))

\* Set Version 2.0 / Set Version 2.2 as figc4.7b expands them.
MISetV20(c) == [c EXCEPT !.v22 = FALSE, !.srej = FALSE, !.mod = 8, !.k = 8, !.n2 = 10]
MISetV22(c) == [c EXCEPT !.v22 = TRUE, !.srej = TRUE, !.mod = 128, !.k = 32, !.n2 = 10]

(***************************************************************************)
(* The station record                                                      *)
(***************************************************************************)

\* Initial values: state variables 0 (6.3.1), timers stopped, flags clear
\* (C4.3), version 2.0 defaults mod 8, k 7, N2 10 (6.3.2, 6.7.2.3).
NewMachine(st) ==
    [st |-> st, vs |-> 0, vr |-> 0, va |-> 0, rc |-> 0, mod |-> 8, k |-> 7, n2 |-> 10,
     v22 |-> FALSE, srej |-> FALSE, l3 |-> FALSE, own |-> FALSE, ack |-> FALSE, srejx |-> 0,
     t1 |-> "stopped", t3 |-> "stopped", q |-> <<>>, saved |-> {},
     peer |-> FALSE, rej |-> FALSE, sent |-> {}]

Begin(m, e) ==
    [st |-> m.st, vs |-> m.vs, vr |-> m.vr, va |-> m.va, rc |-> m.rc, mod |-> m.mod, k |-> m.k, n2 |-> m.n2,
     v22 |-> m.v22, srej |-> m.srej, l3 |-> m.l3, own |-> m.own, ack |-> m.ack, srejx |-> m.srejx,
     t1 |-> m.t1, t3 |-> m.t3, q |-> m.q, saved |-> m.saved,
     peer |-> m.peer, rej |-> m.rej, sent |-> m.sent,
     \* scratch; P/F is 0 unless set (6.2: "When not used, the P/F bit is set to 0")
     pf |-> FALSE, txnr |-> None, txns |-> None, cur |-> "", ind |-> e.data, x |-> None,
     fx |-> <<>>, err |-> "", tid |-> "", arms |-> {}]

Persist(c) ==
    [st |-> c.st, vs |-> c.vs, vr |-> c.vr, va |-> c.va, rc |-> c.rc, mod |-> c.mod, k |-> c.k, n2 |-> c.n2,
     v22 |-> c.v22, srej |-> c.srej, l3 |-> c.l3, own |-> c.own, ack |-> c.ack, srejx |-> c.srejx,
     t1 |-> c.t1, t3 |-> c.t3, q |-> c.q, saved |-> c.saved,
     peer |-> c.peer, rej |-> c.rej, sent |-> c.sent]

(***************************************************************************)
(* What the event itself does                                              *)
(***************************************************************************)

Arrive(c, e) ==
    CASE e.ev = "T1_expiry" -> [c EXCEPT !.t1 = "expired"]      \* C1.1: expiry raises the input; T1 is now expired (figc4.7b "T1 Expired?")
      [] e.ev = "T3_expiry" -> [c EXCEPT !.t3 = "expired"]      \* C1.1, as T1
      [] e.ev = "I_frame_pops_off_queue" ->                      \* C1.2: the internal signal is an item popped off the queue; C4.3 FIFO: the head
            IF c.q = <<>> THEN Fail(c, "I frame pops off queue: the I frame queue is empty")
            ELSE [c EXCEPT !.cur = Head(c.q), !.q = Tail(c.q)]
      [] e.ev = "DL_DATA_request" -> [c EXCEPT !.cur = e.data]  \* 5.3: the data to send in an I frame
      [] OTHER -> c

Preempts(c, e) == FALSE
Preempt(c, e) == c

\* Bookkeeping: forget sent I frames now acknowledged (4.2.2.6, 6.4.6), so
\* equal stations give equal records.
AfterTransition(c) == [c EXCEPT !.sent = MIOutstanding(c)]

(***************************************************************************)
(* Guard atoms                                                             *)
(***************************************************************************)

MIFrameAtoms == {"P_eq_1", "F_eq_1", "P_or_F_eq_1", "command", "response",
               "command_and_P_eq_1", "response_and_F_eq_1"}
MINrAtoms == {"va_le_nr_le_vs", "nr_eq_va", "nr_eq_vs", "vs_eq_nr"}
MINsAtoms == {"ns_eq_vr", "ns_gt_vr_plus_1", "vr_lt_ns_lt_vr_plus_k"}
MIStateAtoms == {"RC_eq_0", "RC_eq_N2", "SREJ_enabled", "T1_expired", "T1_running",
               "able_to_establish", "ack_pending", "info_field_length_le_N1_and_content_is_octet_aligned",
               "layer_3_initiated", "mod_128", "mod_8", "out_of_sequence_frames_in_receive_buffer",
               "own_receiver_busy", "peer_receiver_busy", "reject_exception", "sreject_exception_gt_0",
               "version_2_2", "vr_I_frame_stored", "vs_eq_va", "vs_eq_va_plus_k",
               "F_eq_1_and_frame_eq_RR_or_frame_eq_RNR_or_frame_eq_I", "vs_eq_X"}

AtomError(c, e, a) ==
    CASE a \in MIFrameAtoms -> IF MIIsFrame(e) THEN "" ELSE "guard atom '" \o a \o "' asked of a stimulus that is not a received frame"
      [] a \in MINrAtoms -> IF e.nr # None THEN "" ELSE "guard atom '" \o a \o "' asked of a stimulus with no N(r)"
      [] a \in MINsAtoms -> IF e.ns # None THEN "" ELSE "guard atom '" \o a \o "' asked of a stimulus with no N(s)"
      [] a = "vs_eq_X" -> IF c.x # None THEN "" ELSE "guard atom 'vs_eq_X' asked before X was assigned"
      [] a \in MIStateAtoms -> ""
      [] OTHER -> "guard atom '" \o a \o "' has no binding"

AtomValue(c, e, a) ==
    CASE a = "P_eq_1" -> e.pf = 1                           \* 4.2.1, 6.2: the received P bit
      [] a = "F_eq_1" -> e.pf = 1                           \* 4.2.1, 6.2: the received F bit (same bit as P)
      [] a = "P_or_F_eq_1" -> e.pf = 1                      \* 4.3.2.4, 6.4.8: the P/F bit of the SREJ
      [] a = "command" -> e.cmd = 1                         \* 6.1.2
      [] a = "response" -> e.cmd = 0                        \* 6.1.2
      [] a = "command_and_P_eq_1" -> e.cmd = 1 /\ e.pf = 1  \* figc4.7b "Command & P == 1?"
      [] a = "response_and_F_eq_1" -> e.cmd = 0 /\ e.pf = 1 \* figc4.7b "Response & F == 1?"
      \* figc4.7a Enquiry Response "F==1 & (Frame==RR || Frame==RNR || Frame==I)?":
      \* F is the subroutine's parameter (staged by the runner as "F := 1/0"),
      \* Frame is the frame that triggered this dispatch (silent.md S6).
      [] a = "F_eq_1_and_frame_eq_RR_or_frame_eq_RNR_or_frame_eq_I" ->
            c.pf /\ e.ev \in {"RR_received", "RNR_received", "I_received"}
      \* 6.4.11 "within the range from the last N(R) received to the last N(S) sent plus one", modular (4.2.2.1)
      [] a = "va_le_nr_le_vs" -> MIFwd(c, c.va, e.nr) <= MIFwd(c, c.va, c.vs)
      [] a = "nr_eq_va" -> e.nr = c.va                      \* figc4.7a "N(r) == V(a)?"
      [] a = "nr_eq_vs" -> e.nr = c.vs                      \* figc4.7a "N(r) == V(s)?"
      [] a = "vs_eq_nr" -> c.vs = e.nr                      \* figc4.5 "V(s) = N(r)?"
      [] a = "ns_eq_vr" -> e.ns = c.vr                      \* 4.2.2.4, 6.4.2.1 "N(s) == V(r)?"
      \* figc4.4c "N(s) > V(r) + 1?", modular: N(s) at least two past V(r) (silent.md S7)
      [] a = "ns_gt_vr_plus_1" -> MIFwd(c, c.vr, e.ns) > 1
      \* 4.4.2 '"greater-than V(r)" and "less-than V(r)+k"', modular, open at both ends
      [] a = "vr_lt_ns_lt_vr_plus_k" -> MIFwd(c, c.vr, e.ns) > 0 /\ MIFwd(c, c.vr, e.ns) < c.k
      [] a = "RC_eq_0" -> c.rc = 0                          \* figc4.7b "RC == 0?"
      [] a = "RC_eq_N2" -> c.rc = c.n2                      \* 6.3.1, 6.3.4, 6.4.11: N2 attempts
      [] a = "SREJ_enabled" -> c.srej                       \* 6.3.2, figc4.7b Set Selective Reject
      [] a = "T1_expired" -> c.t1 = "expired"               \* figc4.7b "T1 Expired?"
      [] a = "T1_running" -> c.t1 = "running"               \* figc4.4, figc4.7a "T1 Running?"
      [] a = "able_to_establish" -> e.able                  \* 6.3.5: environment's choice (interface)
      [] a = "ack_pending" -> c.ack                         \* C4.3 Acknowledge Pending
      [] a = "info_field_length_le_N1_and_content_is_octet_aligned" -> e.info \* 6.7.2.1: environment's answer (interface)
      [] a = "layer_3_initiated" -> c.l3                    \* C4.3 Layer 3 Initiated
      [] a = "mod_128" -> c.mod = 128                       \* figc4.7a "Mod 128?"
      [] a = "mod_8" -> c.mod = 8                           \* figc4.7a "Mod 8?"
      [] a = "out_of_sequence_frames_in_receive_buffer" -> c.saved # {} \* 6.4.4.2, figc4.7a
      [] a = "own_receiver_busy" -> c.own                   \* C4.3 Own Receiver Busy
      [] a = "peer_receiver_busy" -> c.peer                 \* C4.3 Peer Receiver Busy
      [] a = "reject_exception" -> c.rej                    \* C4.3 Reject Exception
      [] a = "sreject_exception_gt_0" -> c.srejx > 0        \* figc4.4c "Sreject Exception > 0?"
      [] a = "version_2_2" -> c.v22                         \* 6.3.2
      [] a = "vr_I_frame_stored" -> Has(c.saved, c.vr)      \* figc4.4c "V(r) I Frame Stored?", 4.4.4
      [] a = "vs_eq_va" -> c.vs = c.va                      \* figc4.x "V(s) == V(a)?"
      [] a = "vs_eq_va_plus_k" -> c.vs = (c.va + c.k) % c.mod \* 6.4.1 "equals the last received N(R) ... plus k", modular (silent.md S8)
      [] a = "vs_eq_X" -> c.vs = c.x                        \* figc4.7a "V(s) == X?"
      [] OTHER -> FALSE

(***************************************************************************)
(* Processing boxes                                                        *)
(***************************************************************************)

Processing(c, e, verb) ==
    CASE \* figc4.7a Invoke Retransmission: "Backtrack" titles the box; the assignments in it do the work (silent.md S5)
         verb = "Backtrack" -> c
      [] verb = "X := V(s)" -> [c EXCEPT !.x = c.vs]                          \* figc4.7a
      \* --- flags (C4.3 Flags) ---
      [] verb = "Clear Acknowledge Pending" -> [c EXCEPT !.ack = FALSE]
      [] verb = "set_acknowledge_pending" -> [c EXCEPT !.ack = TRUE]
      [] verb = "Set Layer 3 Initiated" -> [c EXCEPT !.l3 = TRUE]
      [] verb = "Clear Layer 3 Initiated" -> [c EXCEPT !.l3 = FALSE]
      [] verb = "Set Own Receiver Busy" -> [c EXCEPT !.own = TRUE]           \* 6.4.10
      [] verb = "Clear Own Receiver Busy" -> [c EXCEPT !.own = FALSE]
      [] verb = "set_peer_receiver_busy" -> [c EXCEPT !.peer = TRUE]         \* 6.4.9
      [] verb = "clear_peer_receiver_busy" -> [c EXCEPT !.peer = FALSE]      \* 4.3.2.1
      [] verb = "Set Reject Exception" -> [c EXCEPT !.rej = TRUE]            \* 4.3.2.3, 6.4.4.1
      [] verb = "Clear Reject Exception" -> [c EXCEPT !.rej = FALSE]         \* 4.4.3
      [] verb = "Clear Reject Condition" -> [c EXCEPT !.rej = FALSE]         \* figc4.7a; 4.3.2.3 "reject condition"
      [] verb = "Clear Sreject Condition" -> [c EXCEPT !.srejx = 0]          \* figc4.7a; 4.4.4
      [] verb = "Increment Sreject Exception" -> [c EXCEPT !.srejx = @ + 1]  \* figc4.4c; 4.4.4 one or more
      [] verb = "Sreject := Sreject + 1" -> [c EXCEPT !.srejx = @ + 1]       \* figc4.5
      [] verb = "Decrement Sreject Exception if > 0" ->                      \* figc4.4c
            [c EXCEPT !.srejx = IF @ > 0 THEN @ - 1 ELSE 0]
      \* --- discards ---
      [] verb = "Discard Contents of I Frame" -> c   \* 4.4.2, 6.4.4.1: the received data is not delivered (silent.md S10)
      [] verb = "Discard I Frame" -> c               \* 4.4.6: no further action
      [] verb = "Discard Primitive" -> c             \* figc4.1: the input is dropped
      [] verb = "Discard Queue" -> [c EXCEPT !.q = <<>>]          \* figc4.2, figc4.6: the I frame queue (C4.3 Queues)
      [] verb = "discard_I_frame_queue" -> [c EXCEPT !.q = <<>>]  \* figc4.4, figc4.5, figc4.6
      [] verb = "discard_frame_queue" -> [c EXCEPT !.q = <<>>]    \* figc4.2, figc4.6
      [] verb = "Discard I Queue Entries" -> [c EXCEPT !.q = <<>>] \* figc4.7a Clear Exception Conditions
      \* --- the P/F bit of the next frame sent (4.2.1, 6.2) ---
      [] verb = "F := P" ->
            IF e.pf = None THEN Fail(c, "F := P on a stimulus that carries no P bit")
            ELSE [c EXCEPT !.pf = (e.pf = 1)]                       \* 6.2: answer a P=1 with F=1
      [] verb = "F := 1" -> [c EXCEPT !.pf = TRUE]
      [] verb = "F := 0" -> [c EXCEPT !.pf = FALSE]
      [] verb = "P := 1" -> [c EXCEPT !.pf = TRUE]                  \* 6.3.6.3, 4.4.5.2
      [] verb = "P := 0" -> [c EXCEPT !.pf = FALSE]                 \* 6.2 "When not used ... 0"
      \* --- numbers of the next frame sent ---
      [] verb = "N(r) := V(r)" -> [c EXCEPT !.txnr = c.vr]          \* 4.2.2.5
      [] verb = "N(s) := V(s)" -> [c EXCEPT !.txns = c.vs]          \* 4.2.2.3
      \* figc4.4c: N(s) is the received I frame's send sequence number (conflicts.md K3)
      [] verb = "N(r) := N(s)" ->
            IF e.ns = None THEN Fail(c, "N(r) := N(s) on a stimulus with no N(s)")
            ELSE [c EXCEPT !.txnr = e.ns]
      \* --- state variables (4.2.2) ---
      [] verb = "V(s) := 0" -> [c EXCEPT !.vs = 0]                  \* 6.3.1, 6.5
      [] verb = "V(a) := 0" -> [c EXCEPT !.va = 0]
      [] verb = "V(r) := 0" -> [c EXCEPT !.vr = 0]
      [] verb = "V(s) := V(s) + 1" -> [c EXCEPT !.vs = MIInc(c, c.vs)] \* 6.4.1
      [] verb = "V(r) := V(r) + 1" -> [c EXCEPT !.vr = MIInc(c, c.vr)] \* 4.2.2.4, 6.4.2.1
      [] verb = "V(r) := V(r) - 1" -> [c EXCEPT !.vr = MIDec(c, c.vr)] \* figc4.5 as drawn (conflicts.md K1)
      \* N(r) here is the received frame's N(R) (6.4.7, 6.4.11; silent.md S1)
      [] verb = "V(a) := N(r)" ->
            IF e.nr = None THEN Fail(c, "V(a) := N(r) on a stimulus with no N(r)")
            ELSE [c EXCEPT !.va = e.nr]                                \* 4.2.2.6, 6.4.6
      [] verb = "V(s) := N(r)" ->
            IF e.nr = None THEN Fail(c, "V(s) := N(r) on a stimulus with no N(r)")
            ELSE [c EXCEPT !.vs = e.nr]                                \* 6.4.7
      \* --- retry count (6.3.1, 6.4.11) ---
      [] verb = "RC := 0" -> [c EXCEPT !.rc = 0]
      [] verb = "RC := 1" -> [c EXCEPT !.rc = 1]
      [] verb = "RC := RC + 1" -> [c EXCEPT !.rc = @ + 1]
      \* --- timers (6.7.1.1, 6.7.1.3; C1.1 timers may be set and stopped) ---
      [] verb = "Start T1" -> [c EXCEPT !.t1 = "running"]
      [] verb = "Stop T1" -> [c EXCEPT !.t1 = "stopped"]
      [] verb = "Start T3" -> [c EXCEPT !.t3 = "running"]
      [] verb = "Stop T3" -> [c EXCEPT !.t3 = "stopped"]
      \* --- timer values: the model has no time (silent.md S9) ---
      [] verb = "SRT := Initial Default" -> c                                                     \* 6.7.1.2 note in C4.3 Variables
      [] verb = "T1V := 2 * SRT" -> c                                                             \* C4.3 Variables
      [] verb = "SRT := 7(SRT)/8 + (T1)/8 - (Remaining Time on T1 When Last Stopped)/8" -> c      \* figc4.7b
      [] verb = "Next T1 := 2 * SRT" -> c                                                         \* figc4.7b
      [] verb = "Next T1 := (RC*0.25)+SRT*2" -> c                                                 \* figc4.7b
      [] verb = "T2 := 3000" -> c                                                                 \* figc4.7b; 6.7.1.2 T2 is optional and never started here
      [] verb = "N1 := 2048" -> c                                                                 \* figc4.7b; N1 only feeds the environment's info atom
      [] verb = "Set Half Duplex" -> c                                                            \* figc4.7b; 6.3.2 duplex is not modelled
      \* --- version parameters (6.3.2, figc4.7b) ---
      [] verb = "Set Implicit Reject" -> [c EXCEPT !.srej = FALSE]
      [] verb = "Set Selective Reject" -> [c EXCEPT !.srej = TRUE]
      [] verb = "Modulo := 8" -> [c EXCEPT !.mod = 8]
      [] verb = "Modulo := 128" -> [c EXCEPT !.mod = 128]
      [] verb = "k := 8" -> [c EXCEPT !.k = 8]                      \* figc4.7b (conflicts.md K2)
      [] verb = "k := 32" -> [c EXCEPT !.k = 32]
      [] verb = "N2 := 10" -> [c EXCEPT !.n2 = 10]
      \* the processing boxes "Set Version 2.0/2.2" mean the figc4.7b expansions of the same names (silent.md S11)
      [] verb = "set_version_2_0" -> MISetV20(c)
      [] verb = "Set Version 2.2" -> MISetV22(c)
      \* --- receive buffer (4.4.2, 4.4.4, 6.4.4.2) ---
      [] verb = "Save Contents of I Frame" ->
            IF e.ns = None THEN Fail(c, "Save Contents of I Frame on a stimulus with no N(s)")
            ELSE [c EXCEPT !.saved = Put(@, e.ns, e.data)]
      [] verb = "Retrieve Stored V(r) I Frame" ->
            IF Has(c.saved, c.vr) THEN [c EXCEPT !.ind = Get(c.saved, c.vr), !.saved = Del(@, c.vr)]
            ELSE Fail(c, "Retrieve Stored V(r) I Frame: none stored for V(r) = " \o ToString(c.vr))
      [] OTHER -> Fail(c, "processing verb '" \o verb \o "' has no binding")

(***************************************************************************)
(* Signals                                                                 *)
(***************************************************************************)

\* To layer 3 (5.3, C4.1). Error letters as C4.3 "Error Codes".
SignalUpper(c, e, verb) ==
    CASE verb = "DL_DATA_indication" -> MIUp(c, verb, c.ind)          \* 5.3, 6.4.2.1; 4.4.4 in order
      [] verb = "DL_UNIT_DATA_indication" -> MIUp(c, verb, e.data)    \* 5.3, 4.3.3.6
      [] verb \in {"DL_CONNECT_confirm", "DL_CONNECT_indication", "DL_DISCONNECT_confirm",
                   "DL_DISCONNECT_indication", "DL_ERROR_indication_C_D", "DL_ERROR_indication_D",
                   "DL-ERROR Indication (A)", "DL-ERROR Indication (E)", "DL-ERROR Indication (F)",
                   "DL-ERROR Indication (G)", "DL-ERROR Indication (I)", "DL-ERROR Indication (J)",
                   "DL-ERROR Indication (K)", "DL-ERROR Indication (L)", "DL-ERROR Indication (M)",
                   "DL-ERROR Indication (N)", "DL-ERROR Indication (O)", "DL-ERROR Indication (Q)",
                   "DL-ERROR Indication (T)", "DL-ERROR Indication (U)", "DL-ERROR Indication (add)"}
            -> MIUp(c, verb, "")                                       \* 5.3, C4.3 Error Codes
      [] OTHER -> Fail(c, "signal_upper verb '" \o verb \o "' has no binding")

\* To the peer (frames) or the link multiplexer (C4.2). A bare S frame name
\* is a response (6.2, 4.3.2.4); U frames per figure 4.4; P/F is the scratch
\* bit unless the box names it.
SignalLower(c, e, verb) ==
    CASE verb = "RR" -> MISFrame(c, "RR", FALSE, c.pf)                 \* 6.2, 4.3.2.1
      [] verb = "RR Response" -> MISFrame(c, "RR", FALSE, c.pf)
      [] verb = "RR Command" -> MISFrame(c, "RR", TRUE, c.pf)          \* 4.4.5.2, 6.4.11
      [] verb = "RR Command (P = 0)" -> MISFrame(c, "RR", TRUE, FALSE)
      [] verb = "RNR" -> MISFrame(c, "RNR", FALSE, c.pf)               \* 4.3.2.2, 6.4.10
      [] verb = "RNR Response" -> MISFrame(c, "RNR", FALSE, c.pf)
      [] verb = "RNR Response (F = 0)" -> MISFrame(c, "RNR", FALSE, FALSE)
      [] verb = "RNR Command" -> MISFrame(c, "RNR", TRUE, c.pf)
      [] verb = "REJ" -> MISFrame(c, "REJ", FALSE, c.pf)               \* 4.3.2.3, 6.4.4.1, 6.2
      [] verb = "SREJ" -> MISFrame(c, "SREJ", FALSE, c.pf)             \* 4.3.2.4 "only sent as a response"
      [] verb = "I Command" ->                                       \* 4.3.1, 6.4.1
            LET ns == MITxNs(c)
            IN  [MIEmit(c, Frm("I", TRUE, c.pf, MITxNr(c), ns, c.cur)) EXCEPT !.sent = Put(@, ns, c.cur)]
      [] verb = "UA" -> MIUFrame(c, "UA", FALSE, c.pf)                 \* 4.3.3.4, 6.2
      [] verb = "Expedited UA" -> MIUFrame(c, "UA", FALSE, c.pf)       \* C4.2 LM-EXPEDITED-DATA; same frame (silent.md S12)
      [] verb = "DM" -> MIUFrame(c, "DM", FALSE, c.pf)                 \* 4.3.3.5
      [] verb = "Expedited DM" -> MIUFrame(c, "DM", FALSE, c.pf)
      [] verb = "DM (F = 1)" -> MIUFrame(c, "DM", FALSE, TRUE)         \* 6.3.5
      [] verb = "DM Response (F = 0)" -> MIUFrame(c, "DM", FALSE, FALSE) \* 6.3.6.3 unsolicited DM F=0
      [] verb = "SABM" -> MIUFrame(c, "SABM", TRUE, c.pf)              \* 4.3.3.1, 6.3.1
      [] verb = "SABM (P == 1)" -> MIUFrame(c, "SABM", TRUE, TRUE)     \* 6.3.6.3
      [] verb = "SABME" -> MIUFrame(c, "SABME", TRUE, c.pf)            \* 4.3.3.2
      [] verb = "SABME (P = 1)" -> MIUFrame(c, "SABME", TRUE, TRUE)
      [] verb = "DISC (P = 1)" -> MIUFrame(c, "DISC", TRUE, TRUE)      \* 4.3.3.3, 6.3.4, 6.3.6.3
      [] verb = "UI Command" -> MIEmit(c, Frm("UI", TRUE, c.pf, None, None, e.data)) \* 5.3, 4.3.3.6
      [] verb = "LM_seize_request" -> MILm(c, verb)                    \* 5.4, C4.2
      [] verb = "LM_release_request" -> MILm(c, verb)                  \* 5.4, C4.2
      [] verb = "LM_data_request" -> MILm(c, verb)                     \* figc4.4c, 5.4 (silent.md S13)
      [] OTHER -> Fail(c, "signal_lower verb '" \o verb \o "' has no binding")

\* Internal signals (C1.2): posts onto the I frame queue (FIFO, C4.3), and
\* the request to the Management Data Link, which this machine does not
\* model and which has no effect here.
InternalOut(c, e, verb) ==
    CASE verb \in {"Push on I Frame Queue", "Push on I Frame Queue (note: word order?)",
                   "Push I Frame on I Queue", "push_frame_on_queue"}
            -> [c EXCEPT !.q = Append(@, c.cur)]                     \* C1.2, C4.3 FIFO (silent.md S3)
      [] verb = "Push Old I Frame N(r) on Queue" ->                   \* figc4.4c, 6.4.8: the frame numbered by the SREJ's N(r)
            IF e.nr = None THEN Fail(c, "Push Old I Frame N(r) on a stimulus with no N(r)")
            ELSE MIPushOld(c, e.nr)
      [] verb = "Push Old I Frame onto Queue" -> MIPushOld(c, c.vs)    \* figc4.7a: the frame numbered V(s) in the loop
      [] verb = "MDL-NEGOTIATE Request" -> c                         \* figc4.6, C5: internal to MDL (silent.md S14)
      [] OTHER -> Fail(c, "internal_out verb '" \o verb \o "' has no binding")

=============================================================================

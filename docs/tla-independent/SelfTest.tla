------------------------------ MODULE SelfTest ------------------------------
\* Sanity checks of Ax25MeaningIndependent through the runner. Not a model.
EXTENDS Ax25Semantics

FE(ev, pf, cmd, nr, ns, d) == [Event(ev) EXCEPT !.pf = pf, !.cmd = cmd, !.nr = nr, !.ns = ns, !.data = d]
Conn == [NewMachine("Connected") EXCEPT !.t3 = "running"]

R1 == Dispatch(Conn, FE("I_received", 0, 1, 0, 0, "a"))
M2 == Persist(Dispatch(Conn, [Event("DL_DATA_request") EXCEPT !.data = "b"]))
R2 == Dispatch(M2, Event("I_frame_pops_off_queue"))
R3 == Dispatch(NewMachine("Disconnected"), FE("SABM_received", 1, 1, None, None, ""))
\* out of sequence then fill the gap, SREJ enabled
S0 == [Conn EXCEPT !.srej = TRUE]
R4 == Dispatch(S0, FE("I_received", 0, 1, 0, 1, "y"))
R5 == Dispatch(Persist(R4), FE("I_received", 0, 1, 0, 0, "x"))
\* REJ received with N(r) inside the window: retransmit
M6 == Persist(Dispatch(Persist(R2), FE("REJ_received", 0, 0, 0, None, "")))

FrameEvs == {"I_received","RR_received","RNR_received","REJ_received","SREJ_received","UA_received",
  "DM_received","DISC_received","SABM_received","SABME_received","FRMR_received","UI_received",
  "all_other_commands","i_or_s_command_received","all_other_primitives__from_lower_layer"}
Sweep == { <<st, ev, pf, cmd, Dispatch(NewMachine(st), IF ev \in FrameEvs THEN FE(ev, pf, cmd, 0, 0, "d") ELSE Event(ev)).err>> :
           st \in DOMAIN Pages, ev \in {"T1_expiry","T3_expiry","LM_SEIZE_confirm","DL_DATA_request","DL_CONNECT_request",
             "DL_DISCONNECT_request","DL_UNIT_DATA_request","DL_FLOW_OFF_request","DL_FLOW_ON_request",
             "control_field_error","info_not_permitted_in_frame","u_or_s_frame_length_error",
             "all_other_primitives__from_upper_layer"} \cup FrameEvs, pf \in {0,1}, cmd \in {0,1} }
Errs == {s \in Sweep : s[5] # "" /\ Handles(s[1], s[2])}

ASSUME FixedFields \subseteq DOMAIN NewMachine("Connected")
ASSUME FixedFields \subseteq DOMAIN Persist(Begin(NewMachine("Connected"), Event("T1_expiry")))
ASSUME PrintT(<<"R1", R1.err, R1.vr, R1.ack, R1.fx>>) /\ R1.err = "" /\ R1.vr = 1 /\ R1.ack
ASSUME PrintT(<<"R2", R2.err, R2.vs, R2.t1, R2.fx, R2.sent>>) /\ R2.err = "" /\ R2.vs = 1 /\ R2.t1 = "running"
ASSUME PrintT(<<"R3", R3.err, R3.st, R3.fx>>) /\ R3.err = "" /\ R3.st = "Connected"
ASSUME PrintT(<<"R4", R4.err, R4.saved, R4.srejx, R4.fx>>)
ASSUME PrintT(<<"R5", R5.err, R5.vr, R5.saved, R5.fx>>)
ASSUME PrintT(<<"M6", M6.vs, M6.q>>)
ASSUME PrintT(<<"errors", Errs>>)

VARIABLE z
Init == z = 0
Next == UNCHANGED z
=============================================================================

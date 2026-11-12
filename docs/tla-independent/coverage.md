# Coverage: Ax25MeaningIndependent.tla

Every action verb, guard atom and event that appears in `tla/Ax25DataLinkTables.tla`, with the clause the meaning is based on. S-numbers refer to `silent.md`, K-numbers to `conflicts.md`. All 136 distinct box verbs (123 non-subroutine plus 13 subroutine call labels), all 36 guard atoms and all 29 events are bound; nothing is left unbound. A static check (every verb and atom string appears as an arm) and a TLC sweep (every state x event x P/F x C/R on a fresh station) were run; the only dispatch errors in the sweep are the deliberate "no sent I frame" failures of S4.

## Action verbs

| Kind | Verb | Based on |
| --- | --- | --- |
| internal_out | `MDL-NEGOTIATE Request` | figc4.6; C5; no Data Link effect (S14) |
| internal_out | `Push I Frame on I Queue` | C1.2; C4.3 FIFO (S3) |
| internal_out | `Push Old I Frame N(r) on Queue` | 6.4.8; figc4.4c (S4, K4) |
| internal_out | `Push Old I Frame onto Queue` | 6.4.7; figc4.7a (S4, K4) |
| internal_out | `Push on I Frame Queue` | C1.2; C4.3 FIFO (S3) |
| internal_out | `Push on I Frame Queue (note: word order?)` | C1.2; C4.3 FIFO (S3) |
| internal_out | `push_frame_on_queue` | C1.2; C4.3 FIFO (S3) |
| processing | `Backtrack` | figc4.7a box title; no-op (S5) |
| processing | `Clear Acknowledge Pending` | C4.3 Flags |
| processing | `Clear Exception Conditions` | runner calls figc4.7a subroutine; not reached by the meaning |
| processing | `Clear Layer 3 Initiated` | C4.3 Flags |
| processing | `Clear Own Receiver Busy` | C4.3 Flags; 6.4.10 |
| processing | `Clear Reject Condition` | figc4.7a; 4.3.2.3 |
| processing | `Clear Reject Exception` | 4.4.3 |
| processing | `Clear Sreject Condition` | figc4.7a; 4.4.4 (srejx := 0) |
| processing | `Decrement Sreject Exception if > 0` | figc4.4c |
| processing | `Discard Contents of I Frame` | 4.4.2; 6.4.4.1; no state effect (S10) |
| processing | `Discard I Frame` | 4.4.6 |
| processing | `Discard I Queue Entries` | figc4.7a; C4.3 Queues |
| processing | `Discard Primitive` | figc4.1 |
| processing | `Discard Queue` | C4.3 Queues |
| processing | `F := 0` | 6.2; 4.4.4 |
| processing | `F := 1` | 6.2 |
| processing | `F := P` | 6.2 |
| processing | `Increment Sreject Exception` | figc4.4c; 4.4.4 |
| processing | `Modulo := 128` | figc4.7b; 6.3.2 |
| processing | `Modulo := 8` | figc4.7b; 6.3.2 |
| processing | `N(r) := N(s)` | figc4.4c; received N(s) (K3) |
| processing | `N(r) := V(r)` | 4.2.2.5 |
| processing | `N(s) := V(s)` | 4.2.2.3 |
| processing | `N1 := 2048` | figc4.7b; 6.7.2.1 (S9) |
| processing | `N2 := 10` | figc4.7b; 6.3.2 |
| processing | `Next T1 := (RC*0.25)+SRT*2` | figc4.7b; no time (S9) |
| processing | `Next T1 := 2 * SRT` | figc4.7b; no time (S9) |
| processing | `P := 0` | 6.2 |
| processing | `P := 1` | 6.3.6.3; 4.4.5.2 |
| processing | `RC := 0` | 6.3.1; 6.4.11 |
| processing | `RC := 1` | 6.3.1; 6.4.11 |
| processing | `RC := RC + 1` | 6.3.1; 6.3.4; 6.4.11 |
| processing | `Retrieve Stored V(r) I Frame` | 4.4.4; figc4.4c |
| processing | `SRT := 7(SRT)/8 + (T1)/8 - (Remaining Time on T1 When Last Stopped)/8` | figc4.7b; no time (S9) |
| processing | `SRT := Initial Default` | C4.3 Variables; no time (S9) |
| processing | `Save Contents of I Frame` | 4.4.2; 6.4.4.2 |
| processing | `Set Half Duplex` | figc4.7b; 6.3.2 (S9) |
| processing | `Set Implicit Reject` | figc4.7b; 6.3.2 |
| processing | `Set Layer 3 Initiated` | C4.3 Flags |
| processing | `Set Own Receiver Busy` | C4.3 Flags; 6.4.10 |
| processing | `Set Reject Exception` | 4.3.2.3; 6.4.4.1 |
| processing | `Set Selective Reject` | figc4.7b; 6.3.2 |
| processing | `Set Version 2.2` | figc4.7b Set Version 2.2 expansion; 6.3.2 (S11) |
| processing | `Sreject := Sreject + 1` | figc4.5 (same as increment) |
| processing | `Start T1` | 6.7.1.1; C1.1 |
| processing | `Start T3` | 6.7.1.3; C1.1 |
| processing | `Stop T1` | 6.7.1.1; C1.1 |
| processing | `Stop T3` | 6.7.1.3; C1.1 |
| processing | `T1V := 2 * SRT` | C4.3 Variables; no time (S9) |
| processing | `T2 := 3000` | figc4.7b; 6.7.1.2 (S9) |
| processing | `V(a) := 0` | 6.3.1; 6.5 |
| processing | `V(a) := N(r)` | 4.2.2.6; 6.4.6; received N(r) (S1) |
| processing | `V(r) := 0` | 6.3.1; 6.5 |
| processing | `V(r) := V(r) + 1` | 4.2.2.4; 6.4.2.1 |
| processing | `V(r) := V(r) - 1` | figc4.5 as drawn (K1) |
| processing | `V(s) := 0` | 6.3.1; 6.5 |
| processing | `V(s) := N(r)` | 6.4.7; received N(r) (S1) |
| processing | `V(s) := V(s) + 1` | 6.4.1; 4.2.2.2 |
| processing | `X := V(s)` | figc4.7a |
| processing | `clear_peer_receiver_busy` | C4.3 Flags; 4.3.2.1 |
| processing | `discard_I_frame_queue` | C4.3 Queues |
| processing | `discard_frame_queue` | C4.3 Queues |
| processing | `k := 32` | figc4.7b; 6.3.2 |
| processing | `k := 8` | figc4.7b (K2) |
| processing | `set_acknowledge_pending` | C4.3 Flags; 6.4.2.1 |
| processing | `set_peer_receiver_busy` | C4.3 Flags; 6.4.9 |
| processing | `set_version_2_0` | figc4.7b Set Version 2.0 expansion; 6.3.2 (S11, K2) |
| signal_lower | `DISC (P = 1)` | 4.3.3.3; 6.3.4; 6.3.6.3 |
| signal_lower | `DM` | 4.3.3.5 |
| signal_lower | `DM (F = 1)` | 6.3.5 |
| signal_lower | `DM Response (F = 0)` | 6.3.6.3 |
| signal_lower | `Expedited DM` | 4.3.3.5; C4.2 (S12) |
| signal_lower | `Expedited UA` | 4.3.3.4; C4.2 (S12) |
| signal_lower | `I Command` | 4.3.1; 6.4.1 |
| signal_lower | `LM_data_request` | 5.4; C4.2 (S13) |
| signal_lower | `LM_release_request` | 5.4; C4.2 |
| signal_lower | `LM_seize_request` | 5.4; C4.2 |
| signal_lower | `REJ` | 4.3.2.3; 6.4.4.1 (response) |
| signal_lower | `RNR` | 4.3.2.2; 6.4.10 (response) |
| signal_lower | `RNR Command` | 4.4.5.2; 6.4.11 |
| signal_lower | `RNR Response` | 6.4.10 |
| signal_lower | `RNR Response (F = 0)` | 6.4.10 |
| signal_lower | `RR` | 4.3.2.1; 6.2 (response) |
| signal_lower | `RR Command` | 4.4.5.2; 6.4.11 |
| signal_lower | `RR Command (P = 0)` | 4.3.2.1 |
| signal_lower | `RR Response` | 4.3.2.1; 6.2 |
| signal_lower | `SABM` | 4.3.3.1; 6.3.1 |
| signal_lower | `SABM (P == 1)` | 6.3.6.3 |
| signal_lower | `SABME` | 4.3.3.2 |
| signal_lower | `SABME (P = 1)` | 6.3.6.3 |
| signal_lower | `SREJ` | 4.3.2.4 (response only) |
| signal_lower | `UA` | 4.3.3.4; 6.2 |
| signal_lower | `UI Command` | 4.3.3.6; 5.3 |
| signal_upper | `DL-ERROR Indication (A)` | 5.3; C4.3 Error Codes letter A |
| signal_upper | `DL-ERROR Indication (E)` | 5.3; C4.3 Error Codes letter E |
| signal_upper | `DL-ERROR Indication (F)` | 5.3; C4.3 Error Codes letter F |
| signal_upper | `DL-ERROR Indication (G)` | 5.3; C4.3 Error Codes letter G |
| signal_upper | `DL-ERROR Indication (I)` | 5.3; C4.3 Error Codes letter I |
| signal_upper | `DL-ERROR Indication (J)` | 5.3; C4.3 Error Codes letter J |
| signal_upper | `DL-ERROR Indication (K)` | 5.3; C4.3 Error Codes letter K |
| signal_upper | `DL-ERROR Indication (L)` | 5.3; C4.3 Error Codes letter L |
| signal_upper | `DL-ERROR Indication (M)` | 5.3; C4.3 Error Codes letter M |
| signal_upper | `DL-ERROR Indication (N)` | 5.3; C4.3 Error Codes letter N |
| signal_upper | `DL-ERROR Indication (O)` | 5.3; C4.3 Error Codes letter O |
| signal_upper | `DL-ERROR Indication (Q)` | 5.3; C4.3 Error Codes letter Q |
| signal_upper | `DL-ERROR Indication (T)` | 5.3; C4.3 Error Codes letter T |
| signal_upper | `DL-ERROR Indication (U)` | 5.3; C4.3 Error Codes letter U |
| signal_upper | `DL-ERROR Indication (add)` | figc4.7a (green, 2025 addition); label kept verbatim (S15) |
| signal_upper | `DL_CONNECT_confirm` | 5.3 |
| signal_upper | `DL_CONNECT_indication` | 5.3 |
| signal_upper | `DL_DATA_indication` | 5.3; 6.4.2.1; 4.4.4 |
| signal_upper | `DL_DISCONNECT_confirm` | 5.3 |
| signal_upper | `DL_DISCONNECT_indication` | 5.3 |
| signal_upper | `DL_ERROR_indication_C_D` | 5.3; C4.3 codes C, D |
| signal_upper | `DL_ERROR_indication_D` | 5.3; C4.3 code D |
| signal_upper | `DL_UNIT_DATA_indication` | 5.3; 4.3.3.6 |
| subroutine | `Check Need For Response` | runner binds the call site to a figc4.7 subroutine table (SubroutineBindings) |
| subroutine | `Check_I_Frame_Acknowledged` | runner binds the call site to a figc4.7 subroutine table (SubroutineBindings) |
| subroutine | `Clear Exception Conditions` | runner binds the call site to a figc4.7 subroutine table (SubroutineBindings) |
| subroutine | `Enquiry Response (F = 0)` | runner binds the call site to a figc4.7 subroutine table (SubroutineBindings) |
| subroutine | `Enquiry_Response_F_1` | runner binds the call site to a figc4.7 subroutine table (SubroutineBindings) |
| subroutine | `Establish_Data_Link` | runner binds the call site to a figc4.7 subroutine table (SubroutineBindings) |
| subroutine | `Invoke Retransmission` | runner binds the call site to a figc4.7 subroutine table (SubroutineBindings) |
| subroutine | `N(r) Error Recovery` | runner binds the call site to a figc4.7 subroutine table (SubroutineBindings) |
| subroutine | `N(r) Recovery` | runner binds the call site to a figc4.7 subroutine table (SubroutineBindings) |
| subroutine | `Select_T1_Value` | runner binds the call site to a figc4.7 subroutine table (SubroutineBindings) |
| subroutine | `Transmit Enquery` | runner binds the call site to a figc4.7 subroutine table (SubroutineBindings) |
| subroutine | `Transmit Enquiry` | runner binds the call site to a figc4.7 subroutine table (SubroutineBindings) |
| subroutine | `UI Check` | runner binds the call site to a figc4.7 subroutine table (SubroutineBindings) |

## Guard atoms

| Atom | Meaning | Based on |
| --- | --- | --- |
| `P_eq_1` | received P/F bit is 1 | 4.2.1, 6.2 |
| `F_eq_1` | received P/F bit is 1 | 4.2.1, 6.2 |
| `P_or_F_eq_1` | received P/F bit is 1 | 4.3.2.4, 6.4.8 |
| `command` | received frame is a command | 6.1.2 |
| `response` | received frame is a response | 6.1.2 |
| `command_and_P_eq_1` | command and P = 1 | figc4.7b |
| `response_and_F_eq_1` | response and F = 1 | figc4.7b |
| `F_eq_1_and_frame_eq_RR_or_frame_eq_RNR_or_frame_eq_I` | the Enquiry Response F parameter is 1 and the triggering frame is RR, RNR or I | figc4.7a (S6) |
| `va_le_nr_le_vs` | (N(r)-V(a)) mod m <= (V(s)-V(a)) mod m | 6.4.11, 4.2.2.1 |
| `nr_eq_va` | received N(r) = V(a) | figc4.7a |
| `nr_eq_vs` | received N(r) = V(s) | figc4.7a |
| `vs_eq_nr` | V(s) = received N(r) | figc4.5 |
| `ns_eq_vr` | received N(s) = V(r) | 4.2.2.4, 6.4.2.1 |
| `ns_gt_vr_plus_1` | (N(s)-V(r)) mod m > 1 | figc4.4c (S7) |
| `vr_lt_ns_lt_vr_plus_k` | 0 < (N(s)-V(r)) mod m < k | 4.4.2 |
| `RC_eq_0` | RC = 0 | figc4.7b |
| `RC_eq_N2` | RC = N2 | 6.3.1, 6.3.4, 6.4.11 |
| `SREJ_enabled` | srej | 6.3.2 |
| `T1_expired` | t1 = "expired" | figc4.7b (S2) |
| `T1_running` | t1 = "running" | figc4.7a, 6.4.1 |
| `able_to_establish` | event's `able` | 6.3.5, interface |
| `ack_pending` | ack | C4.3 Flags |
| `info_field_length_le_N1_and_content_is_octet_aligned` | event's `info` | 6.7.2.1, interface |
| `layer_3_initiated` | l3 | C4.3 Flags |
| `mod_128` / `mod_8` | mod = 128 / 8 | figc4.7a |
| `out_of_sequence_frames_in_receive_buffer` | saved is non-empty | 6.4.4.2, figc4.7a |
| `own_receiver_busy` | own | C4.3 Flags |
| `peer_receiver_busy` | peer (extra field) | C4.3 Flags |
| `reject_exception` | rej (extra field) | C4.3 Flags |
| `sreject_exception_gt_0` | srejx > 0 | figc4.4c |
| `version_2_2` | v22 | 6.3.2 |
| `vr_I_frame_stored` | Has(saved, V(r)) | figc4.4c, 4.4.4 |
| `vs_eq_va` | V(s) = V(a) | figc4.x |
| `vs_eq_va_plus_k` | V(s) = (V(a)+k) mod m | 6.4.1 (S8) |
| `vs_eq_X` | V(s) = X | figc4.7a |

## Events

| Event | What the meaning does before the table (Arrive) | Based on |
| --- | --- | --- |
| `I_received`, `RR_received`, `RNR_received`, `REJ_received`, `SREJ_received`, `UA_received`, `DM_received`, `DISC_received`, `SABM_received`, `SABME_received`, `FRMR_received`, `UI_received` | nothing; atoms and verbs read pf, cmd, nr, ns, data | 4.3, 6.4 |
| `all_other_commands`, `i_or_s_command_received`, `all_other_primitives__from_lower_layer` | nothing; carry the frame's fields when a frame raised them | interface, figc4.1, figc4.3 |
| `DL_DATA_request` | the request's label becomes the I frame in hand | 5.3 |
| `DL_CONNECT_request`, `DL_DISCONNECT_request`, `DL_UNIT_DATA_request`, `DL_FLOW_OFF_request`, `DL_FLOW_ON_request`, `all_other_primitives__from_upper_layer` | nothing | 5.3, C4.1 |
| `LM_SEIZE_confirm` | nothing | 5.4, C4.2 |
| `T1_expiry` | t1 := "expired" | C1.1, figc4.7b (S2) |
| `T3_expiry` | t3 := "expired" | C1.1 (S2) |
| `I_frame_pops_off_queue` | the head of the I frame queue is removed and becomes the I frame in hand; Fail on an empty queue | C1.2, C4.3 FIFO |
| `control_field_error`, `info_not_permitted_in_frame`, `u_or_s_frame_length_error` | nothing | C4.3 codes L, M, N |

`XID_response_received` is named in the interface but no Data Link page has it, so it is never dispatched here.

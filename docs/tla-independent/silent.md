# Where the spec is silent, and what this meaning chose

Each item: what the text does not decide, the reading chosen (the most literal one available), and why.

## S1. Two N(r)s: the received one and the one to send

The figures write "N(r)" both for the received frame's N(R) ("V(a) <- N(r)", "V(s) <- N(r)", "V(a) <= N(r) <= V(s)?") and for the N(R) of the frame about to be sent ("N(r) <- V(r)"). The spec never says they are one variable. Chosen: two variables. Every read of N(r) in an assignment or test is the received frame's N(R) (6.4.7 "the same value as the REJ frame's received sequence number", 6.4.11); "N(r) := V(r)" sets the outgoing N(R) only. This matters in figc4.5 where Enquiry Response (which does "N(r) <- V(r)") is followed by "V(a) <- N(r)": here V(a) takes the received N(R), not V(r). The same split applies to N(s): "N(s) := V(s)" sets the outgoing N(S), while "N(r) := N(s)" and "Save Contents of I Frame" read the received N(S).

## S2. What expiry does to the timer state

C1.1 says only that expiry generates an input. figc4.7b asks "T1 Expired?". Chosen: the T1_expiry (T3_expiry) event sets t1 (t3) to "expired" before the table runs; it stays "expired" until a Start or Stop box. So Select T1 after a Stop T1 sees "not expired", and Select T1 on the T1 expiry paths of figc4.2, figc4.3 and figc4.6 (no Stop before it) sees "expired".

## S3. Where a push puts an item on the I frame queue

C4.3: "All queues are first-in, first-out." C1.2: internal signal symbols "post items onto queues". No text says a returned item goes to the front. Chosen: every push appends at the tail, including "Push on I Frame Queue" on the pop-off-queue path when the peer is busy or the window is full, and the "Push Old I Frame" retransmission pushes. Consequences: with more than one item queued, a pop that is pushed back rotates the queue (data order changes), and retransmitted frames queue behind new data. This is the reading most likely to differ from another meaning and should be looked at first in any comparison.

## S4. Pushing an "old" I frame that was never sent

"Push Old I Frame N(r) on Queue" (figc4.4c, figc4.5) and "Push Old I Frame onto Queue" (figc4.7a Invoke Retransmission) need the data of an I frame already sent. The spec does not say what happens when no such frame exists, for example a REJ whose N(r) equals V(s) (allowed by "V(a) <= N(r) <= V(s)?"), which sends the figc4.7a do-while loop round from V(s) through numbers never sent. Chosen: the meaning keeps the labels of sent, unacknowledged I frames (extra field `sent`, pruned to [V(a), V(s)) after each dispatch, 4.2.2.6), and a push of a number not in it Fails the dispatch with "Push Old I Frame: no sent I frame with N(s) = n". The TLC sweep hits this for REJ and SREJ received in Connected with N(r) = V(s) = 0.

## S5. "Backtrack"

figc4.7a's first Invoke Retransmission box reads "Backtrack / X <- V(s) / V(s) <- N(r)". "Backtrack" defines no operation. Chosen: a title for the box, no effect.

## S6. "F" and "Frame" in Enquiry Response's first test

figc4.7a: "F==1 & (Frame==RR || Frame==RNR || Frame==I)?". Chosen: F is the subroutine's parameter (the call sites say "Enquiry Response (F = 1)" or "(F = 0)", staged by the runner through "F := 1"/"F := 0", which set the outgoing P/F bit); Frame is the frame that triggered the dispatch (its event name). On LM-SEIZE Confirm no frame triggered it, so the test is false.

## S7. "N(s) > V(r) + 1?"

The comparison is not defined for modulo numbers. Chosen: measured forward from V(r) modulo the modulus, (N(s) - V(r)) mod m > 1, consistent with 4.4.2's window reading.

## S8. "V(s) = V(a) + k" (window full)

6.4.1 "equals the last received N(R) ... plus k". Chosen: V(s) = (V(a) + k) mod m. With k = 8 and modulo 8 (figc4.7b Set Version 2.0, see K2) this is true whenever V(s) = V(a), so no I frame can ever be sent on such a link.

## S9. Time and unused parameters

The model has no time. SRT, T1V, Next T1, T2 := 3000, N1 := 2048 and Set Half Duplex are accepted as no-ops and not stored: nothing in the Data Link tables reads them (N1 reaches the tables only through the environment's answer to the info-field atom).

## S10. Save then Discard on the same path

figc4.4c and figc4.5 draw "Save Contents of I Frame" followed by "Discard Contents of I Frame" on the SREJ-enabled, N(s) > V(r)+1 path. Chosen: Save copies the frame into the receive buffer; Discard drops the received frame itself and does not remove the saved copy. Discard boxes have no state effect anywhere; their effect is that nothing is delivered.

## S11. "Set Version 2.0" and "Set Version 2.2" as processing boxes

The pages draw these as processing boxes; figc4.7b draws subroutines of the same names. Chosen: the box means the figc4.7b expansion: version flag, implicit/selective reject, modulo, k, N2 (and the unmodelled N1, T2, duplex). So these boxes overwrite the harness-seeded mod, k and n2.

## S12. Expedited UA / Expedited DM

C4.2 names LM-EXPEDITED-DATA Request as the way to pass expedited data. The frame on the air is the same. Chosen: the same FrameFx as UA / DM; no separate LM effect (ordinary frames carry no LM-DATA effect either).

## S13. The bare "LM-DATA Request" box after Push Old I Frame N(r)

figc4.4c and figc4.5 draw "LM-DATA Request" with no frame named. Chosen: an LmFx("LM_data_request") only; it puts no frame on the air (the pushed frame goes out when it pops off the queue).

## S14. MDL-NEGOTIATE Request

An internal signal to the Management Data Link (figc4.6, Appendix C5), which is not part of this machine. Chosen: no effect, nothing recorded (the interface records no internal effects).

## S15. "DL-ERROR Indication (add)"

figc4.7a (a green 2025 addition) labels the error "(add)", which is not a C4.3 error letter. Chosen: emitted verbatim as an UpperFx with that verb.

## S16. P/F, N(r) and N(s) when the figure does not set them

Chosen: per dispatch, P/F starts at 0 (6.2 "When not used, the P/F bit is set to 0"); an unassigned outgoing N(R) is V(r) at the moment of sending (4.2.2.5) and an unassigned N(S) is V(S) (4.2.2.3). Once a box assigns one of them it holds for every later frame of the same dispatch (an SDL variable keeps its value); none carries over to the next dispatch. Examples: figc4.4 DL-FLOW-ON "RR Command" goes out with P = 0, N(R) = V(r); the figc4.3 "Expedited DM" for DL-DISCONNECT Request goes out with F = 0.

## S17. Bare S frame names are responses

A bare "RR", "RNR", "REJ" or "SREJ" box is sent as a response: 6.2 says the reply to a P = 1 I or S command "is an RR, RNR or REJ response", and 4.3.2.4 says SREJ is only sent as a response. "RR Command" and "RNR Command" are commands. UA and DM are responses; SABM, SABME, DISC, UI Command and I Command are commands (figure 4.4, 4.3.1).

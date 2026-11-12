# Where the figure and the prose disagree (the figure was followed)

## K1. TimerRecovery drain loop decrements V(r)

figc4.5 (TimerRecovery, I received in sequence) draws "V(r) <- V(r) - 1" inside the stored-frame drain loop and after it, where figc4.4 draws "+ 1". 4.2.2.4 and 6.4.2.1 say V(r) is incremented on acceptance. Followed the figure: V(r) := (V(r) - 1) mod m. Effect: after delivering a stored frame the loop moves V(r) backwards.

## K2. Set Version 2.0 sets k := 8 with modulo 8

figc4.7b Set Version 2.0: "Modulo <- 8, k <- 8". 6.3.2 gives the v2.0 default Window Size Receive as 7 and 6.7.2.3 says at most seven I frames outstanding for modulo 8. Followed the figure: k := 8. With S8's reading, a station that runs "Set Version 2.0" can then send no I frames at all.

## K3. SREJ with N(r) := N(s)

figc4.4c (and the table's TimerRecovery counterpart) on the "Sreject Exception > 0?" yes path draws "N(r) <- N(s), F <- 0" then SREJ, after "Save Contents of I Frame". 4.2.2.5 says N(R) is set to V(R) before sending an S frame; 6.4.4.2 says the SREJ carries the N(R) of the missing frame. Followed the figure: the SREJ carries the received frame's N(S), which is the frame just saved, not a missing one. (The interface's reject-coherence check, N(r) not in the saved buffer, will flag it.) The TimerRecovery page in the tables draws N(r) := V(r) on this path instead, so the two pages also disagree with each other.

## K4. Retransmitted frames are renumbered

6.4.7 and 4.4.3: after a REJ the station retransmits starting with the frame numbered N(r). figc4.7a Invoke Retransmission moves V(s) to N(r), pushes the old frames onto the I frame queue while stepping V(s) back up to X (its value on entry), and the pop-off-queue path then sends each with "N(s) <- V(s)". Followed the figure: retransmitted data goes out under new sequence numbers starting at the old V(s). The same holds for "Push Old I Frame N(r) on Queue" after SREJ (6.4.8 asks for the frame N(r) itself).

## K5. I response in Connected and TimerRecovery reports error O

figc4.4c (I frame, "Command?" No) raises "DL-ERROR Indication (O)". C4.3's code list gives O as "I frame exceeded maximum allowed length" and S as "I response received". Followed the figure: O.

## K6. Smaller ones noticed while binding

- TimerRecovery UA received (table t11) raises DL-ERROR (N) where 6.5 and C4.3 code C say an unexpected UA; followed the table.
- TimerRecovery SABM/SABME received with V(s) = V(a) (t13/t14 yes) does not reset V(s), V(a), V(r), T1 or T3, although 6.5 says the receiver of a SABM(E) sets V(S) and V(R) to 0; followed the table. Combined with Set Version 2.0 changing modulo 128 to 8 (S11), V(s) may be left outside 0..7.
- AwaitingV22Connection UI received: the table sends DM (F = 1) on the P = 0 branch and nothing on P = 1, the reverse of every other page and of 4.3.3.6; followed the table.
- AwaitingV22Connection UA received with F = 1, not layer 3 initiated and V(s) != V(a) does "Start T3" twice and never "Start T1" (figc4.2 has Start T1); followed the table.
- Enquiry Response with own receiver busy answers a REJ, SREJ or UI poll with RR, not RNR (4.3.3.6 "an RR (or RNR, if appropriate)"); followed the figure.

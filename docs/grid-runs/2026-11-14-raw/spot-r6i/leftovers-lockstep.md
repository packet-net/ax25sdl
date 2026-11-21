Lockstep: Ax25Meaning (A) against Ax25MeaningIndependent (B), up to 8 run(s) per row.

| Scenario | Tables | Result |
| --- | --- | --- |
| g-ab3-ba3-b2-k4-nosrej-drop+dup | Current | `Connected / I_frame_pops_off_queue / fx, vs` (6 steps)<br>`Connected / I_frame_pops_off_queue / fx` (8 steps)<br>`TimerRecovery / I_frame_pops_off_queue / fx, vs` (18 steps) |

## Each row's differences

### g-ab3-ba3-b2-k4-nosrej-drop+dup

1. `Connected / I_frame_pops_off_queue / fx, vs`, 6 steps: 1. pop at A: t03_i_frame_pops_off_queue_no_no_no; 2. pop at A: t03_i_frame_pops_off_queue_no_no_yes; 3. drop at B: (no transition); 4. deliver at B: t26_i_received_yes_yes_yes_no_no_yes_no_no; 5. deliver at A: t25_rej_received_yes; 6. pop at A: (the dispatch on which the meanings differ)

   `interpreter error at station A: lockstep{Connected / I_frame_pops_off_queue / fx, vs}: in Connected on I_frame_pops_off_queue (A took pinned:retransmit_old_i_frame, B took t03_i_frame_pops_off_queue_no_no_yes): fx: A <<[kind |-> "frame", frame |-> [type |-> "I", cmd |-> TRUE, pf |-> FALSE, ns |-> 0, nr |-> 0, data |-> "a0"], prim |-> "", detail |-> ""]>>, B <<[kind |-> "frame", frame |-> [type |-> "I", cmd |-> TRUE, pf |-> FALSE, ns |-> 2, nr |-> 0, data |-> "a2"], prim |-> "", detail |-> ""]>>; vs: A 2, B 3`

2. `Connected / I_frame_pops_off_queue / fx`, 8 steps: 1. pop at A: t03_i_frame_pops_off_queue_no_no_no; 2. pop at A: t03_i_frame_pops_off_queue_no_no_yes; 3. drop at B: (no transition); 4. deliver at B: t26_i_received_yes_yes_yes_no_no_yes_no_no; 5. deliver at A: t25_rej_received_yes; 6. pop at A: pinned:retransmit_old_i_frame; 7. pop at A: pinned:retransmit_old_i_frame; 8. pop at A: (the dispatch on which the meanings differ)

   `interpreter error at station A: lockstep{Connected / I_frame_pops_off_queue / fx}: in Connected on I_frame_pops_off_queue (A took t03_i_frame_pops_off_queue_no_no_yes, B took t03_i_frame_pops_off_queue_no_no_yes): fx: A <<[kind |-> "frame", frame |-> [type |-> "I", cmd |-> TRUE, pf |-> FALSE, ns |-> 2, nr |-> 0, data |-> "a2"], prim |-> "", detail |-> ""]>>, B <<[kind |-> "frame", frame |-> [type |-> "I", cmd |-> TRUE, pf |-> FALSE, ns |-> 2, nr |-> 0, data |-> "a1"], prim |-> "", detail |-> ""]>>`

3. `TimerRecovery / I_frame_pops_off_queue / fx, vs`, 18 steps: 1. pop at A: t03_i_frame_pops_off_queue_no_no_no; 2. pop at A: t03_i_frame_pops_off_queue_no_no_yes; 3. pop at A: t03_i_frame_pops_off_queue_no_no_yes; 4. deliver at B: t26_i_received_yes_yes_yes_no_yes_no_no; 5. deliver at B: t26_i_received_yes_yes_yes_no_yes_no_yes; 6. deliver at B: t26_i_received_yes_yes_yes_no_yes_no_yes; 7. pop at B: t03_i_frame_pops_off_queue_no_no_no; 8. drop at A: (no transition); 9. pop at B: t03_i_frame_pops_off_queue_no_no_yes; 10. deliver at A: t26_i_received_yes_yes_yes_no_no_yes_no_no; 11. drop at B: (no transition); 12. pop at B: t03_i_frame_pops_off_queue_no_no_yes; 13. deliver at A: t26_i_received_yes_yes_yes_no_no_yes_yes_no; 14. seize-confirm at B: t23_lm_seize_confirm_no; 15. T1-expiry at B: t12_t1_expiry; 16. deliver at A: t21_rr_received_yes; 17. deliver at B: t18_rr_received_yes_yes_no; 18. pop at B: (the dispatch on which the meanings differ)

   `interpreter error at station B: lockstep{TimerRecovery / I_frame_pops_off_queue / fx, vs}: in TimerRecovery on I_frame_pops_off_queue (A took pinned:retransmit_old_i_frame, B took t03_i_frame_pops_off_queue_no_no_yes): fx: A <<[kind |-> "frame", frame |-> [type |-> "I", cmd |-> TRUE, pf |-> FALSE, ns |-> 0, nr |-> 3, data |-> "b0"], prim |-> "", detail |-> ""]>>, B <<[kind |-> "frame", frame |-> [type |-> "I", cmd |-> TRUE, pf |-> FALSE, ns |-> 3, nr |-> 3, data |-> "b0"], prim |-> "", detail |-> ""]>>; vs: A 3, B 4`

## Each signature

Rows run: 1; the same throughout: 0; with a difference: 1; errors: 0; distinct differences: 3.
Grid: 0 cell(s) read from the results file; 8842 s of TLC in all; largest cell 1002420 distinct states.

### Connected / I_frame_pops_off_queue / fx

Rows: g-ab3-ba3-b2-k4-nosrej-drop+dup

Shortest: g-ab3-ba3-b2-k4-nosrej-drop+dup, 8 steps:

1. pop at A: t03_i_frame_pops_off_queue_no_no_no
2. pop at A: t03_i_frame_pops_off_queue_no_no_yes
3. drop at B: (no transition)
4. deliver at B: t26_i_received_yes_yes_yes_no_no_yes_no_no
5. deliver at A: t25_rej_received_yes
6. pop at A: pinned:retransmit_old_i_frame
7. pop at A: pinned:retransmit_old_i_frame
8. pop at A: (the dispatch on which the meanings differ)

```
interpreter error at station A: lockstep{Connected / I_frame_pops_off_queue / fx}: in Connected on I_frame_pops_off_queue (A took t03_i_frame_pops_off_queue_no_no_yes, B took t03_i_frame_pops_off_queue_no_no_yes): fx: A <<[kind |-> "frame", frame |-> [type |-> "I", cmd |-> TRUE, pf |-> FALSE, ns |-> 2, nr |-> 0, data |-> "a2"], prim |-> "", detail |-> ""]>>, B <<[kind |-> "frame", frame |-> [type |-> "I", cmd |-> TRUE, pf |-> FALSE, ns |-> 2, nr |-> 0, data |-> "a1"], prim |-> "", detail |-> ""]>>
```

### Connected / I_frame_pops_off_queue / fx, vs

Rows: g-ab3-ba3-b2-k4-nosrej-drop+dup

Shortest: g-ab3-ba3-b2-k4-nosrej-drop+dup, 6 steps:

1. pop at A: t03_i_frame_pops_off_queue_no_no_no
2. pop at A: t03_i_frame_pops_off_queue_no_no_yes
3. drop at B: (no transition)
4. deliver at B: t26_i_received_yes_yes_yes_no_no_yes_no_no
5. deliver at A: t25_rej_received_yes
6. pop at A: (the dispatch on which the meanings differ)

```
interpreter error at station A: lockstep{Connected / I_frame_pops_off_queue / fx, vs}: in Connected on I_frame_pops_off_queue (A took pinned:retransmit_old_i_frame, B took t03_i_frame_pops_off_queue_no_no_yes): fx: A <<[kind |-> "frame", frame |-> [type |-> "I", cmd |-> TRUE, pf |-> FALSE, ns |-> 0, nr |-> 0, data |-> "a0"], prim |-> "", detail |-> ""]>>, B <<[kind |-> "frame", frame |-> [type |-> "I", cmd |-> TRUE, pf |-> FALSE, ns |-> 2, nr |-> 0, data |-> "a2"], prim |-> "", detail |-> ""]>>; vs: A 2, B 3
```

### TimerRecovery / I_frame_pops_off_queue / fx, vs

Rows: g-ab3-ba3-b2-k4-nosrej-drop+dup

Shortest: g-ab3-ba3-b2-k4-nosrej-drop+dup, 18 steps:

1. pop at A: t03_i_frame_pops_off_queue_no_no_no
2. pop at A: t03_i_frame_pops_off_queue_no_no_yes
3. pop at A: t03_i_frame_pops_off_queue_no_no_yes
4. deliver at B: t26_i_received_yes_yes_yes_no_yes_no_no
5. deliver at B: t26_i_received_yes_yes_yes_no_yes_no_yes
6. deliver at B: t26_i_received_yes_yes_yes_no_yes_no_yes
7. pop at B: t03_i_frame_pops_off_queue_no_no_no
8. drop at A: (no transition)
9. pop at B: t03_i_frame_pops_off_queue_no_no_yes
10. deliver at A: t26_i_received_yes_yes_yes_no_no_yes_no_no
11. drop at B: (no transition)
12. pop at B: t03_i_frame_pops_off_queue_no_no_yes
13. deliver at A: t26_i_received_yes_yes_yes_no_no_yes_yes_no
14. seize-confirm at B: t23_lm_seize_confirm_no
15. T1-expiry at B: t12_t1_expiry
16. deliver at A: t21_rr_received_yes
17. deliver at B: t18_rr_received_yes_yes_no
18. pop at B: (the dispatch on which the meanings differ)

```
interpreter error at station B: lockstep{TimerRecovery / I_frame_pops_off_queue / fx, vs}: in TimerRecovery on I_frame_pops_off_queue (A took pinned:retransmit_old_i_frame, B took t03_i_frame_pops_off_queue_no_no_yes): fx: A <<[kind |-> "frame", frame |-> [type |-> "I", cmd |-> TRUE, pf |-> FALSE, ns |-> 0, nr |-> 3, data |-> "b0"], prim |-> "", detail |-> ""]>>, B <<[kind |-> "frame", frame |-> [type |-> "I", cmd |-> TRUE, pf |-> FALSE, ns |-> 3, nr |-> 3, data |-> "b0"], prim |-> "", detail |-> ""]>>; vs: A 3, B 4
```


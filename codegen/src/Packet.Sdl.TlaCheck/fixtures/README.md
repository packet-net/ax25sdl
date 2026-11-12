# Seeded-defect fixtures

**These tables are deliberately broken.** They are not figures and say nothing about AX.25. They exist because no real defect, on any table set the explorer has, fails two of its checks: acknowledgement coherence and quiescence; and four more give a failing case to a quiescence rule: `tables-release-stuck` to the release rest (both stations Disconnected after a release), `tables-dm-ignored` to the give-up rule's settled state, `tables-reconnect-swallowed` to the reconnect answer rule (a layer 3 that issued DL-CONNECT Request must hear back), `tables-confirm-as-disconnect` to the report rule (at rest each layer 3's last up/down report matches its station's state); and coverage batch 4 adds six for the peer-busy family and T3: `tables-rnr-ignored` (the peer-busy hold), `tables-busy-delivers` (no delivery while busy), `tables-busy-says-rr` and, from coverage batch 5, `tables-enquiry-says-rr` (a busy station indicates RNR, answering a poll and polling), `tables-t3-dead` (with T3 modelled a station left up after its peer gave up is not settled) and `tables-no-keepalive` (at rest a Connected station keeps T1 or T3 running); and coverage batch 5 adds five for UI frames: `tables-ui-swallowed` and `tables-ui-arm-deleted` (a UI command is delivered, also when its page has no UI arm), `tables-ui-response-delivered` (a UI response is refused with DL-ERROR Q), `tables-ui-poll-unanswered` (a UI poll is answered) and `tables-ui-unsent` (at rest every UI datagram was sent); and coverage batch 6 adds four for frame errors: `tables-frame-error-unreported` and `tables-frame-error-arms-deleted` (a corrupted frame is reported with its DL-ERROR, also when its page has no arm for it), `tables-reset-unconfirmed` and `tables-reset-wrong-code` (a reset loss counts only when layer 3 was told, with the code that names the reset) and `tables-rr-resets` (a reset no corrupted frame justifies never rests and is never a reset loss); and coverage batch 7 adds one for injected I frames: `tables-injected-delivered` (an injected payload counts only when delivered once, by its own in-sequence receipt). A check that never fires in calibration could be switched off without anyone noticing (an independent review did exactly that, setting each to TRUE, and nothing failed). Each fixture breaks the tables just enough to fail one check, so `TlaCheck` can show that both the explorer and TLC still catch it.

The first two are copies of `spec/json` at the ax25spec pin of `feat/transcribe-all-machines` (ax25spec 3027400); `tables-release-stuck` and `tables-dm-ignored` are copies of `spec/json` at ax25spec b44b897 (coverage batch 2), and so are `tables-reconnect-swallowed` and `tables-confirm-as-disconnect` (coverage batch 3), the five coverage batch 4 fixtures and the six coverage batch 5 fixtures (`tables-enquiry-says-rr` and the five `tables-ui-*`) and the five coverage batch 6 fixtures and the coverage batch 7 fixture. All are edited by the script below. Every edited row carries a `notes` field starting `SEEDED DEFECT`, except in `tables-t3-dead`, `tables-ui-arm-deleted` and `tables-frame-error-arms-deleted`, whose edits delete rows. The first two were not regenerated when the pin moved: they still fail exactly the check they exist for, in both tools, and that is all they are for.

| Fixture | Edit | Scenario (`TlaCheck` row) | What fires, in both tools |
| --- | --- | --- | --- |
| `tables-ack-broken` | In `connected.g.json`, the three in-sequence I-frame arms (`t26_i_received_yes_yes_yes_no_yes_yes`, `..._no_yes_no_yes`, `..._no_yes_no_no`) lose their first `DL_DATA_indication`; the loop region after it moves up by one. B now acknowledges a frame it never delivered. | `seeded-ack`: connected seed, A sends 1 frame, no faults | AckCoherence at A, 4 steps |
| `tables-livelock` | The same three arms are reduced to `Discard Contents of I Frame` (no loop), and in `timer_recovery.g.json` `t21_t1_expiry_no` loses `RC := RC + 1`. B never accepts A's frame and A's retries never reach N2, so the poll cycle runs for ever without ending in a terminal state. | `seeded-livelock`: the same scenario | Quiescence (a livelock, not a deadlock) |
| `tables-release-stuck` | In `connected.g.json`, `t19_disc_received` keeps its UA but goes to Connected instead of Disconnected and loses `Stop T3` and `Start T1`. B acknowledges A's DISC and stays up, so the release ends with one end Disconnected and the other not. | `seeded-release-stuck`: connected seed, A sends 2 frames, A releases (`release-a` on the real tables, which is clean) | Deadlock, 9 steps |
| `tables-reconnect-swallowed` | In `connected.g.json`, `t07_dl_connect_request_yes` and `_no` lose all their actions and stay in Connected. A layer 3 that asks for a reset never hears back. | `seeded-reconnect-swallowed`: connected seed, A sends 2 frames, A's layer 3 resets once they are delivered (`reconnect-a` on the real tables, which is clean) | Deadlock, 7 steps |
| `tables-confirm-as-disconnect` | In `awaiting_connection.g.json`, `t04_ua_received_yes_yes` emits `DL_DISCONNECT_indication` instead of `DL_CONNECT_confirm` and still goes Connected. | `seeded-confirm-as-disconnect`: cold connect, no data, A's layer 3 resets while awaiting (`reconnect-cold` on the real tables, which is clean) | Deadlock, 3 steps |
| `tables-rnr-ignored` | In `connected.g.json`, the three `t22_rnr_received_*` arms lose `set_peer_receiver_busy`. A station that receives RNR in Connected keeps sending. | `seeded-rnr-ignored`: connected seed, A sends 2 frames, B enters its local busy condition (`busy-b` on the real tables, which is clean) | PeerBusyHold at A, 3 steps |
| `tables-busy-delivers` | In `connected.g.json`, every `I_received` guard that tests `own_receiver_busy` tests `peer_receiver_busy` instead. A busy station accepts and delivers I frames. | `seeded-busy-delivers`: the same scenario | FlowOffDelivery at B, 3 steps |
| `tables-busy-says-rr` | In `subroutines.g.json`, `Enquiry_Response` path `t01_enquiry_response_yes_yes` (own receiver busy) sends `RR Response` instead of `RNR Response`. This is the answer side, not the mutation ax25spec#94's calibration gate names (that one is `tables-enquiry-says-rr`). A busy station answers a poll as if ready. | `seeded-busy-says-rr`: the same scenario | BusyIndication at B, 4 steps |
| `tables-enquiry-says-rr` | In `subroutines.g.json`, `Transmit_Enquiry` path `t01_transmit_enquiry_yes` (own receiver busy) sends `RR Command` instead of `RNR Command`: the mutation ax25spec#94's calibration gate names. A busy station's own T1 poll claims it is ready. | `seeded-enquiry-says-rr`: connected seed, A sends 1 frame, A enters its local busy condition, one drop (`busy-a-enq` on the real tables, which is clean, 222 states) | BusyIndication at A, 5 steps |
| `tables-ui-swallowed` | In `connected.g.json`, `t18_ui_received_no` loses `UI Check`. Connected swallows a UI command with P = 0. | `seeded-ui-swallowed`: connected seed, A sends 1 frame, both send a UI datagram (`ui-connected` on the real tables, which is clean, 51 states) | UnitData at B, 2 steps |
| `tables-ui-arm-deleted` | In `timer_recovery.g.json`, `t16_ui_received_yes` and `t16_ui_received_no` are deleted. A UI reaching Timer Recovery falls to the lower-layer catch-all and is dropped. | `seeded-ui-arm-deleted`: connected seed, A sends 1 frame, both send a UI datagram, one drop (`ui-tr` on the real tables, which is clean, 214 states) | UnitData at A, 5 steps |
| `tables-ui-unsent` | In `connected.g.json`, `t04_dl_unit_data_request` loses `UI Command`. DL-UNIT-DATA Request in Connected sends nothing. | `seeded-ui-unsent`: the same scenario | Deadlock, 6 steps |
| `tables-ui-poll-unanswered` | In `connected.g.json`, `t18_ui_received_yes` loses `Enquiry_Response_F_1`. A UI poll in Connected goes unanswered. | `seeded-ui-poll-unanswered`: connected seed, no data, A sends a UI poll (`ui-poll` on the real tables, which is clean, 4 states) | UnitData at B, 2 steps |
| `tables-ui-response-delivered` | In `subroutines.g.json`, `UI_Check` path `t03_ui_check_no` delivers `DL_UNIT_DATA_indication` instead of `DL-ERROR Indication (Q)`. A UI response is passed to layer 3. | `seeded-ui-response-delivered`: connected seed, no data, both send a UI response (`ui-response` on the real tables, which is clean, 9 states) | UnitData at B, 2 steps |
| `tables-t3-dead` | In `connected.g.json`, `t13_t3_expiry` is deleted (ax25spec#98's calibration mutation). An idle Connected station never polls. | `seeded-t3-dead`: connected seed, no data, N2 = 1, two drops, A releases, give-up rule and T3 on (`t3-gu-release` on the real tables, which is clean) | Deadlock, 5 steps |
| `tables-no-keepalive` | In `disconnected.g.json` `t13_sabm_received_yes` and in `awaiting_connection.g.json` `t04_ua_received_yes_yes` lose `Start T3`. Neither end of a cold connect has a keepalive. | `seeded-no-keepalive`: cold connect, no data, T3 on (clean on the real tables, 10 states) | Deadlock, 2 steps |
| `tables-frame-error-unreported` | In `connected.g.json`, `t09_control_field_error_no` loses `DL-ERROR Indication (L)`. A control field error in Connected is not reported. | `seeded-fe-unreported`: connected seed, A sends 1 frame, one control field error (`fe-a1` on the real tables, a reset loss) | FrameError at B, 2 steps |
| `tables-frame-error-arms-deleted` | In `timer_recovery.g.json`, the three frame-error arms (`t08_control_field_error`, `t09_info_not_permitted_in_frame`, `t10_u_or_s_frame_length_error`) are deleted. A corrupted frame in Timer Recovery falls to the lower-layer catch-all. | `seeded-fe-arms-deleted`: connected seed, no data, T3, one control, information or length error (`fe-t3-tr` on the real tables, the #167 quiescence) | FrameError at A, 3 steps |
| `tables-reset-unconfirmed` | In `awaiting_connection.g.json`, `t04_ua_received_yes_yes` loses `DL_CONNECT_confirm`. A frame-error reset, which sets Layer 3 Initiated, is never confirmed to the resetting station's layer 3. | `seeded-reset-unconfirmed`: A and B send 1, one control field error; `seeded-reset-unconfirmed-a`: A sends 2 at k = 1, one information field error (`fe-ab1-ctl`, `fe-a2-k1-info` on the real tables, both reset losses) | Deadlock, 4 steps; Deadlock, 6 steps |
| `tables-reset-wrong-code` | In `connected.g.json`, `t14_sabm_received_no` reports `DL-ERROR Indication (A)` instead of `(F)`. A station whose peer resets the link is told of the wrong error. | `seeded-reset-wrong-code`: A sends 1, one control field error (`fe-a1` on the real tables, a reset loss) | Deadlock, 4 steps |
| `tables-rr-resets` | In `connected.g.json`, `t21_rr_received_yes` is replaced by the frame-error reset (DL-ERROR K, discard the I queue, Establish Data Link, Set Layer 3 Initiated, to AwaitingConnection). An in-window RR resets the link with layer 3 told, and no corrupted frame justifies it. | `seeded-rr-resets`: A sends 1, frame errors on, no fault; `seeded-rr-resets-loss`: A sends 2 at k = 1 (clean on the real tables, 5 and 9 states) | Deadlock, 6 steps (not a rest); Deadlock, 6 steps (not a reset loss) |
| `tables-injected-delivered` | In `connected.g.json`, `t26_i_received_yes_yes_yes_no_no_no_yes` (an I frame outside the window, P = 1) delivers it (`Discard Contents of I Frame` becomes `DL_DATA_indication`), and `t26_i_received_yes_yes_yes_no_yes_yes` (in sequence, P = 1) delivers it twice. | `seeded-injected-stale`: connected seed, no data, A injects an I poll outside the window (`ip-stale` on the real tables, which is clean, 4 states); `seeded-injected-twice`: A injects an I poll in sequence (`ip-inseq`, clean, 6 states) | Delivery at B, 2 steps; Delivery at B, 2 steps |
| `tables-dm-ignored` | In `connected.g.json`, `t20_dm_received` loses all its actions and goes to Connected. A station that receives a DM stays up and says nothing. | `seeded-dm-ignored`: connected seed, A sends 1 frame, N2 = 1, two drops, give-up rule on (the real tables give the give-up loss at the same length) | Deadlock, 6 steps |

To regenerate after a pin bump, rerun the script against the new `spec/json` (only when a fixture stops failing its check, since a regenerated fixture can change its row's length):

```python
import json, shutil, os
INSEQ = ['t26_i_received_yes_yes_yes_no_yes_yes', 't26_i_received_yes_yes_yes_no_yes_no_yes', 't26_i_received_yes_yes_yes_no_yes_no_no']
def copy(src, dst):
    os.makedirs(dst, exist_ok=True)
    for f in os.listdir(src): shutil.copy(os.path.join(src, f), dst)
def edit(path, fn):
    d = json.load(open(path)); fn(d)
    with open(path, 'w') as fh: json.dump(d, fh, indent=2, ensure_ascii=False); fh.write('\n')
def drop_first_indication(d):
    for t in d['transitions']:
        if t['id'] in INSEQ:
            i = [a['verb'] for a in t['actions']].index('DL_DATA_indication')
            del t['actions'][i]
            for l in t['loops']:
                if l['start'] > i: l['start'] -= 1
            t['notes'] = 'SEEDED DEFECT (TlaCheck fixture): the in-sequence DL_DATA_indication is removed.'
def discard_inseq(d):
    for t in d['transitions']:
        if t['id'] in INSEQ:
            t['actions'] = [{'verb': 'Discard Contents of I Frame', 'kind': 'processing'}]; t['loops'] = []
            t['notes'] = 'SEEDED DEFECT (TlaCheck fixture): an in-sequence I frame is discarded and nothing else happens.'
def no_rc_increment(d):
    for t in d['transitions']:
        if t['id'] == 't21_t1_expiry_no':
            t['actions'] = [a for a in t['actions'] if a['verb'] != 'RC := RC + 1']
            t['notes'] = 'SEEDED DEFECT (TlaCheck fixture): RC := RC + 1 removed, so N2 is never reached.'
def dm_ignored(d):
    for t in d['transitions']:
        if t['id'] == 't20_dm_received':
            t['actions'] = []; t['loops'] = []; t['next'] = 'Connected'
            t['notes'] = 'SEEDED DEFECT (TlaCheck fixture): a DM in Connected is ignored and the station stays up.'
def connect_swallowed(d):
    for t in d['transitions']:
        if t['id'] in ('t07_dl_connect_request_yes', 't07_dl_connect_request_no'):
            t['actions'] = []; t['loops'] = []; t['next'] = 'Connected'
            t['notes'] = 'SEEDED DEFECT (TlaCheck fixture): DL-CONNECT Request in Connected is ignored; layer 3 never hears back.'
def confirm_as_disconnect(d):
    for t in d['transitions']:
        if t['id'] == 't04_ua_received_yes_yes':
            for a in t['actions']:
                if a['verb'] == 'DL_CONNECT_confirm': a['verb'] = 'DL_DISCONNECT_indication'
            t['notes'] = 'SEEDED DEFECT (TlaCheck fixture): the connect is reported to layer 3 as DL-DISCONNECT Indication, though the station goes Connected.'
def disc_stays_connected(d):
    for t in d['transitions']:
        if t['id'] == 't19_disc_received':
            t['actions'] = [a for a in t['actions'] if a['verb'] not in ('Stop T3', 'Start T1')]
            t['next'] = 'Connected'
            t['notes'] = 'SEEDED DEFECT (TlaCheck fixture): DISC is acknowledged with UA but the station stays Connected.'
F = 'codegen/src/Packet.Sdl.TlaCheck/fixtures'
copy('spec/json', F + '/tables-ack-broken'); edit(F + '/tables-ack-broken/connected.g.json', drop_first_indication)
copy('spec/json', F + '/tables-livelock'); edit(F + '/tables-livelock/connected.g.json', discard_inseq)
edit(F + '/tables-livelock/timer_recovery.g.json', no_rc_increment)
copy('spec/json', F + '/tables-dm-ignored'); edit(F + '/tables-dm-ignored/connected.g.json', dm_ignored)
copy('spec/json', F + '/tables-release-stuck'); edit(F + '/tables-release-stuck/connected.g.json', disc_stays_connected)
copy('spec/json', F + '/tables-reconnect-swallowed'); edit(F + '/tables-reconnect-swallowed/connected.g.json', connect_swallowed)
copy('spec/json', F + '/tables-confirm-as-disconnect'); edit(F + '/tables-confirm-as-disconnect/awaiting_connection.g.json', confirm_as_disconnect)
# Coverage batch 4.
def rnr_ignored(d):
    for t in d['transitions']:
        if t['id'].startswith('t22_rnr_received'):
            t['actions'] = [a for a in t['actions'] if a['verb'] != 'set_peer_receiver_busy']
            t['notes'] = 'SEEDED DEFECT (TlaCheck fixture): set_peer_receiver_busy removed, so an RNR does not stop the station sending.'
def busy_delivers(d):
    for t in d['transitions']:
        if t['on'] == 'I_received' and 'own_receiver_busy' in t['guard']:
            t['guard'] = t['guard'].replace('own_receiver_busy', 'peer_receiver_busy')
            t['notes'] = 'SEEDED DEFECT (TlaCheck fixture): the own-receiver-busy test reads peer receiver busy, so a busy station accepts and delivers I frames.'
def busy_says_rr(d):
    for s in d['subroutines']:
        if s['name'] == 'Enquiry_Response':
            for p in s['paths']:
                if p['id'] == 't01_enquiry_response_yes_yes':
                    for a in p['actions']:
                        if a['verb'] == 'RNR Response': a['verb'] = 'RR Response'
                    p['notes'] = 'SEEDED DEFECT (TlaCheck fixture): a busy station answers a poll with RR instead of RNR.'
def enquiry_says_rr(d):
    for s in d['subroutines']:
        if s['name'] == 'Transmit_Enquiry':
            for p in s['paths']:
                if p['id'] == 't01_transmit_enquiry_yes':
                    for a in p['actions']:
                        if a['verb'] == 'RNR Command': a['verb'] = 'RR Command'
                    p['notes'] = 'SEEDED DEFECT (TlaCheck fixture): a busy station polls with RR instead of RNR.'
def t3_dead(d):
    d['transitions'] = [t for t in d['transitions'] if t['id'] != 't13_t3_expiry']
def no_t3(tid, who):
    def f(d):
        for t in d['transitions']:
            if t['id'] == tid:
                t['actions'] = [a for a in t['actions'] if a['verb'] != 'Start T3']
                t['notes'] = 'SEEDED DEFECT (TlaCheck fixture): Start T3 removed, so the ' + who + ' of a connect has no keepalive.'
    return f
copy('spec/json', F + '/tables-rnr-ignored'); edit(F + '/tables-rnr-ignored/connected.g.json', rnr_ignored)
copy('spec/json', F + '/tables-busy-delivers'); edit(F + '/tables-busy-delivers/connected.g.json', busy_delivers)
copy('spec/json', F + '/tables-busy-says-rr'); edit(F + '/tables-busy-says-rr/subroutines.g.json', busy_says_rr)
copy('spec/json', F + '/tables-enquiry-says-rr'); edit(F + '/tables-enquiry-says-rr/subroutines.g.json', enquiry_says_rr)
copy('spec/json', F + '/tables-t3-dead'); edit(F + '/tables-t3-dead/connected.g.json', t3_dead)
copy('spec/json', F + '/tables-no-keepalive'); edit(F + '/tables-no-keepalive/disconnected.g.json', no_t3('t13_sabm_received_yes', 'responder'))
edit(F + '/tables-no-keepalive/awaiting_connection.g.json', no_t3('t04_ua_received_yes_yes', 'initiator'))
# Coverage batch 5 (enquiry_says_rr is above).
def ui_swallowed(d):
    for t in d['transitions']:
        if t['id'] == 't18_ui_received_no':
            t['actions'] = []; t['loops'] = []
            t['notes'] = 'SEEDED DEFECT (TlaCheck fixture): UI Check removed, so a UI command with P = 0 is swallowed in Connected.'
def ui_unsent(d):
    for t in d['transitions']:
        if t['id'] == 't04_dl_unit_data_request':
            t['actions'] = []; t['loops'] = []
            t['notes'] = 'SEEDED DEFECT (TlaCheck fixture): UI Command removed, so DL-UNIT-DATA Request in Connected sends nothing.'
def ui_poll_unanswered(d):
    for t in d['transitions']:
        if t['id'] == 't18_ui_received_yes':
            t['actions'] = [a for a in t['actions'] if a['verb'] != 'Enquiry_Response_F_1']
            t['notes'] = 'SEEDED DEFECT (TlaCheck fixture): Enquiry Response removed, so a UI poll in Connected is not answered.'
def ui_response_delivered(d):
    for s in d['subroutines']:
        if s['name'] == 'UI_Check':
            for p in s['paths']:
                if p['id'] == 't03_ui_check_no':
                    for a in p['actions']:
                        if a['verb'] == 'DL-ERROR Indication (Q)': a['verb'] = 'DL_UNIT_DATA_indication'
                    p['notes'] = 'SEEDED DEFECT (TlaCheck fixture): a UI response is delivered upward instead of drawing DL-ERROR (Q).'
copy('spec/json', F + '/tables-ui-swallowed'); edit(F + '/tables-ui-swallowed/connected.g.json', ui_swallowed)
def ui_arm_deleted(d):
    d['transitions'] = [t for t in d['transitions'] if not t['id'].startswith('t16_ui_received')]
copy('spec/json', F + '/tables-ui-arm-deleted'); edit(F + '/tables-ui-arm-deleted/timer_recovery.g.json', ui_arm_deleted)
copy('spec/json', F + '/tables-ui-unsent'); edit(F + '/tables-ui-unsent/connected.g.json', ui_unsent)
copy('spec/json', F + '/tables-ui-poll-unanswered'); edit(F + '/tables-ui-poll-unanswered/connected.g.json', ui_poll_unanswered)
copy('spec/json', F + '/tables-ui-response-delivered'); edit(F + '/tables-ui-response-delivered/subroutines.g.json', ui_response_delivered)
# Coverage batch 6.
def frame_error_unreported(d):
    for t in d['transitions']:
        if t['id'] == 't09_control_field_error_no':
            t['actions'] = [a for a in t['actions'] if a['verb'] != 'DL-ERROR Indication (L)']
            t['notes'] = 'SEEDED DEFECT (TlaCheck fixture): the control field error is not reported to layer 3.'
def frame_error_arms_deleted(d):
    d['transitions'] = [t for t in d['transitions'] if t['on'] not in ('control_field_error', 'info_not_permitted_in_frame', 'u_or_s_frame_length_error')]
def reset_unconfirmed(d):
    for t in d['transitions']:
        if t['id'] == 't04_ua_received_yes_yes':
            t['actions'] = [a for a in t['actions'] if a['verb'] != 'DL_CONNECT_confirm']
            t['notes'] = 'SEEDED DEFECT (TlaCheck fixture): a connect layer 3 initiated is not confirmed to it.'
def rr_resets(d):
    for t in d['transitions']:
        if t['id'] == 't21_rr_received_yes':
            t['actions'] = [{'verb': 'DL-ERROR Indication (K)', 'kind': 'signal_upper'}, {'verb': 'discard_I_frame_queue', 'kind': 'processing'},
                            {'verb': 'Establish_Data_Link', 'kind': 'subroutine'}, {'verb': 'Set Layer 3 Initiated', 'kind': 'processing'}]
            t['loops'] = []; t['next'] = 'AwaitingConnection'
            t['notes'] = 'SEEDED DEFECT (TlaCheck fixture): an in-window RR resets the link (reported, discarding the I queue), with no frame error to justify it.'
copy('spec/json', F + '/tables-frame-error-unreported'); edit(F + '/tables-frame-error-unreported/connected.g.json', frame_error_unreported)
copy('spec/json', F + '/tables-frame-error-arms-deleted'); edit(F + '/tables-frame-error-arms-deleted/timer_recovery.g.json', frame_error_arms_deleted)
copy('spec/json', F + '/tables-reset-unconfirmed'); edit(F + '/tables-reset-unconfirmed/awaiting_connection.g.json', reset_unconfirmed)
copy('spec/json', F + '/tables-rr-resets'); edit(F + '/tables-rr-resets/connected.g.json', rr_resets)
def reset_reported_as_a(d):
    for t in d['transitions']:
        if t['id'] == 't14_sabm_received_no':
            for a in t['actions']:
                if a['verb'] == 'DL-ERROR Indication (F)': a['verb'] = 'DL-ERROR Indication (A)'
            t['notes'] = 'SEEDED DEFECT (TlaCheck fixture): a reset by the peer is reported as DL-ERROR (A), not (F).'
copy('spec/json', F + '/tables-reset-wrong-code'); edit(F + '/tables-reset-wrong-code/connected.g.json', reset_reported_as_a)
# Coverage batch 7.
def injected_delivered(d):
    for t in d['transitions']:
        if t['id'] == 't26_i_received_yes_yes_yes_no_no_no_yes':
            for a in t['actions']:
                if a['verb'] == 'Discard Contents of I Frame': a['verb'] = 'DL_DATA_indication'; a['kind'] = 'signal_upper'
            t['notes'] = 'SEEDED DEFECT (TlaCheck fixture): an I frame outside the receive window (P = 1) is delivered upward instead of discarded.'
        if t['id'] == 't26_i_received_yes_yes_yes_no_yes_yes':
            i = [a['verb'] for a in t['actions']].index('DL_DATA_indication')
            t['actions'].insert(i + 1, {'verb': 'DL_DATA_indication', 'kind': 'signal_upper'})
            for l in t['loops']:
                if l['start'] > i: l['start'] += 1
            t['notes'] = 'SEEDED DEFECT (TlaCheck fixture): an in-sequence I frame with P = 1 is delivered upward twice.'
copy('spec/json', F + '/tables-injected-delivered'); edit(F + '/tables-injected-delivered/connected.g.json', injected_delivered)
```

# Seeded-defect fixtures

**These tables are deliberately broken.** They are not figures and say nothing about AX.25. They exist because no real defect, on any table set the explorer has, fails two of its checks: acknowledgement coherence and quiescence. A check that never fires in calibration could be switched off without anyone noticing (an independent review did exactly that, setting each to TRUE, and nothing failed). Each fixture breaks the tables just enough to fail one check, so `TlaCheck` can show that both the explorer and TLC still catch it.

Both are copies of `spec/json` at the ax25spec pin of `feat/transcribe-all-machines` (ax25spec 3027400), edited by a script. Every edited row carries a `notes` field starting `SEEDED DEFECT`.

| Fixture | Edit | Scenario (`TlaCheck` row) | What fires, in both tools |
| --- | --- | --- | --- |
| `tables-ack-broken` | In `connected.g.json`, the three in-sequence I-frame arms (`t26_i_received_yes_yes_yes_no_yes_yes`, `..._no_yes_no_yes`, `..._no_yes_no_no`) lose their first `DL_DATA_indication`; the loop region after it moves up by one. B now acknowledges a frame it never delivered. | `seeded-ack`: connected seed, A sends 1 frame, no faults | AckCoherence at A, 4 steps |
| `tables-livelock` | The same three arms are reduced to `Discard Contents of I Frame` (no loop), and in `timer_recovery.g.json` `t21_t1_expiry_no` loses `RC := RC + 1`. B never accepts A's frame and A's retries never reach N2, so the poll cycle runs for ever without ending in a terminal state. | `seeded-livelock`: the same scenario | Quiescence (a livelock, not a deadlock) |

To regenerate after a pin bump, rerun the script against the new `spec/json`:

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
F = 'codegen/src/Packet.Sdl.TlaCheck/fixtures'
copy('spec/json', F + '/tables-ack-broken'); edit(F + '/tables-ack-broken/connected.g.json', drop_first_indication)
copy('spec/json', F + '/tables-livelock'); edit(F + '/tables-livelock/connected.g.json', discard_inseq)
edit(F + '/tables-livelock/timer_recovery.g.json', no_rc_increment)
```

# tables-mut-rr-nr-unchecked

A **deliberate mutation** of the current tables (`spec/json` at the commit this fixture was added), not a historical snapshot: figc4.4's RR-received arm with the `V(a) <= N(r) <= V(s)` decision removed, so an RR carrying an out-of-window N(r) is accepted and `Check_I_Frame_Acknowledged` sets V(a) from it. It is the calibration gate of packethacking/ax25spec#95 (V2, channel reordering): a search that cannot catch this mutation could not catch a real N(r) validity defect either.

The only difference from `spec/json` is in `connected.g.json`:

- `t21_rr_received_yes`: guard `va_le_nr_le_vs` replaced by the empty guard (always taken);
- `t21_rr_received_no_no` and `t21_rr_received_no_yes` (the N(r) Error Recovery arms) removed.

On a FIFO channel the mutation is unobservable: a peer never sends an N(r) outside [V(a), V(s)], so the removed decision is never false. Under reordering a stale RR overtaken by a newer one is accepted and V(a) steps backwards, which the acknowledgement-coherence invariant reports. See `CalibrationV2V3Tests.cs` and docs/explorer.md.

Regenerate from the current tables with:

```sh
cp -r spec/json codegen/tests/Packet.Sdl.Explorer.Tests/fixtures/tables-mut-rr-nr-unchecked
python3 - <<'PY'
import json
p = 'codegen/tests/Packet.Sdl.Explorer.Tests/fixtures/tables-mut-rr-nr-unchecked/connected.g.json'
t = json.load(open(p))
keep = []
for tr in t['transitions']:
    if tr['id'] == 't21_rr_received_yes':
        tr['guard'] = ''
    elif tr['id'] in ('t21_rr_received_no_no', 't21_rr_received_no_yes'):
        continue
    keep.append(tr)
t['transitions'] = keep
open(p, 'w').write(json.dumps(t, indent=2, ensure_ascii=False) + '\n')
PY
```

(`json.dumps(indent=2)` reproduces the codegen's JSON byte for byte, so `diff -r spec/json <fixture>` shows only the mutation.) Do not hand-edit; regenerate when the codegen's JSON shape changes.

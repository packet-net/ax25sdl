# tables-mut-establish-swapped

A **deliberate mutation** of the current tables (`spec/json` at the commit this fixture was added), not a historical snapshot: the SABM and SABME emissions of the figc4.7 `Establish_Data_Link` subroutine swapped, so the `not mod_128` path sends SABME and the `mod_128` path sends SABM. It is the calibration gate of packethacking/ax25spec#96 (V3, modulo 128): a mutation in a `mod_128`-guarded arm that the modulo-128 search must catch.

The only difference from `spec/json` is in `subroutines.g.json`, `Establish_Data_Link`:

- `t01_establish_data_link_no` (guard `not mod_128`): verb `SABM` -> `SABME`;
- `t02_establish_data_link_yes` (guard `mod_128`): verb `SABME` -> `SABM`.

The default invariants cannot see it: the abstract frame model accepts either establishment frame and the tables never assign the modulus (#54), so a SABM into a modulo-128 peer connects and carries on at modulo 128. The opt-in modulus-coherence invariant (`--modulus-coherence`: SABM only at modulo 8, SABME only at modulo 128, §4.3.3.1 and §4.3.3.2) reports it at the emission. From the Connected seed `Establish_Data_Link` is reached through N(r) Error Recovery, i.e. under reordering; from the `awaiting-v22` seed through figc4.6's FRMR arm. See `CalibrationV2V3Tests.cs` and docs/explorer.md.

Regenerate from the current tables with:

```sh
cp -r spec/json codegen/tests/Packet.Sdl.Explorer.Tests/fixtures/tables-mut-establish-swapped
python3 - <<'PY'
import json
p = 'codegen/tests/Packet.Sdl.Explorer.Tests/fixtures/tables-mut-establish-swapped/subroutines.g.json'
s = json.load(open(p))
for sub in s['subroutines']:
    if sub['name'] == 'Establish_Data_Link':
        for path in sub['paths']:
            for a in path['actions']:
                if a['verb'] == 'SABM' and path['guard'] == 'not mod_128': a['verb'] = 'SABME'
                elif a['verb'] == 'SABME' and path['guard'] == 'mod_128': a['verb'] = 'SABM'
open(p, 'w').write(json.dumps(s, indent=2, ensure_ascii=False) + '\n')
PY
```

Do not hand-edit; regenerate when the codegen's JSON shape changes.

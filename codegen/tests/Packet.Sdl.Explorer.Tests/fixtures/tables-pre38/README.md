# tables-pre38

JSON tables generated from `packethacking/ax25spec` at commit `fb727db` ("Relocate normative SDL sources (spec-sdl/ + tools/render/) here from packet-net/ax25sdl, history-preserving (#64)"): the figures BEFORE the #38 fix (figc4.5's SREJ arms still call `Invoke Retransmission`, go-back-N) and before the #40 guard.

Generated with the codegen on this branch:

```sh
git -C /home/tf/ax25spec worktree add /home/tf/ax25spec-wt/pre38 fb727db
dotnet run --project codegen/src/Packet.Sdl.CodeGen -- --in /home/tf/ax25spec-wt/pre38/spec-sdl --json --json-out codegen/tests/Packet.Sdl.Explorer.Tests/fixtures/tables-pre38/
```

Used by the calibration suite (`CalibrationTests.cs`) for the #38 case, which is a documented limit: the explorer's invariants do not discriminate these tables from `tables-pre40`. Do not hand-edit; regenerate with the command above if the codegen's JSON shape changes.

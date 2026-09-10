# tables-pre40

JSON tables generated from `packethacking/ax25spec` at commit `abd46b0` ("fix(figc4.5 + §6.4.11): SREJ retransmits the single frame N(r), not go-back-N (#38) (#65)"): the figures WITH the #38 fix and WITHOUT the #40 out-of-window guard (`vr_lt_ns_lt_vr_plus_k` does not exist here).

Generated with the codegen on this branch:

```sh
git -C /home/tf/ax25spec worktree add /home/tf/ax25spec-wt/pre40 abd46b0
dotnet run --project codegen/src/Packet.Sdl.CodeGen -- --in /home/tf/ax25spec-wt/pre40/spec-sdl --json --json-out codegen/tests/Packet.Sdl.Explorer.Tests/fixtures/tables-pre40/
```

Used by the calibration suite (`CalibrationTests.cs`) as the defective side of the #40 case and as the fixed side of the #38 case. Do not hand-edit; regenerate with the command above if the codegen's JSON shape changes.

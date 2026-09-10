# tables-43fixed

The current tables (`spec/json` at the submodule pin this branch carries: `packethacking/ax25spec` d8f67ad) with two changes applied by hand to the JSON, and nothing else:

1. **The ax25spec#43 branch swap.** On `connected.g.json` (figc4.4) and `timer_recovery.g.json` (figc4.5) the guards of the two `DL_FLOW_OFF_request` arms are exchanged, so that "Set Own Receiver Busy, RNR Response, Clear Acknowledge Pending" runs on `not own_receiver_busy` and the empty arm on `own_receiver_busy`. The transition ids are kept (`t05_dl_flow_off_request_yes` is still the arm with the actions), so a trace on this fixture reads against the figure's own branch labels; only the guard text differs.
2. **The H3 staging box** (`docs/explorer.md`, "Novel findings", H3). An `N(r) := V(r)` processing action is inserted before the RNR Response on that busy arm and before the RR Command on both `DL_FLOW_ON_request` busy arms, on both pages. Without it the interpreter's staging policy (pinned semantic 3) makes every one of those emissions an error the moment the branch swap makes them reachable.

This is the fixture route of ax25spec#94 (V1, the peer-busy family): the busy arms of figc4.4 and figc4.5 are unreachable on the current tables because #43 makes a station unable to become busy, and the umbrella #93 says a blocked verification issue explores a fixture copy of the tables with the proposed fix applied, never a change to `spec-sdl`. When #43 lands in the figures (with or without the H3 box, which the working group has to decide), point the tests in `CalibrationTests.cs` that use `Fixtures.Fixed43Dir` at the current tables and delete this directory.

The mutants the calibration gate plants (no "Set Own Receiver Busy" on the DL-FLOW-OFF arm; RR instead of RNR in `Transmit_Enquiry`'s busy path; a pop that ignores peer-busy) are not committed: `Fixtures.Mutant` derives them from this directory at test time, into `bin/.../mutants/`, so the mutation is code next to the test that must catch it.

Regenerate from the current tables with the patch script below (Python 3, run from the repo root); the diff it produces is recorded after it.

```python
import json, collections, shutil
F = "codegen/tests/Packet.Sdl.Explorer.Tests/fixtures/tables-43fixed"
shutil.copytree("spec/json", F, dirs_exist_ok=True)
NR = {"verb": "N(r) := V(r)", "kind": "processing"}
for page, rnr, rr in [("connected", "RNR Response", "RR Command"), ("timer_recovery", "RNR Response (F = 0)", "RR Command (P = 0)")]:
    p = f"{F}/{page}.g.json"
    d = json.load(open(p), object_pairs_hook=collections.OrderedDict)
    byid = {t["id"]: t for t in d["transitions"]}
    yes, no = byid["t05_dl_flow_off_request_yes"], byid["t05_dl_flow_off_request_no"]
    yes["guard"], no["guard"] = no["guard"], yes["guard"]          # the #43 swap
    yes["actions"].insert(1, dict(NR))                              # H3: before the RNR Response
    for tid in ("t06_dl_flow_on_request_yes_yes", "t06_dl_flow_on_request_yes_no"):
        t = byid[tid]
        i = [a["verb"] for a in t["actions"]].index(rr)
        t["actions"].insert(i, dict(NR))                            # H3: before the RR Command
    with open(p, "w") as f:
        json.dump(d, f, indent=2); f.write("\n")
```

## The diff against `spec/json`

```diff
 {spec/json => codegen/tests/Packet.Sdl.Explorer.Tests/fixtures/tables-43fixed}/connected.g.json      | 16 ++++++++++++++--
 {spec/json => codegen/tests/Packet.Sdl.Explorer.Tests/fixtures/tables-43fixed}/timer_recovery.g.json | 16 ++++++++++++++--
 2 files changed, 28 insertions(+), 4 deletions(-)
diff --git a/spec/json/connected.g.json b/fixtures/tables-43fixed/connected.g.json
index 6d2da16..d4b90c1 100644
--- a/spec/json/connected.g.json
+++ b/fixtures/tables-43fixed/connected.g.json
@@ -189,12 +189,16 @@
       "id": "t05_dl_flow_off_request_yes",
       "from": "Connected",
       "on": "DL_FLOW_OFF_request",
-      "guard": "own_receiver_busy",
+      "guard": "not own_receiver_busy",
       "actions": [
         {
           "verb": "Set Own Receiver Busy",
           "kind": "processing"
         },
+        {
+          "verb": "N(r) := V(r)",
+          "kind": "processing"
+        },
         {
           "verb": "RNR Response",
           "kind": "signal_lower"
@@ -213,7 +217,7 @@
       "id": "t05_dl_flow_off_request_no",
       "from": "Connected",
       "on": "DL_FLOW_OFF_request",
-      "guard": "not own_receiver_busy",
+      "guard": "own_receiver_busy",
       "actions": [],
       "next": "Connected",
       "notes": "",
@@ -241,6 +245,10 @@
           "verb": "Clear Own Receiver Busy",
           "kind": "processing"
         },
+        {
+          "verb": "N(r) := V(r)",
+          "kind": "processing"
+        },
         {
           "verb": "RR Command",
           "kind": "signal_lower"
@@ -265,6 +273,10 @@
           "verb": "Clear Own Receiver Busy",
           "kind": "processing"
         },
+        {
+          "verb": "N(r) := V(r)",
+          "kind": "processing"
+        },
         {
           "verb": "RR Command",
           "kind": "signal_lower"
diff --git a/spec/json/timer_recovery.g.json b/fixtures/tables-43fixed/timer_recovery.g.json
index 5725155..fd3cea9 100644
--- a/spec/json/timer_recovery.g.json
+++ b/fixtures/tables-43fixed/timer_recovery.g.json
@@ -189,12 +189,16 @@
       "id": "t05_dl_flow_off_request_yes",
       "from": "TimerRecovery",
       "on": "DL_FLOW_OFF_request",
-      "guard": "own_receiver_busy",
+      "guard": "not own_receiver_busy",
       "actions": [
         {
           "verb": "Set Own Receiver Busy",
           "kind": "processing"
         },
+        {
+          "verb": "N(r) := V(r)",
+          "kind": "processing"
+        },
         {
           "verb": "RNR Response (F = 0)",
           "kind": "signal_lower"
@@ -213,7 +217,7 @@
       "id": "t05_dl_flow_off_request_no",
       "from": "TimerRecovery",
       "on": "DL_FLOW_OFF_request",
-      "guard": "not own_receiver_busy",
+      "guard": "own_receiver_busy",
       "actions": [],
       "next": "TimerRecovery",
       "notes": "",
@@ -230,6 +234,10 @@
           "verb": "Clear Own Receiver Busy",
           "kind": "processing"
         },
+        {
+          "verb": "N(r) := V(r)",
+          "kind": "processing"
+        },
         {
           "verb": "RR Command (P = 0)",
           "kind": "signal_lower"
@@ -262,6 +270,10 @@
           "verb": "Clear Own Receiver Busy",
           "kind": "processing"
         },
+        {
+          "verb": "N(r) := V(r)",
+          "kind": "processing"
+        },
         {
           "verb": "RR Command (P = 0)",
           "kind": "signal_lower"
```

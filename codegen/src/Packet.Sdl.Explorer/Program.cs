using System.Globalization;
using Packet.Sdl.Explorer;

var usage = """
usage: dotnet run --project codegen/src/Packet.Sdl.Explorer -- [options]

  --tables DIR           JSON tables directory (default spec/json)
  --frames-ab N          I frames A submits for B, 0..3 (default 2)
  --frames-ba N          I frames B submits for A, 0..3 (default 0)
  --k N                  window size 1..7 (default 4; keep <= 4 unless testing the k > modulus/2 constraint)
  --srej                 selective reject negotiated (default off)
  --n2 N                 retry limit (default 4)
  --budget N             channel fault budget (default 0)
  --faults LIST          comma list of drop,dup,reorder (default drop,dup)
  --drop-scope any|interior-i   which frames a drop may hit (default any)
  --seed connected|disconnected|awaiting-v22 (default connected; awaiting-v22 = A already in
                         AwaitingV22Connection with its SABME in flight, no data)
  --peer tables|v20-frmr|v20-dm   what plays B (default tables): the hand-written v2.0 stub peer answers
                         SABME with FRMR or with DM F=1, accepts SABM; connect-phase seeds only, no data
  --peer-declines-sabme  the table-driven B answers SABME with DM but accepts SABM (able_to_establish = false)
  --mod128               seed A at modulo 128 so DL_CONNECT_request sends SABME
  --flow-control A|B|both   let that station's layer 3 issue DL_FLOW_OFF_request then DL_FLOW_ON_request
  --flow-rounds N        FLOW_OFF/FLOW_ON rounds per flow-controlling station (default 1)
  --flow-off-after N     frames a station must have delivered upward before FLOW_OFF is enabled (default 1)
  --disable LIST         comma list of invariants to switch off: defined-state, sequence, delivery,
                         reject-coherence, ack-coherence, quiescence, deadlock, flow-off-delivery, busy-tracks-flow
  --selective-progress   quiescence must be reachable without any T1 expiry (off by default): selective-recovery
                         progress in the data phase, a timeout-free fallback in the connect phase
  --rej-may-equal-vs     accept a REJ whose N(r) equals the receiver's V(s)
  --quiescence-accepts-timer-recovery   triage aid: a station in TimerRecovery counts as quiescent
  --max-depth N          depth bound (default 80)
  --max-states N         visited-state bound (default 400000)

exit code: 0 no violation, 1 violation, 2 bound hit
""";

string tables = "spec/json";
int framesAb = 2, framesBa = 0, k = 4, n2 = 4, budget = 0, maxDepth = 80, maxStates = 400_000, flowRounds = 1, flowOffAfter = 1;
bool srej = false, peerDeclines = false, mod128 = false, selective = false, rejMayEqualVs = false, trQuiescent = false;
var faults = FaultKinds.Drop | FaultKinds.Duplicate;
var dropScope = DropScope.Any;
var seed = SeedKind.Connected;
var peer = PeerKind.Tables;
var flowControl = FlowControlAt.None;
var invariants = Invariants.Default;

try
{
    for (var i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--help" or "-h":
                Console.WriteLine(usage);
                return 0;
            case "--tables": tables = Next(args, ref i); break;
            case "--frames-ab": framesAb = Int(Next(args, ref i)); break;
            case "--frames-ba": framesBa = Int(Next(args, ref i)); break;
            case "--k": k = Int(Next(args, ref i)); break;
            case "--n2": n2 = Int(Next(args, ref i)); break;
            case "--budget": budget = Int(Next(args, ref i)); break;
            case "--max-depth": maxDepth = Int(Next(args, ref i)); break;
            case "--max-states": maxStates = Int(Next(args, ref i)); break;
            case "--flow-rounds": flowRounds = Int(Next(args, ref i)); break;
            case "--flow-off-after": flowOffAfter = Int(Next(args, ref i)); break;
            case "--flow-control":
                flowControl = Next(args, ref i) switch
                {
                    "A" or "a" => FlowControlAt.A,
                    "B" or "b" => FlowControlAt.B,
                    "both" => FlowControlAt.Both,
                    "none" => FlowControlAt.None,
                    var v => throw new ArgumentException($"unknown flow-control station `{v}` (A, B, both, none)"),
                };
                break;
            case "--peer":
                peer = Next(args, ref i) switch
                {
                    "tables" => PeerKind.Tables,
                    "v20-frmr" => PeerKind.V20Frmr,
                    "v20-dm" => PeerKind.V20Dm,
                    var v => throw new ArgumentException($"unknown peer `{v}` (tables, v20-frmr, v20-dm)"),
                };
                break;
            case "--srej": srej = true; break;
            case "--peer-declines-sabme": peerDeclines = true; break;
            case "--mod128": mod128 = true; break;
            case "--selective-progress": selective = true; break;
            case "--rej-may-equal-vs": rejMayEqualVs = true; break;
            case "--quiescence-accepts-timer-recovery": trQuiescent = true; break;
            case "--faults":
                faults = FaultKinds.None;
                foreach (var f in Next(args, ref i).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    faults |= f switch
                    {
                        "drop" => FaultKinds.Drop,
                        "dup" => FaultKinds.Duplicate,
                        "reorder" => FaultKinds.Reorder,
                        _ => throw new ArgumentException($"unknown fault kind `{f}` (drop, dup, reorder)"),
                    };
                }
                break;
            case "--drop-scope":
                dropScope = Next(args, ref i) switch
                {
                    "any" => DropScope.Any,
                    "interior-i" => DropScope.InteriorI,
                    var v => throw new ArgumentException($"unknown drop scope `{v}` (any, interior-i)"),
                };
                break;
            case "--seed":
                seed = Next(args, ref i) switch
                {
                    "connected" => SeedKind.Connected,
                    "disconnected" => SeedKind.Disconnected,
                    "awaiting-v22" => SeedKind.AwaitingV22Connection,
                    var v => throw new ArgumentException($"unknown seed `{v}` (connected, disconnected, awaiting-v22)"),
                };
                break;
            case "--disable":
                foreach (var name in Next(args, ref i).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    invariants &= ~(name switch
                    {
                        "defined-state" => Invariants.DefinedState,
                        "sequence" => Invariants.SequenceSanity,
                        "delivery" => Invariants.Delivery,
                        "reject-coherence" => Invariants.RejectCoherence,
                        "ack-coherence" => Invariants.AckCoherence,
                        "quiescence" => Invariants.Quiescence,
                        "deadlock" => Invariants.Deadlock,
                        "flow-off-delivery" => Invariants.FlowOffDelivery,
                        "busy-tracks-flow" => Invariants.BusyTracksFlow,
                        _ => throw new ArgumentException($"unknown invariant `{name}`"),
                    });
                }
                break;
            default:
                throw new ArgumentException($"unknown option `{args[i]}`\n{usage}");
        }
    }
    if (selective) invariants |= Invariants.SelectiveProgress;

    var options = new ExplorerOptions
    {
        TablesDir = tables,
        FramesAb = framesAb,
        FramesBa = framesBa,
        K = k,
        Srej = srej,
        N2 = n2,
        Budget = budget,
        Faults = faults,
        DropScope = dropScope,
        Seed = seed,
        PeerDeclinesSabme = peerDeclines,
        Modulo128A = mod128,
        Peer = peer,
        FlowControl = flowControl,
        FlowRounds = flowRounds,
        FlowOffAfterDelivered = flowOffAfter,
        Invariants = invariants,
        RejMayEqualVs = rejMayEqualVs,
        TimerRecoveryIsQuiescent = trQuiescent,
        MaxDepth = maxDepth,
        MaxStates = maxStates,
    };

    var result = Explorer.Run(options);
    Console.WriteLine(result.Render());
    return result.Outcome switch
    {
        Outcome.NoViolation => 0,
        Outcome.Violation => 1,
        _ => 2,
    };
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine("error: " + ex.Message);
    return 3;
}
catch (InvalidOperationException ex)
{
    Console.Error.WriteLine("error: " + ex.Message);
    return 3;
}
catch (IOException ex)
{
    Console.Error.WriteLine("error: " + ex.Message);
    return 3;
}

static string Next(string[] args, ref int i)
{
    if (i + 1 >= args.Length) throw new ArgumentException($"option {args[i]} needs a value");
    return args[++i];
}

static int Int(string v) =>
    int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : throw new ArgumentException($"expected an integer, got `{v}`");

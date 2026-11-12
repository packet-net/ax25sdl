using System.Globalization;
using Packet.Sdl.Explorer;

var usage = """
usage: dotnet run --project codegen/src/Packet.Sdl.Explorer -- [options]

  --tables DIR           JSON tables directory (default spec/json)
  --frames-ab N          I frames A submits for B, 0 or more (default 2; the space grows fast)
  --frames-ba N          I frames B submits for A, 0 or more (default 0; the space grows fast)
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
  --data-after-confirm   disconnected seed: each station's layer 3 submits its data only once the link is up
                         (DL_CONNECT_confirm at A, DL_CONNECT_indication at B); B may then have data too
  --data-before-confirm  with the disconnected seed: A's layer 3 submits its data by moves from the start, without
                         waiting for DL_CONNECT_confirm (implies --data-after-confirm for B)
  --release A|both       that station's layer 3 (or either's) issues DL_DISCONNECT_request once, on an established
                         link, when everything both sides submitted is delivered; quiescence is then both Disconnected
  --release-early        with --release: the release may also come while the link is being set up
  --reconnect A|both     that station's layer 3 (or either's) issues DL_CONNECT_request once, on a live or waiting
                         link, when everything both sides submitted is delivered, and must then get its confirm
  --accept-give-up       the give-up rule: a station that exhausts N2 after enough losses since its last progress
                         has given up honestly, and a settled honest give-up with everything delivered is quiescent
                         (one that lost data is a give-up-loss violation; an unjustified give-up never rests)
  --flow-control A|B|both   let that station's layer 3 issue DL_FLOW_OFF_request then DL_FLOW_ON_request
  --local-busy A|B|both  that station enters a local busy condition once on an established link (own receiver busy,
                         RNR, as the #43-fixed DL-FLOW-OFF arm draws it) and may leave it (RR) while it holds
  --long-busy            with --local-busy: the busy period may outlast the peer's N2 retries (by default the peer's
                         T1 holds back the expiry that would exhaust N2 while the station is busy)
  --t3                   model T3 expiry (idle keepalive, T1 not running); at rest a Connected station keeps T1 or
                         T3 running, and with --accept-give-up a station left up after its peer gave up never settles
  --unit-data A|B|both   that station's layer 3 (or either's) sends one UI datagram, once, in any state; at rest every
                         datagram was sent and then reached its peer or was dropped
  --ui-form plain|poll|response|long   with --unit-data: the datagram's form on the air (default plain, a UI command
                         P=0 through DL_UNIT_DATA_request; poll and response are put on the air by the harness; long
                         goes through the table and fails the receiver's N1 check)
  --inject A|B|both      that station (or either) puts one injected frame on the air, as from another implementation
  --inject-frame FORM    with --inject: rej-poll (receiver in TimerRecovery), srej-command (receiver in Connected or
                         TimerRecovery; srej-poll the same with P=1), each naming a frame outstanding at the receiver, ua-f0 (receiver on a connect or release page) or frmr (receiver in
                         Connected or TimerRecovery), or an I command carrying payload xa0 (xb0): i-frame (P=0) or
                         i-poll (P=1) with N(s) the receiver's V(r), i-poll-ahead (V(r)+1) or i-poll-stale (V(r)-1);
                         FIRST,SECOND puts a second frame on the air after the first
  --frame-errors LIST    comma list of corrupted forms a frame may arrive in, as a channel fault under the budget:
                         control, info, length, n1, i-response; the receiver reports each with its DL-ERROR, a
                         reset is honest only on a corrupted frame, and data an honest reset loses is reset-loss
  --flow-rounds N        FLOW_OFF/FLOW_ON rounds per flow-controlling station (default 1)
  --flow-off-after N     frames a station must have delivered upward before FLOW_OFF is enabled (default 1)
  --disable LIST         comma list of invariants to switch off: defined-state, sequence, delivery,
                         reject-coherence, ack-coherence, quiescence, deadlock, flow-off-delivery, busy-tracks-flow,
                         peer-busy-hold, busy-indication, unit-data, frame-error
  --selective-progress   quiescence must be reachable without any T1 expiry (off by default): selective-recovery
                         progress in the data phase, a timeout-free fallback in the connect phase
  --rej-may-equal-vs     accept a REJ whose N(r) equals the receiver's V(s); with --inject, also let the injected
                         REJ and SREJ commands name the receiver's V(s) (receiver in TimerRecovery, channels empty)
  --quiescence-accepts-timer-recovery   triage aid: a station in TimerRecovery counts as quiescent
  --max-depth N          depth bound (default 80)
  --max-states N         visited-state bound (default 400000)
  --fired                after the verdict, list the table rows the search fired (State/id, Subroutine:id)

exit code: 0 no violation, 1 violation, 2 bound hit
""";

string tables = "spec/json";
int framesAb = 2, framesBa = 0, k = 4, n2 = 4, budget = 0, maxDepth = 80, maxStates = 400_000, flowRounds = 1, flowOffAfter = 1;
bool fired = false;
bool srej = false, peerDeclines = false, mod128 = false, dataAfterConfirm = false, dataBeforeConfirm = false, releaseEarly = false, acceptGiveUp = false, selective = false, rejMayEqualVs = false, trQuiescent = false;
var faults = FaultKinds.Drop | FaultKinds.Duplicate;
var dropScope = DropScope.Any;
var seed = SeedKind.Connected;
var peer = PeerKind.Tables;
var flowControl = FlowControlAt.None;
var localBusy = FlowControlAt.None;
var t3 = false;
var longBusy = false;
var unitData = FlowControlAt.None;
var uiForm = UiForm.Plain;
var frameErrors = FrameErrors.None;
var inject = FlowControlAt.None;
var injectedFrame = InjectedFrame.None;
var injectedThen = InjectedFrame.None;
var release = ReleaseBy.None;
var reconnect = ReconnectBy.None;
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
            case "--local-busy":
                localBusy = Next(args, ref i) switch
                {
                    "A" or "a" => FlowControlAt.A,
                    "B" or "b" => FlowControlAt.B,
                    "both" => FlowControlAt.Both,
                    "none" => FlowControlAt.None,
                    var v => throw new ArgumentException($"unknown local-busy station `{v}` (A, B, both, none)"),
                };
                break;
            case "--unit-data":
                unitData = Next(args, ref i) switch
                {
                    "A" or "a" => FlowControlAt.A,
                    "B" or "b" => FlowControlAt.B,
                    "both" => FlowControlAt.Both,
                    "none" => FlowControlAt.None,
                    var v => throw new ArgumentException($"unknown unit-data station `{v}` (A, B, both, none)"),
                };
                break;
            case "--ui-form":
                uiForm = Next(args, ref i) switch
                {
                    "plain" => UiForm.Plain,
                    "poll" => UiForm.Poll,
                    "response" => UiForm.Response,
                    "long" => UiForm.Oversize,
                    var v => throw new ArgumentException($"unknown UI form `{v}` (plain, poll, response, long)"),
                };
                break;
            case "--inject":
                inject = Next(args, ref i) switch
                {
                    "A" => FlowControlAt.A,
                    "B" => FlowControlAt.B,
                    "both" => FlowControlAt.Both,
                    var v => throw new ArgumentException($"unknown inject station `{v}` (A, B, both)"),
                };
                break;
            case "--inject-frame":
            {
                var forms = Next(args, ref i).Split(',', StringSplitOptions.RemoveEmptyEntries).Select(f => f switch
                {
                    "rej-poll" => InjectedFrame.RejPoll,
                    "srej-command" => InjectedFrame.SrejCommand,
                    "srej-poll" => InjectedFrame.SrejPoll,
                    "ua-f0" => InjectedFrame.UaF0,
                    "frmr" => InjectedFrame.Frmr,
                    "i-frame" => InjectedFrame.IFrame,
                    "i-poll" => InjectedFrame.IPoll,
                    "i-poll-ahead" => InjectedFrame.IPollAhead,
                    "i-poll-stale" => InjectedFrame.IPollStale,
                    var v => throw new ArgumentException($"unknown injected frame `{v}` (rej-poll, srej-command, srej-poll, ua-f0, frmr, i-frame, i-poll, i-poll-ahead, i-poll-stale)"),
                }).ToArray();
                if (forms.Length is < 1 or > 2) throw new ArgumentException("--inject-frame takes one form, or two separated by a comma");
                injectedFrame = forms[0];
                injectedThen = forms.Length == 2 ? forms[1] : InjectedFrame.None;
                break;
            }
            case "--frame-errors":
                foreach (var f in Next(args, ref i).Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    frameErrors |= f switch
                    {
                        "control" => FrameErrors.ControlField,
                        "info" => FrameErrors.InfoNotPermitted,
                        "length" => FrameErrors.Length,
                        "n1" => FrameErrors.N1,
                        "i-response" => FrameErrors.IResponse,
                        var v => throw new ArgumentException($"unknown frame error `{v}` (control, info, length, n1, i-response)"),
                    };
                }
                break;
            case "--t3": t3 = true; break;
            case "--long-busy": longBusy = true; break;
            case "--release":
                release = Next(args, ref i) switch
                {
                    "A" or "a" => ReleaseBy.A,
                    "both" => ReleaseBy.Both,
                    "none" => ReleaseBy.None,
                    var v => throw new ArgumentException($"unknown release station `{v}` (A, both, none)"),
                };
                break;
            case "--reconnect":
                reconnect = Next(args, ref i) switch
                {
                    "A" or "a" => ReconnectBy.A,
                    "both" => ReconnectBy.Both,
                    "none" => ReconnectBy.None,
                    var v => throw new ArgumentException($"unknown reconnect station `{v}` (A, both, none)"),
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
            case "--data-after-confirm": dataAfterConfirm = true; break;
            case "--data-before-confirm": dataAfterConfirm = true; dataBeforeConfirm = true; break;
            case "--release-early": releaseEarly = true; break;
            case "--accept-give-up": acceptGiveUp = true; break;
            case "--selective-progress": selective = true; break;
            case "--rej-may-equal-vs": rejMayEqualVs = true; break;
            case "--fired": fired = true; break;
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
                        "peer-busy-hold" => Invariants.PeerBusyHold,
                        "busy-indication" => Invariants.BusyIndication,
                        "unit-data" => Invariants.UnitData,
                        "frame-error" => Invariants.FrameError,
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
        DataAfterConfirm = dataAfterConfirm,
        Peer = peer,
        FlowControl = flowControl,
        LocalBusy = localBusy,
        T3 = t3,
        LongBusy = longBusy,
        UnitData = unitData,
        UiForm = uiForm,
        FrameErrors = frameErrors,
        Inject = inject,
        InjectedFrame = injectedFrame,
        InjectedThen = injectedThen,
        Release = release,
        ReleaseEarly = releaseEarly,
        Reconnect = reconnect,
        DataBeforeConfirm = dataBeforeConfirm,
        AcceptGiveUp = acceptGiveUp,
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
    if (fired)
    {
        Console.WriteLine("table rows fired:");
        foreach (var t in result.FiredTransitions.Order(StringComparer.Ordinal)) Console.WriteLine("  " + t);
    }
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

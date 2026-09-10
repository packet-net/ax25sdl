using Packet.Sdl.Interpreter;

namespace Packet.Sdl.Interpreter.Tests;

/// <summary>
/// Direct interpreter tests against the committed JSON tables — the
/// mechanics golden traces rely on (transition selection diagnostics,
/// modular sequence arithmetic, live subroutine guards, loop execution).
/// </summary>
public class InterpreterTests
{
    private static readonly Lazy<TableSet> Tables = new(() =>
    {
        var assemblyDir = Path.GetDirectoryName(typeof(InterpreterTests).Assembly.Location)!;
        var d = new DirectoryInfo(assemblyDir);
        while (d is not null && !Directory.Exists(Path.Combine(d.FullName, "spec-sdl")))
            d = d.Parent!;
        if (d is null) throw new InvalidOperationException("repo root not found");
        return TableLoader.Load(Path.Combine(d.FullName, "spec", "json"));
    });

    [Fact]
    public void Unknown_Event_In_State_Reports_Handled_Events()
    {
        var machine = new DataLinkMachine(Tables.Value, "AwaitingRelease");
        var act = () => machine.Dispatch(new EventInput("REJ_received", Pf: false, Command: false, Nr: 0));
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*no transition for event `REJ_received`*");
    }

    [Fact]
    public void Missing_Pf_On_A_Consulting_Guard_Is_A_Clear_Error()
    {
        var machine = new DataLinkMachine(Tables.Value, "AwaitingConnection");
        var act = () => machine.Dispatch(new EventInput("UA_received")); // F_eq_1 guard needs pf
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*does not set `pf`*");
    }

    [Fact]
    public void Sending_An_I_Frame_Wraps_Vs_At_The_Modulus()
    {
        var machine = new DataLinkMachine(Tables.Value, "Connected");
        machine.SetVariable("vs", "7");
        machine.SetVariable("va", "5");
        machine.SetVariable("k", "4");
        machine.SetTimer("t1", TimerStatus.Running);

        var result = machine.Dispatch(new EventInput("I_frame_pops_off_queue", Data: "payload"));

        result.Effects.Should().ContainSingle()
            .Which.Should().BeOfType<FrameEffect>()
            .Which.Should().Match<FrameEffect>(f => f.Frame == "I" && f.Ns == 7 && f.Nr == 0 && !f.Pf);
        machine.Vs.Should().Be(0, "V(s) := V(s) + 1 is modulo-8 arithmetic");
    }

    [Fact]
    public void Timer_Expiry_Of_A_Stopped_Timer_Is_Rejected()
    {
        var machine = new DataLinkMachine(Tables.Value, "Connected");
        var act = () => machine.Dispatch(new EventInput("T1_expiry"));
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*T1 is Stopped*");
    }

    [Fact]
    public void Subroutine_Guards_See_Mutations_From_Earlier_Actions_In_The_Same_Chain()
    {
        // RNR_received in Connected runs set_peer_receiver_busy *before*
        // Check_I_Frame_Acknowledged, whose paths branch on
        // peer_receiver_busy — the busy branch (V(a) := N(r); Stop T3;
        // Start T1) must be the one taken.
        var machine = new DataLinkMachine(Tables.Value, "Connected");
        machine.SetVariable("vs", "1");
        machine.SetVariable("va", "1");
        machine.SetTimer("t3", TimerStatus.Running);

        machine.Dispatch(new EventInput("RNR_received", Pf: false, Command: false, Nr: 1));

        machine.PeerReceiverBusy.Should().BeTrue();
        machine.T1.Should().Be(TimerStatus.Running, "the not-T1-running busy branch starts T1");
        machine.T3.Should().Be(TimerStatus.Stopped);
        machine.Va.Should().Be(1);
    }

    [Fact]
    public void Invoke_Retransmission_Loop_Requeues_The_Unacknowledged_Window_In_Order()
    {
        // REJ nr=0 with V(s)=2: the figc4.7 do-while loop pushes I(0), I(1)
        // and leaves V(s) back at X.
        var machine = new DataLinkMachine(Tables.Value, "Connected");
        machine.SetVariable("vs", "2");
        machine.SetTimer("t1", TimerStatus.Running);
        machine.SetTimer("t3", TimerStatus.Stopped);

        machine.Dispatch(new EventInput("REJ_received", Pf: false, Command: false, Nr: 0));

        machine.Queue.Should().Equal("I(ns=0)", "I(ns=1)");
        machine.Vs.Should().Be(2, "the loop re-increments V(s) up to X");
    }

    [Fact]
    public void A_Requeued_Old_Frame_Is_Held_While_The_Peer_Is_Busy_And_Sent_Once_It_Clears()
    {
        // Pinned delegated semantic 1 (docs/explorer.md): a re-queued old
        // frame is re-emitted with its original N(s), but the pop path's
        // first decision, "peer receiver busy? yes: push I frame on I queue",
        // still applies to it (§6.4.9: a station that has received RNR stops
        // transmitting I frames until the busy condition is cleared).
        var machine = new DataLinkMachine(Tables.Value, "Connected");
        machine.SetTimer("t3", TimerStatus.Running);
        machine.Dispatch(new EventInput("DL_DATA_request", Data: "a0"));
        machine.Dispatch(new EventInput("DL_DATA_request", Data: "a1"));
        machine.Dispatch(new EventInput("I_frame_pops_off_queue"));
        machine.Dispatch(new EventInput("I_frame_pops_off_queue"));
        machine.Dispatch(new EventInput("REJ_received", Pf: false, Command: false, Nr: 0)); // re-queues I(0), I(1); clears peer busy
        machine.Dispatch(new EventInput("RNR_received", Pf: false, Command: false, Nr: 0));
        machine.PeerReceiverBusy.Should().BeTrue();
        machine.Queue.Should().Equal("I(ns=0)", "I(ns=1)");

        var held = machine.Dispatch(new EventInput("I_frame_pops_off_queue"));

        held.TransitionId.Should().Be(DataLinkMachine.HoldTransitionId);
        held.Effects.Should().NotContain(e => e is FrameEffect, "nothing goes out to a busy peer");
        machine.Queue.Should().Equal(new[] { "I(ns=0)", "I(ns=1)" }, "the frame is back at the head, order preserved");
        machine.Vs.Should().Be(2);

        machine.Dispatch(new EventInput("RR_received", Pf: false, Command: false, Nr: 0));
        machine.PeerReceiverBusy.Should().BeFalse();
        var sent = machine.Dispatch(new EventInput("I_frame_pops_off_queue"));
        sent.TransitionId.Should().Be(DataLinkMachine.RetransmitTransitionId);
        sent.Effects.Should().ContainSingle(e => e is FrameEffect)
            .Which.Should().Match<FrameEffect>(f => f.Frame == "I" && f.Ns == 0 && f.Data == "a0");
        machine.Queue.Should().Equal("I(ns=1)");
    }

    [Fact]
    public void Environment_May_Only_Supply_Declared_Environment_Atoms()
    {
        var machine = new DataLinkMachine(Tables.Value, "Disconnected");
        var act = () => machine.Dispatch(new EventInput(
            "SABM_received", Pf: true,
            Atoms: new Dictionary<string, bool> { ["vs_eq_va"] = true }));
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*cannot be supplied by the environment*");
    }
}

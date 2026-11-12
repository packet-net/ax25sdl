using Packet.Sdl.Interpreter;

namespace Packet.Sdl.Interpreter.Tests;

/// <summary>
/// SDL save semantics in the reference interpreter (packet-net/ax25sdl#97;
/// AX.25 2.2 C1.2; ITU-T Z.101 clause 11.7): a saved input is held in
/// arrival order and consumed in the first state entered that does not save
/// it. The end-to-end scenarios are the <c>dl-disconnect-saved-*</c> golden
/// traces; these tests pin the mechanics.
/// </summary>
public class SaveTests
{
    private static string RepoRoot()
    {
        var d = new DirectoryInfo(Path.GetDirectoryName(typeof(SaveTests).Assembly.Location)!);
        while (d is not null && !Directory.Exists(Path.Combine(d.FullName, "spec-sdl")))
            d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    private static readonly Lazy<TableSet> Tables = new(() => TableLoader.Load(Path.Combine(RepoRoot(), "spec", "json")));

    private static DataLinkMachine AwaitingConnection()
    {
        var machine = new DataLinkMachine(Tables.Value, "AwaitingConnection");
        machine.SetVariable("layer_3_initiated", "true");
        machine.SetVariable("rc", "1");
        machine.SetTimer("t1", TimerStatus.Running);
        return machine;
    }

    [Fact]
    public void Tables_Carry_The_Four_Saves_Of_The_Figures()
    {
        Tables.Value.States["AwaitingConnection"].Saves.Should().Equal("DL_DISCONNECT_request");
        Tables.Value.States["AwaitingV22Connection"].Saves.Should().Equal("DL_DISCONNECT_request");
        foreach (var state in new[] { "Disconnected", "Connected", "TimerRecovery", "AwaitingRelease" })
            Tables.Value.States[state].Saves.Should().BeEmpty(state);

        var mdl = TableLoader.Load(Path.Combine(RepoRoot(), "spec", "json"), "management_data_link");
        mdl.States["Negotiating"].Saves.Should().Equal("MDL_NEGOTIATE_request", "XID_command_received");
        mdl.States["Ready"].Saves.Should().BeEmpty();
    }

    [Fact]
    public void Tables_Without_Saves_Load_As_Saving_Nothing()
    {
        // The explorer's pre-fix fixtures were emitted before saves existed.
        var tables = TableLoader.Load(Path.Combine(RepoRoot(), "codegen", "tests", "Packet.Sdl.Explorer.Tests", "fixtures", "tables-pre38"));
        tables.States.Values.Should().OnlyContain(s => s.Saves.Count == 0);
    }

    [Fact]
    public void A_Saved_Input_Is_Held_And_Nothing_Else_Happens()
    {
        var machine = AwaitingConnection();

        var result = machine.Dispatch(new EventInput("DL_DISCONNECT_request"));

        result.TransitionId.Should().Be(DataLinkMachine.SaveTransitionId);
        result.Event.Should().Be("DL_DISCONNECT_request");
        result.Effects.Should().BeEmpty();
        result.Replayed.Should().BeEmpty();
        machine.State.Should().Be("AwaitingConnection");
        machine.T1.Should().Be(TimerStatus.Running);
        machine.HeldInputs.Select(h => h.Event).Should().Equal("DL_DISCONNECT_request");
    }

    [Fact]
    public void An_Explicit_Save_Takes_Precedence_Over_The_Catch_All()
    {
        var machine = AwaitingConnection();
        Tables.Value.States["AwaitingConnection"].Transitions
            .Should().Contain(t => t.On == "all_other_primitives__from_upper_layer", "the page has an upper-layer catch-all");

        machine.IsSavedHere("DL_DISCONNECT_request").Should().BeTrue();
        machine.IsSavedHere("DL_DATA_request").Should().BeFalse();
    }

    [Fact]
    public void Re_Entering_The_Saving_State_Keeps_The_Input_Held()
    {
        var machine = AwaitingConnection();
        machine.Dispatch(new EventInput("DL_DISCONNECT_request"));

        var retry = machine.Dispatch(new EventInput("T1_expiry"));

        retry.NextState.Should().Be("AwaitingConnection");
        retry.Replayed.Should().BeEmpty();
        machine.HeldInputs.Should().ContainSingle();
    }

    [Fact]
    public void Held_Inputs_Are_Consumed_In_Arrival_Order_Each_On_Entering_A_State_That_Does_Not_Save_It()
    {
        var machine = AwaitingConnection();
        machine.Dispatch(new EventInput("DL_DISCONNECT_request", Data: "first"));
        machine.Dispatch(new EventInput("DL_DISCONNECT_request", Data: "second"));
        machine.HeldInputs.Select(h => h.Data).Should().Equal("first", "second");

        var result = machine.Dispatch(new EventInput("UA_received", Pf: true));

        // Connected consumes the first (DISC, Awaiting Release); Awaiting
        // Release does not save it either, so the second is consumed there.
        result.NextState.Should().Be("Connected", "the triggering transition's own next state");
        result.Replayed.Select(r => (r.Event, r.NextState)).Should().Equal(
            ("DL_DISCONNECT_request", "AwaitingRelease"),
            ("DL_DISCONNECT_request", "AwaitingRelease"));
        result.Replayed[0].TransitionId.Should().Be("t01_dl_disconnect_request");
        result.Replayed.Select(r => r.Input!.Data).Should().Equal(new[] { "first", "second" }, "held inputs are consumed in arrival order");
        result.AllEffects.Should().HaveCount(result.Effects.Count + result.Replayed.Sum(r => r.Effects.Count));
        machine.HeldInputs.Should().BeEmpty();
        machine.State.Should().Be("AwaitingRelease");
    }

    [Fact]
    public void A_Replay_Is_Not_Reported_Unless_A_Held_Input_Was_Consumed()
    {
        var machine = AwaitingConnection();
        var result = machine.Dispatch(new EventInput("UA_received", Pf: true));
        result.Replayed.Should().BeEmpty();
        result.Input!.Event.Should().Be("UA_received");
        result.AllEffects.Should().Equal(result.Effects);
    }

    [Fact]
    public void Clone_Copies_The_Held_Inputs()
    {
        var machine = AwaitingConnection();
        machine.Dispatch(new EventInput("DL_DISCONNECT_request"));

        var copy = machine.Clone();
        copy.Dispatch(new EventInput("UA_received", Pf: true));

        copy.HeldInputs.Should().BeEmpty();
        machine.HeldInputs.Should().ContainSingle("the original is untouched");
    }
}

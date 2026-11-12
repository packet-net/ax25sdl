using Packet.Sdl.IR;

namespace Packet.Sdl.CodeGen.Tests;

/// <summary>
/// The figure's Save symbol (<c>save:</c> in the yaml) reaches the resolved
/// IR, and the validator refuses the shapes Z.100 forbids
/// (packet-net/ax25sdl#97).
/// </summary>
public class SaveTests
{
    private static SdlPage Page(params string[] saves) => new()
    {
        Machine = "data_link",
        State = "AwaitingConnection",
        Source = new SdlSourceYaml { Spec = "test", Figure = "figc4.2" },
        Save = saves.ToList(),
        Transitions =
        {
            new SdlTransition { Id = "t01_ua_received", On = "UA_received", Next = "Connected", Path = new List<SdlPathStep>() },
        },
    };

    private static readonly HashSet<string> Events = new(StringComparer.Ordinal) { "UA_received", "DL_DISCONNECT_request" };

    [Fact]
    public void Saves_Reach_The_Resolved_Page_In_Page_Order()
    {
        var resolved = Resolver.Resolve(Page("DL_DISCONNECT_request", "UA_received"));
        resolved.Saves.Should().Equal("DL_DISCONNECT_request", "UA_received");
    }

    [Fact]
    public void A_Page_Without_Saves_Resolves_To_None()
    {
        var page = Page();
        page.Save = null;
        Resolver.Resolve(page).Saves.Should().BeEmpty();
    }

    [Fact]
    public void A_Valid_Save_Passes()
    {
        var errors = new List<string>();
        Validation.ValidatePage(Page("DL_DISCONNECT_request"), Events, errors);
        errors.Should().BeEmpty();
    }

    [Fact]
    public void An_Event_Both_Saved_And_An_Input_Is_Refused()
    {
        var errors = new List<string>();
        Validation.ValidatePage(Page("UA_received"), Events, errors);
        errors.Should().ContainSingle().Which.Should().Contain("both saved and an input");
    }

    [Fact]
    public void An_Unknown_Or_Duplicated_Save_Is_Refused()
    {
        var errors = new List<string>();
        Validation.ValidatePage(Page("DL_DISCONNECT_request", "DL_DISCONNECT_request", "NOT_AN_EVENT"), Events, errors);
        errors.Should().Contain(e => e.Contains("twice"));
        errors.Should().Contain(e => e.Contains("unknown event `NOT_AN_EVENT`"));
    }
}

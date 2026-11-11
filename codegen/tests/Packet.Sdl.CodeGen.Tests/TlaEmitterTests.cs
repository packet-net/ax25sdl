using Packet.Sdl.CodeGen.Tla;

namespace Packet.Sdl.CodeGen.Tests;

/// <summary>
/// The TLA+ tables module is generated, never hand-written: these tests fail
/// when the committed spec/tla/Ax25DataLinkTables.tla is not exactly what the
/// emitter renders from the committed spec/json, so the model TLC checks
/// cannot drift from the figures. Fix a failure by regenerating
/// (<c>dotnet run --project codegen/src/Packet.Sdl.CodeGen -- --tla</c>),
/// never by editing the .tla file.
/// </summary>
public class TlaEmitterTests
{
    [Fact]
    public void Committed_tla_tables_match_a_fresh_render_of_spec_json()
    {
        var root = FindRepoRoot();
        var fresh = TlaEmitter.EmitFromJsonDir(Path.Combine(root, "spec", "json"));
        var committed = File.ReadAllText(Path.Combine(root, "spec", "tla", TlaEmitter.FileName));
        committed.Should().Be(fresh,
            "spec/tla/Ax25DataLinkTables.tla is generated from spec/json; regenerate it with `dotnet run --project codegen/src/Packet.Sdl.CodeGen -- --tla`");
    }

    [Fact]
    public void Every_transition_and_subroutine_path_of_the_json_appears_in_the_module()
    {
        var root = FindRepoRoot();
        var text = TlaEmitter.EmitFromJsonDir(Path.Combine(root, "spec", "json"));
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "spec", "json"), "*.g.json"))
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file));
            var page = doc.RootElement;
            if (page.GetProperty("machine").GetString() != "data_link") continue;
            var rows = page.GetProperty("kind").GetString() == "state"
                ? page.GetProperty("transitions").EnumerateArray().ToList()
                : page.GetProperty("subroutines").EnumerateArray().SelectMany(s => s.GetProperty("paths").EnumerateArray()).ToList();
            foreach (var row in rows)
                text.Should().Contain($"[id |-> \"{row.GetProperty("id").GetString()}\"", $"{Path.GetFileName(file)} row {row.GetProperty("id").GetString()}");
        }
    }

    [Fact]
    public void The_explorer_fixture_tables_render_too()
    {
        var root = FindRepoRoot();
        foreach (var fixture in new[] { "tables-pre38", "tables-pre40" })
        {
            var dir = Path.Combine(root, "codegen", "tests", "Packet.Sdl.Explorer.Tests", "fixtures", fixture);
            TlaEmitter.EmitFromJsonDir(dir).Should().Contain("MODULE Ax25DataLinkTables", fixture);
        }
    }

    private static string FindRepoRoot()
    {
        var assemblyDir = Path.GetDirectoryName(typeof(TlaEmitterTests).Assembly.Location)!;
        var d = new DirectoryInfo(assemblyDir);
        while (d is not null && !Directory.Exists(Path.Combine(d.FullName, "spec-sdl")))
            d = d.Parent;
        if (d is null)
            throw new InvalidOperationException($"can't locate repo root walking up from {assemblyDir}: no spec-sdl/ directory in any ancestor.");
        return d.FullName;
    }
}

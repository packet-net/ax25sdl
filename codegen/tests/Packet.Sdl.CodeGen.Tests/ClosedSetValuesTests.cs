using Packet.Sdl.IR;

namespace Packet.Sdl.CodeGen.Tests;

/// <summary>
/// The closed-set enums keep stable numbers: existing members keep theirs,
/// new members are appended, and a removed member's number is never reused.
/// </summary>
public class ClosedSetValuesTests
{
    [Fact]
    public void Existing_members_keep_their_numbers_and_new_ones_are_appended()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("csv-").FullName, "values.yaml");
        File.WriteAllText(path, "Ax25Event:\n  \"BReceived\": 0\n  \"DReceived\": 1\n");
        var registry = ClosedSetValues.Load(path);

        // AReceived and CReceived are new; they sort before the existing
        // members but must not displace them.
        var values = registry.Assign("Ax25Event", ["AReceived", "BReceived", "CReceived", "DReceived"]);

        values.Should().Equal(new Dictionary<string, int>
        {
            ["AReceived"] = 2, ["BReceived"] = 0, ["CReceived"] = 3, ["DReceived"] = 1,
        });
        registry.Changed.Should().BeTrue();
    }

    [Fact]
    public void A_removed_member_keeps_its_number_and_it_is_never_reused()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("csv-").FullName, "values.yaml");
        File.WriteAllText(path, "Ax25Guard:\n  \"Gone\": 0\n  \"Kept\": 1\n");
        var registry = ClosedSetValues.Load(path);

        var values = registry.Assign("Ax25Guard", ["Kept", "New"]);

        values["Kept"].Should().Be(1);
        values["New"].Should().Be(2);
        registry.ToYaml().Should().Contain("\"Gone\": 0");
    }

    [Fact]
    public void Generated_csharp_and_rust_enums_carry_explicit_values()
    {
        var root = FindRepoRoot();
        File.ReadAllText(Path.Combine(root, "spec", "csharp", "Ax25Event.g.cs")).Should().MatchRegex(@"DISCReceived = \d+,");
        File.ReadAllText(Path.Combine(root, "spec", "rust", "src", "ax25_event.g.rs")).Should().MatchRegex(@"DISCReceived = \d+,");
    }

    private static string FindRepoRoot()
    {
        var d = new DirectoryInfo(Path.GetDirectoryName(typeof(ClosedSetValuesTests).Assembly.Location)!);
        while (d is not null && !Directory.Exists(Path.Combine(d.FullName, "spec", "csharp"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}

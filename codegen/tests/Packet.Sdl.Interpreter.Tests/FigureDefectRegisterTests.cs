using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Packet.Sdl.Interpreter;

namespace Packet.Sdl.Interpreter.Tests;

/// <summary>
/// The "xfail set equals the open figure-defect set" property
/// (packethacking/ax25spec#102), in two halves over the hand-maintained
/// register <c>traces/figure-defects.yaml</c>:
/// <list type="bullet">
/// <item>offline: every <c>expected_failure:</c> marker in <c>traces/</c>
/// names a registered defect, every registered defect that a trace can
/// express has at least one marker, and no defect registered as
/// inexpressible has one;</item>
/// <item>online: the registered defects are exactly the open issues
/// upstream carrying the register's label. Needs a GitHub token
/// (<c>GH_TOKEN</c>, <c>GITHUB_TOKEN</c>, or a logged-in <c>gh</c>); skips
/// with the reason printed when there is none or GitHub is unreachable.</item>
/// </list>
/// Together with the strict-xfail runner in <see cref="GoldenTraceTests"/>
/// (an xfail that passes fails the suite) the register can go stale in
/// neither direction without CI saying so.
/// </summary>
public class FigureDefectRegisterTests
{
    private static readonly Lazy<string> RepoRoot = new(GoldenTraceTests.FindRepoRoot);

    private static string TracesDir => Path.Combine(RepoRoot.Value, "traces");

    private static FigureDefectRegisterDoc LoadRegister() =>
        FigureDefectRegister.Load(Path.Combine(TracesDir, FigureDefectRegister.FileName));

    /// <summary>Every <c>expected_failure:</c> marker in <c>traces/</c>, by trace file name.</summary>
    private static List<(string File, string Issue)> XfailMarkers()
    {
        var markers = new List<(string, string)>();
        foreach (var path in Directory.EnumerateFiles(TracesDir, "*.trace.yaml").Order(StringComparer.Ordinal))
        {
            var trace = TraceLoader.Load(path);
            if (trace.ExpectedFailure is { Length: > 0 } issue)
                markers.Add((Path.GetFileName(path), issue));
        }
        return markers;
    }

    [Fact]
    public void Xfail_Markers_And_The_Register_Agree_In_Both_Directions()
    {
        var register = LoadRegister();
        var markers = XfailMarkers();
        var registered = register.Defects.ToDictionary(d => d.Issue, StringComparer.Ordinal);
        var problems = new List<string>();

        // Direction 1: a trace may only xfail against a registered open defect.
        foreach (var (file, issue) in markers)
        {
            if (!registered.TryGetValue(issue, out var entry))
                problems.Add($"{file} carries expected_failure: {issue}, which is not in traces/{FigureDefectRegister.FileName}; register it (or the trace is wrong)");
            else if (!entry.TraceExpressible)
                problems.Add($"{file} carries expected_failure: {issue}, but the register says that defect is not trace-expressible ({entry.Reason?.Trim()}); one of the two is wrong");
        }

        // Direction 2: every expressible registered defect is pinned by a trace.
        var markedIssues = markers.Select(m => m.Issue).ToHashSet(StringComparer.Ordinal);
        foreach (var entry in register.Defects.Where(d => d.TraceExpressible && !markedIssues.Contains(d.Issue)))
            problems.Add($"{entry.Issue} ({entry.Title}) is registered as trace-expressible but no trace carries it as expected_failure:; author the prose-derived xfail trace, or mark it trace_expressible: false with a reason");

        problems.Count.Should().Be(0,
            "the xfail set must equal the registered open-defect set (packethacking/ax25spec#102):\n  " + string.Join("\n  ", problems));
    }

    [SkippableFact]
    public async Task Registered_Defects_Are_Exactly_The_Open_Labelled_Issues_Upstream()
    {
        var register = LoadRegister();

        var (token, tokenSource) = await ResolveToken();
        Skip.If(token is null,
            "no GitHub token: set GH_TOKEN or GITHUB_TOKEN, or log in with `gh auth login`, to check the register against the live issue tracker (the offline half still ran)");

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("packet-net-ax25sdl-tests", "1"));
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        IReadOnlyDictionary<int, string> open;
        try
        {
            open = await OpenLabelledIssues(http, register.Repository, register.Label);
        }
        catch (HttpRequestException ex)
        {
            Skip.If(true, $"GitHub unreachable (token from {tokenSource}): {ex.Message}");
            throw;
        }
        catch (TaskCanceledException ex)
        {
            Skip.If(true, $"GitHub request timed out (token from {tokenSource}): {ex.Message}");
            throw;
        }

        var problems = new List<string>();
        var markers = XfailMarkers();

        foreach (var entry in register.Defects)
        {
            var number = FigureDefectRegister.IssueNumber(entry.Issue, register.Repository)!.Value;
            if (open.ContainsKey(number))
                continue;
            var why = await DescribeIssue(http, register.Repository, number);
            var carriers = markers.Where(m => string.Equals(m.Issue, entry.Issue, StringComparison.Ordinal)).Select(m => m.File).ToList();
            problems.Add(carriers.Count == 0
                ? $"{entry.Issue} is registered but is not an open `{register.Label}` issue upstream ({why}) and no trace carries it; drop the entry"
                : $"{entry.Issue} is registered but is not an open `{register.Label}` issue upstream ({why}), yet {string.Join(", ", carriers)} still " +
                  "carries it as expected_failure:, so the fix has not been pinned here. Per the closing rule the issue stays open until the pin bump " +
                  "flips the evidence: either reopen it upstream until then, or land the pin bump (which removes the marker and this entry together)");
        }

        foreach (var (number, title) in open.OrderBy(kv => kv.Key))
        {
            var link = $"https://github.com/{register.Repository}/issues/{number.ToString(CultureInfo.InvariantCulture)}";
            if (!register.Defects.Any(d => string.Equals(d.Issue, link, StringComparison.Ordinal)))
                problems.Add($"{link} ({title}) is an open `{register.Label}` issue upstream but is not registered; add it with a trace or a reason");
        }

        problems.Count.Should().Be(0,
            $"traces/{FigureDefectRegister.FileName} must list exactly the open `{register.Label}` issues in {register.Repository}:\n  " +
            string.Join("\n  ", problems));
    }

    /// <summary>Open issues carrying the label, number to title. Pull requests share the endpoint and are dropped.</summary>
    private static async Task<IReadOnlyDictionary<int, string>> OpenLabelledIssues(HttpClient http, string repository, string label)
    {
        var result = new Dictionary<int, string>();
        for (var page = 1; ; page++)
        {
            var uri = new Uri(
                $"https://api.github.com/repos/{repository}/issues?labels={Uri.EscapeDataString(label)}&state=open&per_page=100&page={page.ToString(CultureInfo.InvariantCulture)}");
            using var response = await http.GetAsync(uri);
            var body = await response.Content.ReadAsStringAsync();
            response.IsSuccessStatusCode.Should().BeTrue(
                $"GET {uri} must succeed to check the register (got {(int)response.StatusCode} {response.ReasonPhrase}: {Truncate(body)})");

            using var json = JsonDocument.Parse(body);
            var count = 0;
            foreach (var item in json.RootElement.EnumerateArray())
            {
                count++;
                if (item.TryGetProperty("pull_request", out _))
                    continue;
                result[item.GetProperty("number").GetInt32()] = item.GetProperty("title").GetString() ?? "";
            }
            if (count < 100)
                return result;
        }
    }

    /// <summary>"closed" / "open, labels: a, b" / "not found", for the failure message.</summary>
    private static async Task<string> DescribeIssue(HttpClient http, string repository, int number)
    {
        var uri = new Uri($"https://api.github.com/repos/{repository}/issues/{number.ToString(CultureInfo.InvariantCulture)}");
        using var response = await http.GetAsync(uri);
        if (!response.IsSuccessStatusCode)
            return $"{(int)response.StatusCode} {response.ReasonPhrase}";
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var state = json.RootElement.GetProperty("state").GetString();
        var labels = json.RootElement.GetProperty("labels").EnumerateArray()
            .Select(l => l.GetProperty("name").GetString())
            .ToList();
        return $"{state}, labels: {(labels.Count == 0 ? "none" : string.Join(", ", labels))}";
    }

    /// <summary>GH_TOKEN, then GITHUB_TOKEN, then a logged-in gh CLI; null when none is available.</summary>
    private static async Task<(string? Token, string Source)> ResolveToken()
    {
        foreach (var name in new[] { "GH_TOKEN", "GITHUB_TOKEN" })
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(value))
                return (value.Trim(), name);
        }

        try
        {
            using var gh = Process.Start(new ProcessStartInfo("gh")
            {
                ArgumentList = { "auth", "token" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
            });
            if (gh is null)
                return (null, "none");
            gh.StandardInput.Close();
            var output = await gh.StandardOutput.ReadToEndAsync();
            await gh.StandardError.ReadToEndAsync();
            await gh.WaitForExitAsync();
            return gh.ExitCode == 0 && !string.IsNullOrWhiteSpace(output)
                ? (output.Trim(), "gh auth token")
                : (null, "none");
        }
        catch (Win32Exception)
        {
            // gh is not installed.
            return (null, "none");
        }
    }

    private static string Truncate(string s) => s.Length <= 200 ? s : s[..200] + "...";
}

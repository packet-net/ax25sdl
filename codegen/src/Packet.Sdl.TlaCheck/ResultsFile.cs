using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;
using System.Text.Json.Nodes;

namespace Packet.Sdl.TlaCheck;

/// <summary>
/// <c>--results FILE</c>: one JSON object per line, one line per grid cell,
/// appended and flushed to disk as each cell completes, so a long unattended
/// run loses at most the cells in progress when it stops. With
/// <c>--resume</c> the cells already recorded with a verdict are skipped; a
/// cell recorded without one (TLC timed out or failed, the run crashed) is
/// run again and its new line supersedes the old. Every line carries
/// <c>cell</c> (the name), <c>mode</c> (<c>cross</c> or <c>lockstep</c>), <c>meaning</c> (the
/// <c>--meaning</c> module) and
/// <c>done</c> (false when there was no verdict).
/// </summary>
public sealed class ResultsFile : IDisposable
{
    // Cell names carry + and @, which the default encoder escapes; the file is read by people too.
    private static readonly JsonSerializerOptions Plain = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private readonly FileStream _stream;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, JsonObject> _records = new(StringComparer.Ordinal);

    private ResultsFile(FileStream stream) => _stream = stream;

    /// <summary>
    /// Open <paramref name="path"/>. Without <paramref name="resume"/> it must not
    /// exist (so a rerun never overwrites a finished run by accident); with it, its lines are
    /// read first (a torn last line from a killed run is ignored) and the file is appended to.
    /// </summary>
    public static ResultsFile Open(string path, bool resume)
    {
        var full = Path.GetFullPath(path);
        if (!resume && File.Exists(full))
            throw new IOException($"{path} already exists: pass --resume to continue that run, or name another file");
        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var records = new List<JsonObject>();
        if (File.Exists(full))
        {
            foreach (var line in File.ReadLines(full))
            {
                if (line.Trim().Length == 0) continue;
                try
                {
                    if (JsonNode.Parse(line) is JsonObject o && o["cell"] is not null) records.Add(o);
                }
                catch (JsonException)
                {
                    // A line cut short when the run was killed: that cell runs again.
                }
            }
        }
        var stream = new FileStream(full, FileMode.Append, FileAccess.Write, FileShare.Read);
        // A torn last line has no newline: start on a fresh line so the next record parses.
        if (stream.Length > 0)
        {
            using var r = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            r.Seek(-1, SeekOrigin.End);
            if (r.ReadByte() != '\n') { stream.WriteByte((byte)'\n'); stream.Flush(true); }
        }
        var file = new ResultsFile(stream);
        foreach (var o in records) file._records[(string)o["cell"]!] = o;
        return file;
    }

    /// <summary>
    /// The latest record for <paramref name="cell"/> when it has a verdict from a run of the
    /// same mode with the same meaning (a cell run with another meaning runs again).
    /// </summary>
    public JsonObject? Done(string cell, string mode, string meaning)
    {
        lock (_gate)
        {
            return _records.TryGetValue(cell, out var o) && (string?)o["mode"] == mode && (string?)o["meaning"] == meaning && (bool?)o["done"] == true ? o : null;
        }
    }

    /// <summary>Append one cell's record and flush it to disk.</summary>
    public void Append(JsonObject record)
    {
        var bytes = Encoding.UTF8.GetBytes(record.ToJsonString(Plain) + "\n");
        lock (_gate)
        {
            _stream.Write(bytes);
            _stream.Flush(true);
            _records[(string)record["cell"]!] = record;
        }
    }

    public void Dispose() => _stream.Dispose();
}

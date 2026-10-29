using System.Text.Json.Nodes;
using DotnetIntelligence.Contracts;

namespace DotnetIntelligence.Cli;

/// <summary>Progress and diagnostics as NDJSON events on stdout (4.1); human-readable logs go to stderr.</summary>
public sealed class EventWriter(TextWriter stdout, TextWriter stderr)
{
    private readonly object _gate = new();

    public void Emit(string eventName, JsonObject? fields = null)
    {
        var record = fields ?? new JsonObject();
        record["event"] = eventName;
        var line = CanonicalJson.Serialize(record);
        lock (_gate)
        {
            stdout.Write(line);
            stdout.Write('\n');
            stdout.Flush();
        }
    }

    public void Log(string message)
    {
        lock (_gate)
        {
            stderr.Write(message);
            stderr.Write('\n');
            stderr.Flush();
        }
    }
}

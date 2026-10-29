using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DotnetIntelligence.Analysis;
using DotnetIntelligence.Analysis.Graph;

namespace DotnetIntelligence.Adapters;

/// <summary>
/// Configuration (4.3): keys of appsettings*.json files in the project directory, with file, position and a secret
/// flag (by key name and value shape). Values are never stored (rule 1.4.6).
/// </summary>
public sealed class ConfigurationAdapter(AdapterContext context)
{
    public void Visit(IReadOnlyList<WorkingCopyFile> repositoryFiles)
    {
        var directory = Path.GetDirectoryName(context.Project.ProjectPath)!.Replace('\\', '/');
        var prefix = directory.Length == 0 ? string.Empty : directory + "/";
        foreach (var file in repositoryFiles.Where(f => f.Path.StartsWith(prefix, StringComparison.Ordinal)
                     && Path.GetFileName(f.Path).StartsWith("appsettings", StringComparison.OrdinalIgnoreCase)
                     && f.Path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                     && !f.Path[prefix.Length..].Contains('/')))
        {
            var bytes = File.ReadAllBytes(Path.Combine(context.Files.Root, file.Path));
            foreach (var (key, value, offset, length) in Keys(bytes))
            {
                var (line, column) = Position(bytes, offset);
                var lineText = LineAt(bytes, offset);
                var evidence = context.Graph.AddEvidence(
                    new SourceLocation(file.Path, file.BlobId, new SourceSpan(line, column, line, column + length), "repository", null),
                    "config", context.ContextId, lineText);
                context.Observe("config.key", new JsonObject
                {
                    ["key"] = key,
                    ["file"] = file.Path,
                    ["secret"] = SecretScrubber.LooksSecret(key, value),
                }, evidence);
            }
        }
    }

    /// <summary>Leaf keys as "A:B:0:C" with the byte offset and length of the token that names them.</summary>
    internal static List<(string Key, string? Value, int Offset, int Length)> Keys(byte[] bytes)
    {
        var results = new List<(string, string?, int, int)>();
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var path = new List<string>();
        var containers = new List<(bool IsArray, int Index)>();
        string? name = null;
        int nameOffset = 0, nameLength = 0;

        string Segment()
        {
            var top = containers[^1];
            if (top.IsArray)
            {
                containers[^1] = (true, top.Index + 1);
                return top.Index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            return name ?? string.Empty;
        }

        try
        {
            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.PropertyName:
                        name = reader.GetString();
                        nameOffset = (int)reader.TokenStartIndex;
                        nameLength = reader.ValueSpan.Length + 2;
                        break;
                    case JsonTokenType.StartObject or JsonTokenType.StartArray:
                        if (containers.Count > 0)
                        {
                            path.Add(Segment());
                        }
                        containers.Add((reader.TokenType == JsonTokenType.StartArray, 0));
                        name = null;
                        break;
                    case JsonTokenType.EndObject or JsonTokenType.EndArray:
                        containers.RemoveAt(containers.Count - 1);
                        if (containers.Count > 0)
                        {
                            path.RemoveAt(path.Count - 1);
                        }
                        break;
                    default:
                        if (containers.Count == 0)
                        {
                            break;
                        }
                        var inArray = containers[^1].IsArray;
                        var value = reader.TokenType == JsonTokenType.String ? reader.GetString() : Encoding.UTF8.GetString(reader.ValueSpan);
                        var offset = inArray ? (int)reader.TokenStartIndex : nameOffset;
                        var length = inArray ? reader.ValueSpan.Length : nameLength;
                        results.Add((string.Join(':', path.Append(Segment())), value, offset, length));
                        name = null;
                        break;
                }
            }
        }
        catch (JsonException)
        {
            // A malformed file yields the keys read so far.
        }
        return results;
    }

    /// <summary>1-based line and UTF-16 column of a byte offset (D9).</summary>
    internal static (int Line, int Column) Position(byte[] bytes, int offset)
    {
        var text = Encoding.UTF8.GetString(bytes, 0, offset);
        var lastBreak = text.LastIndexOf('\n');
        return (text.Count(c => c == '\n') + 1, text.Length - lastBreak);
    }

    internal static string LineAt(byte[] bytes, int offset)
    {
        var start = Array.LastIndexOf(bytes, (byte)'\n', Math.Max(0, offset - 1)) + 1;
        var end = Array.IndexOf(bytes, (byte)'\n', offset);
        return Encoding.UTF8.GetString(bytes, start, (end < 0 ? bytes.Length : end) - start);
    }
}

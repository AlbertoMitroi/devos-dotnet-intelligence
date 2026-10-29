using System.Text.Json.Nodes;
using DotnetIntelligence.Contracts;

namespace DotnetIntelligence.Artifacts;

/// <summary>
/// Contract validation (3.6): every record against its schema, then integrity: no duplicate ids, every referenced id
/// exists, every context exists, evidence of generation-stored files matches the stored bytes (and, during analysis,
/// repository evidence matches the committed blob ids), and with a manifest every listed checksum matches.
/// </summary>
public static class GenerationVerifier
{
    public const int MaxReportedErrors = 200;

    public static readonly string[] RequiredCapabilitiesUnderstood = [];

    public static readonly IReadOnlySet<string> RelationKinds = new HashSet<string>(StringComparer.Ordinal)
    {
        "declares", "contains", "references", "calls_declared_target", "overrides", "implements", "inherits", "possible_runtime_target",
    };

    public static readonly IReadOnlySet<string> LinkKinds = new HashSet<string>(StringComparer.Ordinal) { "consumes_contract", "configured_route", "possible_communication" };

    /// <summary>Kinds this engine does not know (from a newer minor): kept, excluded from traversal and linking, reported (3.5).</summary>
    public static IReadOnlyList<string> UnknownKinds(string root)
    {
        var notes = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var (file, known) in new[] { ("graph/relations.ndjson", RelationKinds), ("graph/links.ndjson", LinkKinds) })
        {
            var path = Path.Combine(root, file);
            if (!File.Exists(path))
            {
                continue;
            }
            foreach (var line in File.ReadLines(path))
            {
                if (JsonNode.Parse(line)?["kind"]?.GetValue<string>() is { } kind && !known.Contains(kind))
                {
                    notes.Add($"{file}: unknown kind '{kind}' kept, not interpreted");
                }
            }
        }
        return notes.ToList();
    }

    private static readonly (string File, string? Schema)[] GraphFiles =
    [
        ("graph/projects.ndjson", null),
        ("graph/entities.ndjson", "entity"),
        ("graph/declarations.ndjson", "declaration"),
        ("graph/relations.ndjson", "relation"),
        ("graph/evidence.ndjson", "evidence"),
        ("graph/observations.ndjson", "observation"),
        ("graph/links.ndjson", "link"),
        ("graph/diagnostics.ndjson", "diagnostic"),
        ("snippets.ndjson", "snippet"),
    ];

    /// <param name="committedBlobs">Path to blob id of the analysed commit; null when verifying without the repository.</param>
    public static IReadOnlyList<string> VerifyRecords(string root, IReadOnlyDictionary<string, string>? committedBlobs, string objectFormat)
    {
        var errors = new List<string>();
        void Error(string message)
        {
            if (errors.Count < MaxReportedErrors)
            {
                errors.Add(message);
            }
        }
        var validator = SchemaValidator.LoadEmbedded();
        var records = new Dictionary<string, List<JsonObject>>(StringComparer.Ordinal);
        foreach (var (file, schema) in GraphFiles)
        {
            var path = Path.Combine(root, file);
            var list = new List<JsonObject>();
            records[file] = list;
            if (!File.Exists(path))
            {
                Error($"{file}: missing");
                continue;
            }
            var lineNumber = 0;
            string? previousId = null;
            foreach (var line in File.ReadLines(path))
            {
                lineNumber++;
                JsonObject record;
                try
                {
                    record = JsonNode.Parse(line)!.AsObject();
                }
                catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException)
                {
                    Error($"{file}:{lineNumber}: not a JSON object ({ex.Message})");
                    continue;
                }
                var name = schema ?? ((string?)record["recordType"] == "analysisContext" ? "analysis-context" : "project");
                foreach (var problem in validator.Validate(name, record))
                {
                    Error($"{file}:{lineNumber}: {problem}");
                }
                var id = (string?)record["id"];
                if (id is not null && previousId is not null && string.CompareOrdinal(previousId, id) >= 0)
                {
                    Error(string.CompareOrdinal(previousId, id) == 0 ? $"{file}:{lineNumber}: duplicate id '{id}'" : $"{file}:{lineNumber}: records are not sorted by id");
                }
                previousId = id ?? previousId;
                list.Add(record);
            }
        }

        var projects = records["graph/projects.ndjson"].Where(r => (string?)r["recordType"] == "project").Select(r => (string)r["id"]!).ToHashSet(StringComparer.Ordinal);
        var contexts = records["graph/projects.ndjson"].Where(r => (string?)r["recordType"] == "analysisContext").Select(r => (string)r["id"]!).ToHashSet(StringComparer.Ordinal);
        var entities = Ids(records["graph/entities.ndjson"]);
        var evidence = Ids(records["graph/evidence.ndjson"]);
        var snippets = Ids(records["snippets.ndjson"]);
        var observations = Ids(records["graph/observations.ndjson"]);

        foreach (var entity in records["graph/entities.ndjson"])
        {
            if ((string?)entity["container"] is { } container && !entities.Contains(container))
            {
                Error($"entity {entity["id"]}: container '{container}' does not exist");
            }
        }
        foreach (var declaration in records["graph/declarations.ndjson"])
        {
            Require(entities, (string?)declaration["entity"], $"declaration {declaration["id"]}: entity");
            Require(contexts, (string?)declaration["context"], $"declaration {declaration["id"]}: context");
        }
        foreach (var relation in records["graph/relations.ndjson"])
        {
            var from = (string?)relation["from"];
            if (!(entities.Contains(from ?? string.Empty) || projects.Contains(from ?? string.Empty)))
            {
                Error($"relation {relation["id"]}: from '{from}' does not exist");
            }
            Require(entities, (string?)relation["to"], $"relation {relation["id"]}: to");
            Require(contexts, (string?)relation["context"], $"relation {relation["id"]}: context");
            foreach (var e in relation["evidence"]?.AsArray() ?? [])
            {
                Require(evidence, (string?)e, $"relation {relation["id"]}: evidence");
            }
        }
        foreach (var observation in records["graph/observations.ndjson"])
        {
            Require(contexts, (string?)observation["context"], $"observation {observation["id"]}: context");
            foreach (var e in observation["evidence"]?.AsArray() ?? [])
            {
                Require(evidence, (string?)e, $"observation {observation["id"]}: evidence");
            }
        }
        foreach (var link in records["graph/links.ndjson"])
        {
            Require(observations, (string?)link["from"]?["observation"], $"link {link["id"]}: from");
            if (link["to"] is JsonObject to)
            {
                Require(observations, (string?)to["observation"], $"link {link["id"]}: to");
            }
            foreach (var e in link["evidence"]?.AsArray() ?? [])
            {
                Require(evidence, (string?)e, $"link {link["id"]}: evidence");
            }
        }

        var storedBlobs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in records["graph/evidence.ndjson"])
        {
            var id = (string)item["id"]!;
            Require(contexts, (string?)item["context"], $"evidence {id}: context");
            if ((string?)item["snippet"] is { } snippet)
            {
                Require(snippets, snippet, $"evidence {id}: snippet");
            }
            var path = (string)item["path"]!;
            var blob = (string)item["blobId"]!;
            if ((string?)item["store"] == "generation")
            {
                if (!storedBlobs.TryGetValue(path, out var actual))
                {
                    var file = Path.Combine(root, path);
                    actual = File.Exists(file) ? BlobId(File.ReadAllBytes(file), objectFormat) : "missing";
                    storedBlobs[path] = actual;
                }
                if (actual != blob)
                {
                    Error($"evidence {id}: generated file '{path}' is {(actual == "missing" ? "missing" : $"blob {actual}, not {blob}")}");
                }
            }
            else if (committedBlobs is not null && (!committedBlobs.TryGetValue(path, out var committed) || committed != blob))
            {
                Error($"evidence {id}: '{path}' at blob {blob} is not in the analysed commit");
            }
        }
        return errors;

        void Require(HashSet<string> set, string? id, string what)
        {
            if (id is null || !set.Contains(id))
            {
                Error($"{what} '{id}' does not exist");
            }
        }
    }

    /// <summary>Full check for consumers: manifest schema, capabilities, checksums, no unlisted files, then the records.</summary>
    public static IReadOnlyList<string> Verify(string root)
    {
        var errors = new List<string>();
        var manifestPath = Path.Combine(root, GenerationWriter.ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            return ["manifest.json is missing: the directory is not a generation"];
        }
        JsonObject manifest;
        try
        {
            manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
        }
        catch (System.Text.Json.JsonException ex)
        {
            return [$"manifest.json is not valid JSON: {ex.Message}"];
        }
        errors.AddRange(SchemaValidator.LoadEmbedded().Validate("manifest", manifest).Select(e => "manifest.json: " + e));
        var version = (string?)manifest["schemaVersion"] ?? "0.0";
        if (!version.StartsWith("1.", StringComparison.Ordinal))
        {
            errors.Add($"manifest.json: schemaVersion {version} is not major 1");
        }
        foreach (var capability in manifest["requiredCapabilities"]?.AsArray().Select(c => (string)c!) ?? [])
        {
            if (!RequiredCapabilitiesUnderstood.Contains(capability))
            {
                errors.Add($"manifest.json: required capability '{capability}' is not understood; refusing the generation");
            }
        }
        var listed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in manifest["files"]?.AsArray() ?? [])
        {
            var path = (string)file!["path"]!;
            listed.Add(path);
            var full = Path.Combine(root, path);
            if (!File.Exists(full))
            {
                errors.Add($"{path}: listed in the manifest but missing");
            }
            else if (Hashing.Sha256HexOfFile(full) != (string?)file["sha256"])
            {
                errors.Add($"{path}: checksum does not match the manifest");
            }
        }
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(root, f).Replace('\\', '/')))
        {
            if (file is not (GenerationWriter.ManifestFileName or GenerationWriter.RunFileName) && !listed.Contains(file))
            {
                errors.Add($"{file}: present but not listed in the manifest");
            }
        }
        errors.AddRange(VerifyRecords(root, null, (string?)manifest["objectFormat"] ?? "sha1"));
        return errors;
    }

    private static HashSet<string> Ids(IEnumerable<JsonObject> records) => records.Select(r => (string)r["id"]!).ToHashSet(StringComparer.Ordinal);

    private static string BlobId(byte[] content, string objectFormat)
    {
        var header = System.Text.Encoding.ASCII.GetBytes($"blob {content.Length}\0");
        var data = header.Concat(content).ToArray();
        return objectFormat == "sha256"
            ? Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(data))
            : Convert.ToHexStringLower(System.Security.Cryptography.SHA1.HashData(data));
    }
}

using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace DotnetIntelligence.Artifacts;

/// <summary>
/// Validator for the JSON Schema subset the contract uses (type, required, properties, items, enum, const,
/// pattern, minimum, minItems). Unknown fields are allowed: minors add fields (3.5).
/// </summary>
public sealed class SchemaValidator
{
    private readonly Dictionary<string, JsonObject> _schemas = new(StringComparer.Ordinal);

    public static SchemaValidator LoadEmbedded()
    {
        var validator = new SchemaValidator();
        var assembly = typeof(SchemaValidator).Assembly;
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith("schema/v1/", StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            validator._schemas[Path.GetFileName(name).Replace(".schema.json", string.Empty, StringComparison.Ordinal)] = JsonNode.Parse(stream)!.AsObject();
        }
        return validator;
    }

    /// <summary>Every *.schema.json of a directory (for example the vendored schema/query/v1), by file name without ".schema.json".</summary>
    public static SchemaValidator FromDirectory(string directory)
    {
        var validator = new SchemaValidator();
        foreach (var file in Directory.EnumerateFiles(directory, "*.schema.json"))
        {
            validator._schemas[Path.GetFileName(file).Replace(".schema.json", string.Empty, StringComparison.Ordinal)] = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        }
        return validator;
    }

    public IReadOnlyCollection<string> Names => _schemas.Keys;

    public IEnumerable<string> Validate(string schemaName, JsonNode? value) =>
        _schemas.TryGetValue(schemaName, out var schema) ? Check(schema, value, "$") : [$"unknown schema '{schemaName}'"];

    private static IEnumerable<string> Check(JsonObject schema, JsonNode? value, string path)
    {
        if (schema["const"] is { } constant && !JsonNode.DeepEquals(constant, value))
        {
            yield return $"{path}: must be {constant.ToJsonString()}";
            yield break;
        }
        if (schema["enum"] is JsonArray options && !options.Any(o => JsonNode.DeepEquals(o, value)))
        {
            yield return $"{path}: '{value?.ToJsonString()}' is not one of {options.ToJsonString()}";
            yield break;
        }
        if (schema["type"] is { } type)
        {
            var allowed = type is JsonArray many ? many.Select(t => (string)t!).ToList() : [(string)type!];
            if (!allowed.Contains(TypeOf(value)) && !(allowed.Contains("number") && TypeOf(value) == "integer"))
            {
                yield return $"{path}: expected {string.Join(" or ", allowed)}, found {TypeOf(value)}";
                yield break;
            }
        }
        switch (value)
        {
            case JsonObject obj:
                foreach (var required in schema["required"]?.AsArray().Select(r => (string)r!) ?? [])
                {
                    if (!obj.ContainsKey(required))
                    {
                        yield return $"{path}: missing required field '{required}'";
                    }
                }
                if (schema["properties"] is JsonObject properties)
                {
                    foreach (var (name, propertySchema) in properties)
                    {
                        if (obj.TryGetPropertyValue(name, out var child))
                        {
                            foreach (var error in Check(propertySchema!.AsObject(), child, $"{path}.{name}"))
                            {
                                yield return error;
                            }
                        }
                    }
                }
                break;
            case JsonArray array:
                if (schema["minItems"] is { } minItems && array.Count < (int)minItems)
                {
                    yield return $"{path}: needs at least {minItems} items";
                }
                if (schema["items"] is JsonObject items)
                {
                    for (var i = 0; i < array.Count; i++)
                    {
                        foreach (var error in Check(items, array[i], $"{path}[{i}]"))
                        {
                            yield return error;
                        }
                    }
                }
                break;
            case JsonValue scalar:
                if (schema["pattern"] is { } pattern && scalar.TryGetValue(out string? text) && !Regex.IsMatch(text, (string)pattern!))
                {
                    yield return $"{path}: '{text}' does not match {pattern}";
                }
                if (schema["minimum"] is { } minimum && scalar.TryGetValue(out long number) && number < (long)minimum)
                {
                    yield return $"{path}: {number} is below {minimum}";
                }
                break;
        }
    }

    private static string TypeOf(JsonNode? value) => value switch
    {
        null => "null",
        JsonObject => "object",
        JsonArray => "array",
        JsonValue v when v.TryGetValue(out string? _) => "string",
        JsonValue v when v.TryGetValue(out bool _) => "boolean",
        JsonValue v when v.TryGetValue(out long _) => "integer",
        JsonValue v when v.TryGetValue(out int _) => "integer",
        JsonValue v when v.GetValueKind() == System.Text.Json.JsonValueKind.Number => "number",
        _ => "unknown",
    };
}

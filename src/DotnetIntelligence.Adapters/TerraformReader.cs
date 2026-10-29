using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DotnetIntelligence.Analysis;

namespace DotnetIntelligence.Adapters;

/// <summary>
/// Terraform (contract v1.1, infra.resource): resource, data and module blocks of "*.tf" files, with provider, type and
/// name. Attribute values are never read or stored (rule 1.4.6 and D21: they are not destinations of code).
/// </summary>
public static partial class TerraformReader
{
    public static void Read(WorkingCopyFile file, string root, FileScope scope)
    {
        var lines = File.ReadAllText(Path.Combine(root, file.Path)).Split('\n');
        var inComment = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (inComment)
            {
                inComment = !line.Contains("*/", StringComparison.Ordinal);
                continue;
            }
            if (line.TrimStart().StartsWith("/*", StringComparison.Ordinal))
            {
                inComment = !line.Contains("*/", StringComparison.Ordinal);
                continue;
            }
            var match = Block().Match(line);
            if (!match.Success)
            {
                continue;
            }
            var keyword = match.Groups["keyword"].Value;
            var type = match.Groups["type"].Success ? match.Groups["type"].Value : "module";
            var name = match.Groups["name"].Value;
            var underscore = type.IndexOf('_');
            scope.Observe("infra.resource", new JsonObject
            {
                ["provider"] = keyword == "module" ? "module" : underscore > 0 ? type[..underscore] : type,
                ["type"] = type,
                ["name"] = name,
                ["block"] = keyword,
                ["file"] = file.Path,
            }, scope.Evidence(file.Path, file.BlobId, i + 1, match.Index + 1, match.Length, line, "syntax"));
        }
    }

    [GeneratedRegex(@"^\s*(?:(?<keyword>resource|data)\s+""(?<type>[^""]+)""\s+""(?<name>[^""]+)""|(?<keyword>module)\s+""(?<name>[^""]+)"")\s*\{")]
    private static partial Regex Block();
}

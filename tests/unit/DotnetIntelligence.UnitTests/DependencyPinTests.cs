using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace DotnetIntelligence.UnitTests;

/// <summary>
/// R1.24 (rule 1.5.4): every dependency pinned, with its license recorded; base images pinned by digest.
/// Regenerate the NuGet section of docs/DEPENDENCIES.md with scripts/update-dependencies.sh.
/// </summary>
public partial class DependencyPinTests
{
    private const string BeginMarker = "<!-- nuget:begin -->";
    private const string EndMarker = "<!-- nuget:end -->";

    private static IEnumerable<string> Files(string directory, string pattern) =>
        Directory.Exists(RepositoryRoot.File(directory))
            ? Directory.EnumerateFiles(RepositoryRoot.File(directory), pattern, SearchOption.AllDirectories)
                .Where(f => !f.Contains("/bin/") && !f.Contains("/obj/"))
                .Order(StringComparer.Ordinal)
            : [];

    [Fact]
    public void Every_base_image_is_pinned_by_digest()
    {
        var dockerfile = RepositoryRoot.ReadText("Dockerfile");
        var images = ImageReference().Matches(dockerfile).Select(m => m.Value).ToList();

        Assert.NotEmpty(images);
        Assert.All(images, image => Assert.Matches(@"@sha256:[0-9a-f]{64}$", image));
        Assert.All(FromLine().Matches(dockerfile), from =>
            Assert.True(from.Groups["ref"].Value.StartsWith("${", StringComparison.Ordinal) || from.Groups["ref"].Value.Contains("@sha256:")
                || StageNames(dockerfile).Contains(from.Groups["ref"].Value), $"Unpinned FROM: {from.Value}"));
    }

    [Fact]
    public void Every_workflow_action_is_pinned_to_a_commit()
    {
        var files = Files(".github/workflows", "*.yml").Concat(Files("examples/workflows", "*.yml")).ToList();
        Assert.NotEmpty(files);
        foreach (var file in files)
        {
            foreach (Match use in UsesLine().Matches(File.ReadAllText(file)))
            {
                var reference = use.Groups["ref"].Value;
                var pinned = reference.StartsWith("./", StringComparison.Ordinal)
                    || Regex.IsMatch(reference, "@[0-9a-f]{40}$")
                    || (reference.StartsWith("docker://", StringComparison.Ordinal) && reference.Contains("@sha256:"))
                    // Examples show how callers use this action: by its major tag (2.6).
                    || (file.Contains("/examples/") && reference.StartsWith("AlbertoMitroi/dotnet-intelligence@v", StringComparison.Ordinal));
                Assert.True(pinned, $"{Path.GetFileName(file)}: '{reference}' is not pinned");
            }
        }
    }

    [Fact]
    public void Every_image_and_action_is_listed_in_dependencies()
    {
        var dependencies = RepositoryRoot.ReadText("docs/DEPENDENCIES.md");
        var references = ImageReference().Matches(RepositoryRoot.ReadText("Dockerfile")).Select(m => m.Value)
            .Concat(Files(".github/workflows", "*.yml").Concat(Files("examples/workflows", "*.yml"))
                .SelectMany(f => UsesLine().Matches(File.ReadAllText(f)).Select(m => m.Groups["ref"].Value))
                .Where(r => !r.StartsWith("./", StringComparison.Ordinal) && !r.StartsWith("AlbertoMitroi/", StringComparison.Ordinal)))
            .Concat(Files(".github/workflows", "*.yml").Concat(Files("examples/workflows", "*.yml")).Concat(Files("scripts", "*"))
                .SelectMany(f => ImageReference().Matches(File.ReadAllText(f)).Select(m => m.Value)))
            .Distinct(StringComparer.Ordinal);

        Assert.All(references, r => Assert.True(dependencies.Contains(r.Replace("docker://", string.Empty), StringComparison.Ordinal), $"Not in DEPENDENCIES.md: {r}"));
    }

    [Fact]
    public void Every_project_has_a_lock_file()
    {
        var projects = Files("src", "*.csproj").Concat(Files("tests", "*.csproj"))
            .Where(p => !p.Contains("/tests/fixtures/"));
        Assert.All(projects, p => Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(p)!, "packages.lock.json")), $"No lock file for {p}"));
    }

    [Fact]
    public void Nuget_section_of_dependencies_matches_lock_files_and_licenses()
    {
        var expected = RenderNugetSection();
        var path = RepositoryRoot.File("docs/DEPENDENCIES.md");
        var document = File.ReadAllText(path);
        var begin = document.IndexOf(BeginMarker, StringComparison.Ordinal);
        var end = document.IndexOf(EndMarker, StringComparison.Ordinal);
        Assert.True(begin >= 0 && end > begin, "DEPENDENCIES.md lacks the nuget markers");
        var actual = document[(begin + BeginMarker.Length)..end];

        if (Environment.GetEnvironmentVariable("DI_UPDATE_DEPENDENCIES") == "1" && actual != expected)
        {
            File.WriteAllText(path, document[..(begin + BeginMarker.Length)] + expected + document[end..], new UTF8Encoding(false));
            return;
        }
        Assert.Equal(expected, actual);
    }

    private static string RenderNugetSection()
    {
        var packages = new SortedDictionary<(string Id, string Version), bool>(Comparer<(string Id, string Version)>.Create((a, b) =>
        {
            var c = string.Compare(a.Id, b.Id, StringComparison.OrdinalIgnoreCase);
            return c != 0 ? c : string.CompareOrdinal(a.Version, b.Version);
        }));
        foreach (var lockFile in Files("src", "packages.lock.json").Concat(Files("tests", "packages.lock.json")))
        {
            using var json = JsonDocument.Parse(File.ReadAllText(lockFile));
            foreach (var framework in json.RootElement.GetProperty("dependencies").EnumerateObject())
            {
                foreach (var package in framework.Value.EnumerateObject())
                {
                    var type = package.Value.GetProperty("type").GetString();
                    if (type == "Project")
                    {
                        continue;
                    }
                    var key = (package.Name, package.Value.GetProperty("resolved").GetString()!);
                    packages[key] = (packages.TryGetValue(key, out var direct) && direct) || type == "Direct";
                }
            }
        }

        var root = Environment.GetEnvironmentVariable("NUGET_PACKAGES") is { Length: > 0 } configured
            ? configured
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        var builder = new StringBuilder("\n| Package | Version | Use | License |\n|---|---|---|---|\n");
        foreach (var ((id, version), direct) in packages)
        {
            builder.Append($"| {id} | {version} | {(direct ? "direct" : "transitive")} | {License(root, id, version)} |\n");
        }
        return builder.ToString();
    }

    private static string License(string root, string id, string version)
    {
        var nuspec = Path.Combine(root, id.ToLowerInvariant(), version.ToLowerInvariant(), id.ToLowerInvariant() + ".nuspec");
        if (!File.Exists(nuspec))
        {
            throw new FileNotFoundException($"nuspec not found for {id} {version}; restore first.", nuspec);
        }
        var metadata = XDocument.Load(nuspec).Root!.Elements().First(e => e.Name.LocalName == "metadata");
        var license = metadata.Elements().FirstOrDefault(e => e.Name.LocalName == "license");
        if (license is not null)
        {
            return license.Attribute("type")?.Value == "file" ? $"file in package: {license.Value}" : license.Value;
        }
        var url = metadata.Elements().FirstOrDefault(e => e.Name.LocalName == "licenseUrl")?.Value;
        return url is null ? "not declared" : $"<{url}>";
    }

    private static HashSet<string> StageNames(string dockerfile) =>
        StageName().Matches(dockerfile).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

    [GeneratedRegex(@"(?:mcr\.microsoft\.com|ghcr\.io|docker\.io|rhysd)/[A-Za-z0-9./_-]+:[A-Za-z0-9._-]+(?:@sha256:[0-9a-f]{64})?")]
    private static partial Regex ImageReference();

    [GeneratedRegex(@"^FROM\s+(?<ref>\S+)", RegexOptions.Multiline)]
    private static partial Regex FromLine();

    [GeneratedRegex(@"\sAS\s+(\S+)\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex StageName();

    [GeneratedRegex(@"^\s*-?\s*uses:\s*(?<ref>\S+)", RegexOptions.Multiline)]
    private static partial Regex UsesLine();
}

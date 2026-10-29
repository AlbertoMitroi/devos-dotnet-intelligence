using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

namespace DotnetIntelligence.UnitTests;

/// <summary>R1.16 to R1.18 and R1.23: action.yml declares the contract of 2.5 and the released version.</summary>
public partial class ActionManifestTests
{
    private static YamlMappingNode Load(string relative)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(RepositoryRoot.ReadText(relative)));
        return (YamlMappingNode)stream.Documents[0].RootNode;
    }

    private static YamlMappingNode Map(YamlMappingNode node, string key) => (YamlMappingNode)node[new YamlScalarNode(key)];

    private static string Scalar(YamlMappingNode node, string key) => ((YamlScalarNode)node[new YamlScalarNode(key)]).Value!;

    public static string EngineVersion => VersionElement().Match(RepositoryRoot.ReadText("Directory.Build.props")).Groups[1].Value;

    [Fact]
    public void Runs_the_published_action_image_of_this_version()
    {
        var runs = Map(Load("action.yml"), "runs");

        Assert.Equal("docker", Scalar(runs, "using"));
        Assert.Equal($"docker://ghcr.io/albertomitroi/dotnet-intelligence:{EngineVersion}-action", Scalar(runs, "image"));
        var args = ((YamlSequenceNode)runs[new YamlScalarNode("args")]).Select(n => ((YamlScalarNode)n).Value).ToList();
        Assert.Equal(["gha"], args);
    }

    [Fact]
    public void Declares_the_inputs_with_their_defaults()
    {
        var inputs = Map(Load("action.yml"), "inputs");
        var expected = new Dictionary<string, string?>
        {
            ["path"] = ".",
            ["solution"] = null,
            ["config"] = null,
            ["output"] = "intelligence-out",
            ["include-working-tree"] = "false",
            ["fail-on"] = "failed",
        };

        Assert.Equal(expected.Keys.Order(), inputs.Children.Keys.Select(k => ((YamlScalarNode)k).Value!).Order());
        foreach (var (name, value) in expected)
        {
            var input = Map(inputs, name);
            input.Children.TryGetValue(new YamlScalarNode("default"), out var defaultNode);
            Assert.Equal(value, (defaultNode as YamlScalarNode)?.Value is { Length: > 0 } v ? v : null);
        }
    }

    [Fact]
    public void Declares_the_outputs()
    {
        var outputs = Map(Load("action.yml"), "outputs");

        Assert.Equal(new[] { "coverage", "generation-path", "manifest-path", "status" },
            outputs.Children.Keys.Select(k => ((YamlScalarNode)k).Value!).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Engine_version_is_semver()
    {
        Assert.Matches(@"^\d+\.\d+\.\d+$", EngineVersion);
    }

    [GeneratedRegex(@"<Version>([^<]+)</Version>")]
    private static partial Regex VersionElement();
}

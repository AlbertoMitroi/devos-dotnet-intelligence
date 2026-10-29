using DotnetIntelligence.Analysis;
using DotnetIntelligence.Cli;

namespace DotnetIntelligence.UnitTests;

public class GitHubActionModeTests
{
    [Fact]
    public void Defaults_apply_when_inputs_are_absent()
    {
        var inputs = GitHubActionMode.ReadInputs(new Dictionary<string, string?>());

        Assert.Equal(new ActionInputs(".", null, null, "intelligence-out", false, FailOn.Failed), inputs);
    }

    [Fact]
    public void Hyphenated_and_underscored_input_names_are_read()
    {
        var inputs = GitHubActionMode.ReadInputs(new Dictionary<string, string?>
        {
            ["INPUT_PATH"] = "repo",
            ["INPUT_INCLUDE-WORKING-TREE"] = "true",
            ["INPUT_FAIL_ON"] = "partial",
            ["INPUT_OUTPUT"] = "out",
            ["INPUT_SOLUTION"] = "All.slnx",
            ["INPUT_CONFIG"] = "di.yml",
        });

        Assert.Equal(new ActionInputs("repo", "All.slnx", "di.yml", "out", true, FailOn.Partial), inputs);
    }

    [Theory]
    [InlineData("INPUT_FAIL-ON", "sometimes")]
    [InlineData("INPUT_INCLUDE-WORKING-TREE", "yes")]
    public void Invalid_inputs_are_usage_errors(string name, string value)
    {
        Assert.Throws<UsageException>(() => GitHubActionMode.ReadInputs(new Dictionary<string, string?> { [name] = value }));
    }

    [Theory]
    [InlineData(FailOn.Failed, "complete", false)]
    [InlineData(FailOn.Failed, "partial", false)]
    [InlineData(FailOn.Failed, "failed", true)]
    [InlineData(FailOn.Partial, "complete", false)]
    [InlineData(FailOn.Partial, "partial", true)]
    [InlineData(FailOn.Partial, "failed", true)]
    [InlineData(FailOn.Never, "failed", false)]
    public void Fail_on_maps_status_to_step_failure(FailOn failOn, string status, bool expected)
    {
        Assert.Equal(expected, GitHubActionMode.ShouldFail(failOn, status));
    }

    [Fact]
    public void Annotations_escape_workflow_command_characters()
    {
        var diagnostic = new AnalysisDiagnostic(DiagnosticSeverity.Warning, "a:b,c", "src/x", DiagnosticState.Unknown, "100% broken\nnext");

        Assert.Equal("::warning title=a%3Ab%2Cc::100%25 broken%0Anext [src/x]", GitHubActionMode.Annotation(diagnostic));
    }
}

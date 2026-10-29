using DotnetIntelligence.Cli;

namespace DotnetIntelligence.UnitTests;

public class AnalyzeCommandTests
{
    private static readonly Dictionary<string, string?> NoEnvironment = new();

    [Fact]
    public void Defaults_follow_the_runtime_mount_contract()
    {
        var options = AnalyzeCommand.Parse([], NoEnvironment);

        Assert.Equal("/src", options.Source);
        Assert.Equal("/out", options.Output);
        Assert.Equal("/work", options.WorkBase);
        Assert.False(options.IncludeWorkingTree);
        Assert.False(options.Offline);
    }

    [Fact]
    public void All_options_are_parsed()
    {
        var options = AnalyzeCommand.Parse(
            ["--src", "/a", "--out", "/b", "--config", "/config/x.yml", "--solution", "All.slnx", "--include-working-tree", "--offline"],
            new Dictionary<string, string?> { ["DI_WORK"] = "/tmp/w" });

        Assert.Equal(new[] { "/a", "/b", "/config/x.yml", "All.slnx", "/tmp/w" },
            new[] { options.Source, options.Output, options.ConfigPath, options.Solution, options.WorkBase });
        Assert.True(options.IncludeWorkingTree);
        Assert.True(options.Offline);
    }

    [Theory]
    [InlineData("--unknown")]
    [InlineData("--src")]
    public void Invalid_arguments_are_usage_errors(string argument)
    {
        Assert.Throws<UsageException>(() => AnalyzeCommand.Parse([argument], NoEnvironment));
    }

    [Fact]
    public async Task Unknown_command_returns_usage_exit_code()
    {
        var code = await CommandLine.RunAsync(["frobnicate"], TextWriter.Null, TextWriter.Null, NoEnvironment, CancellationToken.None);

        Assert.Equal(ExitCodes.Usage, code);
    }
}

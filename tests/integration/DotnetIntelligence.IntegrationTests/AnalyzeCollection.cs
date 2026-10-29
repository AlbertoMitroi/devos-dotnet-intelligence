namespace DotnetIntelligence.IntegrationTests;

/// <summary>Analyze runs restore and load MSBuild projects; they share NuGet folders and process environment, so they run one at a time.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AnalyzeCollection
{
    public const string Name = "analyze";
}

namespace DotnetIntelligence.UnitTests;

/// <summary>R1.1: the repository layout of 2.1, for the parts that exist from Step 1 on.</summary>
public class LayoutTests
{
    // Added by later steps (and asserted there): schema/v1, docs/CONTRACT.md (Step 2),
    // tests/eshop and .github/workflows/eshop-validation.yml (Step 5).
    public static TheoryData<string> RequiredPaths => new()
    {
        "action.yml",
        "Dockerfile",
        "scripts/verify.sh",
        "scripts/verify.ps1",
        ".devcontainer/devcontainer.json",
        "global.json",
        "DotnetIntelligence.slnx",
        "src/DotnetIntelligence.Contracts",
        "src/DotnetIntelligence.Analysis",
        "src/DotnetIntelligence.Adapters",
        "src/DotnetIntelligence.Artifacts",
        "src/DotnetIntelligence.Cli",
        "tests/unit",
        "tests/integration",
        "tests/fixtures",
        "examples/workflows",
        "docs/PLAN.md",
        "docs/DECISIONS.md",
        "docs/OPEN-QUESTIONS.md",
        "docs/USAGE.md",
        "docs/DEPENDENCIES.md",
        ".github/workflows/ci.yml",
        ".github/workflows/release.yml",
    };

    [Theory]
    [MemberData(nameof(RequiredPaths))]
    public void Required_path_exists(string relative)
    {
        var full = RepositoryRoot.File(relative);
        Assert.True(File.Exists(full) || Directory.Exists(full), $"Missing {relative}");
    }
}

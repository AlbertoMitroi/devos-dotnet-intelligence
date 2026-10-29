using DotnetIntelligence.Analysis;

namespace DotnetIntelligence.UnitTests;

public class SemanticVersionTests
{
    [Theory]
    [InlineData("5.9.0", "5.9.0-1.26523.112", 1)]
    [InlineData("5.9.0-2.final", "5.9.0-1.26523.112", 1)]
    [InlineData("5.9.0-1.26523.112", "5.9.0-1.26523.112", 0)]
    [InlineData("5.6.0", "5.9.0-1.26523.112", -1)]
    [InlineData("5.9.0-1.2", "5.9.0-1.10", -1)]
    [InlineData("5.9.0-alpha", "5.9.0-1", 1)]
    [InlineData("5.9.0-1", "5.9.0-1.1", -1)]
    [InlineData("5.9.0+abc", "5.9.0", 0)]
    public void Precedence_follows_semver(string left, string right, int expected)
    {
        Assert.Equal(expected, Math.Sign(SemanticVersion.Parse(left).CompareTo(SemanticVersion.Parse(right))));
    }

    [Fact]
    public void Roslyn_rule_compares_assembly_versions_not_build_numbers()
    {
        // Same release line, later SDK build: same assembly version, compatible.
        Assert.True(SdkInventory.IsRoslynCompatible(new Version(5, 9, 0, 0), [new InstalledSdk("10.0.403", "5.9.0-1.26523.112", new Version(5, 9, 0, 0))]));
        Assert.False(SdkInventory.IsRoslynCompatible(new Version(5, 6, 0, 0), [new InstalledSdk("10.0.403", "5.9.0-1.26523.112", new Version(5, 9, 0, 0))]));
        Assert.True(SdkInventory.IsRoslynCompatible(new Version(5, 9, 0, 0), [new InstalledSdk("9.0.320", "4.14.0-3.26201.4", new Version(4, 14, 0, 0))]));
    }

    [Fact]
    public void Roslyn_rule_fails_when_a_compiler_version_is_unknown()
    {
        Assert.False(SdkInventory.IsRoslynCompatible(new Version(5, 9, 0, 0), [new InstalledSdk("10.0.403", null, null)]));
    }
}

using DotnetIntelligence.Analysis;

namespace DotnetIntelligence.UnitTests;

public class RemoteUrlTests
{
    [Theory]
    [InlineData("https://x-access-token:ghs_secret@github.com/org/repo.git", "https://github.com/org/repo.git")]
    [InlineData("https://user@example.com/repo.git", "https://example.com/repo.git")]
    [InlineData("https://github.com/org/repo.git", "https://github.com/org/repo.git")]
    [InlineData("ssh://git@github.com/org/repo.git", "ssh://git@github.com/org/repo.git")]
    [InlineData("git@github.com:org/repo.git", "git@github.com:org/repo.git")]
    public void Credentials_are_removed(string url, string expected)
    {
        Assert.Equal(expected, RemoteUrl.Redact(url));
    }
}

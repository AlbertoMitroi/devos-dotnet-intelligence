namespace DotnetIntelligence.UnitTests;

internal static class RepositoryRoot
{
    /// <summary>The engine repository root: the nearest parent directory containing DotnetIntelligence.slnx.</summary>
    public static string Path { get; } = Find();

    public static string File(string relative) => System.IO.Path.Combine(Path, relative);

    public static string ReadText(string relative) => System.IO.File.ReadAllText(File(relative));

    private static string Find()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (System.IO.File.Exists(System.IO.Path.Combine(directory.FullName, "DotnetIntelligence.slnx")))
            {
                return directory.FullName;
            }
        }
        throw new InvalidOperationException("Repository root not found above " + AppContext.BaseDirectory);
    }
}

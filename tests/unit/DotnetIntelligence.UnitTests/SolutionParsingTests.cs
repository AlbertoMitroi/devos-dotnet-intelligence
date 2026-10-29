using DotnetIntelligence.Analysis;

namespace DotnetIntelligence.UnitTests;

public class SolutionParsingTests
{
    [Fact]
    public void Slnx_projects_inside_folders_are_resolved_relative_to_the_solution()
    {
        const string slnx = """
            <Solution>
              <Folder Name="/src/">
                <Project Path="src\App\App.csproj" />
              </Folder>
              <Project Path="../shared/Lib/Lib.csproj" />
            </Solution>
            """;

        Assert.Equal(["apps/src/App/App.csproj", "shared/Lib/Lib.csproj"], SolutionDiscovery.ParseSlnx(slnx, "apps/All.slnx"));
    }

    [Fact]
    public void Sln_skips_solution_folders_and_normalizes_backslashes()
    {
        const string sln = """
            Microsoft Visual Studio Solution File, Format Version 12.00
            Project("{2150E333-8FDC-42A3-9474-1A3956D46DE8}") = "src", "src", "{11111111-1111-1111-1111-111111111111}"
            EndProject
            Project("{9A19103F-16F7-4668-BE54-9A1E7A4F7556}") = "App", "src\App\App.csproj", "{22222222-2222-2222-2222-222222222222}"
            EndProject
            """;

        Assert.Equal(["src/App/App.csproj"], SolutionDiscovery.ParseSln(sln, "All.sln"));
    }

    [Fact]
    public void Slnf_projects_are_relative_to_the_referenced_solution()
    {
        const string slnf = """{ "solution": { "path": "..\\All.sln", "projects": [ "src\\App\\App.csproj" ] } }""";

        Assert.Equal(["src/App/App.csproj"], SolutionDiscovery.ParseSlnf(slnf, "filters/App.slnf"));
    }

    [Fact]
    public void Paths_escaping_the_repository_are_rejected()
    {
        Assert.Throws<FormatException>(() => SolutionDiscovery.ResolveRelative("All.sln", "../outside/X.csproj"));
    }
}

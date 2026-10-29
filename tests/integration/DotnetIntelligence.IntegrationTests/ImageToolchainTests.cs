using DotnetIntelligence.Analysis;

namespace DotnetIntelligence.IntegrationTests;

/// <summary>
/// R1.2 to R1.4, run in the image's test stage (same base image digest as the runtime stage):
/// the SDK set matches the declared one and the engine's Roslyn is at least every SDK compiler.
/// </summary>
public class ImageToolchainTests
{
    [Fact]
    public async Task Sdk_set_matches_the_declared_image_sdks()
    {
        var declared = Environment.GetEnvironmentVariable("DI_EXPECTED_SDKS");
        if (string.IsNullOrWhiteSpace(declared))
        {
            Assert.Skip("DI_EXPECTED_SDKS is set by the Dockerfile test stage only.");
        }

        var inventory = await SdkInventory.ReadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(declared.Split(',').Order(StringComparer.Ordinal), inventory.Sdks.Select(s => s.Version));
        Assert.All(inventory.Sdks, s => Assert.False(string.IsNullOrEmpty(s.CompilerVersion), $"No compiler build for SDK {s.Version}"));
        Assert.All(inventory.Sdks, s => Assert.NotNull(s.CompilerAssemblyVersion));
        Assert.Contains(inventory.Runtimes, r => r.Name == "Microsoft.NETCore.App");
        Assert.Contains(inventory.Runtimes, r => r.Name == "Microsoft.AspNetCore.App");
    }

    [Fact]
    public async Task Engine_roslyn_is_at_least_the_compiler_of_every_sdk()
    {
        var inventory = await SdkInventory.ReadAsync(TestContext.Current.CancellationToken);
        var engine = SdkInventory.EngineRoslyn();
        TestContext.Current.SendDiagnosticMessage($"engine Roslyn {engine.ApiVersion} (build {engine.Build}); " +
            string.Join(", ", inventory.Sdks.Select(s => $"SDK {s.Version}: compiler {s.CompilerAssemblyVersion} (build {s.CompilerVersion})")));

        Assert.NotEmpty(inventory.Sdks);
        Assert.True(SdkInventory.IsRoslynCompatible(engine.AssemblyVersion, inventory.Sdks),
            $"Engine Roslyn {engine.AssemblyVersion} is older than an SDK compiler: {string.Join(", ", inventory.Sdks.Select(s => $"{s.Version}={s.CompilerAssemblyVersion}"))}");
    }
}

using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace DotnetIntelligence.Analysis;

/// <summary>
/// Stage 5 (4.2.5): analyzers and source generators delivered by packages, and the Microsoft.CodeAnalysis version
/// each one references. Any newer than the engine's Roslyn is flagged: it may fail to load (CS8032).
/// </summary>
public static class AnalyzerCompatibility
{
    public static IReadOnlyList<AnalysisDiagnostic> Check(string projectPath, AssetsFile assets, Version engineRoslyn)
    {
        var diagnostics = new List<AnalysisDiagnostic>();
        foreach (var library in assets.Libraries.Values.Where(l => l.Type == "package").OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var file in library.Files.Where(f => f.StartsWith("analyzers/", StringComparison.OrdinalIgnoreCase)
                         && f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                         && !f.Contains("/vb/", StringComparison.OrdinalIgnoreCase)
                         && !f.Contains("/fs/", StringComparison.OrdinalIgnoreCase)
                         && !f.Contains("/resources/", StringComparison.OrdinalIgnoreCase)
                         && !Path.GetFileName(f).Contains(".resources.", StringComparison.OrdinalIgnoreCase)))
            {
                var path = assets.ResolvePackageFile(library, file);
                if (path is null)
                {
                    continue;
                }
                var required = ReferencedRoslyn(path);
                if (required is not null && required > engineRoslyn)
                {
                    diagnostics.Add(new AnalysisDiagnostic(DiagnosticSeverity.Warning, "analyzer-requires-newer-roslyn", projectPath, DiagnosticState.Partial,
                        $"{library.Name} {library.Version} ({file}) references Microsoft.CodeAnalysis {required}, newer than the engine's {engineRoslyn}; it may not load."));
                }
            }
        }
        return diagnostics;
    }

    private static Version? ReferencedRoslyn(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata)
            {
                return null;
            }
            var reader = pe.GetMetadataReader();
            return reader.AssemblyReferences
                .Select(h => reader.GetAssemblyReference(h))
                .Where(r => reader.GetString(r.Name) is "Microsoft.CodeAnalysis" or "Microsoft.CodeAnalysis.CSharp")
                .Select(r => r.Version)
                .OrderDescending()
                .FirstOrDefault();
        }
        catch (BadImageFormatException)
        {
            return null;
        }
    }
}

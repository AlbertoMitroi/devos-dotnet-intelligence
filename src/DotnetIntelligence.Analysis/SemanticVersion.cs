using System.Globalization;

namespace DotnetIntelligence.Analysis;

/// <summary>SemVer 2.0 precedence (build metadata ignored), used for the Roslyn compatibility rule.</summary>
public sealed class SemanticVersion : IComparable<SemanticVersion>
{
    private SemanticVersion(int major, int minor, int patch, IReadOnlyList<string> prerelease, string original)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        Prerelease = prerelease;
        Original = original;
    }

    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }
    public IReadOnlyList<string> Prerelease { get; }
    public string Original { get; }

    public static SemanticVersion Parse(string text)
    {
        var withoutBuild = text.Split('+', 2)[0].Trim();
        var dash = withoutBuild.IndexOf('-');
        var core = dash < 0 ? withoutBuild : withoutBuild[..dash];
        var prerelease = dash < 0 ? [] : withoutBuild[(dash + 1)..].Split('.');
        var numbers = core.Split('.');
        if (numbers.Length < 3)
        {
            throw new FormatException($"'{text}' is not a semantic version.");
        }
        return new SemanticVersion(
            int.Parse(numbers[0], CultureInfo.InvariantCulture),
            int.Parse(numbers[1], CultureInfo.InvariantCulture),
            int.Parse(numbers[2], CultureInfo.InvariantCulture),
            prerelease,
            text.Trim());
    }

    public int CompareTo(SemanticVersion? other)
    {
        if (other is null)
        {
            return 1;
        }
        var result = Major.CompareTo(other.Major);
        if (result == 0) result = Minor.CompareTo(other.Minor);
        if (result == 0) result = Patch.CompareTo(other.Patch);
        if (result != 0)
        {
            return result;
        }
        if (Prerelease.Count == 0 || other.Prerelease.Count == 0)
        {
            // A release has higher precedence than any of its prereleases.
            return (Prerelease.Count == 0, other.Prerelease.Count == 0) switch
            {
                (true, true) => 0,
                (true, false) => 1,
                _ => -1,
            };
        }
        for (var i = 0; i < Math.Min(Prerelease.Count, other.Prerelease.Count); i++)
        {
            var a = Prerelease[i];
            var b = other.Prerelease[i];
            var aNumeric = long.TryParse(a, NumberStyles.None, CultureInfo.InvariantCulture, out var an);
            var bNumeric = long.TryParse(b, NumberStyles.None, CultureInfo.InvariantCulture, out var bn);
            var c = (aNumeric, bNumeric) switch
            {
                (true, true) => an.CompareTo(bn),
                (true, false) => -1,
                (false, true) => 1,
                _ => string.CompareOrdinal(a, b),
            };
            if (c != 0)
            {
                return Math.Sign(c);
            }
        }
        return Prerelease.Count.CompareTo(other.Prerelease.Count);
    }

    public override string ToString() => Original;
}

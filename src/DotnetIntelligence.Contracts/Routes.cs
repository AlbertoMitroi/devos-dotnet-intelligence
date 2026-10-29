using System.Text;
using System.Text.RegularExpressions;

namespace DotnetIntelligence.Contracts;

/// <summary>
/// Canonical route templates (contract 3.4): one leading "/", no trailing "/", literal segments lower-case,
/// parameters reduced to "{}" (optional "{?}", catch-all "{**}"), constraints and defaults dropped, query removed.
/// Used on both sides of HTTP links, so "/api/items/{id:int}" and $"api/items/{id}" meet.
/// </summary>
public static partial class Routes
{
    public static string Combine(params string?[] parts)
    {
        var segments = parts.Where(p => !string.IsNullOrEmpty(p))
            .SelectMany(p => p!.Split('/', StringSplitOptions.RemoveEmptyEntries));
        return "/" + string.Join('/', segments);
    }

    public static string Canonical(string template)
    {
        var path = template;
        var query = path.IndexOf('?');
        if (query >= 0)
        {
            path = path[..query];
        }
        if (Uri.TryCreate(path, UriKind.Absolute, out var absolute) && absolute.Scheme is "http" or "https")
        {
            path = absolute.AbsolutePath;
        }
        var builder = new StringBuilder();
        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            builder.Append('/');
            builder.Append(Parameter().Replace(segment.ToLowerInvariant(), m =>
            {
                var body = m.Groups[1].Value;
                if (body.StartsWith("**", StringComparison.Ordinal) || body.StartsWith('*'))
                {
                    return "{**}";
                }
                return body.EndsWith('?') || body.Contains("=", StringComparison.Ordinal) ? "{?}" : "{}";
            }));
        }
        return builder.Length == 0 ? "/" : builder.ToString();
    }

    [GeneratedRegex(@"\{([^{}]*)\}")]
    private static partial Regex Parameter();
}

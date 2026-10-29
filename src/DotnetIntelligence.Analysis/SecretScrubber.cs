using System.Text.RegularExpressions;

namespace DotnetIntelligence.Analysis;

/// <summary>
/// Removes credentials from any text the engine writes (rule 1.4.6): values of NuGet credential
/// variables passed to the container, and secret-looking content in source snippets.
/// </summary>
public sealed partial class SecretScrubber
{
    public const string Redacted = "[REDACTED]";

    private readonly string[] _knownSecrets;

    public SecretScrubber(IReadOnlyDictionary<string, string?> environment)
    {
        var secrets = new List<string>();
        foreach (var (key, value) in environment)
        {
            if (value is null || !key.StartsWith("NuGetPackageSourceCredentials_", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            foreach (var part in value.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var pair = part.Split('=', 2);
                if (pair.Length == 2 && pair[1].Length >= 4 && pair[0].Trim().Equals("Password", StringComparison.OrdinalIgnoreCase))
                {
                    secrets.Add(pair[1]);
                }
            }
            secrets.Add(value);
        }
        _knownSecrets = secrets.Distinct(StringComparer.Ordinal).OrderByDescending(s => s.Length).ToArray();
    }

    /// <summary>Removes the known credential values from tool output and messages.</summary>
    public string Scrub(string text)
    {
        foreach (var secret in _knownSecrets)
        {
            text = text.Replace(secret, Redacted, StringComparison.Ordinal);
        }
        return text;
    }

    /// <summary>Redacts secret-looking content from a source line before it becomes a snippet.</summary>
    public string RedactSnippet(string line)
    {
        var result = Scrub(line);
        result = PrivateKey().Replace(result, Redacted);
        result = ConnectionStringSecret().Replace(result, m => m.Groups["key"].Value + "=" + Redacted);
        result = AssignedSecret().Replace(result, m => m.Groups["prefix"].Value + Redacted + m.Groups["quote"].Value);
        result = LongToken().Replace(result, Redacted);
        return result;
    }

    /// <summary>True when a configuration key name or value looks like a secret (4.3, configuration adapter).</summary>
    public static bool LooksSecret(string key, string? value)
    {
        if (SecretKeyName().IsMatch(key))
        {
            return true;
        }
        return value is not null && (ConnectionStringSecret().IsMatch(value) || PrivateKey().IsMatch(value) || LongToken().IsMatch(value));
    }

    [GeneratedRegex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----.*", RegexOptions.Singleline)]
    private static partial Regex PrivateKey();

    [GeneratedRegex(@"(?<key>\b(?:password|pwd|secret|accountkey|sharedaccesskey|apikey|api_key|token|access_token|client_secret)\b)\s*=\s*[^;""'\s]+", RegexOptions.IgnoreCase)]
    private static partial Regex ConnectionStringSecret();

    [GeneratedRegex(@"(?<prefix>(?:password|passwd|passphrase|pwd|secret|apikey|api_key|token|clientsecret|client_secret|connectionstring)\w*""?\s*[:=]\s*(?<quote>[""']))[^""']{4,}(?=[""'])", RegexOptions.IgnoreCase)]
    private static partial Regex AssignedSecret();

    [GeneratedRegex(@"\b(?:[A-Za-z0-9+/]{40,}={0,2}|[A-Fa-f0-9]{40,}|gh[pousr]_[A-Za-z0-9]{20,}|xox[abpr]-[A-Za-z0-9-]{10,}|AKIA[0-9A-Z]{16})\b")]
    private static partial Regex LongToken();

    [GeneratedRegex(@"(password|passwd|passphrase|pass_phrase|pwd|secret|apikey|api_key|accesskey|access_key|accountkey|privatekey|private_key|clientsecret|client_secret|token|credential|connectionstring|sas)", RegexOptions.IgnoreCase)]
    private static partial Regex SecretKeyName();
}

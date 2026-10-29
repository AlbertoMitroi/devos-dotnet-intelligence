namespace DotnetIntelligence.Analysis;

public static class RemoteUrl
{
    /// <summary>
    /// Removes credentials from a remote URL (https://user:token@host/repo becomes https://host/repo),
    /// so a token embedded by CI never reaches the artifacts (rule 1.4.6).
    /// </summary>
    public static string Redact(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "ssh" or "git")
        {
            if (uri.UserInfo.Length == 0 || (uri.Scheme == "ssh" && !uri.UserInfo.Contains(':')))
            {
                // No user info, or ssh://git@host/repo where a user name without a password is not a secret.
                return url;
            }
            return new UriBuilder(uri) { UserName = string.Empty, Password = string.Empty }.Uri.ToString();
        }
        return url;
    }
}

using System.Runtime.InteropServices;

namespace DotnetIntelligence.Analysis.Extraction;

/// <summary>
/// A passwd and group entry for the current user when /etc/passwd has none (D24): child processes load nss_wrapper
/// (LD_PRELOAD) with files written under the tool HOME. Nothing is set when the entry exists, when not on Linux or
/// when the library is not installed.
/// </summary>
internal static class UserEntry
{
    private static readonly string[] LibraryPatterns = ["/usr/lib/aarch64-linux-gnu/libnss_wrapper.so", "/usr/lib/x86_64-linux-gnu/libnss_wrapper.so"];

    [DllImport("libc", SetLastError = false)]
    private static extern uint geteuid();

    [DllImport("libc", SetLastError = false)]
    private static extern uint getegid();

    public static IReadOnlyDictionary<string, string> Environment(string? home)
    {
        if (!OperatingSystem.IsLinux() || string.IsNullOrEmpty(home) || LibraryPatterns.FirstOrDefault(File.Exists) is not { } library)
        {
            return new Dictionary<string, string>();
        }
        var uid = geteuid();
        var gid = getegid();
        if (HasEntry("/etc/passwd", uid))
        {
            return new Dictionary<string, string>();
        }
        var passwd = Path.Combine(home, ".di-passwd");
        var group = Path.Combine(home, ".di-group");
        File.WriteAllText(passwd, File.ReadAllText("/etc/passwd") + $"di-user:x:{uid}:{gid}:dotnet-intelligence:{home}:/bin/sh\n");
        File.WriteAllText(group, File.ReadAllText("/etc/group") + (HasEntry("/etc/group", gid) ? string.Empty : $"di-group:x:{gid}:\n"));
        return new Dictionary<string, string>
        {
            ["LD_PRELOAD"] = library,
            ["NSS_WRAPPER_PASSWD"] = passwd,
            ["NSS_WRAPPER_GROUP"] = group,
        };
    }

    /// <summary>True when a line of the file has <paramref name="id"/> as its third field (passwd uid, group gid).</summary>
    private static bool HasEntry(string file, uint id) =>
        File.Exists(file) && File.ReadLines(file).Any(line => line.Split(':') is { Length: > 2 } fields && fields[2] == id.ToString(System.Globalization.CultureInfo.InvariantCulture));
}

using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace DotnetIntelligence.Contracts;

public static class Hashing
{
    public static string Sha256Hex(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(SHA256.HashData(data));

    public static string Sha256Hex(string text) => Sha256Hex(Encoding.UTF8.GetBytes(text));

    public static string Sha256HexOfFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    /// <summary>
    /// Record id: prefix + first 32 hex characters of SHA-256 over the canonical JSON of the identifying fields (D7).
    /// </summary>
    public static string RecordId(string prefix, JsonNode identifyingFields) =>
        prefix + ":" + Sha256Hex(CanonicalJson.SerializeToUtf8(identifyingFields))[..32];
}

using System.Security.Cryptography;

namespace KnowledgeApp.Data;

internal static class RecoveryKeyCodec
{
    private const string Alphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";

    internal static string Generate()
    {
        var bytes = RandomNumberGenerator.GetBytes(16);
        try { return new string(bytes.Select(value => Alphabet[value & 31]).ToArray()); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    internal static string Display(string key) => string.Join('-', Enumerable.Range(0, 4).Select(i => key.Substring(i * 4, 4)));

    internal static string? Normalize(string? input)
    {
        if (input is null || input.Length > 64) return null;
        var normalized = new System.Text.StringBuilder(16);
        foreach (var character in input)
        {
            if (character is '-' or ' ') continue;
            var upper = character is >= 'a' and <= 'z' ? (char)(character - 32) : character;
            if (!Alphabet.Contains(upper) || normalized.Length >= 16) return null;
            normalized.Append(upper);
        }
        return normalized.Length == 16 ? normalized.ToString() : null;
    }
}

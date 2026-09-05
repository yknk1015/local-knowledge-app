using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Isopoh.Cryptography.Argon2;

namespace KnowledgeApp.Data;

internal static class Argon2PasswordCodec
{
    private const int Version = 19;
    private const int MemorySize = 19_456;
    private const int Iterations = 2;
    private const int Parallelism = 1;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    internal static string Hash(string password)
    {
        ValidateLength(password);
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var passwordBytes = Encoding.UTF8.GetBytes(password);
        try
        {
            return Argon2.Hash(new Argon2Config
            {
                Type = Argon2Type.HybridAddressing,
                Version = Argon2Version.Nineteen,
                TimeCost = Iterations,
                MemoryCost = MemorySize,
                Lanes = Parallelism,
                Threads = Parallelism,
                Password = passwordBytes,
                Salt = salt,
                HashLength = HashBytes
            });
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
        }
    }

    internal static bool Verify(string password, string encodedHash)
    {
        if (CountRunes(password) > 1024 || !TryParse(encodedHash, out var parsedHash))
        {
            return false;
        }

        var passwordBytes = Encoding.UTF8.GetBytes(password);
        try
        {
            return Argon2.Verify(encodedHash, passwordBytes, null, Parallelism);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
            CryptographicOperations.ZeroMemory(parsedHash);
        }
    }

    internal static void ValidateLength(string password)
    {
        if (CountRunes(password) > 1024)
        {
            throw new AppProblemException(new AppProblem(
                "USR-001",
                "パスワードは1024文字以内で入力してください。",
                "パスワードを短くして、もう一度お試しください。"));
        }
    }

    // Reuse authentication's format/resource limits without trying a password or
    // running Argon2 during backup inspection. Stored credentials are never reset.
    internal static bool IsSupportedHash(string encodedHash)
    {
        var supported = TryParse(encodedHash, out var parsedHash);
        CryptographicOperations.ZeroMemory(parsedHash);
        return supported;
    }

    private static bool TryParse(string value, out byte[] hash)
    {
        hash = [];
        var sections = value.Split('$', StringSplitOptions.None);
        if (sections.Length != 6 ||
            sections[0].Length != 0 ||
            sections[1] != "argon2id" ||
            sections[2] != $"v={Version}")
        {
            return false;
        }

        var values = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var item in sections[3].Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = item.Split('=', 2);
            if (pair.Length != 2 ||
                !int.TryParse(pair[1], NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
            {
                return false;
            }
            values[pair[0]] = parsed;
        }

        byte[] salt = [];
        if (!values.TryGetValue("m", out var memorySize) || memorySize is < 8 or > 262_144 ||
            !values.TryGetValue("t", out var iterations) || iterations is < 1 or > 10 ||
            !values.TryGetValue("p", out var parallelism) || parallelism is < 1 or > 16 ||
            !TryDecode(sections[4], out salt) || salt.Length is < 8 or > 64 ||
            !TryDecode(sections[5], out hash) || hash.Length is < 16 or > 64)
        {
            CryptographicOperations.ZeroMemory(salt);
            CryptographicOperations.ZeroMemory(hash);
            hash = [];
            return false;
        }

        CryptographicOperations.ZeroMemory(salt);
        return true;
    }

    private static bool TryDecode(string value, out byte[] bytes)
    {
        bytes = [];
        try
        {
            var padding = (4 - (value.Length % 4)) % 4;
            bytes = Convert.FromBase64String(value + new string('=', padding));
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static int CountRunes(string value) => value.EnumerateRunes().Count();
}

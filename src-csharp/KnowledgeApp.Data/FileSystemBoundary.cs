namespace KnowledgeApp.Data;

/// <summary>Physical-path checks shared by the isolated prototype's file operations.</summary>
public static class FileSystemBoundary
{
    private static readonly string[] SyntheticPrefixes =
        ["knowledgeapp-csharp-auth-", "knowledgeapp-csharp-search-", "knowledgeapp-data-check-"];

    public static string ValidateSyntheticRoot(string path)
    {
        var full = ValidatePath(path, allowUnc: false);
        var temporary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        var leaf = Path.GetFileName(full);
        var prefix = SyntheticPrefixes.FirstOrDefault(value => leaf.StartsWith(value, StringComparison.Ordinal));
        if (!string.Equals(Path.GetDirectoryName(full), temporary, StringComparison.OrdinalIgnoreCase) ||
            prefix is null || !Guid.TryParseExact(leaf[prefix.Length..], "D", out _))
        {
            throw UnsafePath();
        }
        return full;
    }

    public static string ValidateManagedDataRoot(string path)
    {
        var full = ValidatePath(path, allowUnc: false);
        if (string.Equals(full, ProductionDataRoot.FixedPath, StringComparison.OrdinalIgnoreCase))
        {
            ProductionDataRoot.ValidateFixedPath(full);
            ProductionDataRoot.ValidateExistingLayout(full);
            return full;
        }
        if (string.Equals(full, RehearsalDataRoot.FixedPath, StringComparison.OrdinalIgnoreCase))
        {
            RehearsalDataRoot.ValidateFixedPath(full);
            RehearsalDataRoot.ValidateInitialized(full);
            return full;
        }
        return ValidateSyntheticRoot(full);
    }

    public static string ValidatePath(string path, bool allowUnc = true)
    {
        var full = ValidatePathSyntax(path, allowUnc);
        string? current = full;
        while (current is not null)
        {
            try
            {
                var attributes = File.GetAttributes(current);
                if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0) throw UnsafePath();
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            current = Path.GetDirectoryName(current);
        }
        return full;
    }

    /// <summary>Checks remembered path text without probing a network or filesystem.</summary>
    public static string ValidatePathSyntax(string path, bool allowUnc = true)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) throw UnsafePath();
        var windowsPath = path.Replace('/', '\\');
        if (windowsPath.StartsWith(@"\\?\", StringComparison.Ordinal) ||
            windowsPath.StartsWith(@"\\.\", StringComparison.Ordinal) ||
            windowsPath.StartsWith(@"\??\", StringComparison.Ordinal)) throw UnsafePath();
        var unc = windowsPath.StartsWith(@"\\", StringComparison.Ordinal);
        if (unc && !allowUnc) throw UnsafePath();
        var tail = !unc && windowsPath.Length >= 3 && char.IsAsciiLetter(windowsPath[0]) && windowsPath[1] == ':'
            ? windowsPath[2..] : windowsPath;
        if (tail.Contains(':') || tail.Any(character => character is '<' or '>' or '"' or '|' or '?' or '*' || char.IsControl(character)))
            throw UnsafePath();
        foreach (var part in tail.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part is "." or ".." || part.EndsWith(' ') || part.EndsWith('.')) throw UnsafePath();
            var stem = part.Split('.')[0];
            if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
                stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
                stem.Equals("CONIN$", StringComparison.OrdinalIgnoreCase) || stem.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase) ||
                stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
                stem[3] is >= '1' and <= '9') throw UnsafePath();
        }
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    public static string ValidateManagedPath(string root, string path)
    {
        var fullRoot = ValidatePath(root, allowUnc: false);
        var fullPath = ValidatePath(path, allowUnc: false);
        if (!string.Equals(fullRoot, fullPath, StringComparison.OrdinalIgnoreCase) &&
            !fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw UnsafePath();
        return fullPath;
    }

    public static void DeleteSyntheticRoot(string path)
    {
        var root = ValidateSyntheticRoot(path);
        DeleteManagedDirectory(root, root);
    }

    internal static void CreateManagedDirectory(string root, string path)
    {
        var full = ValidateManagedPath(root, path);
        Directory.CreateDirectory(full);
        ValidateManagedPath(root, full);
    }

    internal static byte[] ReadBoundedFile(string path, int maximumBytes)
    {
        ValidatePath(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > maximumBytes) throw new InvalidDataException("ファイルのサイズが上限を超えています。");
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1) throw new InvalidDataException("読取中にファイルのサイズが変更されました。");
        return bytes;
    }

    internal static void DeleteManagedDirectory(string root, string path)
    {
        var full = ValidateManagedPath(root, path);
        if (!Directory.Exists(full)) return;
        ValidateTree(full);
        // Delete each already-checked entry without traversing reparse points.
        DeleteTree(full);
    }

    internal static void ValidateTree(string path)
    {
        var full = ValidatePath(path);
        if (!Directory.Exists(full)) return;
        foreach (var entry in new DirectoryInfo(full).EnumerateFileSystemInfos())
        {
            ValidatePath(entry.FullName);
            if (entry is DirectoryInfo directory) ValidateTree(directory.FullName);
        }
    }

    private static void DeleteTree(string directory)
    {
        ValidatePath(directory, allowUnc: false);
        foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
        {
            ValidatePath(entry.FullName, allowUnc: false);
            if (entry is DirectoryInfo child) DeleteTree(child.FullName);
            else File.Delete(entry.FullName);
        }
        ValidatePath(directory, allowUnc: false);
        Directory.Delete(directory, recursive: false);
    }

    private static IOException UnsafePath() => new("通常ファイルではないパス、リンクまたは管理対象外の保存先は使用できません。");
}

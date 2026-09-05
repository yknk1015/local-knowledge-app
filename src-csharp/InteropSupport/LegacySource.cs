// Developer-only interop harness support; never linked into the application.
internal static class LegacySource
{
    internal static string Resolve()
    {
        var value = Environment.GetEnvironmentVariable("KNOWLEDGEAPP_LEGACY_SOURCE");
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value))
            throw new InvalidOperationException("Set KNOWLEDGEAPP_LEGACY_SOURCE to the explicitly selected archived src-tauri directory. No source or user-data search is performed.");
        var root = Path.GetFullPath(value);
        if (!File.Exists(Path.Combine(root, "Cargo.toml")) || !File.Exists(Path.Combine(root, "tauri.conf.json")))
            throw new InvalidOperationException("The selected legacy source is missing Cargo.toml or tauri.conf.json.");
        return root;
    }
}

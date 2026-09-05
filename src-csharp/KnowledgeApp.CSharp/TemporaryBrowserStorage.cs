using System.IO;
using KnowledgeApp.Data;

namespace KnowledgeApp.CSharp;

// A host owns only the fresh browser directory it allocated. There is deliberately
// no constructor or cleanup API accepting the persistent FAQ data root.
public sealed class TemporaryBrowserStorage
{
    private TemporaryBrowserStorage(string root) => Root = root;

    public string Root { get; }
    public string ProfileDirectory => Path.Combine(Root, "temp", "webview2-profile");

    public static TemporaryBrowserStorage Create()
    {
        var root = FileSystemBoundary.ValidateSyntheticRoot(Path.Combine(
            Path.GetTempPath(), $"knowledgeapp-csharp-search-{Guid.NewGuid():D}"));
        if (Directory.Exists(root) || File.Exists(root)) throw new IOException("一時保存先を準備できません。");
        Directory.CreateDirectory(root);
        FileSystemBoundary.ValidateManagedPath(root, Path.Combine(root, "temp", "webview2-profile"));
        return new TemporaryBrowserStorage(root);
    }

    public bool TryCleanup()
    {
        try
        {
            FileSystemBoundary.DeleteSyntheticRoot(Root);
            return !Directory.Exists(Root);
        }
        catch
        {
            return false;
        }
    }
}

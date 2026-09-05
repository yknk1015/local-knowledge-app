using System.IO;
using KnowledgeApp.Data;

namespace KnowledgeApp.CSharp;

internal sealed class HostResourceFiles(string uiRoot, string attachmentsRoot, string stagedImagesRoot)
{
    public byte[] Read(HostResource resource)
    {
        var root = resource.Root switch
        {
            HostResourceRoot.Ui => uiRoot,
            HostResourceRoot.Attachments => attachmentsRoot,
            HostResourceRoot.StagedImages => stagedImagesRoot,
            _ => throw new IOException("Unknown application resource.")
        };
        var path = FileSystemBoundary.ValidateManagedPath(
            root, Path.Combine(root, resource.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length <= 0 || stream.Length > resource.MaximumBytes)
        {
            throw new IOException("Application resource size is not permitted.");
        }
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1) throw new IOException("Application resource changed.");
        return bytes;
    }
}

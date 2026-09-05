using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using KnowledgeApp.Data;
using Microsoft.Win32.SafeHandles;

internal static class FileBoundaryCheck
{
    internal static void Run()
    {
        if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("Windows filesystem boundary tests require Windows.");
        var temporary = Path.GetFullPath(Path.GetTempPath());
        var root = Path.Combine(temporary, $"knowledgeapp-data-check-{Guid.NewGuid():D}");
        var outside = Path.Combine(temporary, $"knowledgeapp-data-check-{Guid.NewGuid():D}");
        var links = new List<string>();
        KnowledgeDatabase? database = null;
        try
        {
            FileSystemBoundary.ValidateSyntheticRoot(root);
            FileSystemBoundary.ValidateSyntheticRoot(outside);
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(outside);
            ExpectUnsafe(() => FileSystemBoundary.ValidateSyntheticRoot(Path.Combine(temporary, "knowledgeapp-data-check-not-a-uuid")));
            ExpectUnsafe(() => FileSystemBoundary.ValidatePathSyntax(@"\\?\C:\synthetic.faqbackup"));
            ExpectUnsafe(() => FileSystemBoundary.ValidatePathSyntax(@"\\.\GLOBALROOT\Device\synthetic.faqbackup"));
            ExpectUnsafe(() => FileSystemBoundary.ValidatePathSyntax(Path.Combine(root, "sample.png:stream")));
            Check(FileSystemBoundary.ValidatePathSyntax(@"\\synthetic.invalid\share\sample.faqbackup") ==
                  @"\\synthetic.invalid\share\sample.faqbackup", "Normal UNC backup syntax was rejected");
            ExpectUnsafe(() => FileSystemBoundary.ValidatePathSyntax(@"\\synthetic.invalid\share\sample.faqbackup", allowUnc: false));

            var victim = Path.Combine(outside, "sentinels");
            Directory.CreateDirectory(victim);
            var sentinel = Path.Combine(victim, "untouched.txt");
            File.WriteAllText(sentinel, "SYNTHETIC_BOUNDARY_SENTINEL", new UTF8Encoding(false));
            File.SetLastWriteTimeUtc(sentinel, DateTime.UtcNow.AddDays(-2));
            var dataLink = Path.Combine(root, "data");
            Junction(dataLink, victim, root, outside, links);
            ExpectUnsafe(() => { using var rejected = KnowledgeDatabase.OpenSynthetic(root); });
            Check(!File.Exists(Path.Combine(victim, "knowledge.db")), "Opening the synthetic DB wrote through a junction");
            RemoveJunction(dataLink, root);

            var stagedParent = Path.Combine(root, "temp", "staged-article-images");
            Directory.CreateDirectory(stagedParent);
            var stagedLink = Path.Combine(stagedParent, "files");
            Junction(stagedLink, victim, root, outside, links);
            ExpectUnsafe(() => _ = new ArticleAttachmentService(root));
            CheckSentinel(sentinel);
            RemoveJunction(stagedLink, root);

            Directory.CreateDirectory(stagedLink);
            var unrelatedStage = Path.Combine(stagedLink, "keep-sentinel.txt");
            var expiredStage = Path.Combine(stagedLink, $"{Guid.NewGuid():D}.png");
            File.WriteAllText(unrelatedStage, "SYNTHETIC_UNRELATED_STAGE", new UTF8Encoding(false));
            File.WriteAllBytes(expiredStage, [0]);
            File.SetLastWriteTimeUtc(unrelatedStage, DateTime.UtcNow.AddDays(-2));
            File.SetLastWriteTimeUtc(expiredStage, DateTime.UtcNow.AddDays(-2));
            var attachments = new ArticleAttachmentService(root);
            Check(File.Exists(unrelatedStage) && !File.Exists(expiredStage), "Stage cleanup deleted unrelated names or failed to clean its own generated file");

            database = KnowledgeDatabase.OpenSynthetic(root);
            database.SeedSyntheticCategorySearchFixture();
            var authentication = new AuthenticationService(database);
            authentication.Login("0000", "");
            var backup = new BackupService(database, authentication, root);
            var transfer = new TransferService(database, authentication, root);
            var safeBackup = Path.Combine(outside, "baseline.faqbackup");
            backup.CreateFullBackup(new(safeBackup, "Synthetic boundary backup", false));

            var fakeRepo = Path.Combine(outside, "fake-repository");
            Directory.CreateDirectory(Path.Combine(fakeRepo, ".git"));
            var alias = Path.Combine(root, "git-alias");
            Junction(alias, fakeRepo, root, outside, links);
            ExpectUnsafe(() => transfer.ExportFaqCsv(new(Path.Combine(alias, "blocked.knowledge-faq.csv"))));
            ExpectUnsafe(() => transfer.ExportJson(new(Path.Combine(alias, "blocked.knowledge-export.json"))));
            ExpectUnsafe(() => backup.CreateFullBackup(new(Path.Combine(alias, "blocked.faqbackup"), "Blocked", false)));
            Check(Directory.GetFiles(fakeRepo).Length == 0, "Export or backup bypassed Git protection through a junction");
            ExpectUnsafe(() => FileSystemBoundary.DeleteSyntheticRoot(root));
            Check(Directory.Exists(fakeRepo), "Synthetic cleanup traversed a junction");
            RemoveJunction(alias, root);

            var manuals = Path.Combine(root, "manuals");
            Junction(manuals, victim, root, outside, links);
            ExpectUnsafe(() => backup.GetOverview());
            ExpectUnsafe(() => backup.CreateFullBackup(new(Path.Combine(outside, "blocked-manuals.faqbackup"), "Blocked", false)));
            ExpectUnsafe(() => database.CreateTransferSafetyBackup(root, "csv"));
            Check(!File.Exists(Path.Combine(outside, "blocked-manuals.faqbackup")), "Backup included an external managed-root alias");
            CheckSentinel(sentinel);
            RemoveJunction(manuals, root);

            var externalAttachments = Path.Combine(outside, "external-attachments");
            Directory.CreateDirectory(Path.Combine(externalAttachments, "articles"));
            var externalSentinel = Path.Combine(externalAttachments, "articles", "untouched.txt");
            File.WriteAllText(externalSentinel, "SYNTHETIC_BOUNDARY_SENTINEL", new UTF8Encoding(false));
            var attachmentParent = Path.Combine(root, "attachments");
            Directory.Delete(Path.Combine(attachmentParent, "articles"), recursive: false);
            Directory.Delete(attachmentParent, recursive: false);
            Junction(attachmentParent, externalAttachments, root, outside, links);
            ExpectUnsafe(() => backup.RestoreBackup(safeBackup));
            ExpectUnsafe(() => attachments.ResolveManagedAttachmentForTest("article/image.png"));
            CheckSentinel(externalSentinel);
            RemoveJunction(attachmentParent, root);
            Directory.CreateDirectory(Path.Combine(attachmentParent, "articles"));

            const string png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a/8sAAAAASUVORK5CYII=";
            var sourceImage = Path.Combine(victim, "synthetic.png");
            File.WriteAllBytes(sourceImage, Convert.FromBase64String(png));
            var imageAlias = Path.Combine(root, "image-alias");
            Junction(imageAlias, victim, root, outside, links);
            ExpectUnsafe(() => attachments.StageFromPath(Path.Combine(imageAlias, "synthetic.png")));
            RemoveJunction(imageAlias, root);
            var staged = attachments.StageBase64(new("synthetic.png", png));
            var articleId = Guid.NewGuid().ToString();
            var articleLink = Path.Combine(attachmentParent, "articles", articleId);
            Junction(articleLink, victim, root, outside, links);
            ExpectUnsafe(() => attachments.Prepare(articleId, [new AttachmentReference(staged.Id, "合成画像")], []));
            Check(!File.Exists(Path.Combine(victim, staged.Id + ".png")), "Image preparation wrote through a managed directory junction");
            CheckSentinel(sentinel);
            RemoveJunction(articleLink, root);

            Console.WriteLine("OK: 合成DB・添付・起動時整理・Git迂回・バックアップ/復元・CSV/JSONの再解析点拒否と通常UNC構文を合成junctionで確認しました。");
        }
        finally
        {
            database?.Dispose();
            foreach (var link in links.AsEnumerable().Reverse()) RemoveJunction(link, root);
            FileSystemBoundary.DeleteSyntheticRoot(root);
            FileSystemBoundary.DeleteSyntheticRoot(outside);
        }
    }

    private static void CheckSentinel(string path) => Check(
        File.ReadAllText(path, Encoding.UTF8) == "SYNTHETIC_BOUNDARY_SENTINEL", "An external synthetic sentinel was changed");

    private static void ExpectUnsafe(Action action)
    {
        try { action(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or AppProblemException or ArgumentException)
        { return; }
        throw new InvalidOperationException("An unsafe synthetic filesystem operation was accepted");
    }

    private static void Junction(string link, string target, string root, string outside, ICollection<string> links)
    {
        FileSystemBoundary.ValidateManagedPath(root, link);
        FileSystemBoundary.ValidateManagedPath(outside, target);
        Check(!Directory.Exists(link) && !File.Exists(link), "Test junction would overwrite an existing path");
        Directory.CreateDirectory(link);
        links.Add(link);
        using var handle = CreateFile(link, 0x40000000, 0, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var substitute = Encoding.Unicode.GetBytes(@"\??\" + Path.GetFullPath(target));
        var print = Encoding.Unicode.GetBytes(Path.GetFullPath(target));
        var buffer = new byte[16 + substitute.Length + 2 + print.Length + 2];
        BitConverter.GetBytes(0xA0000003u).CopyTo(buffer, 0);
        BitConverter.GetBytes(checked((ushort)(buffer.Length - 8))).CopyTo(buffer, 4);
        BitConverter.GetBytes(checked((ushort)substitute.Length)).CopyTo(buffer, 10);
        BitConverter.GetBytes(checked((ushort)(substitute.Length + 2))).CopyTo(buffer, 12);
        BitConverter.GetBytes(checked((ushort)print.Length)).CopyTo(buffer, 14);
        substitute.CopyTo(buffer, 16);
        print.CopyTo(buffer, 18 + substitute.Length);
        if (!DeviceIoControl(handle, 0x000900A4, buffer, buffer.Length, IntPtr.Zero, 0, out _, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        Check((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0, "Test junction was not created");
    }

    private static void RemoveJunction(string link, string root)
    {
        var fullRoot = FileSystemBoundary.ValidateSyntheticRoot(root);
        var fullLink = Path.GetFullPath(link);
        Check(fullLink.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Test junction cleanup escaped its root");
        FileSystemBoundary.ValidateManagedPath(root, Path.GetDirectoryName(fullLink)!);
        if (!Directory.Exists(fullLink)) return;
        // Delete only the link object, never recursively delete its target.
        if ((File.GetAttributes(fullLink) & FileAttributes.ReparsePoint) != 0)
            Directory.Delete(fullLink, recursive: false);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security,
        uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint control, byte[] input, int inputSize,
        IntPtr output, int outputSize, out int returned, IntPtr overlapped);
}

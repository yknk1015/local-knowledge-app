using KnowledgeApp.CSharp;

var root = Path.Combine(Path.GetTempPath(), "knowledgeapp-installation-guard-check-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException(name);
    checks++;
}
void Refuse(Action action, string name)
{
    try { action(); }
    catch (IOException) { checks++; return; }
    throw new InvalidOperationException(name);
}
try
{
    Refuse(() => { using var denied = InstallationActivityGuard.AcquireAt(Path.Combine(root, "missing")); }, "Unavailable directories must fail closed");
    using (InstallationActivityGuard.AcquireAt(root))
        Check(!File.Exists(Path.Combine(root, InstallationActivityGuard.FileName)), "Portable launch must not create a marker");
    var marker = Path.Combine(root, InstallationActivityGuard.FileName);
    File.WriteAllBytes(marker, []);
    using (InstallationActivityGuard.AcquireAt(root))
    using (InstallationActivityGuard.AcquireAt(root))
    {
        checks++;
        Refuse(() => { using var denied = new FileStream(marker, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }, "Installer must not acquire a running application's marker");
        Refuse(() => File.Delete(marker), "Running-app marker cannot be deleted");
    }
    using (var installer = new FileStream(marker, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        Refuse(() => { using var denied = InstallationActivityGuard.AcquireAt(root); }, "App must not launch during installation");
    using (InstallationActivityGuard.AcquireAt(root)) checks++;
    File.WriteAllBytes(marker, [1]);
    Refuse(() => { using var denied = InstallationActivityGuard.AcquireAt(root); }, "A nonempty activity file must fail closed");
    File.Delete(marker);
    Directory.CreateDirectory(marker);
    Refuse(() => { using var denied = InstallationActivityGuard.AcquireAt(root); }, "A directory activity marker must fail closed");
    Directory.Delete(marker);
    var outside = Path.Combine(root, "outside");
    Directory.CreateDirectory(outside);
    var link = Path.Combine(root, "linked");
    // Directory junction creation does not require symbolic-link privileges.
    // Both paths are new owned synthetic directories; this command only creates
    // the junction. Removal below uses Directory.Delete on the junction itself.
    var start = new System.Diagnostics.ProcessStartInfo("cmd.exe")
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        Arguments = $"/d /v:off /c mklink /J \"{link}\" \"{outside}\""
    };
    using (var child = System.Diagnostics.Process.Start(start) ?? throw new IOException("Unable to start owned junction fixture"))
    {
        if (!child.WaitForExit(5000)) { child.Kill(); throw new IOException("Owned junction fixture timed out"); }
        if (child.ExitCode != 0) throw new IOException("Unable to create the owned junction fixture");
    }
    try { Refuse(() => { using var denied = InstallationActivityGuard.AcquireAt(link); }, "Linked installation directories must fail closed"); }
    finally { Directory.Delete(link); }
    Console.WriteLine($"Installation activity guard: {checks} checks passed; synthetic files only.");
}
finally
{
    var expected = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
    if (!string.Equals(Path.GetDirectoryName(root), expected, StringComparison.OrdinalIgnoreCase) ||
        !Path.GetFileName(root).StartsWith("knowledgeapp-installation-guard-check-", StringComparison.Ordinal))
        throw new InvalidOperationException("Refusing cleanup outside the owned temporary root");
    Directory.Delete(root, recursive: true);
}

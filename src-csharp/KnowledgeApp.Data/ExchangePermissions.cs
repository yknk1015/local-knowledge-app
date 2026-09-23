using System.Security.AccessControl;
using System.Security.Principal;
namespace KnowledgeApp.Data;
public static class ExchangePermissions
{
    public static void ProtectNew(string path)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var directory = new DirectoryInfo(FileSystemBoundary.ValidatePath(path, allowUnc: false));
        var security = new DirectorySecurity(); security.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { WindowsIdentity.GetCurrent().User!, new SecurityIdentifier("S-1-5-18"), new SecurityIdentifier("S-1-5-32-544") })
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        directory.SetAccessControl(security);
        Validate(path);
    }
    public static void Validate(string path)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var allowed = new[] { WindowsIdentity.GetCurrent().User!.Value, "S-1-5-18", "S-1-5-32-544" };
        var security = new DirectoryInfo(FileSystemBoundary.ValidatePath(path, allowUnc: false)).GetAccessControl();
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == AccessControlType.Allow && !allowed.Contains(rule.IdentityReference.Value) &&
                (rule.FileSystemRights & (FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership)) != 0)
                throw CodexLocationService.Problem("Codex連携先を他のWindows利用者が変更できる権限になっています。専用フォルダーの権限を確認してください。");
    }
}

using System.IO;
using KnowledgeApp.Data;
using KnowledgeApp.Mail;

namespace KnowledgeApp.CSharp;

public static class RehearsalMailDelegation
{
    public static MailDelegationWriter CreateWriter() => CreateWriter(() => RehearsalDataRoot.FixedPath);

    // Synthetic checks and the native candidate host share the same validation.
    // The host selects a fixed root only; no renderer/command-line path is accepted.
    internal static MailDelegationWriter CreateWriter(Func<string> rootProvider) => new(() =>
    {
        var dataRoot = FileSystemBoundary.ValidateManagedDataRoot(rootProvider());
        var root = CodexLocationService.ResolveExchangeRoot(dataRoot);
        // Validate the entire target ancestry before the mail writer creates any
        // directory. The mail library's unvalidated default provider is not used.
        FileSystemBoundary.ValidateManagedPath(root, Path.Combine(root, "codex-bridge", "mail-delegations"));
        return root;
    });
}

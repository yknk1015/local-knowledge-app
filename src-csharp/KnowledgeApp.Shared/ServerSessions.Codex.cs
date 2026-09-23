using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KnowledgeApp.Data;
namespace KnowledgeApp.Shared;
public sealed partial class ServerSessions
{
    private static CodexLocation MakeCodexLocation(KnowledgeDatabase db, AuthenticationService auth, string root, string serverId)
    {
        var user = auth.RequireEditor();
        var environment = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(serverId + ":" + user.Id)).AsSpan(0, 16)).ToString("D");
        return new(1, environment, db.GetAuthenticationVersion(user.Id) + 1, Path.Combine(root, "codex-users", user.Id));
    }
    private static bool IsCodexCommand(string command) => command.Contains("codex", StringComparison.Ordinal) || command == "clear_article_merge";
    private object? ExecuteCodex(Session session, string command, JsonElement args)
    {
        var user = session.Authentication.RequireEditor();
        var location = session.CodexLocation();
        var files = new CodexProposalFiles(database, session.CodexLocation);
        if (command == "shared_codex_exchange")
        {
            files.WriteCategoryCatalog(database.ListCategories());
            var catalog = FileSystemBoundary.ReadBoundedFile(files.CategoryCatalogPath, 5 * 1024 * 1024);
            return new { location.EnvironmentId, location.Generation, catalogJson = Encoding.UTF8.GetString(catalog) };
        }
        if (command == "shared_submit_codex")
        {
            var proposal = args.GetProperty("proposal").Deserialize<CodexFaqProposal>(CodexJson.Options) ?? throw new JsonException();
            CodexProposalFiles.ValidateProposal(proposal);
            database.ClaimCodexProposal(proposal.RequestId, user.Id, proposal.SeriesId ?? proposal.RequestId);
            files.StoreIncoming(Encoding.UTF8.GetBytes(args.GetProperty("proposal").GetRawText()));
            return null;
        }
        if (command is "accept_codex_proposal" or "reject_codex_proposal" or "reopen_rejected_codex_proposal")
        {
            var id = (command == "accept_codex_proposal" ? args.GetProperty("input") : args).GetProperty("requestId").GetString()!;
            if (!database.OwnsCodexProposal(id, user.Id)) throw new AppProblemException(AppProblem.AdminRequired());
        }
        var result = session.Dispatcher.Execute(command, args);
        if (result is CodexProposalInbox inbox)
            return inbox with { Proposals = inbox.Proposals.Where(p => database.OwnsCodexProposal(p.RequestId, user.Id)).ToArray(),
                History = inbox.History.Where(h => database.OwnsCodexProposal(h.Proposal.RequestId, user.Id)).ToArray(), InboxPath = "", CategoryCatalogPath = "" };
        if (result is CodexDelegationResult delegation) return delegation with { FilePath = "shared-delegation:" + delegation.DelegationId };
        return result;
    }
    public byte[] ReadDelegation(string token, string id)
    {
        var session = Resolve(token);
        session.Authentication.RequireEditor();
        if (!Guid.TryParseExact(id, "D", out _)) throw Problem("委譲番号が正しくありません。");
        var location = session.CodexLocation();
        var path = FileSystemBoundary.ValidateManagedPath(root, Path.Combine(location.Root, "codex-bridge", "delegations", id + CodexProposalFiles.DelegationSuffix));
        return FileSystemBoundary.ReadBoundedFile(path, CodexProposalFiles.MaximumDelegationBytes);
    }
}

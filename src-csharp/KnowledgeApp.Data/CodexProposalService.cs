namespace KnowledgeApp.Data;

/// <summary>Authenticated, explicit Codex hand-off and approval operations.</summary>
public sealed class CodexProposalService
{
    private readonly KnowledgeDatabase _database;
    private readonly AuthenticationService _authentication;
    private readonly ArticleAttachmentService _attachments;
    private readonly CodexProposalFiles _files;
    private readonly object _operationSync = new();

    public CodexProposalService(
        KnowledgeDatabase database,
        AuthenticationService authentication,
        ArticleAttachmentService? attachments = null, Func<CodexLocation>? locationProvider = null)
    {
        _database = database;
        _operationSync = database.OperationSync;
        _authentication = authentication;
        _attachments = attachments ?? new ArticleAttachmentService(
            Directory.GetParent(Path.GetDirectoryName(database.OpenInfo.DatabasePath)!)!.FullName);
        _files = new CodexProposalFiles(database, locationProvider);
    }

    // Host lifecycle hook, never exposed as an arbitrary path/file command.
    public void RefreshCategoryCatalogBestEffort()
    {
        lock (_operationSync)
        {
            try { _files.WriteCategoryCatalog(_database.ListCategories()); }
            catch (AppProblemException) { }
        }
    }

    public CodexProposalInbox ListProposals()
    {
        _authentication.RequireEditor();
        lock (_operationSync)
        {
            _files.WriteCategoryCatalog(_database.ListCategories());
            var (proposals, invalidFiles) = _files.ListProposals();
            var rejected = invalidFiles.ToList();
            foreach (var proposal in proposals)
            {
                if (_database.IsCodexProposalAccepted(proposal.RequestId)) continue;
                try { _files.ValidateDelegatedSources(proposal); }
                catch (AppProblemException exception)
                {
                    rejected.Add(new RejectedCodexProposal(
                        $"{proposal.RequestId}.knowledge-proposal.json",
                        exception.Problem.Message));
                    continue;
                }
                try { _database.RecordCodexProposal(proposal); }
                catch (AppProblemException exception) when (exception.Problem.Code == "CDX-013")
                {
                    rejected.Add(new RejectedCodexProposal(
                        $"{proposal.RequestId}.knowledge-proposal.json", exception.Problem.Message));
                }
            }
            return new CodexProposalInbox(
                _database.ListPendingCodexProposals(),
                _database.ListCodexProposalHistory(),
                rejected, _files.InboxPath, _files.CategoryCatalogPath);
        }
    }

    public AcceptCodexProposalResult AcceptProposal(AcceptCodexProposalInput input)
    {
        var actor = _authentication.RequireEditor();
        if (input.CreateProposedCategory) _authentication.RequireAdmin();
        RequireRequestId(input.RequestId);
        lock (_operationSync)
        {
            CodexFaqProposal proposal;
            try { proposal = _database.GetPendingCodexProposal(input.RequestId); }
            catch (AppProblemException exception) when (exception.Problem.Code == "CDX-003")
            {
                proposal = _files.ReadProposal(input.RequestId);
                _files.ValidateDelegatedSources(proposal);
                _database.RecordCodexProposal(proposal);
            }
            CodexProposalFiles.ValidateProposal(proposal);
            _files.ValidateDelegatedSources(proposal);
            var content = SafeRichContentValidator.Validate(proposal.Faq.BodyDoc);
            if (proposal.ProposalKind != CodexProposalKinds.Revise && content.Attachments.Count != 0)
            {
                throw Problem("CDX-002", "Codex提案から画像は取り込めません。",
                    "画像は下書きを取り込んだ後、FAQ編集画面から追加してください。");
            }

            AcceptCodexProposalResult result;
            if (proposal.ProposalKind == CodexProposalKinds.Revise)
            {
                var source = proposal.SourceArticles[0];
                var current = _database.GetArticle(source.ArticleId);
                var expected = current.Attachments.Select(item => (item.Id, item.AltText)).ToHashSet();
                var actual = content.Attachments.Select(item => (item.Id, item.AltText)).ToHashSet();
                if (!expected.SetEquals(actual))
                {
                    throw Problem("CDX-021", "Codex修正案で既存画像の参照が変更されています。",
                        "元FAQの画像ノードを変更せずに修正案を作り直してください。");
                }
                var article = _database.AcceptCodexRevision(proposal, content.PlainText, actor.Id);
                result = new AcceptCodexProposalResult(_attachments.HydrateArticle(article), null);
            }
            else
            {
                if (input.CreateProposedCategory && proposal.NewCategoryProposal is null)
                {
                    throw Problem("CDX-004", "このCodex提案には新規分類案がありません。",
                        "既存分類を選ぶか、Codexへ分類案を含めて再依頼してください。");
                }
                if (!input.CreateProposedCategory && string.IsNullOrWhiteSpace(input.CategoryId))
                {
                    throw Problem("CDX-005", "下書きの所属分類を選択してください。",
                        "既存分類を1つ選んでから取り込んでください。");
                }
                var categoryId = input.CreateProposedCategory ? Guid.CreateVersion7().ToString() : input.CategoryId!;
                var accepted = _database.AcceptCodexProposal(
                    proposal, Guid.CreateVersion7().ToString(), categoryId,
                    input.CreateProposedCategory, content.PlainText, actor.Id);
                result = new AcceptCodexProposalResult(
                    _attachments.HydrateArticle(accepted.Article), accepted.CreatedCategory);
            }
            _files.DiscardProposalBestEffort(proposal.RequestId);
            RefreshCategoryCatalogBestEffort();
            return result;
        }
    }

    public void RejectProposal(string requestId)
    {
        _authentication.RequireEditor();
        RequireRequestId(requestId);
        lock (_operationSync)
        {
            _database.RejectCodexProposal(requestId);
            _files.DiscardProposalBestEffort(requestId);
        }
    }

    public void ReopenRejectedProposal(string requestId)
    {
        _authentication.RequireEditor();
        RequireRequestId(requestId);
        lock (_operationSync) { _database.ReopenRejectedCodexProposal(requestId); }
    }

    public CodexDelegationResult CreateDelegation(CreateCodexDelegationInput input)
    {
        _authentication.RequireEditor();
        if (!CodexProposalKinds.IsDelegation(input.Kind) || input.ArticleIds is null ||
            (input.Kind == CodexProposalKinds.Revise ? input.ArticleIds.Count != 1 : input.ArticleIds.Count is < 2 or > 10))
        {
            throw Problem("CDX-020", "修正はFAQを1件、統合は2～10件選択してください。",
                "FAQ管理画面で対象を選び直してください。");
        }
        if (input.ArticleIds.Any(id => !Guid.TryParseExact(id, "D", out _)) ||
            input.ArticleIds.Distinct(StringComparer.Ordinal).Count() != input.ArticleIds.Count)
        {
            throw Problem("CDX-020", "FAQの選択が正しくないか、同じFAQが複数回選択されています。",
                "重複を外してから、もう一度委譲してください。");
        }
        lock (_operationSync)
        {
            var articles = input.ArticleIds.Select(_database.GetArticle).ToList();
            if (articles.Any(article => article.DeletedAt is not null || article.MergeInfo is not null))
            {
                throw Problem("CDX-020", "削除済み・統合済みFAQはCodexへ委譲できません。",
                    "登録中のFAQを選ぶか、復元・統合解除後に選び直してください。");
            }
            return _files.WriteDelegation(input.Kind, articles, _database.ListCategories());
        }
    }

    public CodexMergePublicationContext? GetMergePublicationContext(string articleId)
    {
        _authentication.RequireEditor();
        return _database.GetCodexMergePublicationContext(articleId);
    }

    public MarkCodexMergeSourcesResult MarkMergeSources(string articleId)
    {
        _authentication.RequireEditor();
        lock (_operationSync) { return _database.MarkCodexMergeSources(articleId); }
    }

    public ArticleDetail ClearArticleMerge(string articleId)
    {
        _authentication.RequireEditor();
        lock (_operationSync) { return _attachments.HydrateArticle(_database.ClearArticleMerge(articleId)); }
    }

    private static void RequireRequestId(string requestId)
    {
        if (!Guid.TryParseExact(requestId, "D", out _))
            throw Problem("CDX-002", "Codex提案の受付番号が正しくありません。", "提案一覧を更新してください。");
    }

    private static AppProblemException Problem(string code, string message, string action) =>
        new(new AppProblem(code, message, action));
}

namespace KnowledgeApp.Data;

public sealed record BackupCounts(
    long Articles,
    long Categories,
    long Attachments,
    long Manuals);

public sealed record BackupOverview(
    long EstimatedBytes,
    BackupCounts Counts,
    string? DefaultDirectory);

public sealed record BackupPreview(
    string SourcePath,
    string DisplayName,
    string CreatedAt,
    string AppVersion,
    long SchemaVersion,
    long BackupFormatVersion,
    long TotalBytes,
    BackupCounts Counts,
    string? ConfirmationToken = null);

internal sealed record VerifiedBackupPreview(BackupPreview Preview, string FileSha256);

public sealed record RestoreConfirmedBackupInput(string Path, string? ConfirmationToken);

public sealed record BackupResult(
    string DestinationPath,
    string DisplayName,
    string CreatedAt,
    long TotalBytes,
    BackupCounts Counts);

public sealed record RestoreResult(
    string SourcePath,
    string SafetyBackupPath,
    string RestoredAt,
    BackupCounts Counts);

public sealed record CreateFullBackupInput(
    string DestinationPath,
    string DisplayName,
    bool Overwrite);

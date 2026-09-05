using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace KnowledgeApp.Mail;

public sealed class MailPreviewItem : INotifyPropertyChanged
{
    private bool _isSelected;
    private string _subject;
    private string _sender;
    private string _recipients;
    private string _bodyText;

    public MailPreviewItem(
        string sourcePath,
        string sourceFileName,
        string subject,
        string sender,
        string recipients,
        DateTimeOffset? sentAt,
        string bodyText)
    {
        SourcePath = sourcePath;
        SourceFileName = sourceFileName;
        _subject = subject;
        _sender = sender;
        _recipients = recipients;
        SentAt = sentAt;
        _bodyText = bodyText;
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetField(ref _isSelected, value);
    }

    public string SourcePath { get; }

    public string SourceFileName { get; }

    public string Subject
    {
        get => _subject;
        set => SetField(ref _subject, value);
    }

    public string Sender
    {
        get => _sender;
        set => SetField(ref _sender, value);
    }

    public string Recipients
    {
        get => _recipients;
        set => SetField(ref _recipients, value);
    }

    public DateTimeOffset? SentAt { get; }

    public string BodyText
    {
        get => _bodyText;
        set => SetField(ref _bodyText, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed record MailScanResult(
    IReadOnlyList<MailPreviewItem> Items,
    IReadOnlyList<string> Warnings,
    int PstFileCount);

public sealed record MailDelegationResult(
    Guid DelegationId,
    string Prompt,
    int MailCount);

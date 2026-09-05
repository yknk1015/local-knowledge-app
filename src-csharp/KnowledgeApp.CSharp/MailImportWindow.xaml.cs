using System.Collections.ObjectModel;
using System.Windows;
using KnowledgeApp.Mail;
using Microsoft.Win32;

namespace KnowledgeApp.CSharp;

public partial class MailImportWindow : Window
{
    private readonly MsgFolderScanner _scanner = new();
    private readonly MailDelegationWriter _delegationWriter;
    private ObservableCollection<MailPreviewItem> _items = [];
    private Guid? _lastDelegationId;
    private readonly bool _productionCandidate;

    public MailImportWindow(MailDelegationWriter delegationWriter, bool productionCandidate = false)
    {
        _delegationWriter = delegationWriter;
        _productionCandidate = productionCandidate;
        InitializeComponent();
        Title = productionCandidate ? "メール履歴からFAQ候補を作る — C#" : "メール履歴からFAQ候補を作る — C#専用検証・送信不可";
        MailGrid.ItemsSource = _items;
    }

    private void ChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = ".msgを配置した確認用フォルダを選択してください",
            Multiselect = false
        };
        if (dialog.ShowDialog(this) == true)
        {
            FolderText.Text = dialog.FolderName;
            ScanButton.IsEnabled = true;
            MailStatusText.Text = "フォルダを選択しました。次に右端の「② .msgを確認」を押してください。選択しただけでは読み取りません。";
            ScanButton.Focus();
        }
    }

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(FolderText.Text))
        {
            MessageBox.Show(this, "先に確認用フォルダを選択してください。", "メール履歴", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        ScanButton.IsEnabled = false;
        MailStatusText.Text = "明示操作により.msgを1回確認しています…";
        try
        {
            // WPFの画面部品はUIスレッドでだけ読み取り、バックグラウンド処理には値だけを渡す。
            var folderPath = FolderText.Text;
            var result = await Task.Run(() => _scanner.ScanOnce(folderPath));
            _items = new ObservableCollection<MailPreviewItem>(result.Items);
            MailGrid.ItemsSource = _items;
            MailGrid.SelectedIndex = _items.Count > 0 ? 0 : -1;
            var warningText = result.Warnings.Count > 0
                ? $" 警告{result.Warnings.Count}件: {string.Join(" / ", result.Warnings.Take(3))}"
                : string.Empty;
            MailStatusText.Text = $"{_items.Count}件をローカルで確認しました。使用するメールを選択し、右側で内容を確認・マスクしてください。{warningText}";
        }
        catch (Exception exception)
        {
            MailStatusText.Text = "メールを確認できませんでした。";
            MessageBox.Show(this, exception.Message, "メール履歴", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            ScanButton.IsEnabled = true;
        }
    }

    private void Delegate_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            MailGrid.CommitEdit();
            var selectedCount = _items.Count(item => item.IsSelected);
            if (selectedCount == 0)
            {
                throw new InvalidOperationException("FAQ候補に使うメールを1件以上選択してください。");
            }

            var confirmed = MessageBox.Show(
                this,
                $"選択した{selectedCount}件の件名・送信者・宛先・本文をCodex委譲用JSONへ保存します。\n\n個人情報、機密情報、認証情報の削除またはマスクを確認しましたか？\n原本と添付ファイルは委譲しません。",
                "メール内容の最終確認",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (confirmed != MessageBoxResult.Yes) return;

            var result = _delegationWriter.WriteSelected(_items);
            _lastDelegationId = result.DelegationId;
            DiscardDelegationButton.IsEnabled = true;
            if (_productionCandidate)
            {
                var prompt = $"KnowledgeAppのメール委譲番号 {result.DelegationId:D} からFAQ案を作成してください。選択・マスク済みの委譲内容だけを使用してください。";
                Clipboard.SetText(prompt);
                MailStatusText.Text = $"メール委譲番号 {result.DelegationId:D} を保存しました。依頼文をコピーしました。送信はご自身で確認して行ってください。";
                MessageBox.Show(this, prompt + "\n\n依頼文をコピーしました。会社規定を確認し、対応するKnowledgeAppプラグインへ明示的に送ってください。",
                    "メール委譲を作成しました", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            // The installed plugin reads the production root, not this rehearsal
            // store. Do not put an unusable production prompt on the clipboard.
            MailStatusText.Text = $"検証用委譲番号 {result.DelegationId:D} を専用フォルダーへ保存しました。Codexへは送信しないでください。";
            MessageBox.Show(
                this,
                $"{result.MailCount}件をC#専用の検証フォルダーへ保存しました。\n実Codexプラグインは未接続のため、依頼文はコピーしていません。送信しないでください。\n\n検証用委譲番号：{result.DelegationId:D}",
                "検証用メール委譲を作成しました",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "メール委譲", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void DiscardDelegation_Click(object sender, RoutedEventArgs e)
    {
        if (_lastDelegationId is not Guid delegationId) return;
        var confirmed = MessageBox.Show(
            this,
            $"メール委譲番号 {delegationId:D} を破棄しますか？\nCodexへ送信済みの場合、Codex側の会話内容は削除されません。",
            "メール委譲を破棄",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirmed != MessageBoxResult.Yes) return;

        try
        {
            var deleted = _delegationWriter.Delete(delegationId);
            MailStatusText.Text = deleted
                ? $"メール委譲番号 {delegationId:D} を破棄しました。"
                : "メール委譲ファイルはすでにありません。";
            _lastDelegationId = null;
            DiscardDelegationButton.IsEnabled = false;
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "メール委譲", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}

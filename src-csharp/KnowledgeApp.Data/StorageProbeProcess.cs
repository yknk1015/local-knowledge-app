using System.Diagnostics;
using System.Text.Json;
namespace KnowledgeApp.Data;
internal static class StorageProbeProcess
{
    internal static StorageProbeResult Run(string executable, string root, string purpose, string path)
    {
        using var process = new Process { StartInfo = new(executable)
        { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
          RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true } };
        if (!process.Start()) throw StorageSettingsService.Problem("保存先の確認プログラムを開始できません。");
        process.StandardInput.WriteLine(JsonSerializer.Serialize(new { root, purpose, path }));
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(10000))
        {
            try { process.Kill(entireProcessTree: true); process.WaitForExit(5000); } catch (InvalidOperationException) { }
            throw StorageSettingsService.Problem("保存先の確認が10秒以内に完了しませんでした。確認処理の停止を要求しました。設定は変更していません。ネットワークと共有権限を確認してください。");
        }
        _ = error.GetAwaiter().GetResult();
        if (process.ExitCode != 0) throw StorageSettingsService.Problem("保存先の接続・権限・Git境界を確認できませんでした。");
        return JsonSerializer.Deserialize<StorageProbeResult>(output.GetAwaiter().GetResult()) ?? throw StorageSettingsService.Problem("保存先の確認結果が正しくありません。");
    }
}

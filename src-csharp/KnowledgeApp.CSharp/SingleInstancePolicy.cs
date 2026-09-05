using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace KnowledgeApp.CSharp;

public enum SingleInstanceReply : byte { Activated = 1, Starting = 2, Closing = 3, Rejected = 4, Unavailable = 5 }

public sealed record SingleInstanceIdentity
{
    private SingleInstanceIdentity(string key)
    {
        MutexName = key + ".mutex";
        PipeName = key + ".activate";
    }

    public string MutexName { get; }
    public string PipeName { get; }

    // Stable across build directories and future trial versions, separate from Tauri
    // and any future production identity. No command line can choose this identity.
    public static SingleInstanceIdentity ForCurrentUser() => Create("KnowledgeApp.CSharp.Trial", null);

    public static SingleInstanceIdentity ForProduction() => Create("KnowledgeApp.CSharp.Production", null);

    internal static SingleInstanceIdentity ForSyntheticTest(Guid runId)
    {
        if (runId == Guid.Empty) throw new ArgumentException("A fresh synthetic run identity is required.");
        return Create("KnowledgeApp.SingleInstanceCheck", runId);
    }

    private static SingleInstanceIdentity Create(string product, Guid? runId)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value ?? throw new InvalidOperationException("The current user is unavailable.");
        var userKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sid)));
        return new SingleInstanceIdentity($"{product}.{userKey}{(runId is { } id ? "." + id.ToString("N") : "")}");
    }
}

public static class SingleInstancePolicy
{
    public static readonly TimeSpan ActivationWaitLimit = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan ConnectionWaitLimit = TimeSpan.FromSeconds(1);
    public static ReadOnlySpan<byte> ActivationRequest => "KnowledgeApp.Activate.v1"u8;

    public static bool IsActivationRequest(ReadOnlySpan<byte> message, bool complete) =>
        complete && message.SequenceEqual(ActivationRequest);

    public static string Notice(SingleInstanceReply reply) => reply switch
    {
        SingleInstanceReply.Closing => "既存のC#版は終了処理中です。終了してから、もう一度起動してください。新しい画面やデータは作成していません。",
        _ => "既存のC#版は起動中、別のWindowsセッションで実行中、または応答待ちです。既存の画面を確認し、少し待ってから再操作してください。新しい画面やデータは作成していません。"
    };
}

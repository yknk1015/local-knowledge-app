using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading.RateLimiting;
using KnowledgeApp.Data;
using KnowledgeApp.Shared;
using Microsoft.AspNetCore.RateLimiting;

if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("共有サーバーはWindows 11用です。");
if (args is ["--initialize"])
{
    using var setup = KnowledgeDatabase.OpenShared();
    if (setup.SharedAdministratorReady()) throw new InvalidOperationException("初期管理者は設定済みです。既存の認証情報は変更しません。");
    Console.Write("初期管理者の新しいパスワード: ");
    var password = ReadPassword();
    Console.Write("確認用パスワード: ");
    if (password != ReadPassword() || string.IsNullOrWhiteSpace(password)) throw new InvalidOperationException("パスワードが一致しないか空欄です。");
    setup.InitializeSharedAdministrator(password);
    Console.WriteLine("管理者を設定しました。サーバー設定と証明書を確認してサービスを開始してください。");
    return;
}
if (args.Length != 0) throw new InvalidOperationException("起動引数は --initialize または指定なしです。");
var root = SharedDataRoot.FixedPath;
var config = SharedServerConfiguration.Read(root);
using var database = KnowledgeDatabase.OpenShared();
if (!database.SharedAdministratorReady()) throw new InvalidOperationException("サービス起動前に --initialize で管理者のパスワードを設定してください。");
using var certificateStore = new X509Store(StoreName.My, StoreLocation.LocalMachine);
certificateStore.Open(OpenFlags.ReadOnly);
var certificates = certificateStore.Certificates.Find(X509FindType.FindByThumbprint, config.CertificateThumbprint, validOnly: true);
if (certificates.Count != 1 || !certificates[0].HasPrivateKey) throw new InvalidOperationException("有効なサーバー証明書と秘密鍵の使用権限を確認してください。");
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory, Args = [] });
builder.Logging.ClearProviders(); // Do not write request bodies, URLs, credentials, FAQ, or tokens to logs.
builder.Services.AddWindowsService(options => options.ServiceName = "KnowledgeApp.Shared");
builder.Services.ConfigureHttpJsonOptions(options => { options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase; options.SerializerOptions.MaxDepth = 144; });
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 16 * 1024 * 1024;
    options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(15);
    options.Listen(IPAddress.Any, config.Port, listener => listener.UseHttps(certificates[0]));
});
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ =>
            new FixedWindowRateLimiterOptions { PermitLimit = 240, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
var app = builder.Build();
var sessions = new ServerSessions(database, root, config.ServerId);
SharedEndpoints.Map(app, sessions, config.HostName, config.ServerId);
await app.RunAsync();

static string ReadPassword()
{
    var text = new System.Text.StringBuilder();
    while (true)
    {
        var key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter) { Console.WriteLine(); return text.ToString(); }
        if (key.Key == ConsoleKey.Backspace) { if (text.Length > 0) text.Length--; }
        else if (!char.IsControl(key.KeyChar) && text.Length < 1024) text.Append(key.KeyChar);
    }
}

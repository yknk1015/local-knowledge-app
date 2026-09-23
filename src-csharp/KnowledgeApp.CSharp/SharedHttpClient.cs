using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KnowledgeApp.Data;
namespace KnowledgeApp.CSharp;

internal sealed partial class SharedHttpClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, MaxDepth = 144 };
    private readonly HttpClient _http;
    private readonly SharedConnectionSettings _settings;
    private readonly SemaphoreSlim _sessionGate = new(1, 1);
    private readonly Dictionary<string, string> _uncertain = new(StringComparer.Ordinal);
    private string? _token;
    private readonly string? _deviceRoot;
    internal SharedHttpClient(SharedConnectionSettings settings, string? deviceRoot = null)
    {
        _settings = settings;
        _deviceRoot = deviceRoot;
        _http = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
        { BaseAddress = settings.Validate(), Timeout = TimeSpan.FromMinutes(5), MaxResponseContentBufferSize = 32 * 1024 * 1024 };
    }
    public async Task VerifyServer()
    {
        using var response = await _http.GetAsync("api/info");
        response.EnsureSuccessStatusCode();
        var info = await response.Content.ReadFromJsonAsync<JsonElement>();
        if (info.GetProperty("protocolVersion").GetInt32() != 1 || info.GetProperty("serverId").GetString() != _settings.ServerId)
            throw Problem("共有先のサーバーIDが一致しません。接続先を確認してください。");
    }
    private async Task EnsureSession()
    {
        if (_token is not null) return;
        await _sessionGate.WaitAsync();
        try
        {
            if (_token is not null) return;
            await VerifyServer();
            using var response = await _http.PostAsync("api/session", null);
            response.EnsureSuccessStatusCode();
            var data = await response.Content.ReadFromJsonAsync<JsonElement>();
            _token = data.GetProperty("token").GetString() ?? throw Problem("接続を開始できませんでした。");
        }
        finally { _sessionGate.Release(); }
    }
    internal async Task<object?> Execute(string command, JsonElement args)
    {
        try
        {
            await EnsureSession();
            var fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(command + "\n" + args.GetRawText())));
            string id;
            lock (_uncertain)
            {
                if (!_uncertain.TryGetValue(fingerprint, out id!)) _uncertain[fingerprint] = id = Guid.NewGuid().ToString("D");
            }
            using var request = new HttpRequestMessage(HttpMethod.Post, "api/command");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
            request.Content = JsonContent.Create(new { id, command, args }, options: JsonOptions);
            using var response = await _http.SendAsync(request);
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            { _token = null; throw new AppProblemException(AppProblem.LoginRequired()); }
            response.EnsureSuccessStatusCode();
            var envelope = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
            lock (_uncertain) _uncertain.Remove(fingerprint);
            if (!envelope.GetProperty("ok").GetBoolean())
                throw new AppProblemException(envelope.GetProperty("error").Deserialize<AppProblem>(JsonOptions) ?? AppProblem.System("共有処理に失敗しました。"));
            if (command is "login" or "logout") { lock (_uncertain) _uncertain.Clear(); _uploads.Clear(); }
            if (command == "logout") _token = null;
            return envelope.TryGetProperty("result", out var result) && result.ValueKind != JsonValueKind.Null ? result.Clone() : null;
        }
        catch (AppProblemException) { throw; }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        { throw Problem("共有サーバーから結果を受信できません。入力内容を保持しています。接続を確認して同じ内容で再実行してください。ローカルDBへの切替は行いません。"); }
    }
    internal async Task<byte[]> ReadImage(HostResource resource)
    {
        await EnsureSession();
        var relative = resource.Root == HostResourceRoot.StagedImages ? "api/staged/" : "api/images/";
        using var request = new HttpRequestMessage(HttpMethod.Get, relative + resource.RelativePath);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        using var response = await _http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync();
        if (bytes.Length > resource.MaximumBytes) throw Problem("画像サイズが上限を超えています。");
        return bytes;
    }
    internal async Task RequireRole(bool admin = false, bool editor = false)
    {
        var result = await Execute("get_current_user", JsonSerializer.SerializeToElement(new { }));
        if (result is not JsonElement user) throw new AppProblemException(AppProblem.LoginRequired());
        var role = user.GetProperty("role").GetString();
        if (admin && role != UserRoles.Admin) throw new AppProblemException(AppProblem.AdminRequired());
        if (editor && role is not (UserRoles.Admin or UserRoles.Editor)) throw Problem("FAQ編集権限が必要です。");
    }
    public void Dispose() { _http.Dispose(); _sessionGate.Dispose(); }
    private static AppProblemException Problem(string message) => new(new AppProblem("SHARE-004", message, "共有先とネットワークを確認してください。保存結果が不明な場合はFAQや履歴も確認してください。"));
}

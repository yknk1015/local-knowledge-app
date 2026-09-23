using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using KnowledgeApp.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

internal static class HttpsCheck
{
    internal static async Task Run(ServerSessions sessions, string serverId, string published, string draft)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder(); names.AddDnsName("localhost"); request.CertificateExtensions.Add(names.Build());
        using var generatedCert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        using var cert = X509CertificateLoader.LoadPkcs12(generatedCert.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.UserKeySet);
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders(); builder.Services.AddRateLimiter(_ => { });
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, listener => listener.UseHttps(cert)));
        await using var app = builder.Build();
        SharedEndpoints.Map(app, sessions, "localhost", serverId);
        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single().Replace("127.0.0.1", "localhost");
            // Ephemeral fixture certificate only. Production SharedHttpClient never bypasses TLS validation.
            using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, actual, _, _) => actual?.Thumbprint == cert.Thumbprint, AllowAutoRedirect = false };
            using var http = new HttpClient(handler) { BaseAddress = new Uri(address) };
            var info = await http.GetFromJsonAsync<JsonElement>("/api/info");
            Require(info.GetProperty("serverId").GetString() == serverId, "server identity");
            using (var invalid = new HttpRequestMessage(HttpMethod.Get, "/api/info"))
            { invalid.Headers.Add("Origin", "https://untrusted.invalid"); using var result = await http.SendAsync(invalid); Require(result.StatusCode == HttpStatusCode.Forbidden, "browser origin denied"); }
            using (var invalid = new HttpRequestMessage(HttpMethod.Get, "/api/info"))
            { invalid.Headers.Host = "untrusted.invalid"; using var result = await http.SendAsync(invalid); Require(result.StatusCode == HttpStatusCode.Forbidden, "wrong host denied"); }
            using (var unauthorized = await http.PostAsJsonAsync("/api/command", Rpc("get_settings", new { })))
                Require(unauthorized.StatusCode == HttpStatusCode.Unauthorized, "missing token denied");
            using var sessionResponse = await http.PostAsync("/api/session", null);
            var token = (await sessionResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            Require((await Command("login", new { input = new { loginId = "viewer", password = "Synthetic-viewer!" } })).GetProperty("ok").GetBoolean(), "viewer login over TLS");
            Require((await Command("get_article", new { id = published })).GetProperty("ok").GetBoolean(), "published read over TLS");
            Require(!(await Command("get_article", new { id = draft })).GetProperty("ok").GetBoolean(), "draft denied over TLS");
            Require(!(await Command("save_storage_folder", new { input = new { purpose = "backup-export", path = "C:\\" } })).GetProperty("ok").GetBoolean(), "forged administrator command denied");
            using (var upload = await http.PostAsync("/api/files/backup", new ByteArrayContent([1, 2, 3])))
                Require(!upload.IsSuccessStatusCode, "viewer upload denied");
            using (var image = await http.GetAsync($"/api/images/{draft}/{Guid.NewGuid():D}.png"))
                Require(!image.IsSuccessStatusCode, "private image denied");
            Require((await Command("logout", new { })).GetProperty("ok").GetBoolean(), "logout over TLS");
            Require(!(await Command("get_article", new { id = published })).GetProperty("ok").GetBoolean(), "logged out read denied");
            Console.WriteLine("PASS: production HTTPS routes, certificate, identity, Origin/Host, token, role, upload/image and logout checks");
            async Task<JsonElement> Command(string command, object args)
            { using var result = await http.PostAsJsonAsync("/api/command", Rpc(command, args)); result.EnsureSuccessStatusCode(); return await result.Content.ReadFromJsonAsync<JsonElement>(); }
        }
        finally { await app.StopAsync(); }
    }
    private static object Rpc(string command, object args) => new { id = Guid.NewGuid().ToString("D"), command, args };
    private static void Require(bool condition, string message) { if (!condition) throw new Exception("HTTPS: " + message); }
}

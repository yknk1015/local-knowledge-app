using System.Text.Json;
using KnowledgeApp.Data;
namespace KnowledgeApp.Shared;
public static class SharedEndpoints
{
    public static void Map(WebApplication app, ServerSessions sessions, string hostName, string serverId)
    {
app.Use(async (context, next) =>
{
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers.XContentTypeOptions = "nosniff";
    if (!context.Request.IsHttps || context.Request.Headers.ContainsKey("Origin") ||
        !string.Equals(context.Request.Host.Host, hostName, StringComparison.OrdinalIgnoreCase))
    { context.Response.StatusCode = 403; return; }
    try { await next(context); }
    catch (AppProblemException exception)
    {
        context.Response.StatusCode = exception.Problem.Code == "AUTH-002" ? 401 : 400;
        await context.Response.WriteAsJsonAsync(new SharedResponse(false, null, exception.Problem));
    }
    catch
    {
        context.Response.StatusCode = 500;
        await context.Response.WriteAsJsonAsync(new SharedResponse(false, null, new AppProblem("SHARE-004", "サーバー処理を完了できませんでした。", "入力内容を保持して、接続とサーバーの状態を確認してください。")));
    }
});
app.UseRateLimiter();
app.MapGet("/api/info", () => new { protocolVersion = 1, serverId = serverId, appVersion = "0.7.1" });
app.MapPost("/api/session", () => new { token = sessions.Create() });
app.MapPost("/api/command", (HttpContext context, SharedRequest request) => sessions.Execute(Token(context), request, context.RequestAborted));
app.MapGet("/api/images/{articleId}/{file}", async (HttpContext context, string articleId, string file) =>
{
    var result = await sessions.ReadImage(Token(context), $"{articleId}/{file}", false);
    return Results.Bytes(result.Bytes, result.ContentType);
});
app.MapGet("/api/staged/{file}", async (HttpContext context, string file) =>
{
    var result = await sessions.ReadImage(Token(context), file, true);
    return Results.Bytes(result.Bytes, result.ContentType);
});
app.MapPost("/api/files/{kind}", async (HttpContext context, string kind) =>
{
    var size = context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
    if (size is not null && !size.IsReadOnly) size.MaxRequestBodySize = 10L * 1024 * 1024 * 1024;
    var id = await sessions.Upload(Token(context), kind, context.Request.Body, context.Request.ContentLength ?? -1, context.RequestAborted);
    return new { fileId = id };
});
app.MapGet("/api/files/{id}", (HttpContext context, string id) => Results.Stream(sessions.Download(Token(context), id), "application/octet-stream"));
app.MapGet("/api/codex/delegations/{id}", (HttpContext context, string id) => Results.Bytes(sessions.ReadDelegation(Token(context), id), "application/json"));
    }
static string Token(HttpContext context)
{
    var value = context.Request.Headers.Authorization.ToString();
    return value.StartsWith("Bearer ", StringComparison.Ordinal) ? value[7..] : "";
}
}

namespace KnowledgeApp.CSharp;

// No WebView or filesystem dependencies: the exact host boundary is tested independently.
public static class HostSecurityPolicy
{
    public const string ApplicationOrigin = "https://app.knowledge.local";
    public const string DocumentUrl = ApplicationOrigin + "/index.html";
    public const string ContentSecurityPolicy =
        "default-src 'none'; script-src 'self'; style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' https://knowledge-attachments.local https://knowledge-staged.local blob:; " +
        "font-src 'self'; connect-src 'none'; object-src 'none'; frame-src 'none'; " +
        "frame-ancestors 'none'; base-uri 'none'; form-action 'none'; worker-src 'none'";

    public static bool IsTrustedDocument(string? value) =>
        TryParseLocalUrl(value, out var uri, out var path) &&
        uri!.Host == "app.knowledge.local" && path == "/index.html";

    public static bool MayReceiveRequest(string? source, string? currentDocument, bool documentReady) =>
        documentReady && IsTrustedDocument(source) && IsTrustedDocument(currentDocument);

    public static bool MaySendResponse(
        string? source, string? currentDocument, bool documentReady,
        long requestGeneration, long currentGeneration) =>
        requestGeneration == currentGeneration &&
        MayReceiveRequest(source, currentDocument, documentReady);

    public static HostResource? ResolveResource(string? value, string? method, HostResourceContext context)
    {
        if (method is not ("GET" or "HEAD") ||
            !TryParseLocalUrl(value, out var uri, out var path))
        {
            return null;
        }
        if (uri!.Host == "app.knowledge.local")
        {
            if (path == "/index.html" && context == HostResourceContext.Document)
            {
                return new(HostResourceRoot.Ui, "index.html", "text/html; charset=utf-8", 1024 * 1024);
            }
            if (!path.StartsWith("/assets/", StringComparison.Ordinal)) return null;
            var name = path[8..];
            if (!IsSafeAssetName(name)) return null;
            var extension = Extension(name);
            var contentType = extension switch
            {
                "js" when context is HostResourceContext.Script or HostResourceContext.Other => "text/javascript; charset=utf-8",
                "css" when context is HostResourceContext.Stylesheet or HostResourceContext.Other => "text/css; charset=utf-8",
                "woff2" when context == HostResourceContext.Font => "font/woff2",
                "woff" when context == HostResourceContext.Font => "font/woff",
                _ when context == HostResourceContext.Image => ImageContentType(extension),
                _ => null
            };
            return contentType is null ? null : new(HostResourceRoot.Ui, $"assets/{name}", contentType, 16 * 1024 * 1024);
        }
        if (context != HostResourceContext.Image) return null;
        var parts = path[1..].Split('/');
        HostResourceRoot root;
        string filename;
        if (uri.Host == "knowledge-attachments.local" && parts.Length == 2 && IsUuid(parts[0]))
        {
            root = HostResourceRoot.Attachments;
            filename = parts[1];
        }
        else if (uri.Host == "knowledge-staged.local" && parts.Length == 1)
        {
            root = HostResourceRoot.StagedImages;
            filename = parts[0];
        }
        else return null;

        var dot = filename.LastIndexOf('.');
        if (dot < 0 || !IsUuid(filename[..dot])) return null;
        var imageExtension = filename[(dot + 1)..];
        if (imageExtension is not ("png" or "jpg" or "jpeg" or "webp" or "gif")) return null;
        var imageContentType = ImageContentType(imageExtension);
        return imageContentType is null ? null : new(root, path[1..], imageContentType, 10 * 1024 * 1024);
    }

    public static string BridgeInitializationScript(string reportJson) =>
        "if (window.top === window && location.origin === 'https://app.knowledge.local' " +
        "&& location.pathname === '/index.html' && location.search === '') { " +
        $"window.__KNOWLEDGE_CSHARP_MIGRATION__ = {reportJson}; " +
        "window.__KNOWLEDGE_CSHARP_BRIDGE__ = true; }";

    private static bool TryParseLocalUrl(string? value, out Uri? uri, out string path)
    {
        uri = null;
        path = string.Empty;
        if (string.IsNullOrEmpty(value) || value.Length > 8192 || value != value.Trim() ||
            value.Any(character => char.IsControl(character) || character == '\\') ||
            !Uri.TryCreate(value, UriKind.Absolute, out uri) ||
            uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0 || uri.Port != 443 ||
            uri.Query.Length != 0 ||
            uri.Host is not ("app.knowledge.local" or "knowledge-attachments.local" or "knowledge-staged.local"))
        {
            return false;
        }
        // Inspect the original path as well: System.Uri canonicalizes dot segments and escapes.
        var fragment = value.IndexOf('#');
        var documentPart = fragment < 0 ? value : value[..fragment];
        var schemeEnd = documentPart.IndexOf("://", StringComparison.Ordinal);
        var pathStart = schemeEnd < 0 ? -1 : documentPart.IndexOf('/', schemeEnd + 3);
        if (pathStart < 0) return false;
        path = documentPart[pathStart..];
        if (path.Contains('%') || path.Contains('?') || path.Any(char.IsWhiteSpace) ||
            path.Split('/').Skip(1).Any(part => part is "" or "." or "..") ||
            !string.Equals(path, uri.AbsolutePath, StringComparison.Ordinal))
        {
            return false;
        }
        return true;
    }

    private static bool IsSafeAssetName(string name) =>
        name.Length is > 0 and <= 200 && !name.StartsWith('.') && !name.Contains("..", StringComparison.Ordinal) &&
        name.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');

    private static bool IsUuid(string value) => Guid.TryParseExact(value, "D", out _);

    private static string Extension(string name) => name[(name.LastIndexOf('.') + 1)..];

    private static string? ImageContentType(string extension) => extension switch
    {
        "png" => "image/png",
        "jpg" or "jpeg" => "image/jpeg",
        "webp" => "image/webp",
        "gif" => "image/gif",
        "ico" => "image/x-icon",
        _ => null
    };
}

public enum HostResourceContext { Document, Script, Stylesheet, Image, Font, Other, Blocked }
public enum HostResourceRoot { Ui, Attachments, StagedImages }
public sealed record HostResource(HostResourceRoot Root, string RelativePath, string ContentType, int MaximumBytes);

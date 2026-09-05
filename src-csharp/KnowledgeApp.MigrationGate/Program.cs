using System.Text.Encodings.Web;
using System.Text.Json;
using KnowledgeApp.Migration;

var uiRoot = args.Length == 1
    ? Path.GetFullPath(args[0])
    : Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "dist"));

var report = MigrationGateReport.Create(uiRoot, webView2RuntimeAvailable: false);
var json = JsonSerializer.Serialize(report, new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    WriteIndented = true,
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
});

Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.WriteLine(json);

if (!report.ProductionUseAuthorized || report.AllFinalChecksPassed || report.ProductionDataOpened)
{
    Console.Error.WriteLine("小規模切替の承認と未実施試験の記録を確認してください。");
    return 2;
}

Console.Error.WriteLine("利用者承認によりC#を主系とします。会社環境等の未実施試験は未実施のまま記録しています。本番DBは開いていません。");
return 0;

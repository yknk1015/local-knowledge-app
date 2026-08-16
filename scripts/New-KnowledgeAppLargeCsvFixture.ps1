[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$BaseCsv,

    [Parameter(Mandatory = $true)]
    [string]$DestinationCsv,

    [ValidateRange(1, 50000)]
    [int]$RowCount = 10000,

    [switch]$Force
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$sourcePath = (Resolve-Path -LiteralPath $BaseCsv -ErrorAction Stop).Path
$destinationPath = [IO.Path]::GetFullPath($DestinationCsv)

if (-not $sourcePath.EndsWith(".knowledge-faq.csv", [StringComparison]::OrdinalIgnoreCase)) {
    throw "BaseCsvにはKnowledgeAppから書き出した .knowledge-faq.csv を指定してください。"
}
if (-not $destinationPath.EndsWith(".knowledge-faq.csv", [StringComparison]::OrdinalIgnoreCase)) {
    throw "DestinationCsvは .knowledge-faq.csv で終わる名前にしてください。"
}
if ($sourcePath.Equals($destinationPath, [StringComparison]::OrdinalIgnoreCase)) {
    throw "元CSVと出力先CSVは別のファイルにしてください。"
}
if ((Test-Path -LiteralPath $destinationPath) -and -not $Force) {
    throw "出力先が既に存在します。別名にするか、内容を確認して -Force を指定してください。"
}

$baseRows = @(Import-Csv -LiteralPath $sourcePath -Encoding UTF8)
if ($baseRows.Count -eq 0) {
    throw "元CSVにFAQ行がありません。Surface検証用FAQを1件作成してから書き出してください。"
}

$requiredHeaders = @(
    "形式バージョン", "FAQ管理ID", "分類管理ID", "分類パス", "タイトル", "概要", "回答本文",
    "回答本文ハッシュ", "状態", "重要度", "新着表示終了日", "更新表示終了日", "非表示",
    "作成者", "更新者", "作成日時", "更新日時"
)
$actualHeaders = @($baseRows[0].PSObject.Properties.Name)
if (($actualHeaders -join "`n") -ne ($requiredHeaders -join "`n")) {
    throw "元CSVの見出しまたは列順がKnowledgeApp形式第2版と一致しません。"
}

$template = $baseRows[0]
if ([string]::IsNullOrWhiteSpace($template."分類管理ID") -and [string]::IsNullOrWhiteSpace($template."分類パス")) {
    throw "元CSVの先頭行に分類管理IDまたは分類パスがありません。"
}

$rows = [System.Collections.Generic.List[object]]::new($RowCount)
for ($index = 1; $index -le $RowCount; $index++) {
    $number = $index.ToString("D5")
    $rows.Add([pscustomobject][ordered]@{
        "形式バージョン" = "2"
        "FAQ管理ID" = ""
        "分類管理ID" = $template."分類管理ID"
        "分類パス" = $template."分類パス"
        "タイトル" = "Surface性能確認FAQ $number"
        "概要" = "合成データによる画面応答確認用FAQです。"
        "回答本文" = "■確認方法`r`n1. Surface性能確認FAQ $number を表示します。"
        "回答本文ハッシュ" = ""
        "状態" = "下書き"
        "重要度" = "1"
        "新着表示終了日" = ""
        "更新表示終了日" = ""
        "非表示" = "FALSE"
        "作成者" = ""
        "更新者" = ""
        "作成日時" = ""
        "更新日時" = ""
    })
}

$csvLines = @($rows | ConvertTo-Csv -NoTypeInformation)
$csvText = ($csvLines -join "`r`n") + "`r`n"
$destinationDirectory = [IO.Path]::GetDirectoryName($destinationPath)
if (-not (Test-Path -LiteralPath $destinationDirectory -PathType Container)) {
    throw "出力先フォルダが存在しません: $destinationDirectory"
}
[IO.File]::WriteAllText($destinationPath, $csvText, [Text.UTF8Encoding]::new($true))

$written = Get-Item -LiteralPath $destinationPath
if ($written.Length -gt 50MB) {
    throw "生成CSVがKnowledgeAppの50MB上限を超えました。RowCountを減らしてください。"
}

Write-Host "作成しました: $destinationPath"
Write-Host "FAQ行数: $RowCount"
Write-Host "サイズ: $($written.Length) bytes"
Write-Warning "このCSVは合成試験専用です。実FAQのCSVと混在させず、Git、メール、正式配布物へ含めないでください。"

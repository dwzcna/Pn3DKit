#Requires -Version 7.2
# 只打包 NETLOAD 插件；所有校验通过后才发布本地 ZIP，不执行真实 CAD。
param(
    [switch]$NoBuild,
    [string]$Plant3DSdkRoot = 'E:\AutoCAD Plant 3D 2026 SDK',
    [string]$AutoCadManagedRoot,
    [string]$PlantManagedRoot,
    [string]$BuildOutputRoot,
    [string]$InputDirectory = (Join-Path $PSScriptRoot '../artifacts/plugins/Pn3DKit'),
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '../artifacts/release')
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'PluginChecks.ps1')
$publishRepo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (!$NoBuild) {
    & (Join-Path $PSScriptRoot 'Build-Pn3DKit.ps1') -Plant3DSdkRoot $Plant3DSdkRoot -AutoCadManagedRoot $AutoCadManagedRoot -PlantManagedRoot $PlantManagedRoot -BuildOutputRoot $BuildOutputRoot -OutputDirectory $InputDirectory | Out-Host
}
$publishVersionText = & dotnet msbuild (Join-Path $publishRepo 'Pn3DKit/Pn3DKit.csproj') -nologo '-getProperty:Version'
if ($LASTEXITCODE) { throw '无法读取发布版本。' }
$publishVersion = ($publishVersionText -join [Environment]::NewLine).Trim()
if ($publishVersion -notmatch '^\d+\.\d+\.\d+(?:\.\d+)?$') { throw '发布版本必须为三段或四段数字。' }
$publishInput = [IO.Path]::GetFullPath($InputDirectory)
Test-PluginDelivery $publishInput $publishVersion
$publishOutput = [IO.Path]::GetFullPath($OutputDirectory)
if ($publishInput.Equals($publishOutput, [StringComparison]::OrdinalIgnoreCase)) { throw '发布目录不能等于交付输入目录。' }
New-Item -ItemType Directory -Path $publishOutput -Force | Out-Null
$publishZip = Join-Path $publishOutput "Pn3DKit-$publishVersion-AutoCAD2026-x64.zip"
$publishTemporary = Join-Path $publishOutput ('.' + [Guid]::NewGuid().ToString('N') + '.zip')
[IO.Compression.ZipFile]::CreateFromDirectory($publishInput, $publishTemporary, [IO.Compression.CompressionLevel]::SmallestSize, $false)

# 回读 ZIP，检查根目录、仅三件载荷及内外 DLL 一致，再替换本地产物。
$publishArchive = [IO.Compression.ZipFile]::OpenRead($publishTemporary)
try {
    $publishNames = @($publishArchive.Entries.FullName | Sort-Object)
    if (($publishNames -join '|') -ne 'Pn3DKit.dll|README.md|SHA256SUMS') { throw 'ZIP 根目录或载荷不符。' }
    foreach ($publishFile in @('Pn3DKit.dll','README.md','SHA256SUMS')) {
        $publishEntry = $publishArchive.GetEntry($publishFile).Open()
        try { $publishEntryHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($publishEntry)) }
        finally { $publishEntry.Dispose() }
        if ($publishEntryHash -ne (Get-FileHash -LiteralPath (Join-Path $publishInput $publishFile) -Algorithm SHA256).Hash) {
            throw "ZIP 文件哈希不符：$publishFile"
        }
    }
} finally { $publishArchive.Dispose() }
[IO.File]::Move($publishTemporary, $publishZip, $true)
Copy-Item -LiteralPath (Join-Path $publishInput 'Pn3DKit.dll') -Destination (Join-Path $publishOutput 'Pn3DKit.dll') -Force
$publishHashes = foreach ($publishFile in @('Pn3DKit.dll', [IO.Path]::GetFileName($publishZip))) {
    $publishHash = (Get-FileHash -LiteralPath (Join-Path $publishOutput $publishFile) -Algorithm SHA256).Hash.ToLowerInvariant()
    "$publishHash  $publishFile"
}
$publishHashes | Set-Content -LiteralPath (Join-Path $publishOutput 'SHA256SUMS') -Encoding ascii
foreach ($publishLine in Get-Content -LiteralPath (Join-Path $publishOutput 'SHA256SUMS')) {
    $publishParts = $publishLine -split '  ',2
    if ((Get-FileHash -LiteralPath (Join-Path $publishOutput $publishParts[1]) -Algorithm SHA256).Hash -ne $publishParts[0]) {
        throw '发布清单复算失败。'
    }
}

# Release 说明只取本版本记录，不混入历史版本。
$publishChanges = Get-Content -LiteralPath (Join-Path $publishRepo 'Changlog.md') -Raw
$publishPattern = '(?ms)^## ' + [regex]::Escape($publishVersion) + '[^\r\n]*\r?\n(?<notes>.*?)(?=^## |\z)'
$publishMatch = [regex]::Match($publishChanges, $publishPattern)
if (!$publishMatch.Success) { throw '缺少当前版本变更记录。' }
$publishNotes = Join-Path $publishOutput 'release-notes.md'
($publishMatch.Groups['notes'].Value -split '(?m)^### 验证\r?$',2)[0].Trim() |
    Set-Content -LiteralPath $publishNotes -Encoding utf8
Write-Host "发布包校验通过：$publishZip"
[pscustomobject]@{
    Version = $publishVersion
    Dll = Join-Path $publishOutput 'Pn3DKit.dll'
    Zip = $publishZip
    Hashes = Join-Path $publishOutput 'SHA256SUMS'
    Notes = $publishNotes
}

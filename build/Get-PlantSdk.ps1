#Requires -Version 7.2
# 原版 SDK 只作为构建参考：校验下载、用 7-Zip 解压，不执行安装程序。
param(
    [string]$RootDirectory = (Join-Path $PSScriptRoot '../artifacts/sdk/Plant2026'),
    [string]$ArchivePath
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'PluginChecks.ps1')
$sdkDefinition = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'PlantSdk.json') -Raw | ConvertFrom-Json
$sdkCache = [IO.Path]::GetFullPath($RootDirectory)
New-Item -ItemType Directory -Path $sdkCache -Force | Out-Null
if (!$ArchivePath) {
    $ArchivePath = Join-Path $sdkCache 'Plant2026.exe'
    if (!(Test-Path -LiteralPath $ArchivePath)) {
        $sdkPartial = $ArchivePath + '.download'
        Write-Host '下载 Autodesk 原版 Plant 3D 2026 SDK…'
        Invoke-WebRequest -Uri $sdkDefinition.url -OutFile $sdkPartial -TimeoutSec 300 -MaximumRetryCount 3 -RetryIntervalSec 3
        if ((Get-FileHash -LiteralPath $sdkPartial -Algorithm SHA256).Hash -ne $sdkDefinition.sha256) {
            throw 'SDK 下载哈希不符，保留临时下载供检查，未执行或解压。'
        }
        Move-Item -LiteralPath $sdkPartial -Destination $ArchivePath -Force
    }
}
$ArchivePath = [IO.Path]::GetFullPath($ArchivePath)
if ((Get-FileHash -LiteralPath $ArchivePath -Algorithm SHA256).Hash -ne $sdkDefinition.sha256) {
    throw 'SDK 归档 SHA-256 不符，拒绝使用。'
}
$sdkRoot = Join-Path $sdkCache $sdkDefinition.archiveRoot
$sdkMarker = Join-Path $sdkCache 'verified.sha256'
if (!(Test-Path -LiteralPath $sdkMarker) -or
    (Get-Content -LiteralPath $sdkMarker -Raw).Trim() -ne $sdkDefinition.sha256 -or
    !(Test-Path -LiteralPath (Join-Path $sdkRoot 'inc-x64/PnPProjectManagerMgd.dll'))) {
    $sdkSevenZip = Get-Command 7z.exe -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty Source
    if (!$sdkSevenZip) { $sdkSevenZip = Join-Path $env:ProgramFiles '7-Zip/7z.exe' }
    if (!(Test-Path -LiteralPath $sdkSevenZip)) { throw '缺少 7-Zip，请安装后重试。' }
    & $sdkSevenZip x $ArchivePath "-o$sdkCache" -y | Out-Host
    if ($LASTEXITCODE) { throw 'SDK 解压失败。' }
}
Assert-PluginReferences (Join-Path $sdkRoot 'inc') (Join-Path $sdkRoot 'inc-x64')
$sdkDefinition.sha256 | Set-Content -LiteralPath $sdkMarker -Encoding ascii
[pscustomobject]@{
    Root = $sdkRoot
    AutoCadManagedRoot = Join-Path $sdkRoot 'inc'
    PlantManagedRoot = Join-Path $sdkRoot 'inc-x64'
}

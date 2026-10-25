#Requires -Version 7.2
# 插件由 CAD 的 .NET 8 宿主加载；构建只读取参考，不启动 CAD 或操作图纸。
param(
    [string]$Plant3DSdkRoot = 'E:\AutoCAD Plant 3D 2026 SDK',
    [string]$AutoCadManagedRoot,
    [string]$PlantManagedRoot,
    [string]$BuildOutputRoot,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '../artifacts/plugins/Pn3DKit')
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'PluginChecks.ps1')
$pluginRepo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$pluginProject = Join-Path $pluginRepo 'Pn3DKit/Pn3DKit.csproj'
$pluginProperties = @('-p:Platform=x64','-p:NuGetAudit=false',"-p:Plant3DSdkRoot=$Plant3DSdkRoot")
if ($AutoCadManagedRoot) { $pluginProperties += "-p:AutoCadManagedRoot=$AutoCadManagedRoot" }
if ($PlantManagedRoot) { $pluginProperties += "-p:PlantManagedRoot=$PlantManagedRoot" }
if ($BuildOutputRoot) {
    # NETLOAD 已加载的 DLL 可能锁定默认目录；独立输出保留用户的 CAD 会话。
    $pluginIsolatedOutput = [IO.Path]::GetFullPath($BuildOutputRoot).TrimEnd([char[]]"\/") + [IO.Path]::DirectorySeparatorChar
    $pluginProperties += "-p:BaseOutputPath=$pluginIsolatedOutput"
}
$pluginMetadataText = & dotnet msbuild $pluginProject -nologo @pluginProperties '-getProperty:AutoCadManagedRoot,PlantManagedRoot,Version'
if ($LASTEXITCODE) { throw '无法读取 Pn3DKit 构建属性。' }
$pluginMetadata = ($pluginMetadataText -join [Environment]::NewLine | ConvertFrom-Json).Properties
Assert-PluginReferences $pluginMetadata.AutoCadManagedRoot $pluginMetadata.PlantManagedRoot

# 两配置串行增量构建，避免同一目标目录竞争，不清理用户已有输出。
foreach ($pluginConfiguration in @('Debug','Release')) {
    & dotnet build $pluginProject -c $pluginConfiguration @pluginProperties
    if ($LASTEXITCODE) { throw "Pn3DKit 构建失败：$pluginConfiguration" }
}
$pluginTargetText = & dotnet msbuild $pluginProject -nologo @pluginProperties '-p:Configuration=Release' '-getProperty:TargetPath'
if ($LASTEXITCODE) { throw '无法读取插件输出路径。' }
$pluginTarget = ($pluginTargetText -join [Environment]::NewLine).Trim()
$null = Test-PluginAssembly $pluginTarget $pluginMetadata.Version
$pluginUnexpected = @(Get-ChildItem -LiteralPath (Split-Path -Parent $pluginTarget) -Filter '*.dll' -File |
    Where-Object Name -ne 'Pn3DKit.dll')
if ($pluginUnexpected.Count) { throw '构建目录包含意外依赖 DLL，请检查 Private 设置。' }

$pluginDelivery = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $pluginDelivery -Force | Out-Null
Copy-Item -LiteralPath $pluginTarget -Destination (Join-Path $pluginDelivery 'Pn3DKit.dll') -Force
Copy-Item -LiteralPath (Join-Path $pluginRepo 'Pn3DKit/README.md') -Destination (Join-Path $pluginDelivery 'README.md') -Force
$pluginHashes = foreach ($pluginFile in @('Pn3DKit.dll','README.md')) {
    $pluginHash = (Get-FileHash -LiteralPath (Join-Path $pluginDelivery $pluginFile) -Algorithm SHA256).Hash.ToLowerInvariant()
    "$pluginHash  $pluginFile"
}
$pluginHashes | Set-Content -LiteralPath (Join-Path $pluginDelivery 'SHA256SUMS') -Encoding ascii
Test-PluginDelivery $pluginDelivery $pluginMetadata.Version
Write-Host "交付完成：$pluginDelivery；$($pluginMetadata.Version)，.NET 8，x64。"

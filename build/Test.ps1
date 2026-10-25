#Requires -Version 7.2
# 两配置结果独立保存；只运行离线宿主，不需要 CAD 或专有 SDK。
param(
    [ValidateSet('Debug','Release')][string[]]$Configurations = @('Debug','Release'),
    [string]$ResultsDirectory = (Join-Path $PSScriptRoot '../artifacts/TestResults')
)
$ErrorActionPreference = 'Stop'
$testRepo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$testProject = Join-Path $testRepo 'Pn3DKit.Tests/Pn3DKit.Tests.csproj'
foreach ($testConfiguration in $Configurations) {
    $testResults = Join-Path $ResultsDirectory $testConfiguration
    & dotnet test $testProject -c $testConfiguration -p:Platform=x64 -p:NuGetAudit=false --logger "trx;LogFileName=Pn3DKit.$testConfiguration.trx" --results-directory $testResults
    if ($LASTEXITCODE) { throw "Pn3DKit 离线测试失败：$testConfiguration" }
}

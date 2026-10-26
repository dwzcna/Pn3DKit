#Requires -Version 7.2
# 仅读取 PE 和托管元数据，不在 PowerShell 中加载或执行 Autodesk 程序集。
function Get-PluginMetadata([string]$Path, [switch]$IncludeCommands) {
    $metadataStream = [IO.File]::OpenRead($Path)
    $metadataPe = [System.Reflection.PortableExecutable.PEReader]::new($metadataStream)
    try {
        $metadataReader = [System.Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($metadataPe)
        $metadataDefinition = $metadataReader.GetAssemblyDefinition()
        $metadataReferences = @{}
        foreach ($metadataHandle in $metadataReader.AssemblyReferences) {
            $metadataReference = $metadataReader.GetAssemblyReference($metadataHandle)
            $metadataReferences[$metadataReader.GetString($metadataReference.Name)] = $metadataReference.Version
        }
        $metadataFramework = $null
        $metadataExtension = $false
        $metadataCommandClass = $false
        foreach ($metadataHandle in $metadataDefinition.GetCustomAttributes()) {
            $metadataAttribute = $metadataReader.GetCustomAttribute($metadataHandle)
            if ($metadataAttribute.Constructor.Kind -ne [System.Reflection.Metadata.HandleKind]::MemberReference) { continue }
            $metadataConstructor = $metadataReader.GetMemberReference($metadataAttribute.Constructor)
            if ($metadataConstructor.Parent.Kind -ne [System.Reflection.Metadata.HandleKind]::TypeReference) { continue }
            $metadataType = $metadataReader.GetTypeReference($metadataConstructor.Parent)
            switch ($metadataReader.GetString($metadataType.Name)) {
                'TargetFrameworkAttribute' {
                    $metadataBlob = $metadataReader.GetBlobReader($metadataAttribute.Value)
                    if ($metadataBlob.ReadUInt16() -ne 1) { throw "目标框架属性无效：$Path" }
                    $metadataFramework = $metadataBlob.ReadSerializedString()
                }
                'ExtensionApplicationAttribute' { $metadataExtension = $true }
                'CommandClassAttribute' { $metadataCommandClass = $true }
            }
        }
        $metadataCommands = @()
        if ($IncludeCommands) {
            foreach ($metadataHandle in $metadataReader.TypeDefinitions) {
                $metadataType = $metadataReader.GetTypeDefinition($metadataHandle)
                foreach ($metadataMethodHandle in $metadataType.GetMethods()) {
                    $metadataMethod = $metadataReader.GetMethodDefinition($metadataMethodHandle)
                    foreach ($metadataAttributeHandle in $metadataMethod.GetCustomAttributes()) {
                        $metadataAttribute = $metadataReader.GetCustomAttribute($metadataAttributeHandle)
                        if ($metadataAttribute.Constructor.Kind -ne [System.Reflection.Metadata.HandleKind]::MemberReference) { continue }
                        $metadataConstructor = $metadataReader.GetMemberReference($metadataAttribute.Constructor)
                        if ($metadataConstructor.Parent.Kind -ne [System.Reflection.Metadata.HandleKind]::TypeReference) { continue }
                        $metadataAttributeType = $metadataReader.GetTypeReference($metadataConstructor.Parent)
                        if ($metadataReader.GetString($metadataAttributeType.Name) -ne 'CommandMethodAttribute') { continue }
                        $metadataBlob = $metadataReader.GetBlobReader($metadataAttribute.Value)
                        if ($metadataBlob.ReadUInt16() -ne 1) { throw "命令属性无效：$Path" }
                        $metadataCommands += $metadataBlob.ReadSerializedString()
                    }
                }
            }
        }
        [pscustomobject]@{
            Path = $Path
            Version = $metadataDefinition.Version.ToString()
            Framework = $metadataFramework
            Architecture = $metadataPe.PEHeaders.CoffHeader.Machine.ToString()
            References = $metadataReferences
            ExtensionApplication = $metadataExtension
            CommandClass = $metadataCommandClass
            Commands = $metadataCommands
        }
    } finally { $metadataPe.Dispose(); $metadataStream.Dispose() }
}

# 核对原版 .NET 8 参考，明确拒绝误用面向更高运行时的 SDK。
function Assert-PluginReferences([string]$AutoCadManagedRoot, [string]$PlantManagedRoot, [string]$PlantCommonManagedRoot) {
    if (!$PlantCommonManagedRoot) { $PlantCommonManagedRoot = $PlantManagedRoot }
    $referencePaths = @('AcCoreMgd.dll','AcDbMgd.dll','AcDbMgdBrep.dll','AcMgd.dll','AdWindows.dll','AcWindows.dll') |
        ForEach-Object { Join-Path $AutoCadManagedRoot $_ }
    $referencePaths += Join-Path $PlantManagedRoot 'PnPProjectManagerMgd.dll'
    $referencePaths += @('PnPDataLinks.dll','PnPDataObjects.dll','PnPCommonDbxMgd.dll') |
        ForEach-Object { Join-Path $PlantCommonManagedRoot $_ }
    foreach ($referencePath in $referencePaths) {
        $referenceMetadata = Get-PluginMetadata $referencePath
        foreach ($referenceFramework in @('System.Runtime','PresentationFramework','PresentationCore','WindowsBase')) {
            if ($referenceMetadata.References.ContainsKey($referenceFramework) -and
                $referenceMetadata.References[$referenceFramework].Major -gt 8) {
                throw "参考程序集要求高于 .NET 8：$referencePath"
            }
        }
        Write-Host "参考校验：$referencePath"
    }
}

# 验证真正交付的插件版本、宿主框架、架构以及公开加载／命令注册。
function Test-PluginAssembly([string]$Path, [string]$Version) {
    $assemblyMetadata = Get-PluginMetadata $Path -IncludeCommands
    $assemblyFileVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($Path)
    $expectedAssemblyVersion = if (($Version -split '\.').Count -eq 3) { "$Version.0" } else { $Version }
    if ($assemblyMetadata.Version -ne $expectedAssemblyVersion -or
        $assemblyFileVersion.FileVersion -ne $expectedAssemblyVersion -or $assemblyFileVersion.ProductVersion -ne $Version) {
        throw '插件程序集、文件或产品版本不符。'
    }
    if ($assemblyMetadata.Architecture -ne 'Amd64' -or
        $assemblyMetadata.Framework -ne '.NETCoreApp,Version=v8.0' -or
        $assemblyMetadata.References['System.Runtime'].Major -ne 8) {
        throw '插件必须为 AMD64 / .NET 8。'
    }
    if (!$assemblyMetadata.ExtensionApplication -or !$assemblyMetadata.CommandClass -or
        $assemblyMetadata.Commands.Count -ne 10 -or
        (@($assemblyMetadata.Commands | Sort-Object) -join '|') -cne 'CHECKPNTAG|CHECKPNTAGBYCLASS|CHECKPROJECTPNTAGBYCLASS|EVALUATEPNTAG|GETPNTAGFORMAT|PNDWGEXPORT|PNFASTPANEL|REBUILDPNTAGBYCLASS|REBUILDPROJECTPNTAGBYCLASS|XREFBLOCKCHECK') {
        throw '插件加载入口或公开命令注册不符。'
    }
    return $assemblyMetadata
}

# 交付目录只含插件、说明及校验文件；拒绝意外 SDK、运行时、符号或测试载荷。
function Test-PluginDelivery([string]$Directory, [string]$Version) {
    $deliveryNames = @(Get-ChildItem -LiteralPath $Directory -Force | Sort-Object Name | Select-Object -ExpandProperty Name)
    if (($deliveryNames -join '|') -ne 'Pn3DKit.dll|README.md|SHA256SUMS') { throw '插件交付目录载荷不符。' }
    $null = Test-PluginAssembly (Join-Path $Directory 'Pn3DKit.dll') $Version
    $deliveryLines = @(Get-Content -LiteralPath (Join-Path $Directory 'SHA256SUMS'))
    $deliverySeen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($deliveryLine in $deliveryLines) {
        if ($deliveryLine -notmatch '^([a-f0-9]{64})  (Pn3DKit\.dll|README\.md)$') { throw '插件校验清单格式不符。' }
        $deliveryExpected = $Matches[1]
        $deliveryName = $Matches[2]
        if (!$deliverySeen.Add($deliveryName) -or
            (Get-FileHash -LiteralPath (Join-Path $Directory $deliveryName) -Algorithm SHA256).Hash -ne $deliveryExpected) {
            throw "插件校验失败：$deliveryName"
        }
    }
    if ($deliverySeen.Count -ne 2) { throw '插件校验清单不完整。' }
}

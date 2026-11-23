[CmdletBinding()]
param(
    [string] $IdentityName,
    [string] $Publisher,
    [string] $PublisherDisplayName,
    [string] $DisplayName = 'GPOCasebook',
    [switch] $ValidationOnly,
    [string] $AppExecutable,
    [string] $Output = (Join-Path (Split-Path -Parent $PSScriptRoot) 'build\store')
)

$ErrorActionPreference = 'Stop'
if ($ValidationOnly) {
    if ($IdentityName -or $Publisher -or $PublisherDisplayName) {
        throw 'Use the Partner Center identity values when building the submission package.'
    }
} else {
    if ($IdentityName -cnotmatch '^[a-zA-Z0-9.-]{3,50}$' -or
        [string]::IsNullOrWhiteSpace($Publisher) -or [string]::IsNullOrWhiteSpace($PublisherDisplayName)) {
        throw 'Enter the package Name, Publisher and PublisherDisplayName from Partner Center.'
    }
    $null = [Security.Cryptography.X509Certificates.X500DistinguishedName]::new($Publisher)
}
if ([string]::IsNullOrWhiteSpace($DisplayName)) { throw 'Enter the reserved application display name.' }

$storeRoot = Split-Path -Parent $PSScriptRoot
$storeOutput = [IO.Path]::GetFullPath($Output)
$storeStage = Join-Path $storeOutput ('work-' + [Guid]::NewGuid().ToString('N'))
$storePayload = Join-Path $storeStage 'payload'
$storeAssets = Join-Path $storePayload 'Assets'
[IO.Directory]::CreateDirectory($storeAssets) | Out-Null

if (-not $AppExecutable) {
    $storeAppBuild = Join-Path $storeStage 'app'
    & (Join-Path $storeRoot 'Build.ps1') -AppPlatform x64 -Output $storeAppBuild | Out-Null
    $AppExecutable = Join-Path $storeAppBuild 'GPOCasebook.exe'
}
$storeApp = (Resolve-Path -LiteralPath $AppExecutable).ProviderPath
$storeReader = [IO.BinaryReader]::new([IO.File]::OpenRead($storeApp))
try {
    $storeReader.BaseStream.Position = 0x3c
    $storeReader.BaseStream.Position = $storeReader.ReadInt32()
    if ($storeReader.ReadUInt32() -ne 0x4550 -or $storeReader.ReadUInt16() -ne 0x8664) {
        throw 'The Store package requires the x64 GPOCasebook executable.'
    }
} finally { $storeReader.Dispose() }
$storeAssembly = [Reflection.AssemblyName]::GetAssemblyName($storeApp)
if ($storeAssembly.Name -ne 'GPOCasebook' -or $storeAssembly.Version.Revision -ne 0) {
    throw 'The executable must be a GPOCasebook release with a zero Store revision.'
}
$storeVersion = $storeAssembly.Version.ToString(4)
[IO.File]::Copy($storeApp, (Join-Path $storePayload 'GPOCasebook.exe'))
[IO.File]::Copy((Join-Path $storeRoot 'LICENSE'), (Join-Path $storePayload 'LICENSE.txt'))

[xml]$storeManifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'AppxManifest.xml') -Raw
$storeManifest.Package.Identity.Version = $storeVersion
$storeManifest.Package.Properties.DisplayName = $DisplayName
$storeManifest.Package.Applications.Application.VisualElements.DisplayName = $DisplayName
if (-not $ValidationOnly) {
    $storeManifest.Package.Identity.Name = $IdentityName
    $storeManifest.Package.Identity.Publisher = $Publisher
    $storeManifest.Package.Properties.PublisherDisplayName = $PublisherDisplayName
}
if ($ValidationOnly) {
    $storeManifest.Package.Properties.DisplayName = 'GPOCasebook Validation'
    $storeManifest.Package.Applications.Application.VisualElements.DisplayName = 'GPOCasebook Validation'
}
$storeManifest.Save((Join-Path $storePayload 'AppxManifest.xml'))

$brandAssets = Join-Path $storeRoot 'assets'
foreach ($asset in @('StoreLogo.png', 'Square44x44Logo.png', 'Square150x150Logo.png')) {
    [IO.File]::Copy((Join-Path $brandAssets $asset), (Join-Path $storeAssets $asset))
}

$storeCompiler = Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$storePacker = Join-Path $storeStage 'PackMsix.exe'
& $storeCompiler /nologo /target:exe /optimize+ "/out:$storePacker" (Join-Path $PSScriptRoot 'PackMsix.cs')
if ($LASTEXITCODE -ne 0) { throw 'The Windows package writer could not be built.' }
$storeLabel = if ($ValidationOnly) { 'Validation' } else { 'Store' }
$storePackage = Join-Path $storeOutput "GPOCasebook-$storeLabel-$storeVersion-x64.msix"
& $storePacker $storePayload $storePackage
if ($LASTEXITCODE -ne 0) { throw 'MSIX packaging or validation failed.' }
Write-Output "MSIX file $storePackage"
Write-Output "SHA-256 $((Get-FileHash -LiteralPath $storePackage -Algorithm SHA256).Hash)"

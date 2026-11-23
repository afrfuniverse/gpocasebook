[CmdletBinding()]
param(
    [switch] $Test,
    [ValidateSet('anycpu', 'x64')]
    [string] $AppPlatform = 'anycpu',
    [string] $Output = (Join-Path $PSScriptRoot 'build')
)

$ErrorActionPreference = 'Stop'
$gpoDotnet = (Get-Command dotnet.exe -ErrorAction Stop).Source
$gpoSdk = & $gpoDotnet --list-sdks | Select-Object -Last 1
if ($LASTEXITCODE -ne 0 -or $gpoSdk -notmatch '^(\S+)\s+\[(.+)\]$') {
    throw 'Install the .NET SDK to build GPOCasebook.'
}
$gpoCompiler = Join-Path (Join-Path $Matches[2] $Matches[1]) 'Roslyn\bincore\csc.dll'
$gpoFramework = Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319'
if (-not (Test-Path -LiteralPath $gpoFramework)) {
    $gpoFramework = Join-Path $env:SystemRoot 'Microsoft.NET\Framework\v4.0.30319'
}
$gpoReferences = @(
    'mscorlib.dll', 'System.dll', 'System.Core.dll', 'System.Xml.dll', 'System.Xml.Linq.dll',
    'System.Runtime.Serialization.dll', 'System.DirectoryServices.dll'
) | ForEach-Object { "/reference:$(Join-Path $gpoFramework $_)" }
$gpoOutput = [IO.Path]::GetFullPath($Output)
[IO.Directory]::CreateDirectory($gpoOutput) | Out-Null

function Invoke-GpoCompiler([string[]] $Options) {
    & $gpoDotnet $gpoCompiler /nologo /noconfig /nostdlib+ /optimize+ /deterministic+ /codepage:65001 /langversion:7.3 @gpoReferences @Options
    if ($LASTEXITCODE -ne 0) { throw "C# compilation failed with exit code $LASTEXITCODE." }
}

$gpoAppSources = @('AssemblyInfo.cs', 'Evidence.cs', 'Capture.cs', 'Reports.cs', 'PackageIdentity.cs',
    'DomainScan.cs', 'DomainWindow.cs', 'Workbench.cs') | ForEach-Object { Join-Path $PSScriptRoot "src\$_" }
$gpoIcon = Join-Path $PSScriptRoot 'assets\GPOCasebook.ico'
$gpoAppResources = @(
    "/resource:$(Join-Path $PSScriptRoot 'assets\GPOCasebook.png'),GPOCasebook.png",
    "/resource:$(Join-Path $PSScriptRoot 'src\Workbench.xaml'),Workbench.xaml",
    "/resource:$(Join-Path $PSScriptRoot 'src\DomainWindow.xaml'),DomainWindow.xaml",
    "/resource:$(Join-Path $PSScriptRoot 'docs\Help.txt'),Help.txt",
    "/resource:$(Join-Path $PSScriptRoot 'LICENSE'),LICENSE.txt"
)
$gpoWpf = @('System.Xaml.dll', 'WPF\WindowsBase.dll', 'WPF\PresentationCore.dll', 'WPF\PresentationFramework.dll') |
    ForEach-Object { "/reference:$(Join-Path $gpoFramework $_)" }
$gpoApp = Join-Path $gpoOutput 'GPOCasebook.exe'
$gpoSetup = Join-Path $gpoOutput 'GPOCasebook-Setup.exe'
Invoke-GpoCompiler (@('/target:winexe', "/platform:$AppPlatform", "/out:$gpoApp", "/win32icon:$gpoIcon") + $gpoAppSources + $gpoWpf + $gpoAppResources)

$gpoSetupReferences = @('System.Drawing.dll', 'System.Windows.Forms.dll', 'Microsoft.CSharp.dll') |
    ForEach-Object { "/reference:$(Join-Path $gpoFramework $_)" }
$gpoSetupArgs = @(
    '/target:winexe', "/out:$gpoSetup", "/win32icon:$gpoIcon", "/resource:$gpoApp,GPOCasebook.exe",
    "/resource:$(Join-Path $PSScriptRoot 'LICENSE'),LICENSE.txt",
    (Join-Path $PSScriptRoot 'src\AssemblyInfo.cs'),
    (Join-Path $PSScriptRoot 'setup\Setup.cs')
)
Invoke-GpoCompiler ($gpoSetupArgs + $gpoSetupReferences)

if ($Test) {
    $gpoTests = Join-Path $gpoOutput 'GPOCasebook.Tests.exe'
    $gpoSamples = @('before', 'after') | ForEach-Object {
        "/resource:$(Join-Path $PSScriptRoot "examples\demo-$_.xml"),demo-$_.xml"
    }
    $gpoTestSources = @('Tests.cs', 'Examples.cs') | ForEach-Object { Join-Path $PSScriptRoot "tests\$_" }
    Invoke-GpoCompiler (@('/target:exe', '/main:GPOCasebook.Tests', "/out:$gpoTests") +
        $gpoTestSources + $gpoAppSources + $gpoWpf + $gpoAppResources + $gpoSamples)
    & $gpoTests
    if ($LASTEXITCODE -ne 0) { throw 'The regression tests failed.' }
    $gpoVerify = Join-Path $gpoOutput ('setup-verify-' + [Guid]::NewGuid().ToString('N') + '.txt')
    $gpoProcess = Start-Process -FilePath $gpoSetup -ArgumentList @('--verify', ('"{0}"' -f $gpoVerify)) -PassThru -Wait -WindowStyle Hidden
    if (Test-Path -LiteralPath $gpoVerify) {
        Get-Content -LiteralPath $gpoVerify
        Remove-Item -LiteralPath $gpoVerify
    }
    if ($gpoProcess.ExitCode -ne 0) { throw 'The setup verification failed.' }
}

Get-Item -LiteralPath $gpoApp, $gpoSetup | Select-Object FullName, Length

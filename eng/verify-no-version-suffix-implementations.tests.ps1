param(
    [string] $VerifierPath = "eng/verify-no-version-suffix-implementations.ps1"
)

$ErrorActionPreference = "Stop"

$RepoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$RepoRootPrefix = $RepoRoot.TrimEnd(
    [System.IO.Path]::DirectorySeparatorChar,
    [System.IO.Path]::AltDirectorySeparatorChar) +
    [System.IO.Path]::DirectorySeparatorChar
$resolvedVerifierPath = if ([System.IO.Path]::IsPathRooted($VerifierPath)) {
    [System.IO.Path]::GetFullPath($VerifierPath)
}
else {
    [System.IO.Path]::GetFullPath((Join-Path $RepoRoot $VerifierPath))
}
if (-not (Test-Path -LiteralPath $resolvedVerifierPath -PathType Leaf)) {
    throw "VerifierPath does not exist: $resolvedVerifierPath"
}

$testRoot = [System.IO.Path]::GetFullPath((Join-Path $RepoRoot (
    "output/no-version-suffix-verifier-tests-" + [Guid]::NewGuid().ToString("N"))))
if (-not $testRoot.StartsWith($RepoRootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Version-suffix verifier test root must stay under the repository output directory."
}

function New-FixtureRoot {
    param([Parameter(Mandatory = $true)][string] $Name)

    $root = Join-Path $testRoot $Name
    New-Item -ItemType Directory -Path (Join-Path $root "modules/OpenLineOps.Sample") -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $root "eng") -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $root "tests/OpenLineOps.Agent.Tests") -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $root "modules/OpenLineOps.Sample/CurrentContract.cs") -Value @'
namespace OpenLineOps.Sample;

public static class CurrentContract
{
    public const string PackageVersion = "1.0.0";
    public const int CurrentFormatVersion = 1;
}
'@
    Set-Content -LiteralPath (Join-Path $root "eng/external-tooling.ps1") -Value @'
$action = "actions/checkout@v4"
$gitMode = "--porcelain=v1"
'@
    Set-Content -LiteralPath (Join-Path $root "tests/OpenLineOps.Agent.Tests/LeastPrivilegeLauncherContractTests.cs") -Value @'
const string PowerShellPath = "WindowsPowerShell" + "v1.0" + "powershell.exe";
'@

    return $root
}

function Invoke-VerifierExpectSuccess {
    param([Parameter(Mandatory = $true)][string] $FixtureRoot)

    & $resolvedVerifierPath -RepositoryRoot $FixtureRoot | Out-Null
}

function Invoke-Mutation {
    param(
        [Parameter(Mandatory = $true)][string] $Name,
        [Parameter(Mandatory = $true)][string] $RelativePath,
        [Parameter(Mandatory = $true)][string] $Content,
        [Parameter(Mandatory = $true)][string] $ExpectedFailure
    )

    $fixtureRoot = New-FixtureRoot $Name
    $path = Join-Path $fixtureRoot $RelativePath
    New-Item -ItemType Directory -Path (Split-Path -Parent $path) -Force | Out-Null
    Set-Content -LiteralPath $path -Value $Content

    $failure = $null
    try {
        & $resolvedVerifierPath -RepositoryRoot $fixtureRoot 2>&1 | Out-Null
    }
    catch {
        $failure = $_.Exception.Message
    }

    if ([string]::IsNullOrWhiteSpace($failure)) {
        throw "Mutation '$Name' unexpectedly passed."
    }
    if ($failure -notmatch $ExpectedFailure) {
        throw "Mutation '$Name' failed for the wrong reason: $failure"
    }

    Write-Host "Mutation rejected: $Name"
}

try {
    Invoke-VerifierExpectSuccess (New-FixtureRoot "valid-current-contract")

    Invoke-Mutation `
        -Name "implementation identifier" `
        -RelativePath "modules/OpenLineOps.Sample/LegacyName.cs" `
        -Content ("public sealed class FlowIr" + "V2 {}") `
        -ExpectedFailure "Version-suffixed implementation identifier"
    Invoke-Mutation `
        -Name "embedded implementation identifier" `
        -RelativePath "tests/OpenLineOps.Sample.Tests/ApiMetadataTests.cs" `
        -Content ("public void ControllersUseBoundedContext" + "V2ApiExplorerGroups() {}") `
        -ExpectedFailure "Version-suffixed implementation identifier"
    Invoke-Mutation `
        -Name "implementation filename" `
        -RelativePath ("modules/OpenLineOps.Sample/Contract" + "V3.cs") `
        -Content "public sealed class Contract {}" `
        -ExpectedFailure "Version-suffixed implementation filename"
    Invoke-Mutation `
        -Name "versioned implementation directory" `
        -RelativePath ("modules/OpenLineOps.Sample/Contracts/" + "V9/Contract.cs") `
        -Content "public sealed class Contract {}" `
        -ExpectedFailure "Version-suffixed implementation path segment"
    Invoke-Mutation `
        -Name "default version literal" `
        -RelativePath "shared/OpenLineOps.Sample/VersionedDefault.cs" `
        -Content 'public sealed class VersionedDefault(string version = "v4");' `
        -ExpectedFailure "Forbidden internal version literal"
    Invoke-Mutation `
        -Name "versioned topic literal" `
        -RelativePath "modules/OpenLineOps.Sample/StationTopic.cs" `
        -Content 'public const string Topic = "openlineops.station.jobs.v5";' `
        -ExpectedFailure "Forbidden internal version literal"
    Invoke-Mutation `
        -Name "versioned signature payload" `
        -RelativePath "modules/OpenLineOps.Sample/SignaturePayload.cs" `
        -Content 'public const string PayloadIdentity = "OpenLineOps.PluginPackageSignature.v6";' `
        -ExpectedFailure "Forbidden internal version literal"
    Invoke-Mutation `
        -Name "versioned generated template" `
        -RelativePath "tools/OpenLineOps.Sample/TemplateCatalog.cs" `
        -Content 'private const string Template = "quality.inspection.sample.v7";' `
        -ExpectedFailure "Forbidden internal version literal"
    Invoke-Mutation `
        -Name "versioned test fixture literal" `
        -RelativePath "tests/OpenLineOps.Sample.Tests/FixtureTests.cs" `
        -Content 'private const string SnapshotId = "snapshot.main.v9";' `
        -ExpectedFailure "Forbidden internal version literal"
    Invoke-Mutation `
        -Name "versioned pre-release route" `
        -RelativePath "eng/versioned-route-fixture.ps1" `
        -Content ('public const string Route = "/api/' + 'v8/items";') `
        -ExpectedFailure "Versioned pre-release route"
}
finally {
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}

Write-Host "Version-suffix verifier mutation tests passed."
exit 0

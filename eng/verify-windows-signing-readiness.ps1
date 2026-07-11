param(
    [string] $WorkRoot = "output/windows-signing-readiness",

    [switch] $SkipClean
)

$ErrorActionPreference = "Stop"

$RepoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))

function Resolve-RepoPath {
    param([Parameter(Mandatory = $true)][string] $Path)

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }

    return [System.IO.Path]::GetFullPath((Join-Path $RepoRoot $Path))
}

function Assert-UnderRepoRoot {
    param([Parameter(Mandatory = $true)][string] $Path)

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $normalizedRoot = $RepoRoot.TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
    $rootPrefix = $normalizedRoot + [System.IO.Path]::DirectorySeparatorChar

    if (-not $fullPath.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to write outside the repository root: $fullPath"
    }
}

function New-CleanDirectory {
    param([Parameter(Mandatory = $true)][string] $Path)

    Assert-UnderRepoRoot $Path
    if ((Test-Path -LiteralPath $Path) -and -not $SkipClean) {
        Remove-Item -LiteralPath $Path -Recurse -Force
    }

    New-Item -ItemType Directory -Path $Path -Force | Out-Null
}

function Invoke-SigningScript {
    param([Parameter(Mandatory = $true)][string[]] $Arguments)

    $previousErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $output = & powershell -NoProfile -ExecutionPolicy Bypass -File $SigningScript @Arguments 2>&1 | Out-String
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }

    return [pscustomobject]@{
        ExitCode = $exitCode
        Output = $output
    }
}

function Invoke-ExpectedFailure {
    param(
        [Parameter(Mandatory = $true)][string] $Description,
        [Parameter(Mandatory = $true)][string[]] $Arguments,
        [Parameter(Mandatory = $true)][string] $ExpectedPattern
    )

    $result = Invoke-SigningScript -Arguments $Arguments
    if ($result.ExitCode -eq 0) {
        throw "Expected signing readiness failure for $Description, but the command succeeded."
    }

    if ($result.Output -notmatch $ExpectedPattern) {
        throw "Expected failure for $Description to match '$ExpectedPattern', but got: $($result.Output)"
    }
}

function Assert-OutputContains {
    param(
        [Parameter(Mandatory = $true)][string] $Output,
        [Parameter(Mandatory = $true)][string] $Expected,
        [Parameter(Mandatory = $true)][string] $Description
    )

    if (-not $Output.Contains($Expected)) {
        throw "$Description did not contain expected text: $Expected"
    }
}

function Assert-OutputDoesNotContain {
    param(
        [Parameter(Mandatory = $true)][string] $Output,
        [Parameter(Mandatory = $true)][string] $Forbidden,
        [Parameter(Mandatory = $true)][string] $Description
    )

    if ($Output.Contains($Forbidden)) {
        throw "$Description contained forbidden text: $Forbidden"
    }
}

$ResolvedWorkRoot = Resolve-RepoPath $WorkRoot
Assert-UnderRepoRoot $ResolvedWorkRoot
New-CleanDirectory $ResolvedWorkRoot

$SigningScript = Resolve-RepoPath "eng/sign-windows-package.ps1"
if (-not (Test-Path -LiteralPath $SigningScript -PathType Leaf)) {
    throw "Missing Windows package signing script: $SigningScript"
}

$packageRoot = Join-Path $ResolvedWorkRoot "win-unpacked"
New-Item -ItemType Directory -Path (Join-Path $packageRoot "resources/app/native") -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $packageRoot "resources/app/lib") -Force | Out-Null
[System.IO.File]::WriteAllBytes((Join-Path $packageRoot "OpenLineOps.exe"), [byte[]]@(77, 90, 0, 0))
[System.IO.File]::WriteAllBytes((Join-Path $packageRoot "resources/app/native/device.node"), [byte[]]@(77, 90, 1, 0))
[System.IO.File]::WriteAllBytes((Join-Path $packageRoot "resources/app/lib/helper.dll"), [byte[]]@(77, 90, 2, 0))
Set-Content -LiteralPath (Join-Path $packageRoot "resources/app/README.md") -Value "Not signable" -Encoding UTF8

Invoke-ExpectedFailure `
    -Description "missing certificate selector" `
    -Arguments @(
        "-PackageRoot",
        $packageRoot,
        "-PlanOnly") `
    -ExpectedPattern "Provide exactly one certificate selector"

Invoke-ExpectedFailure `
    -Description "multiple certificate selectors" `
    -Arguments @(
        "-PackageRoot",
        $packageRoot,
        "-CertificateThumbprint",
        "0123456789ABCDEF0123456789ABCDEF01234567",
        "-AutoSelectCertificate",
        "-PlanOnly") `
    -ExpectedPattern "Provide exactly one certificate selector"

$plan = Invoke-SigningScript -Arguments @(
    "-PackageRoot",
    $packageRoot,
    "-CertificateThumbprint",
    "0123456789ABCDEF0123456789ABCDEF01234567",
    "-PlanOnly")

if ($plan.ExitCode -ne 0) {
    throw "Expected Windows package signing plan to pass, but got exit code $($plan.ExitCode): $($plan.Output)"
}

Assert-OutputContains -Output $plan.Output -Expected "Windows package signing plan only." -Description "Signing plan"
Assert-OutputContains -Output $plan.Output -Expected "Files: 3" -Description "Signing plan"
Assert-OutputContains -Output $plan.Output -Expected "OpenLineOps.exe" -Description "Signing plan"
Assert-OutputContains -Output $plan.Output -Expected "resources/app/lib/helper.dll" -Description "Signing plan"
Assert-OutputContains -Output $plan.Output -Expected "resources/app/native/device.node" -Description "Signing plan"
Assert-OutputDoesNotContain -Output $plan.Output -Forbidden "README.md" -Description "Signing plan"

Write-Host "Windows package signing readiness verification passed."
Write-Host "Package fixture: $packageRoot"

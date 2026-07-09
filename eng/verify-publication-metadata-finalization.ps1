param(
    [string] $WorkRoot = "output/publication-metadata-finalization",

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

function Invoke-FinalizationScript {
    param([Parameter(Mandatory = $true)][string[]] $Arguments)

    $previousErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $output = & powershell -NoProfile -ExecutionPolicy Bypass -File $FinalizeScript @Arguments 2>&1 | Out-String
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

    $result = Invoke-FinalizationScript -Arguments $Arguments
    if ($result.ExitCode -eq 0) {
        throw "Expected finalization failure for $Description, but the command succeeded."
    }

    if ($result.Output -notmatch $ExpectedPattern) {
        throw "Expected failure for $Description to match '$ExpectedPattern', but got: $($result.Output)"
    }
}

function Assert-Contains {
    param(
        [Parameter(Mandatory = $true)][string] $Content,
        [Parameter(Mandatory = $true)][string] $Expected,
        [Parameter(Mandatory = $true)][string] $Description
    )

    if (-not $Content.Contains($Expected)) {
        throw "$Description is missing expected content: $Expected"
    }
}

function Assert-DoesNotMatch {
    param(
        [Parameter(Mandatory = $true)][string] $Content,
        [Parameter(Mandatory = $true)][string] $Pattern,
        [Parameter(Mandatory = $true)][string] $Description
    )

    if ($Content -match $Pattern) {
        throw "$Description contains forbidden content matching: $Pattern"
    }
}

$ResolvedWorkRoot = Resolve-RepoPath $WorkRoot
Assert-UnderRepoRoot $ResolvedWorkRoot
New-CleanDirectory $ResolvedWorkRoot

$FinalizeScript = Resolve-RepoPath "eng/finalize-publication-metadata.ps1"
if (-not (Test-Path -LiteralPath $FinalizeScript -PathType Leaf)) {
    throw "Missing publication finalization script: $FinalizeScript"
}

Invoke-ExpectedFailure `
    -Description "non-HTTPS GitHub repository URL" `
    -Arguments @(
        "-RepositoryUrl",
        "http://github.com/openlineops/openlineops",
        "-SecurityContact",
        "security@openlineops.dev",
        "-ConductContact",
        "conduct@openlineops.dev",
        "-RepoRoot",
        (Join-Path $ResolvedWorkRoot "invalid-scheme"),
        "-SkipReadinessGate") `
    -ExpectedPattern "RepositoryUrl must use https"

Invoke-ExpectedFailure `
    -Description "GitHub repository URL with extra path segments" `
    -Arguments @(
        "-RepositoryUrl",
        "https://github.com/openlineops/openlineops/extra/path",
        "-SecurityContact",
        "security@openlineops.dev",
        "-ConductContact",
        "conduct@openlineops.dev",
        "-RepoRoot",
        (Join-Path $ResolvedWorkRoot "invalid-path"),
        "-SkipReadinessGate") `
    -ExpectedPattern "RepositoryUrl must have the form"

Invoke-ExpectedFailure `
    -Description "placeholder security contact" `
    -Arguments @(
        "-RepositoryUrl",
        "https://github.com/openlineops/openlineops",
        "-SecurityContact",
        "maintainers",
        "-ConductContact",
        "conduct@openlineops.dev",
        "-RepoRoot",
        (Join-Path $ResolvedWorkRoot "placeholder-contact"),
        "-SkipReadinessGate") `
    -ExpectedPattern "SecurityContact must be a final private contact"

$successRoot = Join-Path $ResolvedWorkRoot "success"
$success = Invoke-FinalizationScript -Arguments @(
    "-RepositoryUrl",
    "https://github.com/openlineops/openlineops",
    "-SecurityContact",
    "security@openlineops.dev",
    "-ConductContact",
    "conduct@openlineops.dev",
    "-RepoRoot",
    $successRoot,
    "-SkipReadinessGate")

if ($success.ExitCode -ne 0) {
    throw "Expected successful publication metadata finalization, but got exit code $($success.ExitCode): $($success.Output)"
}

$securityPath = Join-Path $successRoot "SECURITY.md"
$conductPath = Join-Path $successRoot "CODE_OF_CONDUCT.md"
$issueTemplateConfigPath = Join-Path $successRoot ".github/ISSUE_TEMPLATE/config.yml"

foreach ($generatedFile in @($securityPath, $conductPath, $issueTemplateConfigPath)) {
    if (-not (Test-Path -LiteralPath $generatedFile -PathType Leaf)) {
        throw "Expected generated file does not exist: $generatedFile"
    }
}

$security = Get-Content -LiteralPath $securityPath -Raw
$conduct = Get-Content -LiteralPath $conductPath -Raw
$issueTemplateConfig = Get-Content -LiteralPath $issueTemplateConfigPath -Raw
$combinedGeneratedContent = $security + $conduct + $issueTemplateConfig

Assert-Contains `
    -Content $security `
    -Expected "security@openlineops.dev" `
    -Description "Generated SECURITY.md"
Assert-Contains `
    -Content $conduct `
    -Expected "conduct@openlineops.dev" `
    -Description "Generated CODE_OF_CONDUCT.md"
Assert-Contains `
    -Content $issueTemplateConfig `
    -Expected "https://github.com/openlineops/openlineops/security/policy" `
    -Description "Generated issue-template config"
Assert-DoesNotMatch `
    -Content $combinedGeneratedContent `
    -Pattern "When the GitHub repository is created|Until then|when the public repository is available" `
    -Description "Generated publication metadata"

Write-Host "Publication metadata finalization verification passed."
Write-Host "Generated files: $successRoot"

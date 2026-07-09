param(
    [string] $WorkRoot = "output/final-publication-preflight",

    [switch] $SkipClean
)

$ErrorActionPreference = "Stop"

$RepoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$PrepareScript = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "prepare-final-publication.ps1"))
$ValidRepositoryUrl = "https://github.com/openlineops/openlineops"
$ValidSecurityContact = "security@openlineops.example"
$ValidConductContact = "conduct@openlineops.example"
$ValidGitHubActionsRunUrl = "https://github.com/openlineops/openlineops/actions/runs/123456789"

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

function Write-Utf8NoBom {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][string] $Content
    )

    $directory = [System.IO.Path]::GetDirectoryName($Path)
    if (-not [string]::IsNullOrWhiteSpace($directory)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }

    [System.IO.File]::WriteAllText(
        $Path,
        $Content,
        [System.Text.UTF8Encoding]::new($false))
}

function Invoke-Prepare {
    param(
        [Parameter(Mandatory = $true)][string] $Name,
        [Parameter(Mandatory = $true)][string[]] $Arguments
    )

    $previousErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $output = & powershell -NoProfile -ExecutionPolicy Bypass -File $PrepareScript @Arguments 2>&1
        $exitCode = $LASTEXITCODE
        if ($null -eq $exitCode) {
            $exitCode = 0
        }
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }

    return [pscustomobject]@{
        Name = $Name
        ExitCode = $exitCode
        Text = (($output | ForEach-Object { $_.ToString() }) -join [Environment]::NewLine)
    }
}

function New-BaseArguments {
    return @(
        "-Version",
        "0.0.0-preflight",
        "-RepositoryUrl",
        $ValidRepositoryUrl,
        "-SecurityContact",
        $ValidSecurityContact,
        "-ConductContact",
        $ValidConductContact,
        "-GitHubActionsRunUrl",
        $ValidGitHubActionsRunUrl,
        "-CodeSigningCertificateThumbprint",
        "00112233445566778899AABBCCDDEEFF00112233",
        "-ArtifactsRoot",
        (Resolve-RepoPath (Join-Path $WorkRoot "artifacts")),
        "-WorkRoot",
        (Resolve-RepoPath (Join-Path $WorkRoot "work")),
        "-PlanOnly"
    )
}

function Assert-FailsWith {
    param(
        [Parameter(Mandatory = $true)]$Result,
        [Parameter(Mandatory = $true)][string] $Pattern
    )

    if ($Result.ExitCode -eq 0) {
        Write-Host $Result.Text
        throw "Expected '$($Result.Name)' to fail."
    }

    if ($Result.Text -notmatch $Pattern) {
        Write-Host $Result.Text
        throw "'$($Result.Name)' failed for an unexpected reason."
    }
}

$ResolvedWorkRoot = Resolve-RepoPath $WorkRoot
New-CleanDirectory $ResolvedWorkRoot
$caseResults = @()

$missingLicense = Invoke-Prepare -Name "missing-license-confirmation" -Arguments (New-BaseArguments)
$caseResults += [pscustomobject][ordered]@{
    name = $missingLicense.Name
    exitCode = $missingLicense.ExitCode
    expected = "fail"
    output = $missingLicense.Text
}
Assert-FailsWith -Result $missingLicense -Pattern "ConfirmMitLicense is required"

$invalidGitHubActionsUrlArgs = (New-BaseArguments) + "-ConfirmMitLicense"
$index = [Array]::IndexOf($invalidGitHubActionsUrlArgs, "-GitHubActionsRunUrl")
$invalidGitHubActionsUrlArgs[$index + 1] = "https://example.com/actions/runs/123"
$invalidGitHubActionsUrl = Invoke-Prepare -Name "invalid-github-actions-url" -Arguments $invalidGitHubActionsUrlArgs
$caseResults += [pscustomobject][ordered]@{
    name = $invalidGitHubActionsUrl.Name
    exitCode = $invalidGitHubActionsUrl.ExitCode
    expected = "fail"
    output = $invalidGitHubActionsUrl.Text
}
Assert-FailsWith -Result $invalidGitHubActionsUrl -Pattern "GitHubActionsRunUrl must point"

$missingSigningSelectorArgs = @((New-BaseArguments) | Where-Object {
    $_ -ne "-CodeSigningCertificateThumbprint" -and $_ -ne "00112233445566778899AABBCCDDEEFF00112233"
}) + "-ConfirmMitLicense"
$missingSigningSelector = Invoke-Prepare -Name "missing-signing-selector" -Arguments $missingSigningSelectorArgs
$caseResults += [pscustomobject][ordered]@{
    name = $missingSigningSelector.Name
    exitCode = $missingSigningSelector.ExitCode
    expected = "fail"
    output = $missingSigningSelector.Text
}
Assert-FailsWith -Result $missingSigningSelector -Pattern "exactly one code-signing certificate selector"

$validPlanArgs = (New-BaseArguments) + "-ConfirmMitLicense"
$validPlan = Invoke-Prepare -Name "valid-plan" -Arguments $validPlanArgs
$caseResults += [pscustomobject][ordered]@{
    name = $validPlan.Name
    exitCode = $validPlan.ExitCode
    expected = "pass"
    output = $validPlan.Text
}
if ($validPlan.ExitCode -ne 0) {
    Write-Host $validPlan.Text
    throw "Final publication plan-only preflight should pass."
}

foreach ($expected in @(
        "finalize-publication-metadata.ps1",
        "stage-release-artifacts.ps1",
        "-SignDesktopPackage",
        "inspect-release-candidate.ps1",
        "-RequireSignedDesktop",
        "verify-publication-readiness.ps1",
        "write-publication-evidence.ps1",
        "-RequirePublishable")) {
    if ($validPlan.Text -notmatch [regex]::Escape($expected)) {
        Write-Host $validPlan.Text
        throw "Final publication plan did not include expected text '$expected'."
    }
}

$report = [ordered]@{
    schemaVersion = 1
    generatedAtUtc = [System.DateTimeOffset]::UtcNow.ToString("O")
    product = "OpenLineOps"
    workRoot = $ResolvedWorkRoot
    cases = $caseResults
}

$jsonPath = Join-Path $ResolvedWorkRoot "publication-preflight.json"
$markdownPath = Join-Path $ResolvedWorkRoot "publication-preflight.md"
Write-Utf8NoBom -Path $jsonPath -Content (($report | ConvertTo-Json -Depth 8) + [Environment]::NewLine)

$markdown = New-Object System.Collections.Generic.List[string]
$markdown.Add("# Final Publication Preflight") | Out-Null
$markdown.Add("") | Out-Null
$markdown.Add("- Generated at UTC: $($report.generatedAtUtc)") | Out-Null
$markdown.Add("- Product: OpenLineOps") | Out-Null
$markdown.Add("") | Out-Null
$markdown.Add("## Case Results") | Out-Null
$markdown.Add("| Case | Expected | Exit code |") | Out-Null
$markdown.Add("| --- | --- | --- |") | Out-Null
foreach ($case in $caseResults) {
    $markdown.Add("| $($case.name) | $($case.expected) | $($case.exitCode) |") | Out-Null
}

$markdown.Add("") | Out-Null
$markdown.Add("Command output is captured in publication-preflight.json.") | Out-Null
Write-Utf8NoBom -Path $markdownPath -Content (($markdown -join [Environment]::NewLine) + [Environment]::NewLine)

Write-Host "Final publication preflight evidence written."
Write-Host "Markdown: $markdownPath"
Write-Host "JSON: $jsonPath"
Write-Host "Final publication preflight verification passed."
exit 0

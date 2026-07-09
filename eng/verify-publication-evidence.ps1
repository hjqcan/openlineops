param(
    [string] $ArtifactsRoot = "artifacts/release",

    [string] $WorkRoot = "output/publication-evidence-verification",

    [switch] $SkipClean
)

$ErrorActionPreference = "Stop"

$RepoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$EvidenceScript = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "write-publication-evidence.ps1"))
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

function Invoke-EvidenceCase {
    param(
        [Parameter(Mandatory = $true)][string] $Name,
        [string[]] $Arguments = @()
    )

    $outputRoot = Join-Path $ResolvedWorkRoot $Name
    $command = @(
        "-NoProfile",
        "-ExecutionPolicy",
        "Bypass",
        "-File",
        $EvidenceScript,
        "-ArtifactsRoot",
        $ResolvedArtifactsRoot,
        "-OutputRoot",
        $outputRoot
    ) + $Arguments

    $output = & powershell @command 2>&1
    $exitCode = $LASTEXITCODE
    if ($null -eq $exitCode) {
        $exitCode = 0
    }

    return [pscustomobject]@{
        Name = $Name
        ExitCode = $exitCode
        OutputRoot = $outputRoot
        Text = (($output | ForEach-Object { $_.ToString() }) -join [Environment]::NewLine)
    }
}

function Read-EvidenceJson {
    param([Parameter(Mandatory = $true)]$Case)

    $jsonPath = Join-Path $Case.OutputRoot "publication-evidence.json"
    $markdownPath = Join-Path $Case.OutputRoot "publication-evidence.md"
    if (-not (Test-Path -LiteralPath $jsonPath -PathType Leaf)) {
        Write-Host $Case.Text
        throw "Evidence case '$($Case.Name)' did not write publication-evidence.json."
    }

    if (-not (Test-Path -LiteralPath $markdownPath -PathType Leaf)) {
        Write-Host $Case.Text
        throw "Evidence case '$($Case.Name)' did not write publication-evidence.md."
    }

    return Get-Content -LiteralPath $jsonPath -Raw | ConvertFrom-Json
}

function Assert-ExitCode {
    param(
        [Parameter(Mandatory = $true)]$Case,
        [Parameter(Mandatory = $true)][int] $ExpectedExitCode
    )

    if ($Case.ExitCode -ne $ExpectedExitCode) {
        Write-Host $Case.Text
        throw "Evidence case '$($Case.Name)' exited with $($Case.ExitCode), expected $ExpectedExitCode."
    }
}

function Assert-PendingContains {
    param(
        [Parameter(Mandatory = $true)]$Evidence,
        [Parameter(Mandatory = $true)][string] $Pattern,
        [Parameter(Mandatory = $true)][string] $Description
    )

    $pending = @($Evidence.pendingExternal)
    if (-not ($pending | Where-Object { $_ -match $Pattern })) {
        throw "Expected pending external item for $Description."
    }
}

function Assert-PendingDoesNotContain {
    param(
        [Parameter(Mandatory = $true)]$Evidence,
        [Parameter(Mandatory = $true)][string] $Pattern,
        [Parameter(Mandatory = $true)][string] $Description
    )

    $pending = @($Evidence.pendingExternal)
    if ($pending | Where-Object { $_ -match $Pattern }) {
        throw "Did not expect pending external item for $Description."
    }
}

function Assert-GatePresent {
    param(
        [Parameter(Mandatory = $true)]$Evidence,
        [Parameter(Mandatory = $true)][string] $GateName,
        [Parameter(Mandatory = $true)][string] $ExpectedStatus
    )

    $gate = @($Evidence.gates | Where-Object { $_.name -eq $GateName }) | Select-Object -First 1
    if ($null -eq $gate) {
        throw "Expected evidence gate '$GateName'."
    }

    if ($gate.status -ne $ExpectedStatus) {
        throw "Evidence gate '$GateName' had status '$($gate.status)', expected '$ExpectedStatus'."
    }
}

function Assert-GateCommandContains {
    param(
        [Parameter(Mandatory = $true)]$Evidence,
        [Parameter(Mandatory = $true)][string] $GateName,
        [Parameter(Mandatory = $true)][string] $ExpectedText
    )

    $gate = @($Evidence.gates | Where-Object { $_.name -eq $GateName }) | Select-Object -First 1
    if ($null -eq $gate) {
        throw "Expected evidence gate '$GateName'."
    }

    if ($gate.command -notmatch [regex]::Escape($ExpectedText)) {
        throw "Evidence gate '$GateName' command did not contain '$ExpectedText'."
    }
}

$ResolvedArtifactsRoot = Resolve-RepoPath $ArtifactsRoot
$ResolvedWorkRoot = Resolve-RepoPath $WorkRoot
New-CleanDirectory $ResolvedWorkRoot

$defaultCase = Invoke-EvidenceCase -Name "default" -Arguments @()
Assert-ExitCode -Case $defaultCase -ExpectedExitCode 0
$defaultEvidence = Read-EvidenceJson $defaultCase
if ($defaultEvidence.product -ne "OpenLineOps") {
    throw "Default evidence has unexpected product '$($defaultEvidence.product)'."
}

if ($defaultEvidence.publishable -ne $false) {
    throw "Default evidence should not be publishable before final external proof is supplied."
}

if (@($defaultEvidence.internalFailures).Count -ne 0) {
    throw "Default evidence should not contain internal failures."
}

Assert-PendingContains -Evidence $defaultEvidence -Pattern "Final MIT license decision" -Description "MIT confirmation"
Assert-PendingContains -Evidence $defaultEvidence -Pattern "GitHub-hosted Windows CI proof URL" -Description "GitHub Actions proof"
Assert-GatePresent -Evidence $defaultEvidence -GateName "release candidate inspection" -ExpectedStatus "pass"
Assert-GatePresent -Evidence $defaultEvidence -GateName "publication readiness with pending external allowed" -ExpectedStatus "pass"
Assert-GateCommandContains -Evidence $defaultEvidence -GateName "release candidate inspection behavior" -ExpectedText "release-candidate-inspection-verification"
Assert-GateCommandContains -Evidence $defaultEvidence -GateName "strict publication readiness" -ExpectedText "publication-readiness-strict"
Assert-GateCommandContains -Evidence $defaultEvidence -GateName "signed release candidate inspection" -ExpectedText "signed-release-candidate-inspection"

$confirmedCase = Invoke-EvidenceCase `
    -Name "confirmed-proof" `
    -Arguments @("-ConfirmMitLicense", "-GitHubActionsRunUrl", $ValidGitHubActionsRunUrl)
Assert-ExitCode -Case $confirmedCase -ExpectedExitCode 0
$confirmedEvidence = Read-EvidenceJson $confirmedCase
if ($confirmedEvidence.license.confirmedForPublication -ne $true) {
    throw "Confirmed evidence did not record MIT confirmation."
}

if ($confirmedEvidence.githubActions.proofSupplied -ne $true -or
    $confirmedEvidence.githubActions.runUrl -ne $ValidGitHubActionsRunUrl) {
    throw "Confirmed evidence did not record the GitHub Actions run URL."
}

Assert-PendingDoesNotContain -Evidence $confirmedEvidence -Pattern "Final MIT license decision" -Description "MIT confirmation"
Assert-PendingDoesNotContain -Evidence $confirmedEvidence -Pattern "GitHub-hosted Windows CI proof URL" -Description "GitHub Actions proof"

$invalidUrlCase = Invoke-EvidenceCase `
    -Name "invalid-github-actions-url" `
    -Arguments @("-ConfirmMitLicense", "-GitHubActionsRunUrl", "https://example.com/actions/runs/123")
if ($invalidUrlCase.ExitCode -eq 0) {
    Write-Host $invalidUrlCase.Text
    throw "Invalid GitHub Actions run URL should fail publication evidence generation."
}

$invalidUrlEvidence = Read-EvidenceJson $invalidUrlCase
if (-not (@($invalidUrlEvidence.internalFailures) | Where-Object { $_ -match "GitHubActionsRunUrl must point" })) {
    throw "Invalid URL evidence did not record the expected internal failure."
}

$publishableCase = Invoke-EvidenceCase `
    -Name "require-publishable" `
    -Arguments @("-ConfirmMitLicense", "-GitHubActionsRunUrl", $ValidGitHubActionsRunUrl, "-RequirePublishable")
$publishableEvidence = Read-EvidenceJson $publishableCase
if ($confirmedEvidence.publishable -eq $true) {
    Assert-ExitCode -Case $publishableCase -ExpectedExitCode 0
    if ($publishableEvidence.publishable -ne $true) {
        throw "RequirePublishable case succeeded without publishable evidence."
    }
}
else {
    if ($publishableCase.ExitCode -eq 0) {
        Write-Host $publishableCase.Text
        throw "RequirePublishable case should fail while evidence is not publishable."
    }

    if ($publishableCase.Text -notmatch "Publication is not yet publishable") {
        Write-Host $publishableCase.Text
        throw "RequirePublishable failure did not explain that publication is not yet publishable."
    }
}

Write-Host "Publication evidence verification passed."
exit 0

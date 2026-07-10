param(
    [string] $BundleRoot = ".",

    [string] $WorkRoot = "output/ci-release-artifact-inspection",

    [switch] $RequirePublishable,

    [switch] $RequireSignedDesktop
)

$ErrorActionPreference = "Stop"

$RepoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$Failures = New-Object System.Collections.Generic.List[string]
$ExpectedArtifactKinds = @("api", "desktop", "plugin-host", "sample-plugin", "script-worker", "source")

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

function Add-Failure {
    param([Parameter(Mandatory = $true)][string] $Message)
    $Failures.Add($Message) | Out-Null
}

function Write-Utf8NoBom {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][string] $Content
    )

    $directory = Split-Path -Parent $Path
    if (-not [string]::IsNullOrWhiteSpace($directory) -and -not (Test-Path -LiteralPath $directory -PathType Container)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }

    [System.IO.File]::WriteAllText($Path, $Content, [System.Text.UTF8Encoding]::new($false))
}

function Test-RequiredDirectory {
    param([Parameter(Mandatory = $true)][string] $Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        Add-Failure "Missing required directory: $Path"
        return $false
    }

    return $true
}

function Test-RequiredFile {
    param([Parameter(Mandatory = $true)][string] $Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        Add-Failure "Missing required file: $Path"
        return $false
    }

    return $true
}

function Read-JsonFile {
    param([Parameter(Mandatory = $true)][string] $Path)

    if (-not (Test-RequiredFile $Path)) {
        return $null
    }

    try {
        return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    }
    catch {
        Add-Failure "Invalid JSON file '$Path': $($_.Exception.Message)"
        return $null
    }
}

function Test-ArtifactKinds {
    param(
        [Parameter(Mandatory = $true)][string[]] $ActualKinds,
        [Parameter(Mandatory = $true)][string] $Description
    )

    $actual = @($ActualKinds | Sort-Object)
    $expected = @($ExpectedArtifactKinds | Sort-Object)
    if (($actual -join "|") -ne ($expected -join "|")) {
        Add-Failure "$Description artifact kinds were '$($actual -join ", ")', expected '$($expected -join ", ")'."
    }
}

function Test-GateStatus {
    param(
        [Parameter(Mandatory = $true)]$Evidence,
        [Parameter(Mandatory = $true)][string] $GateName,
        [Parameter(Mandatory = $true)][string] $ExpectedStatus
    )

    $gate = @($Evidence.gates | Where-Object { $_.name -eq $GateName }) | Select-Object -First 1
    if ($null -eq $gate) {
        Add-Failure "Publication evidence is missing gate '$GateName'."
        return
    }

    if ($gate.status -ne $ExpectedStatus) {
        Add-Failure "Publication evidence gate '$GateName' had status '$($gate.status)', expected '$ExpectedStatus'."
    }
}

function Test-PreflightCase {
    param(
        [Parameter(Mandatory = $true)]$Preflight,
        [Parameter(Mandatory = $true)][string] $Name,
        [Parameter(Mandatory = $true)][string] $Expected
    )

    $case = @($Preflight.cases | Where-Object { $_.name -eq $Name }) | Select-Object -First 1
    if ($null -eq $case) {
        Add-Failure "Final publication preflight is missing case '$Name'."
        return
    }

    if ($case.expected -ne $Expected) {
        Add-Failure "Final publication preflight case '$Name' expected '$($case.expected)', expected metadata '$Expected'."
    }

    if ($Expected -eq "pass" -and $case.exitCode -ne 0) {
        Add-Failure "Final publication preflight case '$Name' should pass with exit code 0."
    }

    if ($Expected -eq "fail" -and $case.exitCode -eq 0) {
        Add-Failure "Final publication preflight case '$Name' should fail with a non-zero exit code."
    }
}

function Invoke-ReleaseCandidateInspection {
    param([Parameter(Mandatory = $true)][string] $ArtifactsRoot)

    $inspectionWorkRoot = Join-Path $ResolvedWorkRoot "work/release-candidate-inspection"
    $arguments = @(
        "-NoProfile",
        "-ExecutionPolicy",
        "Bypass",
        "-File",
        (Join-Path $PSScriptRoot "inspect-release-candidate.ps1"),
        "-ArtifactsRoot",
        $ArtifactsRoot,
        "-WorkRoot",
        $inspectionWorkRoot
    )

    if ($RequireSignedDesktop -or $RequirePublishable) {
        $arguments += "-RequireSignedDesktop"
    }

    $output = & powershell @arguments 2>&1
    $exitCode = $LASTEXITCODE
    if ($null -eq $exitCode) {
        $exitCode = 0
    }

    if ($exitCode -ne 0) {
        Add-Failure "Release candidate inspection failed with exit code $exitCode. Output: $(($output | Out-String).Trim())"
    }
}

function Remove-TemporaryInspectionWork {
    foreach ($relativePath in @("work", "release-candidate-inspection")) {
        $temporaryWorkRoot = Join-Path $ResolvedWorkRoot $relativePath
        if (Test-Path -LiteralPath $temporaryWorkRoot) {
            Remove-Item -LiteralPath $temporaryWorkRoot -Recurse -Force
        }
    }
}

function Write-InspectionReport {
    $status = if ($Failures.Count -eq 0) { "pass" } else { "fail" }
    $artifactKinds = @()
    $artifactCount = 0
    $releaseVersion = $null
    if ($null -ne $manifest) {
        $releaseVersion = $manifest.version
        $artifactCount = @($manifest.artifacts).Count
        $artifactKinds = @($manifest.artifacts | ForEach-Object { $_.kind } | Sort-Object -Unique)
    }

    $dependencyCounts = [ordered]@{
        total = $null
        nuget = $null
        npm = $null
        uniqueLicenseValues = $null
    }
    if ($null -ne $dependencyInventory) {
        $dependencyCounts.total = $dependencyInventory.packageCounts.total
        $dependencyCounts.nuget = $dependencyInventory.packageCounts.nuget
        $dependencyCounts.npm = $dependencyInventory.packageCounts.npm
        $dependencyCounts.uniqueLicenseValues = $dependencyInventory.packageCounts.uniqueLicenseValues
    }

    $metadataChecksumCount = 0
    if (Test-Path -LiteralPath $metadataChecksumsPath -PathType Leaf) {
        $metadataChecksumCount = @(
            Get-Content -LiteralPath $metadataChecksumsPath |
                Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
        ).Count
    }

    $preflightCases = @()
    if ($null -ne $preflight) {
        $preflightCases = @($preflight.cases | ForEach-Object {
            [ordered]@{
                name = $_.name
                expected = $_.expected
                exitCode = $_.exitCode
            }
        })
    }

    $report = [ordered]@{
        schemaVersion = 1
        generatedAtUtc = [System.DateTimeOffset]::UtcNow.ToString("O")
        product = "OpenLineOps"
        status = $status
        bundleRoot = $ResolvedBundleRoot
        workRoot = $ResolvedWorkRoot
        requirePublishable = [bool] $RequirePublishable
        requireSignedDesktop = [bool] $RequireSignedDesktop
        release = [ordered]@{
            version = $releaseVersion
            artifactCount = $artifactCount
            artifactKinds = $artifactKinds
            metadataChecksumCount = $metadataChecksumCount
            dependencyCounts = $dependencyCounts
        }
        publicationEvidence = [ordered]@{
            present = $null -ne $evidence
            publishable = if ($null -ne $evidence) { $evidence.publishable } else { $null }
            pendingExternalCount = if ($null -ne $evidence) { @($evidence.pendingExternal).Count } else { $null }
            internalFailureCount = if ($null -ne $evidence) { @($evidence.internalFailures).Count } else { $null }
        }
        finalPublicationPreflight = [ordered]@{
            present = $null -ne $preflight
            cases = $preflightCases
        }
        failures = @($Failures)
    }

    $jsonPath = Join-Path $ResolvedWorkRoot "ci-release-artifact-inspection.json"
    $markdownPath = Join-Path $ResolvedWorkRoot "ci-release-artifact-inspection.md"
    Write-Utf8NoBom -Path $jsonPath -Content (($report | ConvertTo-Json -Depth 10) + [Environment]::NewLine)

    $markdown = New-Object System.Collections.Generic.List[string]
    $markdown.Add("# CI Release Artifact Inspection") | Out-Null
    $markdown.Add("") | Out-Null
    $markdown.Add("- Generated at UTC: $($report.generatedAtUtc)") | Out-Null
    $markdown.Add("- Product: OpenLineOps") | Out-Null
    $markdown.Add("- Status: $status") | Out-Null
    $markdown.Add("- Bundle root: $ResolvedBundleRoot") | Out-Null
    $markdown.Add("- Release version: $releaseVersion") | Out-Null
    $markdown.Add("- Artifact kinds: $($artifactKinds -join ', ')") | Out-Null
    $markdown.Add("- Dependency packages: $($dependencyCounts.total)") | Out-Null
    $markdown.Add("- Metadata checksum entries: $metadataChecksumCount") | Out-Null
    $markdown.Add("- Publishable: $($report.publicationEvidence.publishable)") | Out-Null
    $markdown.Add("") | Out-Null
    $markdown.Add("## Failures") | Out-Null
    if ($Failures.Count -eq 0) {
        $markdown.Add("- None") | Out-Null
    }
    else {
        foreach ($failure in $Failures) {
            $markdown.Add("- $failure") | Out-Null
        }
    }

    $markdown.Add("") | Out-Null
    $markdown.Add("Detailed results are captured in ci-release-artifact-inspection.json.") | Out-Null
    Write-Utf8NoBom -Path $markdownPath -Content (($markdown -join [Environment]::NewLine) + [Environment]::NewLine)

    Write-Host "Inspection report: $jsonPath"
    Write-Host "Inspection summary: $markdownPath"
}

$ResolvedBundleRoot = Resolve-RepoPath $BundleRoot
$ResolvedWorkRoot = Resolve-RepoPath $WorkRoot
Assert-UnderRepoRoot $ResolvedWorkRoot
New-Item -ItemType Directory -Path $ResolvedWorkRoot -Force | Out-Null
Remove-TemporaryInspectionWork

if (-not (Test-Path -LiteralPath $ResolvedBundleRoot -PathType Container)) {
    throw "BundleRoot does not exist: $ResolvedBundleRoot"
}

$artifactsRoot = Join-Path $ResolvedBundleRoot "artifacts/release"
$publicationEvidenceRoot = Join-Path $ResolvedBundleRoot "output/publication-evidence"
$publicationEvidenceVerificationRoot = Join-Path $ResolvedBundleRoot "output/publication-evidence-verification"
$finalPublicationPreflightRoot = Join-Path $ResolvedBundleRoot "output/final-publication-preflight"

foreach ($directory in @(
        $artifactsRoot,
        $publicationEvidenceRoot,
        $publicationEvidenceVerificationRoot,
        $finalPublicationPreflightRoot)) {
    Test-RequiredDirectory $directory | Out-Null
}

$manifestPath = Join-Path $artifactsRoot "release-manifest.json"
$checksumsPath = Join-Path $artifactsRoot "checksums.sha256"
$dependencyInventoryPath = Join-Path $artifactsRoot "release-dependency-inventory.json"
$metadataChecksumsPath = Join-Path $artifactsRoot "release-metadata-checksums.sha256"
$provenancePath = Join-Path $artifactsRoot "release-provenance.json"
$releaseNotesPath = Join-Path $artifactsRoot "release-notes.md"
$evidencePath = Join-Path $publicationEvidenceRoot "publication-evidence.json"
$evidenceMarkdownPath = Join-Path $publicationEvidenceRoot "publication-evidence.md"
$preflightPath = Join-Path $finalPublicationPreflightRoot "publication-preflight.json"
$preflightMarkdownPath = Join-Path $finalPublicationPreflightRoot "publication-preflight.md"

foreach ($file in @(
        $checksumsPath,
        $dependencyInventoryPath,
        $metadataChecksumsPath,
        $provenancePath,
        $releaseNotesPath,
        $evidenceMarkdownPath,
        $preflightMarkdownPath)) {
    Test-RequiredFile $file | Out-Null
}

$manifest = Read-JsonFile $manifestPath
$dependencyInventory = Read-JsonFile $dependencyInventoryPath
$evidence = Read-JsonFile $evidencePath
$preflight = Read-JsonFile $preflightPath

if ($null -ne $manifest) {
    if ($manifest.schemaVersion -ne 1) {
        Add-Failure "Release manifest schemaVersion must be 1."
    }

    if ($manifest.product -ne "OpenLineOps") {
        Add-Failure "Release manifest product must be OpenLineOps."
    }

    Test-ArtifactKinds -ActualKinds @($manifest.artifacts | ForEach-Object { $_.kind }) -Description "Release manifest"
}

if ($null -ne $dependencyInventory) {
    if ($dependencyInventory.schemaVersion -ne 1) {
        Add-Failure "Dependency inventory schemaVersion must be 1."
    }

    if ($dependencyInventory.product -ne "OpenLineOps") {
        Add-Failure "Dependency inventory product must be OpenLineOps."
    }

    if ($null -ne $manifest -and $dependencyInventory.version -ne $manifest.version) {
        Add-Failure "Dependency inventory version '$($dependencyInventory.version)' does not match manifest version '$($manifest.version)'."
    }

    if ($dependencyInventory.packageCounts.total -ne @($dependencyInventory.packages).Count) {
        Add-Failure "Dependency inventory package count does not match package list count."
    }
}

Invoke-ReleaseCandidateInspection -ArtifactsRoot $artifactsRoot

if ($null -ne $evidence) {
    if ($evidence.schemaVersion -ne 1) {
        Add-Failure "Publication evidence schemaVersion must be 1."
    }

    if ($evidence.product -ne "OpenLineOps") {
        Add-Failure "Publication evidence product must be OpenLineOps."
    }

    if (@($evidence.internalFailures).Count -ne 0) {
        Add-Failure "Publication evidence contains internal failures: $(@($evidence.internalFailures) -join "; ")"
    }

    if ($null -ne $manifest) {
        if ($evidence.release.version -ne $manifest.version) {
            Add-Failure "Publication evidence release version '$($evidence.release.version)' does not match manifest version '$($manifest.version)'."
        }

        if ($evidence.release.artifactCount -ne @($manifest.artifacts).Count) {
            Add-Failure "Publication evidence artifact count '$($evidence.release.artifactCount)' does not match manifest artifact count '$(@($manifest.artifacts).Count)'."
        }
    }

    Test-ArtifactKinds -ActualKinds @($evidence.release.artifactKinds) -Description "Publication evidence"
    Test-GateStatus -Evidence $evidence -GateName "release candidate inspection" -ExpectedStatus "pass"
    Test-GateStatus -Evidence $evidence -GateName "release candidate inspection behavior" -ExpectedStatus "pass"
    Test-GateStatus -Evidence $evidence -GateName "publication readiness with pending external allowed" -ExpectedStatus "pass"

    if ($RequirePublishable) {
        if ($evidence.publishable -ne $true) {
            Add-Failure "Publication evidence must be publishable when -RequirePublishable is supplied."
        }

        if (@($evidence.pendingExternal).Count -ne 0) {
            Add-Failure "Publication evidence still has pending external items: $(@($evidence.pendingExternal) -join "; ")"
        }

        if ($evidence.license.confirmedForPublication -ne $true) {
            Add-Failure "Publication evidence must record final MIT confirmation when -RequirePublishable is supplied."
        }

        if ($evidence.githubActions.proofSupplied -ne $true -or [string]::IsNullOrWhiteSpace($evidence.githubActions.runUrl)) {
            Add-Failure "Publication evidence must record GitHub-hosted CI proof when -RequirePublishable is supplied."
        }
    }
}

foreach ($caseName in @("default", "confirmed-proof", "invalid-github-actions-url", "require-publishable")) {
    $caseRoot = Join-Path $publicationEvidenceVerificationRoot $caseName
    Test-RequiredDirectory $caseRoot | Out-Null
    Test-RequiredFile (Join-Path $caseRoot "publication-evidence.json") | Out-Null
    Test-RequiredFile (Join-Path $caseRoot "publication-evidence.md") | Out-Null
}

$defaultVerification = Read-JsonFile (Join-Path $publicationEvidenceVerificationRoot "default/publication-evidence.json")
if ($null -ne $defaultVerification -and @($defaultVerification.internalFailures).Count -ne 0) {
    Add-Failure "Default publication evidence verification contains internal failures."
}

$confirmedVerification = Read-JsonFile (Join-Path $publicationEvidenceVerificationRoot "confirmed-proof/publication-evidence.json")
if ($null -ne $confirmedVerification) {
    if ($confirmedVerification.license.confirmedForPublication -ne $true) {
        Add-Failure "Confirmed publication evidence verification must record final MIT confirmation."
    }

    if ($confirmedVerification.githubActions.proofSupplied -ne $true) {
        Add-Failure "Confirmed publication evidence verification must record GitHub Actions proof."
    }
}

$invalidUrlVerification = Read-JsonFile (Join-Path $publicationEvidenceVerificationRoot "invalid-github-actions-url/publication-evidence.json")
if ($null -ne $invalidUrlVerification -and -not (@($invalidUrlVerification.internalFailures) | Where-Object { $_ -match "GitHubActionsRunUrl must point" })) {
    Add-Failure "Invalid GitHub Actions URL verification must record the expected internal failure."
}

if ($null -ne $preflight) {
    if ($preflight.schemaVersion -ne 1) {
        Add-Failure "Final publication preflight schemaVersion must be 1."
    }

    if ($preflight.product -ne "OpenLineOps") {
        Add-Failure "Final publication preflight product must be OpenLineOps."
    }

    Test-PreflightCase -Preflight $preflight -Name "missing-license-confirmation" -Expected "fail"
    Test-PreflightCase -Preflight $preflight -Name "invalid-github-actions-url" -Expected "fail"
    Test-PreflightCase -Preflight $preflight -Name "missing-signing-selector" -Expected "fail"
    Test-PreflightCase -Preflight $preflight -Name "valid-plan" -Expected "pass"
}

Write-InspectionReport
Remove-TemporaryInspectionWork

if ($Failures.Count -gt 0) {
    Write-Host "CI release artifact bundle inspection failed:" -ForegroundColor Red
    foreach ($failure in $Failures) {
        Write-Host " - $failure" -ForegroundColor Red
    }

    exit 1
}

Write-Host "CI release artifact bundle inspection passed."
Write-Host "Bundle: $ResolvedBundleRoot"
if ($RequirePublishable) {
    Write-Host "Publishable requirement: enforced"
}

exit 0

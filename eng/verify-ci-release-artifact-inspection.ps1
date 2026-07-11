param(
    [string] $WorkRoot = "output/ci-release-artifact-inspection-verification",

    [switch] $SkipClean
)

$ErrorActionPreference = "Stop"

$RepoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$InspectorScript = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "inspect-ci-release-artifact.ps1"))
$CandidateVerificationScript = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "verify-release-candidate-inspection.ps1"))
$ExpectedArtifactKinds = @("agent", "api", "desktop", "plugin-host", "runner", "sample-plugin", "script-worker", "source")
$ExpectedGateNames = @(
    "open-source metadata",
    "third-party license metadata",
    "release candidate inspection",
    "release candidate inspection behavior",
    "Windows package signing readiness",
    "publication metadata finalization behavior",
    "publication readiness with pending external allowed",
    "strict publication readiness",
    "signed release candidate inspection")

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
    $rootPrefix = $RepoRoot.TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Verification path must stay inside the repository: $fullPath"
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

    $directory = Split-Path -Parent $Path
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    [System.IO.File]::WriteAllText($Path, $Content, [System.Text.UTF8Encoding]::new($false))
}

function Write-Json {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)]$Value
    )

    Write-Utf8NoBom -Path $Path -Content (($Value | ConvertTo-Json -Depth 12) + [Environment]::NewLine)
}

function New-PublicationEvidence {
    param(
        [Parameter(Mandatory = $true)]$Manifest,
        [Parameter(Mandatory = $true)][string] $Root,
        [string[]] $InternalFailures = @()
    )

    if ($null -eq $InternalFailures) {
        $InternalFailures = @()
    }

    $gates = @($ExpectedGateNames | ForEach-Object {
        $pending = $_ -cin @("strict publication readiness", "signed release candidate inspection")
        [ordered]@{
            name = $_
            status = if ($pending) { "pending external" } else { "pass" }
            exitCode = if ($pending) { 1 } else { 0 }
            pendingAllowed = $pending
            command = "fixture command for $_"
            output = "fixture output for $_"
        }
    })

    return [ordered]@{
        schemaVersion = 1
        generatedAtUtc = [System.DateTimeOffset]::UtcNow.ToString("O")
        product = "OpenLineOps"
        publishable = $false
        repoRoot = $RepoRoot
        artifactsRoot = (Join-Path $Root "artifacts/release")
        outputRoot = (Join-Path $Root "output/publication-evidence")
        license = [ordered]@{
            fileLicense = "MIT"
            confirmedForPublication = $true
        }
        githubActions = [ordered]@{
            runUrl = "https://github.com/openlineops/openlineops/actions/runs/123456789"
            proofSupplied = $true
        }
        release = [ordered]@{
            product = "OpenLineOps"
            version = $Manifest.version
            manifestPath = (Join-Path $Root "artifacts/release/release-manifest.json")
            provenancePath = (Join-Path $Root "artifacts/release/release-provenance.json")
            provenanceGeneratedAtUtc = [System.DateTimeOffset]::UtcNow.ToString("O")
            artifactCount = @($Manifest.artifacts).Count
            artifactKinds = @($Manifest.artifacts | ForEach-Object { $_.kind } | Sort-Object)
        }
        pendingExternal = @("Windows executable signing proof remains external in this fixture.")
        internalFailures = @($InternalFailures)
        gates = $gates
    }
}

function Write-BundleEvidence {
    param([Parameter(Mandatory = $true)][string] $Root)

    $manifest = Get-Content -LiteralPath (Join-Path $Root "artifacts/release/release-manifest.json") -Raw | ConvertFrom-Json
    $evidenceRoot = Join-Path $Root "output/publication-evidence"
    Write-Json `
        -Path (Join-Path $evidenceRoot "publication-evidence.json") `
        -Value (New-PublicationEvidence -Manifest $manifest -Root $Root)
    Write-Utf8NoBom -Path (Join-Path $evidenceRoot "publication-evidence.md") -Content "# Fixture publication evidence`r`n"

    foreach ($caseName in @("default", "confirmed-proof", "invalid-github-actions-url", "require-publishable")) {
        $caseRoot = Join-Path $Root "output/publication-evidence-verification/$caseName"
        $failures = if ($caseName -ceq "invalid-github-actions-url") {
            @("GitHubActionsRunUrl must point to a GitHub Actions run URL.")
        }
        else {
            @()
        }

        Write-Json `
            -Path (Join-Path $caseRoot "publication-evidence.json") `
            -Value (New-PublicationEvidence -Manifest $manifest -Root $Root -InternalFailures $failures)
        Write-Utf8NoBom -Path (Join-Path $caseRoot "publication-evidence.md") -Content "# Fixture evidence case`r`n"
    }

    $preflight = [ordered]@{
        schemaVersion = 1
        generatedAtUtc = [System.DateTimeOffset]::UtcNow.ToString("O")
        product = "OpenLineOps"
        workRoot = (Join-Path $Root "output/final-publication-preflight")
        cases = @(
            [ordered]@{ name = "missing-license-confirmation"; exitCode = 1; expected = "fail"; output = "expected failure" },
            [ordered]@{ name = "invalid-github-actions-url"; exitCode = 1; expected = "fail"; output = "expected failure" },
            [ordered]@{ name = "missing-signing-selector"; exitCode = 1; expected = "fail"; output = "expected failure" },
            [ordered]@{ name = "valid-plan"; exitCode = 0; expected = "pass"; output = "expected pass" })
    }
    $preflightRoot = Join-Path $Root "output/final-publication-preflight"
    Write-Json -Path (Join-Path $preflightRoot "publication-preflight.json") -Value $preflight
    Write-Utf8NoBom -Path (Join-Path $preflightRoot "publication-preflight.md") -Content "# Fixture preflight`r`n"
}

function New-Bundle {
    param(
        [Parameter(Mandatory = $true)][string] $Name,
        [Parameter(Mandatory = $true)][string] $CandidateRoot
    )

    $root = Join-Path $ResolvedWorkRoot "bundles/$Name"
    New-CleanDirectory $root
    $artifactsRoot = Join-Path $root "artifacts/release"
    New-Item -ItemType Directory -Path $artifactsRoot -Force | Out-Null
    Copy-Item -Path (Join-Path $CandidateRoot "*") -Destination $artifactsRoot -Recurse -Force
    Write-BundleEvidence $root
    return $root
}

function Invoke-Inspection {
    param(
        [Parameter(Mandatory = $true)][string] $Root,
        [Parameter(Mandatory = $true)][string] $Name
    )

    $inspectionRoot = Join-Path $ResolvedWorkRoot "inspection/$Name"
    $previousErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $output = & powershell `
            -NoProfile `
            -ExecutionPolicy Bypass `
            -File $InspectorScript `
            -BundleRoot $Root `
            -WorkRoot $inspectionRoot 2>&1
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }

    return [pscustomobject]@{
        ExitCode = $exitCode
        Text = ($output | Out-String)
    }
}

function Assert-Passes {
    param(
        [Parameter(Mandatory = $true)]$Result,
        [Parameter(Mandatory = $true)][string] $Name
    )

    if ($Result.ExitCode -ne 0) {
        Write-Host $Result.Text
        throw "Expected CI release artifact fixture '$Name' to pass."
    }

    Write-Host "Fixture '$Name' passed."
}

function Assert-FailsWith {
    param(
        [Parameter(Mandatory = $true)]$Result,
        [Parameter(Mandatory = $true)][string] $Name,
        [Parameter(Mandatory = $true)][string] $Pattern
    )

    if ($Result.ExitCode -eq 0) {
        throw "Expected CI release artifact fixture '$Name' to fail."
    }

    if ($Result.Text -cnotmatch $Pattern) {
        Write-Host $Result.Text
        throw "CI release artifact fixture '$Name' failed for an unexpected reason."
    }

    Write-Host "Fixture '$Name' failed as expected."
}

function Replace-InFile {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][string] $Pattern,
        [Parameter(Mandatory = $true)][string] $Replacement
    )

    $content = [System.IO.File]::ReadAllText($Path)
    $updated = [regex]::Replace($content, $Pattern, $Replacement)
    if ($updated -ceq $content) {
        throw "Fixture mutation did not match '$Pattern' in $Path."
    }

    Write-Utf8NoBom -Path $Path -Content $updated
}

$ResolvedWorkRoot = Resolve-RepoPath $WorkRoot
New-CleanDirectory $ResolvedWorkRoot
$candidateWorkRoot = Join-Path $ResolvedWorkRoot "release-candidate-fixtures"
$candidateRoot = Join-Path $candidateWorkRoot "positive"
if (-not ($SkipClean -and (Test-Path -LiteralPath (Join-Path $candidateRoot "release-manifest.json") -PathType Leaf))) {
    $candidateOutput = & powershell `
        -NoProfile `
        -ExecutionPolicy Bypass `
        -File $CandidateVerificationScript `
        -WorkRoot $candidateWorkRoot 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Host ($candidateOutput | Out-String)
        throw "Could not create the positive release candidate fixture."
    }
}

$positiveRoot = New-Bundle -Name "positive" -CandidateRoot $candidateRoot
Assert-Passes -Result (Invoke-Inspection -Root $positiveRoot -Name "positive") -Name "positive"

$manifestProductRoot = New-Bundle -Name "manifest-product-case" -CandidateRoot $candidateRoot
Replace-InFile `
    -Path (Join-Path $manifestProductRoot "artifacts/release/release-manifest.json") `
    -Pattern '"product"\s*:\s*"OpenLineOps"' `
    -Replacement '"product":"openlineops"'
Assert-FailsWith `
    -Result (Invoke-Inspection -Root $manifestProductRoot -Name "manifest-product-case") `
    -Name "manifest-product-case" `
    -Pattern "Release manifest product must be exactly 'OpenLineOps'"

$manifestPropertyRoot = New-Bundle -Name "manifest-property-case" -CandidateRoot $candidateRoot
Replace-InFile `
    -Path (Join-Path $manifestPropertyRoot "artifacts/release/release-manifest.json") `
    -Pattern '"product"\s*:' `
    -Replacement '"Product":'
Assert-FailsWith `
    -Result (Invoke-Inspection -Root $manifestPropertyRoot -Name "manifest-property-case") `
    -Name "manifest-property-case" `
    -Pattern "missing exact property name\(s\): product"

$duplicateManifestRoot = New-Bundle -Name "manifest-duplicate-property" -CandidateRoot $candidateRoot
$duplicateManifestPath = Join-Path $duplicateManifestRoot "artifacts/release/release-manifest.json"
$duplicateManifest = [System.IO.File]::ReadAllText($duplicateManifestPath)
Write-Utf8NoBom `
    -Path $duplicateManifestPath `
    -Content ($duplicateManifest.Insert($duplicateManifest.IndexOf('{') + 1, '"product":"OpenLineOps",'))
Assert-FailsWith `
    -Result (Invoke-Inspection -Root $duplicateManifestRoot -Name "manifest-duplicate-property") `
    -Name "manifest-duplicate-property" `
    -Pattern "Duplicate JSON property 'product'"

$inventoryUnknownRoot = New-Bundle -Name "inventory-unknown-property" -CandidateRoot $candidateRoot
$inventoryPath = Join-Path $inventoryUnknownRoot "artifacts/release/release-dependency-inventory.json"
$inventory = [System.IO.File]::ReadAllText($inventoryPath)
Write-Utf8NoBom `
    -Path $inventoryPath `
    -Content ($inventory.Insert($inventory.IndexOf('{') + 1, '"unexpected":true,'))
Assert-FailsWith `
    -Result (Invoke-Inspection -Root $inventoryUnknownRoot -Name "inventory-unknown-property") `
    -Name "inventory-unknown-property" `
    -Pattern "Dependency inventory has unexpected or non-canonical property name\(s\): unexpected"

$evidenceStatusRoot = New-Bundle -Name "evidence-status-case" -CandidateRoot $candidateRoot
Replace-InFile `
    -Path (Join-Path $evidenceStatusRoot "output/publication-evidence/publication-evidence.json") `
    -Pattern '"status"\s*:\s*"pass"' `
    -Replacement '"status":"Pass"'
Assert-FailsWith `
    -Result (Invoke-Inspection -Root $evidenceStatusRoot -Name "evidence-status-case") `
    -Name "evidence-status-case" `
    -Pattern "status 'Pass' is not canonical"

$preflightNameRoot = New-Bundle -Name "preflight-name-case" -CandidateRoot $candidateRoot
Replace-InFile `
    -Path (Join-Path $preflightNameRoot "output/final-publication-preflight/publication-preflight.json") `
    -Pattern '"name"\s*:\s*"valid-plan"' `
    -Replacement '"name":"Valid-plan"'
Assert-FailsWith `
    -Result (Invoke-Inspection -Root $preflightNameRoot -Name "preflight-name-case") `
    -Name "preflight-name-case" `
    -Pattern "Final publication preflight case names were"

Write-Host "CI release artifact inspection verification passed."
exit 0

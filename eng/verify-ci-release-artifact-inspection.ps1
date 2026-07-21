param(
    [string] $WorkRoot = "output/ci-release-artifact-inspection-verification",

    [switch] $SkipClean
)

$ErrorActionPreference = "Stop"

$RepoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
. (Join-Path $PSScriptRoot "github-fixture-process.ps1")
. (Join-Path $PSScriptRoot "publication-evidence-case-contract.ps1")
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
$FixtureGitHubEnvironment = @{
    GITHUB_REPOSITORY = "openlineops/openlineops"
    GITHUB_SHA = "0123456789abcdef0123456789abcdef01234567"
    GITHUB_RUN_ID = "123456789"
    GITHUB_SERVER_URL = "https://github.com"
}

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

function Get-FileSha256 {
    param([Parameter(Mandatory = $true)][string] $Path)

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

. (Join-Path $PSScriptRoot "evidence-validation-test-fixtures.ps1")

function Assert-ExactJsonProperties {
    param(
        [Parameter(Mandatory = $true)]$Value,
        [Parameter(Mandatory = $true)][string[]] $Expected,
        [Parameter(Mandatory = $true)][string] $Description
    )

    if ($null -eq $Value -or $Value -isnot [pscustomobject]) {
        throw "$Description must be a JSON object."
    }

    $actual = @($Value.PSObject.Properties.Name)
    $missing = @($Expected | Where-Object { $actual -cnotcontains $_ })
    $unexpected = @($actual | Where-Object { $Expected -cnotcontains $_ })
    if ($missing.Count -gt 0 -or $unexpected.Count -gt 0) {
        throw "$Description must contain exactly [$($Expected -join ', ')]; missing [$($missing -join ', ')], unexpected [$($unexpected -join ', ')]."
    }
}

function Resolve-PositiveCandidateFixture {
    param([Parameter(Mandatory = $true)][string] $CandidateWorkRoot)

    $indexPath = Join-Path $CandidateWorkRoot "fixture-index.json"
    if (-not (Test-Path -LiteralPath $indexPath -PathType Leaf)) {
        throw "Candidate verification did not write fixture-index.json."
    }

    try {
        $index = Get-Content -LiteralPath $indexPath -Raw | ConvertFrom-Json
    }
    catch {
        throw "Candidate fixture index is invalid JSON: $($_.Exception.Message)"
    }

    Assert-ExactJsonProperties `
        -Value $index `
        -Expected @("schema", "schemaVersion", "fixtures") `
        -Description "Candidate fixture index"
    if ($index.schema -isnot [string] `
        -or $index.schema -cne "openlineops.release-candidate-inspection-fixture-index" `
        -or $index.schemaVersion -isnot [int] `
        -or $index.schemaVersion -ne 1 `
        -or $index.fixtures -isnot [System.Array]) {
        throw "Candidate fixture index has an unsupported schema."
    }

    $fixtures = @($index.fixtures)
    if ($fixtures.Count -eq 0) {
        throw "Candidate fixture index must contain at least one fixture."
    }

    $names = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    $relativeDirectories = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    foreach ($fixture in $fixtures) {
        Assert-ExactJsonProperties `
            -Value $fixture `
            -Expected @("name", "relativeDirectory", "manifestRelativePath") `
            -Description "Candidate fixture index entry"
        if ($fixture.name -isnot [string] `
            -or $fixture.relativeDirectory -isnot [string] `
            -or $fixture.manifestRelativePath -isnot [string]) {
            throw "Candidate fixture index entry fields must be JSON strings."
        }

        if ([string]::IsNullOrWhiteSpace($fixture.name) -or -not $names.Add($fixture.name)) {
            throw "Candidate fixture index contains an empty or duplicate logical name '$($fixture.name)'."
        }

        $relativeDirectory = [string] $fixture.relativeDirectory
        if ([string]::IsNullOrWhiteSpace($relativeDirectory) `
            -or [System.IO.Path]::IsPathRooted($relativeDirectory) `
            -or $relativeDirectory.Contains('/') `
            -or $relativeDirectory.Contains('\') `
            -or $relativeDirectory -cin @(".", "..") `
            -or -not $relativeDirectories.Add($relativeDirectory)) {
            throw "Candidate fixture '$($fixture.name)' does not identify a unique direct-child directory."
        }

        $fixtureRoot = [System.IO.Path]::GetFullPath((Join-Path $CandidateWorkRoot $relativeDirectory))
        $fixtureParent = [System.IO.Path]::GetDirectoryName($fixtureRoot)
        $resolvedCandidateWorkRoot = [System.IO.Path]::GetFullPath($CandidateWorkRoot).TrimEnd('\', '/')
        if (-not $fixtureParent.Equals($resolvedCandidateWorkRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Candidate fixture '$($fixture.name)' is not a direct child of its work root."
        }

        $expectedManifestRelativePath = "$relativeDirectory/release-manifest.json"
        if ($fixture.manifestRelativePath -cne $expectedManifestRelativePath) {
            throw "Candidate fixture '$($fixture.name)' has non-canonical manifestRelativePath '$($fixture.manifestRelativePath)'."
        }

        $manifestPath = Join-Path $fixtureRoot "release-manifest.json"
        if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
            throw "Candidate fixture '$($fixture.name)' is missing its indexed release manifest."
        }

        try {
            $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
        }
        catch {
            throw "Candidate fixture '$($fixture.name)' has an invalid release manifest: $($_.Exception.Message)"
        }
        Assert-ExactJsonProperties `
            -Value $manifest `
            -Expected @("schemaVersion", "product", "version", "generatedAtUtc", "commit", "artifacts") `
            -Description "Candidate fixture '$($fixture.name)' release manifest"
        if ($manifest.schemaVersion -isnot [int] `
            -or $manifest.schemaVersion -ne 1 `
            -or $manifest.product -isnot [string] `
            -or $manifest.product -cne "OpenLineOps" `
            -or $manifest.version -isnot [string] `
            -or $manifest.generatedAtUtc -isnot [string] `
            -or $manifest.artifacts -isnot [System.Array]) {
            throw "Candidate fixture '$($fixture.name)' release manifest has an unsupported identity."
        }
    }

    $positiveFixtures = @($fixtures | Where-Object { $_.name -ceq "positive" })
    if ($positiveFixtures.Count -ne 1) {
        throw "Candidate fixture index must contain exactly one positive fixture."
    }

    $positiveRoot = [System.IO.Path]::GetFullPath(
        (Join-Path $CandidateWorkRoot $positiveFixtures[0].relativeDirectory))
    $positiveManifest = Get-Content `
        -LiteralPath (Join-Path $positiveRoot "release-manifest.json") `
        -Raw | ConvertFrom-Json
    $actualKinds = @($positiveManifest.artifacts | ForEach-Object { $_.kind } | Sort-Object)
    if (($actualKinds -join "`0") -cne (($ExpectedArtifactKinds | Sort-Object) -join "`0")) {
        throw "Indexed positive fixture release manifest does not contain the exact artifact kind set."
    }

    return $positiveRoot
}

function New-PublicationEvidence {
    param(
        [Parameter(Mandatory = $true)]$Manifest,
        [Parameter(Mandatory = $true)][string] $Root,
        [Parameter(Mandatory = $true)]$E2eEvidence,
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
            repository = "openlineops/openlineops"
            commitSha = "0123456789abcdef0123456789abcdef01234567"
            runId = "123456789"
            runUrl = "https://github.com/openlineops/openlineops/actions/runs/123456789"
            productionIntegrationConclusion = "success"
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
        e2eEvidence = $E2eEvidence
        pendingExternal = @("Windows executable signing proof remains external in this fixture.")
        internalFailures = @($InternalFailures)
        gates = $gates
    }
}

function New-E2eEvidenceRecord {
    param(
        [Parameter(Mandatory = $true)][string] $Root,
        [Parameter(Mandatory = $true)][string] $SourceRelativePath,
        [Parameter(Mandatory = $true)][string] $EmbeddedRelativePath
    )

    $sourcePath = Join-Path $Root $SourceRelativePath.Replace('/', [System.IO.Path]::DirectorySeparatorChar)
    $embeddedPath = Join-Path `
        (Join-Path $Root "output/publication-evidence") `
        $EmbeddedRelativePath.Replace('/', [System.IO.Path]::DirectorySeparatorChar)
    if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
        throw "Fixture E2E source does not exist: $sourcePath"
    }

    New-Item -ItemType Directory -Path (Split-Path -Parent $embeddedPath) -Force | Out-Null
    Copy-Item -LiteralPath $sourcePath -Destination $embeddedPath -Force
    $sourceFile = Get-Item -LiteralPath $sourcePath
    return [ordered]@{
        sourceRelativePath = $SourceRelativePath
        embeddedRelativePath = $EmbeddedRelativePath
        sizeBytes = $sourceFile.Length
        sha256 = Get-FileSha256 $sourcePath
    }
}

function Write-BundleEvidence {
    param([Parameter(Mandatory = $true)][string] $Root)

    $manifest = Get-Content -LiteralPath (Join-Path $Root "artifacts/release/release-manifest.json") -Raw | ConvertFrom-Json
    $stagedAgentRoot = Join-Path $Root "output/staged-agent-bundle-e2e"
    Write-StagedAgentEvidenceFixture -Root $stagedAgentRoot
    $stagedAgentSourceRelativePath = "output/staged-agent-bundle-e2e/evidence.json"

    $productionClosureRunRoot = Join-Path $Root "artifacts/production-closure-e2e/fixture"
    Write-ProductionClosureEvidenceFixture -RunRoot $productionClosureRunRoot
    $productionClosureSourceRelativePath = "artifacts/production-closure-e2e/fixture/summary.json"

    $studioTwoAgentRoot = Join-Path $Root "output/studio-two-agent-production-closure"
    & powershell `
        -NoProfile `
        -NonInteractive `
        -ExecutionPolicy Bypass `
        -File (Join-Path $PSScriptRoot "verify-studio-two-agent-production-evidence.tests.ps1") `
        -FixtureOutputRoot $studioTwoAgentRoot *> $null
    if ($LASTEXITCODE -ne 0) {
        throw "Could not create the strict Studio two-Agent evidence fixture."
    }
    $studioTwoAgentSourceRelativePath = "output/studio-two-agent-production-closure/evidence.json"
    $studioTwoAgentManifestSourceRelativePath = "output/studio-two-agent-production-closure/evidence-manifest.json"

    $runnerStagedAgentRoot = Join-Path $Root "output/runner-staged-agent-e2e"
    & powershell `
        -NoProfile `
        -NonInteractive `
        -ExecutionPolicy Bypass `
        -File (Join-Path $PSScriptRoot "verify-runner-staged-agent-evidence.tests.ps1") `
        -FixtureOutputRoot $runnerStagedAgentRoot *> $null
    if ($LASTEXITCODE -ne 0) {
        throw "Could not create the strict Runner staged-Agent evidence fixture."
    }
    $runnerStagedAgentSourceRelativePath = "output/runner-staged-agent-e2e/evidence.json"
    $runnerStagedAgentTrxSourceRelativePath = "output/runner-staged-agent-e2e/test-results/runner-staged-agent-e2e.trx"

    $productionIntegrationTrxSourceRelativePath = "output/production-integration-evidence/production-integration.trx"
    $productionIntegrationTrxSourcePath = Join-Path $Root $productionIntegrationTrxSourceRelativePath.Replace('/', [System.IO.Path]::DirectorySeparatorChar)
    $requiredProductionIntegrationTest = "OpenLineOps.PostgresIntegration.Tests.PostgresRabbitMqProductionCoordinationIntegrationTests.DurableOutboxAndResultInboxSurviveCoordinatorRestartAcrossRealBroker"
    Write-Utf8NoBom `
        -Path $productionIntegrationTrxSourcePath `
        -Content @"
<?xml version="1.0" encoding="utf-8"?>
<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
  <Results>
    <UnitTestResult testName="$requiredProductionIntegrationTest" outcome="Passed" />
    <UnitTestResult testName="OpenLineOps.PostgresIntegration.Tests.Fixture.CompanionPassed" outcome="Passed" />
  </Results>
  <ResultSummary outcome="Completed">
    <Counters total="2" executed="2" passed="2" failed="0" notExecuted="0" />
  </ResultSummary>
</TestRun>
"@
    $productionIntegrationSourceRelativePath = "output/production-integration-evidence/integration-evidence.json"
    $productionIntegrationSourcePath = Join-Path $Root $productionIntegrationSourceRelativePath.Replace('/', [System.IO.Path]::DirectorySeparatorChar)
    Write-Json `
        -Path $productionIntegrationSourcePath `
        -Value ([ordered]@{
            schemaVersion = 1
            generatedAtUtc = [System.DateTimeOffset]::UtcNow.ToString("O")
            product = "OpenLineOps"
            repository = "openlineops/openlineops"
            commitSha = "0123456789abcdef0123456789abcdef01234567"
            runId = "123456789"
            runUrl = "https://github.com/openlineops/openlineops/actions/runs/123456789"
            jobName = "production-integration"
            testName = $requiredProductionIntegrationTest
            conclusion = "success"
            counters = [ordered]@{
                total = 2
                executed = 2
                passed = 2
                failed = 0
                skipped = 0
            }
            trx = [ordered]@{
                relativePath = $productionIntegrationTrxSourceRelativePath
                sizeBytes = (Get-Item -LiteralPath $productionIntegrationTrxSourcePath).Length
                sha256 = Get-FileSha256 $productionIntegrationTrxSourcePath
            }
        })

    $e2eEvidence = [ordered]@{
        stagedAgentBundle = New-E2eEvidenceRecord `
            -Root $Root `
            -SourceRelativePath $stagedAgentSourceRelativePath `
            -EmbeddedRelativePath "e2e-evidence/staged-agent-bundle.json"
        productionClosure = New-E2eEvidenceRecord `
            -Root $Root `
            -SourceRelativePath $productionClosureSourceRelativePath `
            -EmbeddedRelativePath "e2e-evidence/production-closure.json"
        studioTwoAgent = New-E2eEvidenceRecord `
            -Root $Root `
            -SourceRelativePath $studioTwoAgentSourceRelativePath `
            -EmbeddedRelativePath "e2e-evidence/studio-two-agent.json"
        studioTwoAgentManifest = New-E2eEvidenceRecord `
            -Root $Root `
            -SourceRelativePath $studioTwoAgentManifestSourceRelativePath `
            -EmbeddedRelativePath "e2e-evidence/studio-two-agent-manifest.json"
        runnerStagedAgent = New-E2eEvidenceRecord `
            -Root $Root `
            -SourceRelativePath $runnerStagedAgentSourceRelativePath `
            -EmbeddedRelativePath "e2e-evidence/runner-staged-agent.json"
        runnerStagedAgentTrx = New-E2eEvidenceRecord `
            -Root $Root `
            -SourceRelativePath $runnerStagedAgentTrxSourceRelativePath `
            -EmbeddedRelativePath "e2e-evidence/runner-staged-agent.trx"
        productionIntegration = New-E2eEvidenceRecord `
            -Root $Root `
            -SourceRelativePath $productionIntegrationSourceRelativePath `
            -EmbeddedRelativePath "e2e-evidence/production-integration.json"
        productionIntegrationTrx = New-E2eEvidenceRecord `
            -Root $Root `
            -SourceRelativePath $productionIntegrationTrxSourceRelativePath `
            -EmbeddedRelativePath "e2e-evidence/production-integration.trx"
        recoveryComposition = [ordered]@{
            stagedWindowsAgentBoundary = "Published Windows Agent process, signed vendor helper, broker outage, durable SQLite Inbox/Outbox, presence TTL, and transport result-inbox restart"
            durableCoordinatorRecoveryBoundary = "PostgreSQL coordination store and RabbitMQ transport survive Coordinator transport/store cold restart exactly once"
            productionIntegrationWorkflowJob = "production-integration"
            productionIntegrationTest = $requiredProductionIntegrationTest
            proofRepository = "openlineops/openlineops"
            proofCommitSha = "0123456789abcdef0123456789abcdef01234567"
            proofRunId = "123456789"
            proofRunUrl = "https://github.com/openlineops/openlineops/actions/runs/123456789"
            productionIntegrationConclusion = "success"
            releaseManifestSha256 = Get-FileSha256 (Join-Path $Root "artifacts/release/release-manifest.json")
            proofSupplied = $true
        }
    }

    $evidenceRoot = Join-Path $Root "output/publication-evidence"
    Write-Json `
        -Path (Join-Path $evidenceRoot "publication-evidence.json") `
        -Value (New-PublicationEvidence -Manifest $manifest -Root $Root -E2eEvidence $e2eEvidence)
    Write-Utf8NoBom -Path (Join-Path $evidenceRoot "publication-evidence.md") -Content "# Fixture publication evidence`r`n"

    foreach ($case in (Get-PublicationEvidenceCaseContract)) {
        $caseRoot = Join-Path `
            $Root `
            ("output/publication-evidence-verification/" + $case.RelativeDirectory)
        $failures = switch ($case.Name) {
            "invalid-production-integration-evidence" {
                @("Production integration evidence commit does not match a clean release provenance source.")
            }
            "invalid-production-integration-trx" {
                @("Production integration TRX result records do not match its all-passed counters.")
            }
            default {
                @()
            }
        }

        Write-Json `
            -Path (Join-Path $caseRoot "publication-evidence.json") `
            -Value (New-PublicationEvidence `
                -Manifest $manifest `
                -Root $Root `
                -E2eEvidence $e2eEvidence `
                -InternalFailures $failures)
        Write-Utf8NoBom -Path (Join-Path $caseRoot "publication-evidence.md") -Content "# Fixture evidence case`r`n"
    }

    $preflight = [ordered]@{
        schemaVersion = 1
        generatedAtUtc = [System.DateTimeOffset]::UtcNow.ToString("O")
        product = "OpenLineOps"
        workRoot = (Join-Path $Root "output/final-publication-preflight")
        cases = @(
            [ordered]@{ name = "missing-license-confirmation"; exitCode = 1; expected = "fail"; output = "expected failure" },
            [ordered]@{ name = "missing-production-integration-evidence"; exitCode = 1; expected = "fail"; output = "expected failure" },
            [ordered]@{ name = "missing-signing-selector"; exitCode = 1; expected = "fail"; output = "expected failure" },
            [ordered]@{ name = "valid-plan"; exitCode = 0; expected = "pass"; output = "expected pass" })
    }
    $preflightRoot = Join-Path $Root "output/final-publication-preflight"
    Write-Json -Path (Join-Path $preflightRoot "publication-preflight.json") -Value $preflight
    Write-Utf8NoBom -Path (Join-Path $preflightRoot "publication-preflight.md") -Content "# Fixture preflight`r`n"
}

$script:FixtureBundleOrdinal = 0
$script:FixtureInspectionOrdinal = 0

function New-Bundle {
    param(
        [Parameter(Mandatory = $true)][string] $Name,
        [Parameter(Mandatory = $true)][string] $CandidateRoot
    )

    $script:FixtureBundleOrdinal++
    $root = Join-Path $ResolvedWorkRoot (
        "b/{0:D2}" -f $script:FixtureBundleOrdinal)
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

    $script:FixtureInspectionOrdinal++
    $inspectionRoot = Join-Path $ResolvedWorkRoot (
        "i/{0:D2}" -f $script:FixtureInspectionOrdinal)
    $result = Invoke-GitHubFixturePowerShellProcess `
        -ScriptPath $InspectorScript `
        -Arguments @("-BundleRoot", $Root, "-WorkRoot", $inspectionRoot) `
        -GitHubEnvironment $FixtureGitHubEnvironment

    return [pscustomobject]@{
        ExitCode = $result.ExitCode
        Text = $result.Text
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

function Sync-ProductionIntegrationEvidenceBindings {
    param([Parameter(Mandatory = $true)][string] $Root)

    $sourceRoot = Join-Path $Root "output/production-integration-evidence"
    $publicationRoot = Join-Path $Root "output/publication-evidence"
    $trxPath = Join-Path $sourceRoot "production-integration.trx"
    $integrationPath = Join-Path $sourceRoot "integration-evidence.json"
    $publicationPath = Join-Path $publicationRoot "publication-evidence.json"
    $integration = Get-Content -LiteralPath $integrationPath -Raw | ConvertFrom-Json
    $integration.trx.sizeBytes = (Get-Item -LiteralPath $trxPath).Length
    $integration.trx.sha256 = Get-FileSha256 $trxPath
    Write-Json -Path $integrationPath -Value $integration

    $embeddedRoot = Join-Path $publicationRoot "e2e-evidence"
    Copy-Item -LiteralPath $integrationPath -Destination (Join-Path $embeddedRoot "production-integration.json") -Force
    Copy-Item -LiteralPath $trxPath -Destination (Join-Path $embeddedRoot "production-integration.trx") -Force

    $publication = Get-Content -LiteralPath $publicationPath -Raw | ConvertFrom-Json
    $publication.e2eEvidence.productionIntegration.sizeBytes = (Get-Item -LiteralPath $integrationPath).Length
    $publication.e2eEvidence.productionIntegration.sha256 = Get-FileSha256 $integrationPath
    $publication.e2eEvidence.productionIntegrationTrx.sizeBytes = (Get-Item -LiteralPath $trxPath).Length
    $publication.e2eEvidence.productionIntegrationTrx.sha256 = Get-FileSha256 $trxPath
    Write-Json -Path $publicationPath -Value $publication
}

function Sync-PublicationE2eEvidenceBinding {
    param(
        [Parameter(Mandatory = $true)][string] $Root,
        [Parameter(Mandatory = $true)][string] $RecordName,
        [Parameter(Mandatory = $true)][string] $SourceRelativePath,
        [Parameter(Mandatory = $true)][string] $EmbeddedRelativePath
    )

    $sourcePath = Join-Path `
        $Root `
        $SourceRelativePath.Replace('/', [System.IO.Path]::DirectorySeparatorChar)
    $publicationRoot = Join-Path $Root "output/publication-evidence"
    $publicationPath = Join-Path $publicationRoot "publication-evidence.json"
    $embeddedPath = Join-Path `
        $publicationRoot `
        $EmbeddedRelativePath.Replace('/', [System.IO.Path]::DirectorySeparatorChar)
    Copy-Item -LiteralPath $sourcePath -Destination $embeddedPath -Force

    $publication = Get-Content -LiteralPath $publicationPath -Raw | ConvertFrom-Json
    $record = $publication.e2eEvidence.PSObject.Properties[$RecordName].Value
    if ($null -eq $record) {
        throw "Publication fixture does not contain E2E evidence record '$RecordName'."
    }

    $record.sizeBytes = (Get-Item -LiteralPath $sourcePath).Length
    $record.sha256 = Get-FileSha256 $sourcePath
    Write-Json -Path $publicationPath -Value $publication
}

function Assert-FixtureIndexMutationFails {
    param(
        [Parameter(Mandatory = $true)][string] $CandidateWorkRoot,
        [Parameter(Mandatory = $true)][string] $Name,
        [Parameter(Mandatory = $true)][string] $Mutation,
        [Parameter(Mandatory = $true)][string] $ExpectedPattern
    )

    $indexPath = Join-Path $CandidateWorkRoot "fixture-index.json"
    $original = [System.IO.File]::ReadAllText($indexPath)
    try {
        $index = $original | ConvertFrom-Json
        $positive = @($index.fixtures | Where-Object { $_.name -ceq "positive" }) | Select-Object -First 1
        switch ($Mutation) {
            "schema-property-case" {
                $schema = $index.schema
                $index.PSObject.Properties.Remove("schema")
                $index | Add-Member -NotePropertyName "Schema" -NotePropertyValue $schema
            }
            "parent-directory" {
                $positive.relativeDirectory = "../escape"
                $positive.manifestRelativePath = "../escape/release-manifest.json"
            }
            "manifest-path-case" {
                $positive.manifestRelativePath = "$($positive.relativeDirectory)/Release-Manifest.json"
            }
            default {
                throw "Unsupported fixture index mutation '$Mutation'."
            }
        }

        Write-Json -Path $indexPath -Value $index
        try {
            Resolve-PositiveCandidateFixture -CandidateWorkRoot $CandidateWorkRoot | Out-Null
            throw "Mutated candidate fixture index '$Name' was unexpectedly accepted."
        }
        catch {
            if ($_.Exception.Message -cnotmatch $ExpectedPattern) {
                throw "Mutated candidate fixture index '$Name' failed for an unexpected reason: $($_.Exception.Message)"
            }
        }
    }
    finally {
        Write-Utf8NoBom -Path $indexPath -Content $original
    }

    Write-Host "Candidate fixture index mutation '$Name' failed as expected."
}

$ResolvedWorkRoot = Resolve-RepoPath $WorkRoot
New-CleanDirectory $ResolvedWorkRoot
$candidateWorkRoot = Join-Path $ResolvedWorkRoot "release-candidate-fixtures"
$fixtureIndexPath = Join-Path $candidateWorkRoot "fixture-index.json"
if (-not ($SkipClean -and (Test-Path -LiteralPath $fixtureIndexPath -PathType Leaf))) {
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
Assert-FixtureIndexMutationFails `
    -CandidateWorkRoot $candidateWorkRoot `
    -Name "schema-property-case" `
    -Mutation "schema-property-case" `
    -ExpectedPattern "must contain exactly"
Assert-FixtureIndexMutationFails `
    -CandidateWorkRoot $candidateWorkRoot `
    -Name "parent-directory" `
    -Mutation "parent-directory" `
    -ExpectedPattern "direct-child directory"
Assert-FixtureIndexMutationFails `
    -CandidateWorkRoot $candidateWorkRoot `
    -Name "manifest-path-case" `
    -Mutation "manifest-path-case" `
    -ExpectedPattern "non-canonical manifestRelativePath"
$candidateRoot = Resolve-PositiveCandidateFixture -CandidateWorkRoot $candidateWorkRoot

$positiveRoot = New-Bundle -Name "positive" -CandidateRoot $candidateRoot
Assert-Passes -Result (Invoke-Inspection -Root $positiveRoot -Name "positive") -Name "positive"

$sourceTamperRoot = New-Bundle -Name "e2e-source-tamper" -CandidateRoot $candidateRoot
$sourceTamperPath = Join-Path $sourceTamperRoot "output/staged-agent-bundle-e2e/evidence.json"
Write-Utf8NoBom `
    -Path $sourceTamperPath `
    -Content ([System.IO.File]::ReadAllText($sourceTamperPath) + " ")
Assert-FailsWith `
    -Result (Invoke-Inspection -Root $sourceTamperRoot -Name "e2e-source-tamper") `
    -Name "e2e-source-tamper" `
    -Pattern "Publication stagedAgentBundle E2E evidence source, embedded copy, size, or SHA-256 does not match"

$embeddedTamperRoot = New-Bundle -Name "e2e-embedded-tamper" -CandidateRoot $candidateRoot
$embeddedTamperPath = Join-Path $embeddedTamperRoot "output/publication-evidence/e2e-evidence/staged-agent-bundle.json"
Write-Utf8NoBom `
    -Path $embeddedTamperPath `
    -Content ([System.IO.File]::ReadAllText($embeddedTamperPath) + " ")
Assert-FailsWith `
    -Result (Invoke-Inspection -Root $embeddedTamperRoot -Name "e2e-embedded-tamper") `
    -Name "e2e-embedded-tamper" `
    -Pattern "Publication stagedAgentBundle E2E evidence source, embedded copy, size, or SHA-256 does not match"

$hashTamperRoot = New-Bundle -Name "e2e-hash-tamper" -CandidateRoot $candidateRoot
$hashTamperEvidencePath = Join-Path $hashTamperRoot "output/publication-evidence/publication-evidence.json"
$hashTamperEvidence = Get-Content -LiteralPath $hashTamperEvidencePath -Raw | ConvertFrom-Json
$hashTamperEvidence.e2eEvidence.stagedAgentBundle.sha256 = "0000000000000000000000000000000000000000000000000000000000000000"
Write-Json -Path $hashTamperEvidencePath -Value $hashTamperEvidence
Assert-FailsWith `
    -Result (Invoke-Inspection -Root $hashTamperRoot -Name "e2e-hash-tamper") `
    -Name "e2e-hash-tamper" `
    -Pattern "Publication stagedAgentBundle E2E evidence source, embedded copy, size, or SHA-256 does not match"

$stagedSemanticDowngradeRoot = New-Bundle `
    -Name "fully-rebound-staged-material-arrival-downgrade" `
    -CandidateRoot $candidateRoot
$stagedRawPath = Join-Path `
    $stagedSemanticDowngradeRoot `
    "output/staged-agent-bundle-e2e/rabbitmq-process/evidence.json"
$stagedRaw = Get-Content -LiteralPath $stagedRawPath -Raw | ConvertFrom-Json
$stagedRaw.materialArrivalIpc.durablePublicationVerified = $false
Write-Json -Path $stagedRawPath -Value $stagedRaw
$stagedSummaryPath = Join-Path `
    $stagedSemanticDowngradeRoot `
    "output/staged-agent-bundle-e2e/evidence.json"
$stagedSummary = Get-Content -LiteralPath $stagedSummaryPath -Raw | ConvertFrom-Json
$stagedSummary.rabbitMqTransportCoverage.materialArrivalIpc.durablePublicationVerified = $false
$stagedSummary.rabbitMqTransportCoverage.evidenceSha256 = Get-FileSha256 $stagedRawPath
Write-Json -Path $stagedSummaryPath -Value $stagedSummary
Sync-PublicationE2eEvidenceBinding `
    -Root $stagedSemanticDowngradeRoot `
    -RecordName "stagedAgentBundle" `
    -SourceRelativePath "output/staged-agent-bundle-e2e/evidence.json" `
    -EmbeddedRelativePath "e2e-evidence/staged-agent-bundle.json"
Assert-FailsWith `
    -Result (Invoke-Inspection `
        -Root $stagedSemanticDowngradeRoot `
        -Name "fully-rebound-staged-material-arrival-downgrade") `
    -Name "fully-rebound-staged-material-arrival-downgrade" `
    -Pattern "durablePublicationVerified.*JSON boolean true"

$stagedImmutableDowngradeRoot = New-Bundle `
    -Name "fully-rebound-staged-immutable-cache-downgrade" `
    -CandidateRoot $candidateRoot
$stagedImmutableRawPath = Join-Path `
    $stagedImmutableDowngradeRoot `
    "output/staged-agent-bundle-e2e/rabbitmq-process/evidence.json"
$stagedImmutableRaw = Get-Content -LiteralPath $stagedImmutableRawPath -Raw |
    ConvertFrom-Json
$stagedImmutableRaw.immutableContentCache.packagedRemovalCommandVerified = $false
Write-Json -Path $stagedImmutableRawPath -Value $stagedImmutableRaw
$stagedImmutableSummaryPath = Join-Path `
    $stagedImmutableDowngradeRoot `
    "output/staged-agent-bundle-e2e/evidence.json"
$stagedImmutableSummary = Get-Content -LiteralPath $stagedImmutableSummaryPath -Raw |
    ConvertFrom-Json
$stagedImmutableSummary.rabbitMqTransportCoverage.immutableContentCache.packagedRemovalCommandVerified =
    $false
$stagedImmutableSummary.rabbitMqTransportCoverage.evidenceSha256 =
    Get-FileSha256 $stagedImmutableRawPath
Write-Json -Path $stagedImmutableSummaryPath -Value $stagedImmutableSummary
Sync-PublicationE2eEvidenceBinding `
    -Root $stagedImmutableDowngradeRoot `
    -RecordName "stagedAgentBundle" `
    -SourceRelativePath "output/staged-agent-bundle-e2e/evidence.json" `
    -EmbeddedRelativePath "e2e-evidence/staged-agent-bundle.json"
Assert-FailsWith `
    -Result (Invoke-Inspection `
        -Root $stagedImmutableDowngradeRoot `
        -Name "fully-rebound-staged-immutable-cache-downgrade") `
    -Name "fully-rebound-staged-immutable-cache-downgrade" `
    -Pattern "packagedRemovalCommandVerified.*JSON boolean true"

$stagedTruthyBooleanRoot = New-Bundle `
    -Name "fully-rebound-staged-truthy-identity" `
    -CandidateRoot $candidateRoot
$stagedTruthyRawPath = Join-Path `
    $stagedTruthyBooleanRoot `
    "output/staged-agent-bundle-e2e/rabbitmq-process/evidence.json"
$stagedTruthyRaw = Get-Content -LiteralPath $stagedTruthyRawPath -Raw |
    ConvertFrom-Json
$stagedTruthyRaw.agentHostIdentity.NonAdministrative = 1
Write-Json -Path $stagedTruthyRawPath -Value $stagedTruthyRaw
$stagedTruthySummaryPath = Join-Path `
    $stagedTruthyBooleanRoot `
    "output/staged-agent-bundle-e2e/evidence.json"
$stagedTruthySummary = Get-Content -LiteralPath $stagedTruthySummaryPath -Raw |
    ConvertFrom-Json
$stagedTruthySummary.rabbitMqTransportCoverage.agentHostIdentity.nonAdministrative = 1
$stagedTruthySummary.rabbitMqTransportCoverage.evidenceSha256 =
    Get-FileSha256 $stagedTruthyRawPath
Write-Json -Path $stagedTruthySummaryPath -Value $stagedTruthySummary
Sync-PublicationE2eEvidenceBinding `
    -Root $stagedTruthyBooleanRoot `
    -RecordName "stagedAgentBundle" `
    -SourceRelativePath "output/staged-agent-bundle-e2e/evidence.json" `
    -EmbeddedRelativePath "e2e-evidence/staged-agent-bundle.json"
Assert-FailsWith `
    -Result (Invoke-Inspection `
        -Root $stagedTruthyBooleanRoot `
        -Name "fully-rebound-staged-truthy-identity") `
    -Name "fully-rebound-staged-truthy-identity" `
    -Pattern "nonAdministrative.*JSON boolean true"

$stagedLinkedTokenRoot = New-Bundle `
    -Name "fully-rebound-staged-linked-token-downgrade" `
    -CandidateRoot $candidateRoot
$stagedLinkedTokenRawPath = Join-Path `
    $stagedLinkedTokenRoot `
    "output/staged-agent-bundle-e2e/rabbitmq-process/evidence.json"
$stagedLinkedTokenRaw = Get-Content -LiteralPath $stagedLinkedTokenRawPath -Raw |
    ConvertFrom-Json
$stagedLinkedTokenRaw.agentHostIdentity.HasLinkedToken = $true
Write-Json -Path $stagedLinkedTokenRawPath -Value $stagedLinkedTokenRaw
$stagedLinkedTokenSummaryPath = Join-Path `
    $stagedLinkedTokenRoot `
    "output/staged-agent-bundle-e2e/evidence.json"
$stagedLinkedTokenSummary = Get-Content `
    -LiteralPath $stagedLinkedTokenSummaryPath `
    -Raw | ConvertFrom-Json
$stagedLinkedTokenSummary.rabbitMqTransportCoverage.agentHostIdentity.hasLinkedToken =
    $true
$stagedLinkedTokenSummary.rabbitMqTransportCoverage.evidenceSha256 =
    Get-FileSha256 $stagedLinkedTokenRawPath
Write-Json -Path $stagedLinkedTokenSummaryPath -Value $stagedLinkedTokenSummary
Sync-PublicationE2eEvidenceBinding `
    -Root $stagedLinkedTokenRoot `
    -RecordName "stagedAgentBundle" `
    -SourceRelativePath "output/staged-agent-bundle-e2e/evidence.json" `
    -EmbeddedRelativePath "e2e-evidence/staged-agent-bundle.json"
Assert-FailsWith `
    -Result (Invoke-Inspection `
        -Root $stagedLinkedTokenRoot `
        -Name "fully-rebound-staged-linked-token-downgrade") `
    -Name "fully-rebound-staged-linked-token-downgrade" `
    -Pattern "hasLinkedToken.*JSON boolean false"

$studioSourceTamperRoot = New-Bundle -Name "studio-two-agent-source-tamper" -CandidateRoot $candidateRoot
$studioSourceTamperPath = Join-Path $studioSourceTamperRoot "output/studio-two-agent-production-closure/evidence.json"
Write-Utf8NoBom `
    -Path $studioSourceTamperPath `
    -Content ([System.IO.File]::ReadAllText($studioSourceTamperPath) + " ")
Assert-FailsWith `
    -Result (Invoke-Inspection -Root $studioSourceTamperRoot -Name "studio-two-agent-source-tamper") `
    -Name "studio-two-agent-source-tamper" `
    -Pattern "Studio two-Agent evidence failed strict validation|Publication studioTwoAgent E2E evidence source, embedded copy, size, or SHA-256 does not match"

$studioEmbeddedTamperRoot = New-Bundle -Name "studio-two-agent-embedded-tamper" -CandidateRoot $candidateRoot
$studioEmbeddedTamperPath = Join-Path $studioEmbeddedTamperRoot "output/publication-evidence/e2e-evidence/studio-two-agent.json"
Write-Utf8NoBom `
    -Path $studioEmbeddedTamperPath `
    -Content ([System.IO.File]::ReadAllText($studioEmbeddedTamperPath) + " ")
Assert-FailsWith `
    -Result (Invoke-Inspection -Root $studioEmbeddedTamperRoot -Name "studio-two-agent-embedded-tamper") `
    -Name "studio-two-agent-embedded-tamper" `
    -Pattern "Publication studioTwoAgent E2E evidence source, embedded copy, size, or SHA-256 does not match"

$studioSemanticDowngradeRoot = New-Bundle `
    -Name "fully-rebound-studio-windows-identity-downgrade" `
    -CandidateRoot $candidateRoot
$studioEvidencePath = Join-Path `
    $studioSemanticDowngradeRoot `
    "output/studio-two-agent-production-closure/evidence.json"
$studioEvidence = Get-Content -LiteralPath $studioEvidencePath -Raw | ConvertFrom-Json
$studioEvidence.windowsIdentity.entryPipeExactAclVerified = $false
Write-Json -Path $studioEvidencePath -Value $studioEvidence
$studioManifestPath = Join-Path `
    $studioSemanticDowngradeRoot `
    "output/studio-two-agent-production-closure/evidence-manifest.json"
$studioManifest = Get-Content -LiteralPath $studioManifestPath -Raw | ConvertFrom-Json
$studioManifest.files[0].sizeBytes = (Get-Item -LiteralPath $studioEvidencePath).Length
$studioManifest.files[0].sha256 = Get-FileSha256 $studioEvidencePath
Write-Json -Path $studioManifestPath -Value $studioManifest
Sync-PublicationE2eEvidenceBinding `
    -Root $studioSemanticDowngradeRoot `
    -RecordName "studioTwoAgent" `
    -SourceRelativePath "output/studio-two-agent-production-closure/evidence.json" `
    -EmbeddedRelativePath "e2e-evidence/studio-two-agent.json"
Sync-PublicationE2eEvidenceBinding `
    -Root $studioSemanticDowngradeRoot `
    -RecordName "studioTwoAgentManifest" `
    -SourceRelativePath "output/studio-two-agent-production-closure/evidence-manifest.json" `
    -EmbeddedRelativePath "e2e-evidence/studio-two-agent-manifest.json"
Assert-FailsWith `
    -Result (Invoke-Inspection `
        -Root $studioSemanticDowngradeRoot `
        -Name "fully-rebound-studio-windows-identity-downgrade") `
    -Name "fully-rebound-studio-windows-identity-downgrade" `
    -Pattern "entryPipeExactAclVerified.*JSON boolean true"

$studioTruthyBooleanRoot = New-Bundle `
    -Name "fully-rebound-studio-truthy-parallel-proof" `
    -CandidateRoot $candidateRoot
$studioTruthyEvidencePath = Join-Path `
    $studioTruthyBooleanRoot `
    "output/studio-two-agent-production-closure/evidence.json"
$studioTruthyEvidence = Get-Content -LiteralPath $studioTruthyEvidencePath -Raw |
    ConvertFrom-Json
$studioTruthyEvidence.parallelExecution.observed = "true"
Write-Json -Path $studioTruthyEvidencePath -Value $studioTruthyEvidence
$studioTruthyManifestPath = Join-Path `
    $studioTruthyBooleanRoot `
    "output/studio-two-agent-production-closure/evidence-manifest.json"
$studioTruthyManifest = Get-Content -LiteralPath $studioTruthyManifestPath -Raw |
    ConvertFrom-Json
$studioTruthyManifest.files[0].sizeBytes =
    (Get-Item -LiteralPath $studioTruthyEvidencePath).Length
$studioTruthyManifest.files[0].sha256 = Get-FileSha256 $studioTruthyEvidencePath
Write-Json -Path $studioTruthyManifestPath -Value $studioTruthyManifest
Sync-PublicationE2eEvidenceBinding `
    -Root $studioTruthyBooleanRoot `
    -RecordName "studioTwoAgent" `
    -SourceRelativePath "output/studio-two-agent-production-closure/evidence.json" `
    -EmbeddedRelativePath "e2e-evidence/studio-two-agent.json"
Sync-PublicationE2eEvidenceBinding `
    -Root $studioTruthyBooleanRoot `
    -RecordName "studioTwoAgentManifest" `
    -SourceRelativePath "output/studio-two-agent-production-closure/evidence-manifest.json" `
    -EmbeddedRelativePath "e2e-evidence/studio-two-agent-manifest.json"
Assert-FailsWith `
    -Result (Invoke-Inspection `
        -Root $studioTruthyBooleanRoot `
        -Name "fully-rebound-studio-truthy-parallel-proof") `
    -Name "fully-rebound-studio-truthy-parallel-proof" `
    -Pattern "observed.*JSON boolean true"

$runnerSourceTamperRoot = New-Bundle -Name "runner-staged-agent-source-tamper" -CandidateRoot $candidateRoot
$runnerSourceTamperPath = Join-Path $runnerSourceTamperRoot "output/runner-staged-agent-e2e/evidence.json"
Write-Utf8NoBom `
    -Path $runnerSourceTamperPath `
    -Content ([System.IO.File]::ReadAllText($runnerSourceTamperPath) + " ")
Assert-FailsWith `
    -Result (Invoke-Inspection -Root $runnerSourceTamperRoot -Name "runner-staged-agent-source-tamper") `
    -Name "runner-staged-agent-source-tamper" `
    -Pattern "Publication runnerStagedAgent E2E evidence source, embedded copy, size, or SHA-256 does not match"

$runnerTrxTamperRoot = New-Bundle -Name "runner-staged-agent-trx-tamper" -CandidateRoot $candidateRoot
$runnerTrxTamperPath = Join-Path $runnerTrxTamperRoot "output/runner-staged-agent-e2e/test-results/runner-staged-agent-e2e.trx"
$runnerTrxTamper = [System.IO.File]::ReadAllText($runnerTrxTamperPath).Replace(
    'outcome="Passed"',
    'outcome="NotExecuted"')
Write-Utf8NoBom -Path $runnerTrxTamperPath -Content $runnerTrxTamper
Assert-FailsWith `
    -Result (Invoke-Inspection -Root $runnerTrxTamperRoot -Name "runner-staged-agent-trx-tamper") `
    -Name "runner-staged-agent-trx-tamper" `
    -Pattern "Runner staged-Agent evidence failed strict validation|Publication runnerStagedAgentTrx E2E evidence source, embedded copy, size, or SHA-256 does not match"

$runnerTruthyBooleanRoot = New-Bundle `
    -Name "fully-rebound-runner-truthy-manifest-binding" `
    -CandidateRoot $candidateRoot
$runnerTruthyEvidencePath = Join-Path `
    $runnerTruthyBooleanRoot `
    "output/runner-staged-agent-e2e/evidence.json"
$runnerTruthyEvidence = Get-Content -LiteralPath $runnerTruthyEvidencePath -Raw |
    ConvertFrom-Json
$runnerTruthyEvidence.execution.runner.manifestBound = 1
Write-Json -Path $runnerTruthyEvidencePath -Value $runnerTruthyEvidence
Sync-PublicationE2eEvidenceBinding `
    -Root $runnerTruthyBooleanRoot `
    -RecordName "runnerStagedAgent" `
    -SourceRelativePath "output/runner-staged-agent-e2e/evidence.json" `
    -EmbeddedRelativePath "e2e-evidence/runner-staged-agent.json"
Assert-FailsWith `
    -Result (Invoke-Inspection `
        -Root $runnerTruthyBooleanRoot `
        -Name "fully-rebound-runner-truthy-manifest-binding") `
    -Name "fully-rebound-runner-truthy-manifest-binding" `
    -Pattern "manifestBound.*JSON boolean true"

$duplicateClosureRoot = New-Bundle -Name "duplicate-production-closure" -CandidateRoot $candidateRoot
$duplicateClosurePath = Join-Path $duplicateClosureRoot "artifacts/production-closure-e2e/duplicate/summary.json"
New-Item -ItemType Directory -Path (Split-Path -Parent $duplicateClosurePath) -Force | Out-Null
Copy-Item `
    -LiteralPath (Join-Path $duplicateClosureRoot "artifacts/production-closure-e2e/fixture/summary.json") `
    -Destination $duplicateClosurePath
Assert-FailsWith `
    -Result (Invoke-Inspection -Root $duplicateClosureRoot -Name "duplicate-production-closure") `
    -Name "duplicate-production-closure" `
    -Pattern "must contain exactly one packaged production closure summary; found 2"

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

$publishableTruthyRoot = New-Bundle -Name "publication-publishable-truthy" -CandidateRoot $candidateRoot
$publishableTruthyPath = Join-Path `
    $publishableTruthyRoot `
    "output/publication-evidence/publication-evidence.json"
$publishableTruthyEvidence = Get-Content -LiteralPath $publishableTruthyPath -Raw |
    ConvertFrom-Json
$publishableTruthyEvidence.publishable = 1
Write-Json -Path $publishableTruthyPath -Value $publishableTruthyEvidence
Assert-FailsWith `
    -Result (Invoke-Inspection `
        -Root $publishableTruthyRoot `
        -Name "publication-publishable-truthy") `
    -Name "publication-publishable-truthy" `
    -Pattern "publishable must be a JSON boolean"

$pendingAllowedTruthyRoot = New-Bundle `
    -Name "publication-pending-allowed-truthy" `
    -CandidateRoot $candidateRoot
$pendingAllowedTruthyPath = Join-Path `
    $pendingAllowedTruthyRoot `
    "output/publication-evidence/publication-evidence.json"
$pendingAllowedTruthyEvidence = Get-Content -LiteralPath $pendingAllowedTruthyPath -Raw |
    ConvertFrom-Json
$pendingAllowedTruthyEvidence.gates[0].pendingAllowed = "false"
Write-Json -Path $pendingAllowedTruthyPath -Value $pendingAllowedTruthyEvidence
Assert-FailsWith `
    -Result (Invoke-Inspection `
        -Root $pendingAllowedTruthyRoot `
        -Name "publication-pending-allowed-truthy") `
    -Name "publication-pending-allowed-truthy" `
    -Pattern "pendingAllowed must be a JSON boolean"

$preflightNameRoot = New-Bundle -Name "preflight-name-case" -CandidateRoot $candidateRoot
Replace-InFile `
    -Path (Join-Path $preflightNameRoot "output/final-publication-preflight/publication-preflight.json") `
    -Pattern '"name"\s*:\s*"valid-plan"' `
    -Replacement '"name":"Valid-plan"'
Assert-FailsWith `
    -Result (Invoke-Inspection -Root $preflightNameRoot -Name "preflight-name-case") `
    -Name "preflight-name-case" `
    -Pattern "Final publication preflight case names were"

$semanticTrxRoot = New-Bundle -Name "production-integration-trx-result" -CandidateRoot $candidateRoot
Replace-InFile `
    -Path (Join-Path $semanticTrxRoot "output/production-integration-evidence/production-integration.trx") `
    -Pattern '(<UnitTestResult testName="OpenLineOps\.PostgresIntegration\.Tests\.PostgresRabbitMqProductionCoordinationIntegrationTests\.DurableOutboxAndResultInboxSurviveCoordinatorRestartAcrossRealBroker" outcome=")Passed(")' `
    -Replacement '${1}Failed${2}'
Sync-ProductionIntegrationEvidenceBindings -Root $semanticTrxRoot
Assert-FailsWith `
    -Result (Invoke-Inspection -Root $semanticTrxRoot -Name "production-integration-trx-result") `
    -Name "production-integration-trx-result" `
    -Pattern "Production integration TRX is invalid: TRX result records do not match its all-passed counters"

$arbitraryRunUrlRoot = New-Bundle -Name "production-integration-arbitrary-url" -CandidateRoot $candidateRoot
$arbitraryRunUrlIntegrationPath = Join-Path $arbitraryRunUrlRoot "output/production-integration-evidence/integration-evidence.json"
$arbitraryRunUrlIntegration = Get-Content -LiteralPath $arbitraryRunUrlIntegrationPath -Raw | ConvertFrom-Json
$arbitraryRunUrlIntegration.runUrl = "https://example.invalid/claimed-success"
Write-Json -Path $arbitraryRunUrlIntegrationPath -Value $arbitraryRunUrlIntegration
$arbitraryRunUrlPublicationPath = Join-Path $arbitraryRunUrlRoot "output/publication-evidence/publication-evidence.json"
$arbitraryRunUrlPublication = Get-Content -LiteralPath $arbitraryRunUrlPublicationPath -Raw | ConvertFrom-Json
$arbitraryRunUrlPublication.githubActions.runUrl = $arbitraryRunUrlIntegration.runUrl
$arbitraryRunUrlPublication.e2eEvidence.recoveryComposition.proofRunUrl = $arbitraryRunUrlIntegration.runUrl
Write-Json -Path $arbitraryRunUrlPublicationPath -Value $arbitraryRunUrlPublication
Sync-ProductionIntegrationEvidenceBindings -Root $arbitraryRunUrlRoot
Assert-FailsWith `
    -Result (Invoke-Inspection -Root $arbitraryRunUrlRoot -Name "production-integration-arbitrary-url") `
    -Name "production-integration-arbitrary-url" `
    -Pattern "Production integration evidence is not a successful zero-skip same-run TRX proof"

$dirtyProvenanceRoot = New-Bundle -Name "dirty-release-provenance" -CandidateRoot $candidateRoot
$dirtyProvenancePath = Join-Path $dirtyProvenanceRoot "artifacts/release/release-provenance.json"
$dirtyProvenance = Get-Content -LiteralPath $dirtyProvenancePath -Raw | ConvertFrom-Json
$dirtyProvenance.source.dirty = $true
Write-Json -Path $dirtyProvenancePath -Value $dirtyProvenance
Assert-FailsWith `
    -Result (Invoke-Inspection -Root $dirtyProvenanceRoot -Name "dirty-release-provenance") `
    -Name "dirty-release-provenance" `
    -Pattern "Release provenance must be clean and match the production integration commit and release manifest"

$recoveryManifestRoot = New-Bundle -Name "recovery-manifest-binding" -CandidateRoot $candidateRoot
$recoveryManifestEvidencePath = Join-Path $recoveryManifestRoot "output/publication-evidence/publication-evidence.json"
$recoveryManifestEvidence = Get-Content -LiteralPath $recoveryManifestEvidencePath -Raw | ConvertFrom-Json
$recoveryManifestEvidence.e2eEvidence.recoveryComposition.releaseManifestSha256 = "0000000000000000000000000000000000000000000000000000000000000000"
Write-Json -Path $recoveryManifestEvidencePath -Value $recoveryManifestEvidence
Assert-FailsWith `
    -Result (Invoke-Inspection -Root $recoveryManifestRoot -Name "recovery-manifest-binding") `
    -Name "recovery-manifest-binding" `
    -Pattern "Publication evidence is not bound to the successful same-run production integration proof and release manifest"

Write-Host "CI release artifact inspection verification passed."
exit 0

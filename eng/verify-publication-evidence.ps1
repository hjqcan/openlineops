param(
    [string] $ArtifactsRoot = "artifacts/release",

    [string] $WorkRoot = "output/publication-evidence-verification",

    [string] $StagedAgentEvidenceRoot = "output/staged-agent-bundle-e2e",

    [string] $ProductionClosureEvidenceRoot = "artifacts/production-closure-e2e",

    [string] $StudioTwoAgentEvidenceRoot = "output/studio-two-agent-production-closure",

    [string] $RunnerStagedAgentEvidenceRoot = "output/runner-staged-agent-e2e",

    [switch] $SkipClean
)

$ErrorActionPreference = "Stop"

$RepoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$EvidenceScript = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "write-publication-evidence.ps1"))
. (Join-Path $PSScriptRoot "github-fixture-process.ps1")
. (Join-Path $PSScriptRoot "publication-evidence-case-contract.ps1")

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
        [string[]] $Arguments = @(),
        [Parameter(Mandatory = $true)][hashtable] $GitHubEnvironment
    )

    $physicalName = Get-PublicationEvidenceCaseRelativeDirectory -Name $Name
    $outputRoot = Join-Path $ResolvedWorkRoot $physicalName
    $scriptArguments = @(
        "-ArtifactsRoot",
        $ResolvedArtifactsRoot,
        "-OutputRoot",
        $outputRoot,
        "-StagedAgentEvidenceRoot",
        $ResolvedStagedAgentEvidenceRoot,
        "-ProductionClosureEvidenceRoot",
        $ResolvedProductionClosureEvidenceRoot,
        "-StudioTwoAgentEvidenceRoot",
        $ResolvedStudioTwoAgentEvidenceRoot,
        "-RunnerStagedAgentEvidenceRoot",
        $ResolvedRunnerStagedAgentEvidenceRoot
    ) + $Arguments

    $result = Invoke-GitHubFixturePowerShellProcess `
        -ScriptPath $EvidenceScript `
        -Arguments $scriptArguments `
        -GitHubEnvironment $GitHubEnvironment

    return [pscustomobject]@{
        Name = $Name
        ExitCode = $result.ExitCode
        OutputRoot = $outputRoot
        Text = $result.Text
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

function Get-FileSha256 {
    param([Parameter(Mandatory = $true)][string] $Path)

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function New-ProductionIntegrationEvidenceFixture {
    param(
        [Parameter(Mandatory = $true)][string] $Root,
        [switch] $WrongCommit,
        [switch] $InvalidTrx
    )

    New-Item -ItemType Directory -Path $Root -Force | Out-Null
    $provenance = Get-Content `
        -LiteralPath (Join-Path $ResolvedArtifactsRoot "release-provenance.json") `
        -Raw | ConvertFrom-Json
    if ($provenance.source.available -ne $true `
        -or $provenance.source.dirty -ne $false `
        -or $provenance.source.commit -cnotmatch '^[0-9a-f]{40}$') {
        throw "Publication evidence verification requires clean commit-bound release provenance."
    }

    $trxPath = Join-Path $Root "production-integration.trx"
    $requiredTest = "OpenLineOps.PostgresIntegration.Tests.PostgresRabbitMqProductionCoordinationIntegrationTests.DurableOutboxAndResultInboxSurviveCoordinatorRestartAcrossRealBroker"
    $requiredOutcome = if ($InvalidTrx) { "Failed" } else { "Passed" }
    [System.IO.File]::WriteAllText(
        $trxPath,
        @"
<?xml version="1.0" encoding="utf-8"?>
<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
  <Results>
    <UnitTestResult testName="$requiredTest" outcome="$requiredOutcome" />
    <UnitTestResult testName="OpenLineOps.PostgresIntegration.Tests.Fixture.CompanionPassed" outcome="Passed" />
  </Results>
  <ResultSummary outcome="Completed">
    <Counters total="2" executed="2" passed="2" failed="0" notExecuted="0" />
  </ResultSummary>
</TestRun>
"@,
        [System.Text.UTF8Encoding]::new($false))
    $relativeTrxPath = $trxPath.Substring(
        $RepoRoot.TrimEnd('\', '/').Length + 1).Replace('\', '/')
    $commit = if ($WrongCommit) {
        "ffffffffffffffffffffffffffffffffffffffff"
    }
    else {
        [string] $provenance.source.commit
    }
    $document = [ordered]@{
        schemaVersion = 1
        generatedAtUtc = [System.DateTimeOffset]::UtcNow.ToString("O")
        product = "OpenLineOps"
        repository = "openlineops/openlineops"
        commitSha = $commit
        runId = "123456789"
        runUrl = "https://github.com/openlineops/openlineops/actions/runs/123456789"
        jobName = "production-integration"
        testName = $requiredTest
        conclusion = "success"
        counters = [ordered]@{
            total = 2
            executed = 2
            passed = 2
            failed = 0
            skipped = 0
        }
        trx = [ordered]@{
            relativePath = $relativeTrxPath
            sizeBytes = (Get-Item -LiteralPath $trxPath).Length
            sha256 = Get-FileSha256 $trxPath
        }
    }
    $evidencePath = Join-Path $Root "integration-evidence.json"
    [System.IO.File]::WriteAllText(
        $evidencePath,
        (($document | ConvertTo-Json -Depth 8) + [Environment]::NewLine),
        [System.Text.UTF8Encoding]::new($false))
    return [pscustomobject]@{
        EvidencePath = $evidencePath
        GitHubEnvironment = @{
            GITHUB_REPOSITORY = [string] $document.repository
            GITHUB_SHA = [string] $document.commitSha
            GITHUB_RUN_ID = [string] $document.runId
            GITHUB_SERVER_URL = "https://github.com"
        }
    }
}

function Assert-E2eEvidenceHashes {
    param(
        [Parameter(Mandatory = $true)]$Case,
        [Parameter(Mandatory = $true)]$Evidence
    )

    $entries = @(
        $Evidence.e2eEvidence.stagedAgentBundle,
        $Evidence.e2eEvidence.productionClosure,
        $Evidence.e2eEvidence.studioTwoAgent,
        $Evidence.e2eEvidence.studioTwoAgentManifest,
        $Evidence.e2eEvidence.runnerStagedAgent,
        $Evidence.e2eEvidence.runnerStagedAgentTrx)
    if ($Evidence.githubActions.proofSupplied -eq $true) {
        $entries += @(
            $Evidence.e2eEvidence.productionIntegration,
            $Evidence.e2eEvidence.productionIntegrationTrx)
    }
    foreach ($entry in $entries) {
        if ($null -eq $entry `
            -or $entry.sha256 -cnotmatch '^[0-9a-f]{64}$' `
            -or $entry.sizeBytes -le 0 `
            -or $entry.embeddedRelativePath -cnotmatch '^e2e-evidence/[a-z-]+\.(?:json|trx)$') {
            throw "Evidence case '$($Case.Name)' has an invalid required E2E evidence record."
        }

        $embeddedPath = Join-Path `
            $Case.OutputRoot `
            $entry.embeddedRelativePath.Replace('/', [System.IO.Path]::DirectorySeparatorChar)
        if (-not (Test-Path -LiteralPath $embeddedPath -PathType Leaf) `
            -or (Get-Item -LiteralPath $embeddedPath).Length -ne [long]$entry.sizeBytes `
            -or (Get-FileSha256 $embeddedPath) -cne $entry.sha256) {
            throw "Evidence case '$($Case.Name)' embedded E2E evidence does not match its recorded hash."
        }
    }

    $recovery = $Evidence.e2eEvidence.recoveryComposition
    if ($recovery.productionIntegrationWorkflowJob -cne "production-integration" `
        -or $recovery.productionIntegrationTest -cne "OpenLineOps.PostgresIntegration.Tests.PostgresRabbitMqProductionCoordinationIntegrationTests.DurableOutboxAndResultInboxSurviveCoordinatorRestartAcrossRealBroker" `
        -or $recovery.stagedWindowsAgentBoundary -cne "Published Windows Agent process, signed vendor helper, broker outage, durable SQLite Inbox/Outbox, presence TTL, and transport result-inbox restart" `
        -or $recovery.durableCoordinatorRecoveryBoundary -cne "PostgreSQL coordination store and RabbitMQ transport survive Coordinator transport/store cold restart exactly once" `
        -or [bool] $recovery.proofSupplied -ne [bool] $Evidence.githubActions.proofSupplied `
        -or $recovery.proofRepository -cne $Evidence.githubActions.repository `
        -or $recovery.proofCommitSha -cne $Evidence.githubActions.commitSha `
        -or $recovery.proofRunId -cne $Evidence.githubActions.runId `
        -or $recovery.proofRunUrl -cne $Evidence.githubActions.runUrl `
        -or $recovery.productionIntegrationConclusion -cne $Evidence.githubActions.productionIntegrationConclusion `
        -or $recovery.releaseManifestSha256 -cne (Get-FileSha256 (Join-Path $ResolvedArtifactsRoot "release-manifest.json"))) {
        throw "Evidence case '$($Case.Name)' has an invalid staged-Agent plus PostgreSQL/RabbitMQ recovery composition."
    }
}

$ResolvedArtifactsRoot = Resolve-RepoPath $ArtifactsRoot
$ResolvedWorkRoot = Resolve-RepoPath $WorkRoot
$ResolvedStagedAgentEvidenceRoot = Resolve-RepoPath $StagedAgentEvidenceRoot
$ResolvedProductionClosureEvidenceRoot = Resolve-RepoPath $ProductionClosureEvidenceRoot
$ResolvedStudioTwoAgentEvidenceRoot = Resolve-RepoPath $StudioTwoAgentEvidenceRoot
$ResolvedRunnerStagedAgentEvidenceRoot = Resolve-RepoPath $RunnerStagedAgentEvidenceRoot
New-CleanDirectory $ResolvedWorkRoot
$validIntegrationFixture = New-ProductionIntegrationEvidenceFixture `
    -Root (Join-Path $ResolvedWorkRoot "integration-proof-source/valid")
$wrongCommitFixture = New-ProductionIntegrationEvidenceFixture `
    -Root (Join-Path $ResolvedWorkRoot "integration-proof-source/wrong-commit") `
    -WrongCommit
$invalidTrxFixture = New-ProductionIntegrationEvidenceFixture `
    -Root (Join-Path $ResolvedWorkRoot "integration-proof-source/invalid-trx") `
    -InvalidTrx

$defaultCase = Invoke-EvidenceCase `
    -Name "default" `
    -Arguments @() `
    -GitHubEnvironment $validIntegrationFixture.GitHubEnvironment
Assert-ExitCode -Case $defaultCase -ExpectedExitCode 0
$defaultEvidence = Read-EvidenceJson $defaultCase
Assert-E2eEvidenceHashes -Case $defaultCase -Evidence $defaultEvidence
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
Assert-PendingContains -Evidence $defaultEvidence -Pattern "Bound PostgreSQL/RabbitMQ production integration evidence" -Description "production integration proof"
Assert-GatePresent -Evidence $defaultEvidence -GateName "release candidate inspection" -ExpectedStatus "pass"
Assert-GatePresent -Evidence $defaultEvidence -GateName "publication readiness with pending external allowed" -ExpectedStatus "pass"
Assert-GateCommandContains -Evidence $defaultEvidence -GateName "release candidate inspection behavior" -ExpectedText "release-candidate-inspection-verification"
Assert-GateCommandContains -Evidence $defaultEvidence -GateName "strict publication readiness" -ExpectedText "publication-readiness-strict"
Assert-GateCommandContains -Evidence $defaultEvidence -GateName "signed release candidate inspection" -ExpectedText "signed-release-candidate-inspection"

$confirmedCase = Invoke-EvidenceCase `
    -Name "confirmed-proof" `
    -Arguments @("-ConfirmMitLicense", "-ProductionIntegrationEvidencePath", $validIntegrationFixture.EvidencePath) `
    -GitHubEnvironment $validIntegrationFixture.GitHubEnvironment
Assert-ExitCode -Case $confirmedCase -ExpectedExitCode 0
$confirmedEvidence = Read-EvidenceJson $confirmedCase
Assert-E2eEvidenceHashes -Case $confirmedCase -Evidence $confirmedEvidence
if ($confirmedEvidence.license.confirmedForPublication -ne $true) {
    throw "Confirmed evidence did not record MIT confirmation."
}

if ($confirmedEvidence.githubActions.proofSupplied -ne $true `
    -or $confirmedEvidence.githubActions.repository -cne "openlineops/openlineops" `
    -or $confirmedEvidence.githubActions.runId -cne "123456789" `
    -or $confirmedEvidence.githubActions.productionIntegrationConclusion -cne "success") {
    throw "Confirmed evidence did not bind the successful production integration run."
}

Assert-PendingDoesNotContain -Evidence $confirmedEvidence -Pattern "Final MIT license decision" -Description "MIT confirmation"
Assert-PendingDoesNotContain -Evidence $confirmedEvidence -Pattern "Bound PostgreSQL/RabbitMQ production integration evidence" -Description "production integration proof"

$invalidProofCase = Invoke-EvidenceCase `
    -Name "invalid-production-integration-evidence" `
    -Arguments @("-ConfirmMitLicense", "-ProductionIntegrationEvidencePath", $wrongCommitFixture.EvidencePath) `
    -GitHubEnvironment $wrongCommitFixture.GitHubEnvironment
if ($invalidProofCase.ExitCode -eq 0) {
    Write-Host $invalidProofCase.Text
    throw "Mismatched production integration evidence should fail publication evidence generation."
}

$invalidProofEvidence = Read-EvidenceJson $invalidProofCase
Assert-E2eEvidenceHashes -Case $invalidProofCase -Evidence $invalidProofEvidence
if (-not (@($invalidProofEvidence.internalFailures) | Where-Object { $_ -match "commit does not match a clean release provenance" })) {
    throw "Mismatched integration evidence did not record the expected commit-binding failure."
}

$invalidTrxCase = Invoke-EvidenceCase `
    -Name "invalid-production-integration-trx" `
    -Arguments @("-ConfirmMitLicense", "-ProductionIntegrationEvidencePath", $invalidTrxFixture.EvidencePath) `
    -GitHubEnvironment $invalidTrxFixture.GitHubEnvironment
if ($invalidTrxCase.ExitCode -eq 0) {
    Write-Host $invalidTrxCase.Text
    throw "Semantically invalid production integration TRX should fail publication evidence generation."
}

$invalidTrxEvidence = Read-EvidenceJson $invalidTrxCase
Assert-E2eEvidenceHashes -Case $invalidTrxCase -Evidence $invalidTrxEvidence
if (-not (@($invalidTrxEvidence.internalFailures) | Where-Object {
            $_ -match "TRX result records do not match its all-passed counters"
        })) {
    throw "Invalid integration TRX did not record the expected semantic verification failure."
}

$publishableCase = Invoke-EvidenceCase `
    -Name "require-publishable" `
    -Arguments @("-ConfirmMitLicense", "-ProductionIntegrationEvidencePath", $validIntegrationFixture.EvidencePath, "-RequirePublishable") `
    -GitHubEnvironment $validIntegrationFixture.GitHubEnvironment
$publishableEvidence = Read-EvidenceJson $publishableCase
Assert-E2eEvidenceHashes -Case $publishableCase -Evidence $publishableEvidence
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

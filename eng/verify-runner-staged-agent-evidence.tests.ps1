param(
    [string] $FixtureOutputRoot
)

$ErrorActionPreference = "Stop"
$RepoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$Validator = Join-Path $PSScriptRoot 'verify-runner-staged-agent-evidence.ps1'
$ExactTest = "OpenLineOps.Runner.Tests.RunnerPublishedProjectProcessE2ETests.PublishedProjectRunsThroughPostgreSqlRabbitMqAndStagedAgent"
$mutationBase = [System.IO.Path]::GetFullPath(
    (Join-Path ([System.IO.Path]::GetTempPath()) 'openlineops-runner-staged-agent-evidence-tests'))
$ancestor = [System.IO.DirectoryInfo]::new($mutationBase)
while ($null -ne $ancestor) {
    if ($ancestor.Exists `
        -and (($ancestor.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0)) {
        throw "Evidence validator mutation-test root cannot traverse a reparse point."
    }
    $ancestor = $ancestor.Parent
}
$resolvedWorkRoot = Join-Path $mutationBase ([System.Guid]::NewGuid().ToString('N'))

function Write-Utf8NoBom {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][string] $Text
    )
    [System.IO.File]::WriteAllText(
        $Path,
        $Text,
        [System.Text.UTF8Encoding]::new($false))
}

function Get-Sha256 {
    param([Parameter(Mandatory = $true)][string] $Path)
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-TextSha256 {
    param([Parameter(Mandatory = $true)][string] $Text)
    $algorithm = [System.Security.Cryptography.SHA256]::Create()
    try {
        $hash = $algorithm.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($Text))
        ([System.BitConverter]::ToString($hash)).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $algorithm.Dispose()
    }
}

function Write-CatalogCanonicalInt32 {
    param([System.IO.Stream] $Stream, [int] $Value)
    [byte[]]$bytes = [System.BitConverter]::GetBytes($Value)
    if ([System.BitConverter]::IsLittleEndian) {
        [System.Array]::Reverse($bytes)
    }
    $Stream.Write($bytes, 0, $bytes.Length)
}

function Write-CatalogCanonicalText {
    param([System.IO.Stream] $Stream, [string] $Value)
    [byte[]]$bytes = [System.Text.UTF8Encoding]::new($false, $true).GetBytes($Value)
    Write-CatalogCanonicalInt32 $Stream $bytes.Length
    $Stream.Write($bytes, 0, $bytes.Length)
}

function Get-CatalogFileName {
    param(
        [string] $ProjectId,
        [string] $ApplicationId,
        [string] $ProjectSnapshotId,
        [string] $StationSystemId)
    $stream = [System.IO.MemoryStream]::new()
    try {
        Write-CatalogCanonicalText $stream 'openlineops.station-package-deployment-catalog'
        foreach ($value in @($ProjectId, $ApplicationId, $ProjectSnapshotId, $StationSystemId)) {
            Write-CatalogCanonicalText $stream $value
        }
        $algorithm = [System.Security.Cryptography.SHA256]::Create()
        try {
            $hash = $algorithm.ComputeHash($stream.ToArray())
            return ([System.BitConverter]::ToString($hash)).Replace(
                '-', '').ToLowerInvariant() + '.json'
        }
        finally { $algorithm.Dispose() }
    }
    finally { $stream.Dispose() }
}

function New-ValidEvidence {
    param([Parameter(Mandatory = $true)][string] $Root)

    New-Item -ItemType Directory -Path (Join-Path $Root 'test-results') -Force | Out-Null
    $trxPath = Join-Path $Root 'test-results/runner-staged-agent-e2e.trx'
    $trx = @"
<?xml version="1.0" encoding="utf-8"?>
<TestRun id="11111111-1111-1111-1111-111111111111" name="runner-staged-agent-e2e" runUser="redacted">
  <TestDefinitions>
    <UnitTest name="PublishedProjectRunsThroughPostgreSqlRabbitMqAndStagedAgent" storage="OpenLineOps.Runner.Tests.dll" id="22222222-2222-2222-2222-222222222222">
      <TestMethod codeBase="OpenLineOps.Runner.Tests.dll" adapterTypeName="executor://xunit/VsTestRunner3/netcore/" className="OpenLineOps.Runner.Tests.RunnerPublishedProjectProcessE2ETests" name="PublishedProjectRunsThroughPostgreSqlRabbitMqAndStagedAgent" />
    </UnitTest>
  </TestDefinitions>
  <Results>
    <UnitTestResult testId="22222222-2222-2222-2222-222222222222" testName="PublishedProjectRunsThroughPostgreSqlRabbitMqAndStagedAgent" outcome="Passed" computerName="redacted" />
  </Results>
  <ResultSummary outcome="Completed">
    <Counters total="1" executed="1" passed="1" failed="0" error="0" timeout="0" aborted="0" inconclusive="0" passedButRunAborted="0" notRunnable="0" notExecuted="0" disconnected="0" warning="0" completed="1" inProgress="0" pending="0" />
  </ResultSummary>
</TestRun>
"@
    Write-Utf8NoBom $trxPath $trx
    $sha = '0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef'
    $productionRunId = '33333333-3333-3333-3333-333333333333'
    $stationJobId = '55555555-5555-5555-5555-555555555555'
    $stationResultMessageId = '66666666-6666-6666-6666-666666666666'
    $stationSystemId = 'station.main'
    $catalogFileName = Get-CatalogFileName `
        'project-runner-fixture' `
        'application-runner-fixture' `
        'snapshot-runner-fixture' `
        $stationSystemId
    $postgresCanonical = @(
        "productionRunId=$productionRunId",
        'productionRunCount=1',
        'terminalEvidenceCount=1',
        "stationJobId=$stationJobId",
        'stationJobCount=1',
        "stationResultMessageId=$stationResultMessageId",
        'stationResultCount=1',
        'executionStatus=Completed',
        'resultArtifactCount=0') -join "`n"
    $queueIdentitySha256 = $sha
    $rabbitCanonical = @(
        'jobQueueMessageCount=0',
        'jobQueueConsumerCount=0',
        'resultQueueMessageCount=0',
        'resultQueueConsumerCount=0',
        'safetyQueueMessageCount=0',
        "queueIdentitySha256=$queueIdentitySha256") -join "`n"
    $releaseCanonical = @(
        "releaseManifestSha256=$sha",
        'runnerArchiveRelativePath=runner/runner-openlineops-win-x64-fixture.zip',
        'runnerArchiveSizeBytes=100',
        "runnerArchiveSha256=$sha",
        "runnerBundleManifestSha256=$sha",
        "runnerExecutableSha256=$sha",
        'agentArchiveRelativePath=agent/agent-openlineops-win-x64-fixture.zip',
        'agentArchiveSizeBytes=200',
        "agentArchiveSha256=$sha",
        "agentBundleManifestSha256=$sha",
        "agentExecutableSha256=$sha") -join "`n"
    $evidence = [ordered]@{
        schema = 'openlineops.runner-staged-agent-gate-evidence'
        schemaVersion = 1
        outcome = 'Passed'
        testName = $ExactTest
        verifiedAtUtc = '2026-07-16T00:00:00+00:00'
        project = [ordered]@{
            projectId = 'project-runner-fixture'
            applicationId = 'application-runner-fixture'
            snapshotId = 'snapshot-runner-fixture'
            releaseContentSha256 = $sha
            productionRunId = $productionRunId
            productionUnitId = '44444444-4444-4444-4444-444444444444'
        }
        execution = [ordered]@{
            stationExecutionProvider = 'Agent'
            coordinatorProcess = 'OpenLineOps.Runner.exe'
            runner = [ordered]@{
                processId = 1001
                executableFileName = 'OpenLineOps.Runner.exe'
                executableSha256 = $sha
                runningImageSha256 = $sha
                bundleManifestSha256 = $sha
                bundleChecksumsSha256 = $sha
                manifestBound = $true
                mainModuleBound = $true
                jobObjectBound = $true
                processTreeTerminated = $true
                exitCode = 0
            }
            agent = [ordered]@{
                processId = 1002
                executableFileName = 'OpenLineOps.Agent.exe'
                executableSha256 = $sha
                runningImageSha256 = $sha
                bundleManifestSha256 = $sha
                bundleChecksumsSha256 = $sha
                manifestBound = $true
                mainModuleBound = $true
                serviceName = 'OpenLineOpsAgentE2E-0123456789abcdef0123456789abcdef'
                serviceLifecycleVerified = $true
                serviceAccountName = 'NT AUTHORITY\LocalService'
                serviceAccountSid = 'S-1-5-19'
                serviceSidSha256 = 'd3aa07b9acd0fdfc42a4f3c9a54ba4321b9883099f83fcaf76bca38804e1f221'
                isRestrictedToken = $true
                serviceLogonSidPresent = $true
                serviceLogonSidEnabled = $true
                exactServiceSidPresent = $true
                exactServiceSidEnabled = $true
                exactServiceSidRestricted = $true
                nonAdministrative = $true
            }
            terminal = [ordered]@{
                executionStatus = 'Completed'
                resultJudgement = 'NotApplicable'
                operationCount = 1
                completedOperationCount = 1
                completedStepCount = 1
                commandCount = 1
                incidentCount = 0
            }
        }
        stationPackage = [ordered]@{
            stationSystemId = $stationSystemId
            packageFileName = "$sha.olopkg"
            packageContentSha256 = $sha
            packageFileSha256 = $sha
            catalogFileName = $catalogFileName
            catalogFileSha256 = $sha
            signingKeyId = 'runner-process-e2e-signing'
            signatureAlgorithm = 'RSA-PSS-SHA256'
            manifestBound = $true
            deploymentBound = $true
        }
        postgresql = [ordered]@{
            isolatedSchema = $true
            productionRunCount = 1
            terminalEvidenceCount = 1
            stationJobCount = 1
            stationResultCount = 1
            unpublishedJobCount = 0
            terminalOutboxCount = 0
            createdOutboxCount = 0
            executionStatus = 'Completed'
            resultArtifactCount = 0
            rawSnapshot = [ordered]@{
                productionRunId = $productionRunId
                productionRunCount = 1
                terminalEvidenceCount = 1
                stationJobId = $stationJobId
                stationJobCount = 1
                stationResultMessageId = $stationResultMessageId
                stationResultCount = 1
                executionStatus = 'Completed'
                resultArtifactCount = 0
            }
            rawSnapshotSha256 = Get-TextSha256 $postgresCanonical
        }
        agentSqlite = [ordered]@{
            jobCount = 1
            inboxCount = 1
            completionOutboxCount = 1
            acknowledgedCompletionCount = 1
            pendingOutboxCount = 0
            safetyInboxCount = 0
            status = 'Completed'
            terminalCheckpointRevision = 3
            commandCount = 1
            databaseSha256 = $sha
            onceOnly = $true
        }
        rabbitMq = [ordered]@{
            jobQueueMessageCount = 0
            resultQueueMessageCount = 0
            safetyQueueMessageCount = 0
            jobQueueConsumerCount = 0
            resultQueueConsumerCount = 0
            queueIdentitySha256 = $queueIdentitySha256
            rawSnapshotSha256 = Get-TextSha256 $rabbitCanonical
            drained = $true
        }
        trace = [ordered]@{
            recordCount = 1
            operationCount = 1
            commandCount = 1
            artifactCount = 0
            executionStatus = 'Completed'
            judgement = 'NotApplicable'
            disposition = 'Completed'
            databaseSha256 = $sha
        }
        artifactTransport = [ordered]@{
            endpointClass = 'closed-loopback-http'
            endpointReachable = $false
            artifactCount = 0
            artifactUploadAttemptCount = 0
        }
        safetyChannel = [ordered]@{
            configured = $true
            commandCount = 0
            queueMessageCount = 0
            actuatorInvoked = $false
            executableFileName = 'where.exe'
            executableSha256 = $sha
            independentFromStationRuntime = $true
        }
        cleanup = [ordered]@{
            postgresSchemaDropped = $true
            rabbitQueuesDeleted = $true
            runnerTreeTerminated = $true
            agentServiceDeleted = $true
            temporaryRootDeleted = $true
            reparsePointsTraversed = $false
        }
        testEvidence = [ordered]@{
            fullyQualifiedName = $ExactTest
            outcome = 'Passed'
            total = 1
            executed = 1
            passed = 1
            failed = 0
            skipped = 0
            trxRelativePath = 'test-results/runner-staged-agent-e2e.trx'
            trxSha256 = Get-Sha256 $trxPath
        }
        releaseArtifacts = [ordered]@{
            releaseManifestSha256 = $sha
            runner = [ordered]@{
                archiveRelativePath = 'runner/runner-openlineops-win-x64-fixture.zip'
                archiveSizeBytes = 100
                archiveSha256 = $sha
                bundleManifestSha256 = $sha
                executableSha256 = $sha
            }
            agent = [ordered]@{
                archiveRelativePath = 'agent/agent-openlineops-win-x64-fixture.zip'
                archiveSizeBytes = 200
                archiveSha256 = $sha
                bundleManifestSha256 = $sha
                executableSha256 = $sha
            }
            attestationSha256 = Get-TextSha256 $releaseCanonical
        }
    }
    Write-Utf8NoBom `
        (Join-Path $Root 'evidence.json') `
        (($evidence | ConvertTo-Json -Depth 20) + "`n")
}

function Read-Evidence {
    param([Parameter(Mandatory = $true)][string] $Root)
    Get-Content -LiteralPath (Join-Path $Root 'evidence.json') -Raw | ConvertFrom-Json
}

function Write-Evidence {
    param(
        [Parameter(Mandatory = $true)][string] $Root,
        [Parameter(Mandatory = $true)] $Evidence
    )
    Write-Utf8NoBom `
        (Join-Path $Root 'evidence.json') `
        (($Evidence | ConvertTo-Json -Depth 20) + "`n")
}

function Invoke-Expected {
    param(
        [Parameter(Mandatory = $true)][string] $Name,
        [Parameter(Mandatory = $true)][scriptblock] $Mutation,
        [Parameter(Mandatory = $true)][bool] $ShouldPass
    )

    $root = Join-Path $resolvedWorkRoot $Name
    New-ValidEvidence $root
    & $Mutation $root
    $passed = $false
    try {
        & $Validator -EvidenceRoot $root -RequirePassed *> $null
        $passed = $true
    }
    catch {
        $passed = $false
    }
    if ($passed -ne $ShouldPass) {
        throw "Evidence validator mutation '$Name' produced the wrong result."
    }
    $resultText = if ($ShouldPass) { 'accepted' } else { 'rejected' }
    Write-Host " - ${Name}: $resultText"
}

if (-not [string]::IsNullOrWhiteSpace($FixtureOutputRoot)) {
    $fixtureRoot = [System.IO.Path]::GetFullPath($FixtureOutputRoot)
    $outputPrefix = ([System.IO.Path]::GetFullPath((Join-Path $RepoRoot 'output'))).TrimEnd('\', '/') + `
        [System.IO.Path]::DirectorySeparatorChar
    if (-not $fixtureRoot.StartsWith($outputPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Runner evidence fixture output must stay under the repository output directory."
    }
    if (Test-Path -LiteralPath $fixtureRoot) {
        throw "Runner evidence fixture output must be fresh."
    }
    New-ValidEvidence $fixtureRoot
    & $Validator -EvidenceRoot $fixtureRoot -RequirePassed
    Write-Host "Runner staged-Agent valid evidence fixture written."
    exit 0
}

if (Test-Path -LiteralPath $resolvedWorkRoot) {
    throw "Fresh evidence mutation-test scope already exists."
}
New-Item -ItemType Directory -Path $resolvedWorkRoot | Out-Null
try {
    Invoke-Expected 'valid' { param($root) } $true
    Invoke-Expected 'runner-boolean-truthy-integer' {
        param($root)
        $evidence = Read-Evidence $root
        $evidence.execution.runner.manifestBound = 1
        Write-Evidence $root $evidence
    } $false
    Invoke-Expected 'agent-boolean-truthy-string' {
        param($root)
        $evidence = Read-Evidence $root
        $evidence.execution.agent.isRestrictedToken = 'true'
        Write-Evidence $root $evidence
    } $false
    Invoke-Expected 'package-boolean-truthy-integer' {
        param($root)
        $evidence = Read-Evidence $root
        $evidence.stationPackage.manifestBound = 1
        Write-Evidence $root $evidence
    } $false
    Invoke-Expected 'package-catalog-identity-filename-forged' {
        param($root)
        $evidence = Read-Evidence $root
        $evidence.stationPackage.catalogFileName = ('f' * 64) + '.json'
        Write-Evidence $root $evidence
    } $false
    Invoke-Expected 'postgres-boolean-truthy-string' {
        param($root)
        $evidence = Read-Evidence $root
        $evidence.postgresql.isolatedSchema = 'true'
        Write-Evidence $root $evidence
    } $false
    Invoke-Expected 'sqlite-boolean-truthy-integer' {
        param($root)
        $evidence = Read-Evidence $root
        $evidence.agentSqlite.onceOnly = 1
        Write-Evidence $root $evidence
    } $false
    Invoke-Expected 'rabbit-boolean-truthy-string' {
        param($root)
        $evidence = Read-Evidence $root
        $evidence.rabbitMq.drained = 'true'
        Write-Evidence $root $evidence
    } $false
    Invoke-Expected 'artifact-false-boolean-numeric-zero' {
        param($root)
        $evidence = Read-Evidence $root
        $evidence.artifactTransport.endpointReachable = 0
        Write-Evidence $root $evidence
    } $false
    Invoke-Expected 'safety-boolean-truthy-string' {
        param($root)
        $evidence = Read-Evidence $root
        $evidence.safetyChannel.configured = 'true'
        Write-Evidence $root $evidence
    } $false
    Invoke-Expected 'cleanup-boolean-truthy-integer' {
        param($root)
        $evidence = Read-Evidence $root
        $evidence.cleanup.agentServiceDeleted = 1
        Write-Evidence $root $evidence
    } $false
    Invoke-Expected 'extra-property' {
        param($root)
        $evidence = Read-Evidence $root
        $evidence | Add-Member -NotePropertyName unexpected -NotePropertyValue $true
        Write-Evidence $root $evidence
    } $false
    Invoke-Expected 'invalid-hash' {
        param($root)
        $evidence = Read-Evidence $root
        $evidence.execution.runner.executableSha256 = 'g' * 64
        Write-Evidence $root $evidence
    } $false
    Invoke-Expected 'agent-job-object-legacy-field' {
        param($root)
        $evidence = Read-Evidence $root
        $evidence.execution.agent | Add-Member -NotePropertyName jobObjectBound -NotePropertyValue $true
        Write-Evidence $root $evidence
    } $false
    Invoke-Expected 'agent-tree-cleanup-legacy-field' {
        param($root)
        $evidence = Read-Evidence $root
        $evidence.cleanup | Add-Member -NotePropertyName agentTreeTerminated -NotePropertyValue $true
        Write-Evidence $root $evidence
    } $false
    Invoke-Expected 'agent-service-not-deleted' {
        param($root)
        $evidence = Read-Evidence $root
        $evidence.cleanup.agentServiceDeleted = $false
        Write-Evidence $root $evidence
    } $false
    Invoke-Expected 'agent-service-lifecycle-not-verified' {
        param($root)
        $evidence = Read-Evidence $root
        $evidence.execution.agent.serviceLifecycleVerified = $false
        Write-Evidence $root $evidence
    } $false
    Invoke-Expected 'agent-not-restricted' {
        param($root)
        $evidence = Read-Evidence $root
        $evidence.execution.agent.isRestrictedToken = $false
        Write-Evidence $root $evidence
    } $false
    Invoke-Expected 'agent-service-logon-sid-disabled' {
        param($root)
        $evidence = Read-Evidence $root
        $evidence.execution.agent.serviceLogonSidEnabled = $false
        Write-Evidence $root $evidence
    } $false
    Invoke-Expected 'agent-exact-service-sid-not-restricted' {
        param($root)
        $evidence = Read-Evidence $root
        $evidence.execution.agent.exactServiceSidRestricted = $false
        Write-Evidence $root $evidence
    } $false
    Invoke-Expected 'agent-service-account-not-local-service' {
        param($root)
        $evidence = Read-Evidence $root
        $evidence.execution.agent.serviceAccountName = 'NT AUTHORITY\NetworkService'
        Write-Evidence $root $evidence
    } $false
    Invoke-Expected 'agent-service-account-sid-not-local-service' {
        param($root)
        $evidence = Read-Evidence $root
        $evidence.execution.agent.serviceAccountSid = 'S-1-5-20'
        Write-Evidence $root $evidence
    } $false
    Invoke-Expected 'agent-exact-service-sid-disabled' {
        param($root)
        $evidence = Read-Evidence $root
        $evidence.execution.agent.exactServiceSidEnabled = $false
        Write-Evidence $root $evidence
    } $false
    Invoke-Expected 'agent-administrative-token' {
        param($root)
        $evidence = Read-Evidence $root
        $evidence.execution.agent.nonAdministrative = $false
        Write-Evidence $root $evidence
    } $false
    Invoke-Expected 'agent-service-sid-hash-invalid' {
        param($root)
        $evidence = Read-Evidence $root
        $evidence.execution.agent.serviceSidSha256 = '0' * 64
        Write-Evidence $root $evidence
    } $false
    Invoke-Expected 'secret-text' {
        param($root)
        $evidence = Read-Evidence $root
        $evidence.project.projectId = 'Bearer abcdefghijklmnopqrstuvwxyz0123456789'
        Write-Evidence $root $evidence
    } $false
    Invoke-Expected 'absolute-path' {
        param($root)
        $evidence = Read-Evidence $root
        $evidence.project.projectId = 'C:\private\project.oloproj'
        Write-Evidence $root $evidence
    } $false
    Invoke-Expected 'skipped-trx' {
        param($root)
        $trxPath = Join-Path $root 'test-results/runner-staged-agent-e2e.trx'
        $text = Get-Content -LiteralPath $trxPath -Raw
        $text = $text.Replace('outcome="Passed"', 'outcome="NotExecuted"')
        $text = $text.Replace('executed="1" passed="1"', 'executed="0" passed="0"')
        $text = $text.Replace('notExecuted="0"', 'notExecuted="1"')
        Write-Utf8NoBom $trxPath $text
        $evidence = Read-Evidence $root
        $evidence.testEvidence.trxSha256 = Get-Sha256 $trxPath
        Write-Evidence $root $evidence
    } $false
    Invoke-Expected 'unexpected-file' {
        param($root)
        Write-Utf8NoBom (Join-Path $root 'private.log') 'not public evidence'
    } $false
    Invoke-Expected 'reparse-point' {
        param($root)
        $target = Join-Path $resolvedWorkRoot 'junction-target'
        New-Item -ItemType Directory -Path $target -Force | Out-Null
        New-Item -ItemType Junction -Path (Join-Path $root 'linked') -Target $target | Out-Null
    } $false
}
finally {
    if (Test-Path -LiteralPath $resolvedWorkRoot) {
        foreach ($entry in @(Get-ChildItem -LiteralPath $resolvedWorkRoot -Force -Recurse |
                    Where-Object {
                        ($_.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0
                    } | Sort-Object { $_.FullName.Length } -Descending)) {
            Remove-Item -LiteralPath $entry.FullName -Force
        }
        Remove-Item -LiteralPath $resolvedWorkRoot -Recurse -Force
    }
}

Write-Host "Runner staged-Agent evidence validator mutation tests passed."
exit 0

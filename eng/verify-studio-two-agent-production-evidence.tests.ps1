param(
    [string] $WorkRoot = "output/studio-two-agent-evidence-validation-tests",

    [string] $FixtureOutputRoot
)

$ErrorActionPreference = "Stop"
$RepoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$Verifier = Join-Path $PSScriptRoot "verify-studio-two-agent-production-evidence.ps1"

function Resolve-RepoPath {
    param([Parameter(Mandatory = $true)][string] $Path)
    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }
    return [System.IO.Path]::GetFullPath((Join-Path $RepoRoot $Path))
}

function Assert-ControlledTestRoot {
    param([Parameter(Mandatory = $true)][string] $Path)
    $resolved = [System.IO.Path]::GetFullPath($Path)
    $requiredPrefix = ([System.IO.Path]::GetFullPath((Join-Path $RepoRoot "output"))).TrimEnd('\', '/') + `
        [System.IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($requiredPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Studio evidence mutation root must stay under the repository output directory."
    }
    if (Test-Path -LiteralPath $resolved) {
        $pending = [System.Collections.Generic.Stack[System.IO.FileSystemInfo]]::new()
        $pending.Push((Get-Item -LiteralPath $resolved -Force))
        while ($pending.Count -gt 0) {
            $entry = $pending.Pop()
            if (($entry.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Studio evidence mutation root cannot contain reparse points."
            }
            if ($entry -is [System.IO.DirectoryInfo]) {
                foreach ($child in $entry.GetFileSystemInfos()) { $pending.Push($child) }
            }
        }
    }
    return $resolved
}

function Write-Json {
    param([string] $Path, $Value)
    $content = ($Value | ConvertTo-Json -Depth 50) + [Environment]::NewLine
    [System.IO.File]::WriteAllText($Path, $content, [System.Text.UTF8Encoding]::new($false))
}

function Write-EvidenceManifest {
    param([Parameter(Mandatory = $true)][string] $Root)
    $path = Join-Path $Root "evidence.json"
    $file = Get-Item -LiteralPath $path
    Write-Json -Path (Join-Path $Root "evidence-manifest.json") -Value ([ordered]@{
        schema = "openlineops.studio-two-agent-evidence-manifest"
        schemaVersion = 1
        generatedAtUtc = "2026-07-15T00:10:00Z"
        files = @([ordered]@{
            relativePath = "evidence.json"
            sizeBytes = $file.Length
            sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
        })
    })
}

function New-ReleaseArtifact {
    param([string] $Kind, [string] $Hash)
    return [ordered]@{
        kind = $Kind
        archiveSizeBytes = 4096
        archiveSha256 = $Hash
        bundleFileCount = 12
        bundleContentSha256 = $Hash
        entrypointSha256 = $Hash
    }
}

function New-Agent {
    param([string] $Role, [string] $AgentId, [string] $StationId, [string] $SystemId, [int] $ProcessId, [string] $Hash)
    return [ordered]@{
        role = $Role
        agentId = $AgentId
        stationId = $StationId
        stationSystemId = $SystemId
        processId = $ProcessId
        credentialTokenSha256 = $Hash
        nonAdministrativeToken = $true
        exitCode = 0
    }
}

function New-Run {
    param([string] $RunId, [string] $UnitId, [string] $Hash, [AllowNull()] $RestartHash)
    return [ordered]@{
        productionRunId = $RunId
        productionUnitId = $UnitId
        executionStatus = "Completed"
        judgement = "Passed"
        responseSha256 = $Hash
        traceSha256 = $Hash
        responseAfterRestartSha256 = $RestartHash
    }
}

function New-FinalUnload {
    param([string] $UnitId, [string] $RunId, [string] $LocationId, [string] $SlotId, [string] $OccurredAtUtc, [string] $Hash)
    return [ordered]@{
        productionUnitId = $UnitId
        productionRunId = $RunId
        occurredAtUtc = $OccurredAtUtc
        locationEvidenceId = $LocationId
        slotEvidenceId = $SlotId
        lifecycleResponseSizeBytes = 1024
        lifecycleResponseSha256 = $Hash
    }
}

function New-PostgresFinalUnload {
    param($Unload, [string] $Hash)
    return [ordered]@{
        productionUnitId = $Unload.productionUnitId
        productionRunId = $Unload.productionRunId
        occurredAtUtc = $Unload.occurredAtUtc
        locationEvidenceId = $Unload.locationEvidenceId
        slotEvidenceId = $Unload.slotEvidenceId
        locationDocumentSha256 = $Hash
        slotDocumentSha256 = $Hash
        snapshotSizeBytes = 2048
        snapshotSha256 = $Hash
    }
}

function New-StudioEvidence {
    $hash1 = "1" * 64
    $hash2 = "2" * 64
    $hash3 = "3" * 64
    $hash4 = "4" * 64
    $hash5 = "5" * 64
    $hash6 = "6" * 64
    $hash7 = "7" * 64
    $hash8 = "8" * 64
    $hash9 = "9" * 64
    $hashA = "a" * 64
    $hashB = "b" * 64
    $hashC = "c" * 64
    $runA = "10000000-0000-0000-0000-000000000001"
    $runB = "10000000-0000-0000-0000-000000000002"
    $runC = "10000000-0000-0000-0000-000000000003"
    $unitA = "20000000-0000-0000-0000-000000000001"
    $unitB = "20000000-0000-0000-0000-000000000002"
    $unitC = "20000000-0000-0000-0000-000000000003"
    $unloads = @(
        New-FinalUnload $unitA $runA "30000000-0000-0000-0000-000000000001" "40000000-0000-0000-0000-000000000001" "2026-07-15T00:01:01Z" $hashA
        New-FinalUnload $unitB $runB "30000000-0000-0000-0000-000000000002" "40000000-0000-0000-0000-000000000002" "2026-07-15T00:01:02Z" $hashB
        New-FinalUnload $unitC $runC "30000000-0000-0000-0000-000000000003" "40000000-0000-0000-0000-000000000003" "2026-07-15T00:01:03Z" $hashC)
    $artifactNames = @("measurements.csv", "inspection.png", "report.pdf", "stdout.log", "stderr.log")
    $apiProofNames = @(
        "restored-run-a", "terminal-run-a", "terminal-run-b", "terminal-run-c",
        "trace-run-a", "trace-run-b", "trace-run-c", "parallel-line-state",
        "final-line-state", "active-runs", "recovery-required", "reconcile-response",
        "replay-window-run")
    $queues = @(for ($index = 0; $index -lt 9; $index++) {
        [ordered]@{ messages = 0; consumers = if ($index -eq 0) { 1 } else { 0 } }
    })
    $starts = @(for ($index = 0; $index -lt 6; $index++) {
        [ordered]@{
            sequence = $index + 1
            processId = 5001 + $index
            parentProcessId = 4202
            startedAtUtc = "2026-07-15T00:00:$('{0:D2}' -f (20 + $index))Z"
            ancestorDepth = 2
            boundToDownstreamAgent = $true
            boundToEntryAgent = $false
        }
    })
    return [ordered]@{
        schema = "openlineops.studio-two-agent-production-e2e"
        schemaVersion = 1
        verifiedAtUtc = "2026-07-15T00:11:00Z"
        sourceStudioClosure = [ordered]@{
            productionEvidenceManifestSha256 = $hash1
            productionSummarySha256 = $hash2
            signingPublicKeySha256 = $hash3
            entryPackageContentSha256 = $hash4
            downstreamPackageContentSha256 = $hash5
            applicationPortability = [ordered]@{
                sourceProjectId = "project.source"
                targetProjectId = "project.target"
                applicationId = "application.portable"
                fileCount = 24
                totalSizeBytes = 65536
                sourceBeforeCopyTreeSha256 = $hash6
                copiedTreeSha256 = $hash6
                afterImportTreeSha256 = $hash6
                afterPublishTreeSha256 = $hash6
                afterExecutionTreeSha256 = $hash6
                sourceAfterExecutionTreeSha256 = $hash6
                unchanged = $true
            }
            immutableRunTrace = [ordered]@{
                before = [ordered]@{ sizeBytes = 4096; sha256 = $hash6 }
                after = [ordered]@{ sizeBytes = 4096; sha256 = $hash6 }
                unchanged = $true
                terminalCompletedAtUtc = "2026-07-15T00:00:00Z"
                unloadAtUtc = "2026-07-15T00:00:01Z"
            }
        }
        release = [ordered]@{
            version = "1.0.0"
            manifestSha256 = $hash7
            agent = New-ReleaseArtifact "agent" $hash8
            api = New-ReleaseArtifact "api" $hash9
            samplePlugin = New-ReleaseArtifact "sample-plugin" $hashA
        }
        stagedExecutables = [ordered]@{
            api = [ordered]@{ fileName = "OpenLineOps.Api.exe"; sha256 = $hash9 }
            agent = [ordered]@{ fileName = "OpenLineOps.Agent.exe"; sha256 = $hash8 }
        }
        coordinator = [ordered]@{
            processIds = @(4101, 4102, 4103)
            startOrdinals = @(1, 2, 3)
            environmentSha256 = $hashB
            onlyApiWasRestarted = $true
            persistentStateRestored = $true
            runBeforeRestartSha256 = $hashC
            restoredRunSha256 = $hashC
        }
        agents = @(
            New-Agent "entry" "50000000-0000-0000-0000-000000000001" "60000000-0000-0000-0000-000000000001" "70000000-0000-0000-0000-000000000001" 4201 $hash1
            New-Agent "downstream" "50000000-0000-0000-0000-000000000002" "60000000-0000-0000-0000-000000000002" "70000000-0000-0000-0000-000000000002" 4202 $hash2)
        windowsIdentity = [ordered]@{
            sharedRestrictedTestAccount = $false
            entrySidSha256 = $hash3
            downstreamSidSha256 = $hash4
            distinctOsAccounts = $true
        }
        broker = [ordered]@{
            scheme = "amqp"
            host = "127.0.0.1"
            port = 5672
            tls = $false
            queues = $queues
            snapshotSizeBytes = 256
            snapshotSha256 = $hash5
            snapshotValidatedFromPrivateBrokerState = $true
            allQueuesDrained = $true
        }
        parallelExecution = [ordered]@{
            observed = $true
            lineStateSha256 = $hash6
            entryResourceCount = 2
            downstreamResourceCount = 2
            entryResourceIdentityHashes = @($hash1, $hash2)
            downstreamResourceIdentityHashes = @($hash3, $hash4)
            entryFencingTokens = @(1, 2)
            downstreamFencingTokens = @(3, 4)
            resourceIdentitiesDisjoint = $true
        }
        runs = @(
            New-Run $runA $unitA $hash7 $null
            New-Run $runB $unitB $hash8 $hash8)
        vendorExecution = [ordered]@{
            executableName = "OpenLineOps.VendorTestHelper.exe"
            boundStartCount = $starts.Count
            uniqueProcessIds = $starts.Count
            ledgerSizeBytes = 1024
            ledgerSha256 = $hash9
            starts = $starts
            rootInvocationCount = 3
            noAutomaticReplayAfterActiveCoordinatorCrash = $true
        }
        artifacts = @($artifactNames | ForEach-Object {
            [ordered]@{
                name = $_
                storageKeySha256 = $hashA
                sizeBytes = 128
                sha256 = $hashB
                mediaType = "application/octet-stream"
                hashRecomputedFromCoordinatorDownload = $true
            }
        })
        apiResponseProofs = @($apiProofNames | ForEach-Object {
            [ordered]@{
                name = $_
                sizeBytes = 512
                sha256 = $hashC
                validatedFromPrivateResponseBytes = $true
            }
        })
        finalUnloadEvidence = $unloads
        postgresFinalUnloadEvidence = @(
            New-PostgresFinalUnload $unloads[0] $hash1
            New-PostgresFinalUnload $unloads[1] $hash2
            New-PostgresFinalUnload $unloads[2] $hash3)
        recovery = [ordered]@{
            productionUnitId = $unitC
            productionRunId = $runC
            operationRunId = "80000000-0000-0000-0000-000000000001"
            decisionId = "90000000-0000-0000-0000-000000000001"
            rootVendorProcessId = 5005
            childVendorProcessId = 5006
            recoveryRequiredResponseSha256 = $hash4
            reconcileResponseSha256 = $hash5
            terminalResponseSha256 = $hash6
            traceSha256 = $hash7
            operationCount = 1
            rootInvocationCountBeforeReconcile = 3
            rootInvocationCountAfterReconcile = 3
            processStartCountBeforeReconcile = 6
            processStartCountAfterReconcile = 6
            persistedStationResultCountBeforeReconcile = 5
            replayObservationWindowMilliseconds = 5000
            operationCountAfterReplayWindow = 1
            stationJobCountAfterReplayWindow = 6
            stationJobResultCountAfterReplayWindow = 6
            auditEntryPresent = $true
            noAutomaticReplay = $true
        }
        persistence = [ordered]@{
            productionRunCount = 3
            terminalEvidenceCount = 3
            stationJobCount = 6
            publishedStationJobCount = 6
            unpublishedStationJobCount = 0
            quarantinedStationJobCount = 0
            stationJobResultCount = 6
            stationJobEventCount = 24
            activeLeaseCount = 0
            productionUnitCount = 3
            availableSlotCount = 2
            materialTimelineCount = 30
            distinctTimelineRunCount = 3
        }
        projections = [ordered]@{
            activeRunsSha256 = $hash8
            finalLineStateSha256 = $hash9
            finalActiveRunCount = 0
        }
        cleanup = [ordered]@{
            privateStudioHandoffDeleted = $true
            privateStudioProjectDeleted = $true
            privateAgentHarnessDeleted = $true
            postgresSchemaDropped = $true
            rabbitQueueCleanupAttempted = $true
            rabbitQueueCleanupSucceeded = $true
            rabbitQueueCleanupCount = 9
        }
    }
}

function Reset-Fixture {
    $resolved = Assert-ControlledTestRoot (Resolve-RepoPath $WorkRoot)
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
    New-Item -ItemType Directory -Path $resolved | Out-Null
    Write-Json -Path (Join-Path $resolved "evidence.json") -Value (New-StudioEvidence)
    Write-EvidenceManifest -Root $resolved
    return $resolved
}

function Update-FixtureManifest {
    param([string] $Root)
    Remove-Item -LiteralPath (Join-Path $Root "evidence-manifest.json") -Force
    Write-EvidenceManifest -Root $Root
}

function Invoke-Mutation {
    param([string] $Name, [scriptblock] $Mutate, [string] $Pattern = ".+")
    $root = Reset-Fixture
    $path = Join-Path $root "evidence.json"
    $evidence = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    & $Mutate $evidence
    Write-Json -Path $path -Value $evidence
    Update-FixtureManifest $root
    $failure = $null
    try { & $Verifier -EvidenceRoot $root }
    catch { $failure = $_.Exception.Message }
    if ([string]::IsNullOrWhiteSpace($failure)) { throw "Mutation '$Name' unexpectedly passed." }
    if ($failure -notmatch $Pattern) { throw "Mutation '$Name' failed for the wrong reason: $failure" }
    Write-Host "Mutation rejected: $Name"
}

if (-not [string]::IsNullOrWhiteSpace($FixtureOutputRoot)) {
    $WorkRoot = $FixtureOutputRoot
    $fixtureRoot = Reset-Fixture
    & $Verifier -EvidenceRoot $fixtureRoot
    Write-Host "Studio two-Agent valid evidence fixture written."
    exit 0
}

$root = Reset-Fixture
& $Verifier -EvidenceRoot $root

Invoke-Mutation "raw Base64 property" { param($e) $e.recovery | Add-Member rawResponseBase64 "e30=" } "forbidden|exactly"
Invoke-Mutation "absolute path string" { param($e) $e.broker.host = "C:\private\broker" } "unsafe"
Invoke-Mutation "embedded absolute path string" { param($e) $e.release.version = "failure at C:\private\customer-project" } "unsafe"
Invoke-Mutation "JSON decoded UNC string" { param($e) $e.release.version = "failure at \\server\share\customer-project" } "unsafe"
Invoke-Mutation "credential-bearing broker URI" { param($e) $e.broker.host = "amqp://user:secret@host" } "unsafe"
Invoke-Mutation "shared Windows account" { param($e) $e.windowsIdentity.sharedRestrictedTestAccount = $true } "distinct Windows accounts"
Invoke-Mutation "same Windows SID" { param($e) $e.windowsIdentity.downstreamSidSha256 = $e.windowsIdentity.entrySidSha256 } "distinct Windows accounts"
Invoke-Mutation "administrative Agent token" { param($e) $e.agents[0].nonAdministrativeToken = $false } "non-administrative"
Invoke-Mutation "Application copy uses one Project" { param($e) $e.sourceStudioClosure.applicationPortability.targetProjectId = $e.sourceStudioClosure.applicationPortability.sourceProjectId } "two Projects"
Invoke-Mutation "Application copy changed" { param($e) $e.sourceStudioClosure.applicationPortability.unchanged = $false } "unchanged"
Invoke-Mutation "Application publish phase changed" { param($e) $e.sourceStudioClosure.applicationPortability.afterPublishTreeSha256 = "f" * 64 } "phase hashes"
Invoke-Mutation "Coordinator start removed" { param($e) $e.coordinator.processIds = @($e.coordinator.processIds | Select-Object -First 2) } "restart evidence"
Invoke-Mutation "Rabbit queue not drained" { param($e) $e.broker.queues[2].messages = 1 } "not drained"
Invoke-Mutation "Rabbit cleanup failed" { param($e) $e.cleanup.rabbitQueueCleanupSucceeded = $false } "cleanup evidence"
Invoke-Mutation "parallel execution absent" { param($e) $e.parallelExecution.observed = $false } "parallel execution"
Invoke-Mutation "vendor artifact removed" { param($e) $e.artifacts = @($e.artifacts | Select-Object -First 4) } "artifact"
Invoke-Mutation "artifact hash not recomputed" { param($e) $e.artifacts[0].hashRecomputedFromCoordinatorDownload = $false } "recomputed"
Invoke-Mutation "API proof not validated" { param($e) $e.apiResponseProofs[0].validatedFromPrivateResponseBytes = $false } "private bytes"
Invoke-Mutation "final unload PostgreSQL timestamp differs" { param($e) $e.postgresFinalUnloadEvidence[0].occurredAtUtc = "2026-07-15T00:09:00Z" } "API and PostgreSQL"
Invoke-Mutation "recovery process replay" { param($e) $e.recovery.processStartCountAfterReconcile++ } "recovery/no-replay"
Invoke-Mutation "recovery no replay flag reduced" { param($e) $e.recovery.noAutomaticReplay = $false } "recovery/no-replay"
Invoke-Mutation "public raw command line" { param($e) $e.vendorExecution.starts[0] | Add-Member commandLine "vendor.exe --secret" } "forbidden|exactly"

$root = Reset-Fixture
Add-Content -LiteralPath (Join-Path $root "evidence.json") -Value " " -Encoding UTF8
$failure = $null
try { & $Verifier -EvidenceRoot $root }
catch { $failure = $_.Exception.Message }
if ([string]::IsNullOrWhiteSpace($failure) -or $failure -notmatch "exact manifest") {
    throw "Evidence manifest byte-identity mutation failed for the wrong reason: $failure"
}
Write-Host "Mutation rejected: evidence manifest byte identity"

$root = Reset-Fixture
$unknownDirectory = Join-Path $root "raw-logs"
New-Item -ItemType Directory -Path $unknownDirectory | Out-Null
$failure = $null
try { & $Verifier -EvidenceRoot $root }
catch { $failure = $_.Exception.Message }
if ([string]::IsNullOrWhiteSpace($failure) -or $failure -notmatch "only its two public files") {
    throw "Unknown public directory mutation failed for the wrong reason: $failure"
}
Write-Host "Mutation rejected: unknown public directory"

Write-Host "Studio two-Agent evidence mutation tests passed."

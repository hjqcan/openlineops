param(
    [string] $EvidenceRoot = "output/studio-two-agent-production-closure"
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

function Assert-Condition {
    param([bool] $Condition, [string] $Message)
    if (-not $Condition) { throw $Message }
}

function Assert-ExactProperties {
    param($Value, [string[]] $Expected, [string] $Description)
    if ($null -eq $Value) { throw "$Description is missing." }
    $actual = @($Value.PSObject.Properties.Name)
    if ($actual.Count -ne $Expected.Count) {
        throw "$Description must contain exactly: $($Expected -join ', ')."
    }
    foreach ($name in $Expected) {
        if ($actual -cnotcontains $name) { throw "$Description is missing '$name'." }
    }
}

function Assert-JsonBooleanProperties {
    param(
        [Parameter(Mandatory = $true)] $Value,
        [Parameter(Mandatory = $true)][System.Collections.IDictionary] $Expected,
        [Parameter(Mandatory = $true)][string] $Description
    )

    foreach ($field in $Expected.Keys) {
        Assert-Condition ($Value.$field -is [bool] `
                -and $Value.$field -eq $Expected[$field]) `
            "$Description field '$field' must be the JSON boolean $($Expected[$field].ToString().ToLowerInvariant())."
    }
}

function Assert-Sha256 {
    param([AllowNull()][string] $Value, [string] $Description)
    Assert-Condition ($Value -cmatch '^[0-9a-f]{64}$') "$Description must be lowercase SHA-256."
}

function Assert-OptionalSha256 {
    param([AllowNull()] $Value, [string] $Description)
    if ($null -ne $Value) { Assert-Sha256 $Value $Description }
}

function Assert-GuidText {
    param([AllowNull()][string] $Value, [string] $Description)
    $parsed = [System.Guid]::Empty
    Assert-Condition (-not [string]::IsNullOrWhiteSpace($Value) `
            -and [System.Guid]::TryParseExact($Value, "D", [ref]$parsed) `
            -and $parsed -ne [System.Guid]::Empty) "$Description must be a non-empty canonical GUID."
}

function Assert-UtcTimestamp {
    param([AllowNull()][string] $Value, [string] $Description)
    $parsed = [System.DateTimeOffset]::MinValue
    Assert-Condition (-not [string]::IsNullOrWhiteSpace($Value) `
            -and [System.DateTimeOffset]::TryParse($Value, [ref]$parsed) `
            -and $parsed.Offset -eq [System.TimeSpan]::Zero) "$Description must be a UTC timestamp."
    return $parsed
}

function Assert-PositiveInteger {
    param($Value, [string] $Description)
    Assert-Condition ($Value -is [byte] -or $Value -is [int16] -or $Value -is [int32] `
            -or $Value -is [int64] -or $Value -is [uint16] -or $Value -is [uint32] `
            -or $Value -is [uint64]) "$Description must be an integer."
    Assert-Condition ([decimal]$Value -gt 0) "$Description must be positive."
}

function Assert-NonNegativeInteger {
    param($Value, [string] $Description)
    Assert-Condition ($Value -is [byte] -or $Value -is [int16] -or $Value -is [int32] `
            -or $Value -is [int64] -or $Value -is [uint16] -or $Value -is [uint32] `
            -or $Value -is [uint64]) "$Description must be an integer."
    Assert-Condition ([decimal]$Value -ge 0) "$Description must be non-negative."
}

function Assert-SafePublicJson {
    param([AllowNull()] $Value, [string] $Path)
    if ($null -eq $Value) { return }
    if ($Value -is [System.Management.Automation.PSCustomObject]) {
        foreach ($property in $Value.PSObject.Properties) {
            if ($property.Name -match '^(?i:logs|commandLine|resultPayload|textValue|message|failureReason|password|authorization)$' `
                -or $property.Name -match '(?i)Base64$') {
                throw "Studio two-Agent public evidence contains forbidden property '$($property.Name)'."
            }
            Assert-SafePublicJson $property.Value "$Path.$($property.Name)"
        }
        return
    }
    if ($Value -is [System.Collections.IEnumerable] -and $Value -isnot [string]) {
        $index = 0
        foreach ($item in $Value) {
            Assert-SafePublicJson $item "$Path[$index]"
            $index++
        }
        return
    }
    if ($Value -isnot [string]) { return }
    $text = [string]$Value
    if ($text -match '(?i)(?<![A-Za-z0-9])[A-Z]:[\\/]' `
        -or $text -match '(?i)(?<![\\])\\\\(?:\?\\|\.\\)?[^\\/\s"'']+[\\/]' `
        -or $text -match '(?i)\bfile:(?://|\\\\|[A-Z]:)' `
        -or $text -match '(?i)(?<![A-Za-z0-9._-])/(?:home|tmp|var/tmp|workspace|workspaces|users)/' `
        -or $text -match '(^|[\\/])\.\.([\\/]|$)' `
        -or $text -match '(?i)\bBearer\s+' `
        -or $text -match '(?i)Password\s*=' `
        -or $text -match '-----BEGIN (?:RSA |EC |DSA |OPENSSH )?PRIVATE KEY-----' `
        -or $text -match '(?i)\bOPENLINEOPS_[A-Z0-9_]*(?:TOKEN|PASSWORD|SECRET)\s*[=:]' `
        -or $text -match '(?i)(?:amqp|amqps|http|https)://[^\s/:@]+:[^\s/@]+@') {
        throw "Studio two-Agent public evidence contains unsafe string content at '$Path'."
    }
}

function Assert-NoReparseTree {
    param([string] $Root)
    $pending = [System.Collections.Generic.Stack[System.IO.DirectoryInfo]]::new()
    $pending.Push([System.IO.DirectoryInfo]::new($Root))
    while ($pending.Count -gt 0) {
        $directory = $pending.Pop()
        if (($directory.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Studio two-Agent evidence contains a reparse directory."
        }
        foreach ($entry in $directory.GetFileSystemInfos()) {
            if (($entry.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Studio two-Agent evidence contains a reparse entry."
            }
            if ($entry -is [System.IO.DirectoryInfo]) { $pending.Push($entry) }
        }
    }
}

$root = Resolve-RepoPath $EvidenceRoot
$repoPrefix = $RepoRoot.TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
Assert-Condition ($root.StartsWith($repoPrefix, [System.StringComparison]::OrdinalIgnoreCase)) `
    "Studio two-Agent evidence root must stay inside the repository."
Assert-Condition (Test-Path -LiteralPath $root -PathType Container) `
    "Studio two-Agent evidence root does not exist."
Assert-NoReparseTree $root
$children = @(Get-ChildItem -LiteralPath $root -Force)
Assert-Condition ($children.Count -eq 2 -and @($children | Where-Object { -not $_.PSIsContainer }).Count -eq 2) `
    "Studio two-Agent evidence root must contain only its two public files."
$files = @(Get-ChildItem -LiteralPath $root -File -Force)
Assert-Condition ($files.Count -eq 2) `
    "Studio two-Agent evidence root must contain exactly evidence.json and evidence-manifest.json."
$manifestPath = Join-Path $root "evidence-manifest.json"
$evidencePath = Join-Path $root "evidence.json"
Assert-Condition (Test-Path -LiteralPath $manifestPath -PathType Leaf) "Studio evidence manifest is missing."
Assert-Condition (Test-Path -LiteralPath $evidencePath -PathType Leaf) "Studio evidence JSON is missing."
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
Assert-ExactProperties $manifest @("schema", "schemaVersion", "generatedAtUtc", "files") `
    "Studio two-Agent evidence manifest"
Assert-Condition ($manifest.schema -ceq "openlineops.studio-two-agent-evidence-manifest" `
        -and $manifest.schemaVersion -eq 1) "Studio evidence manifest identity is invalid."
Assert-UtcTimestamp ([string]$manifest.generatedAtUtc) "Studio evidence manifest generatedAtUtc" | Out-Null
$manifestFiles = @($manifest.files)
Assert-Condition ($manifestFiles.Count -eq 1) "Studio evidence manifest must bind exactly one file."
$manifestFile = $manifestFiles[0]
Assert-ExactProperties $manifestFile @("relativePath", "sizeBytes", "sha256") `
    "Studio evidence manifest file"
Assert-Condition ($manifestFile.relativePath -ceq "evidence.json") `
    "Studio evidence manifest path is not canonical."
$evidenceFile = Get-Item -LiteralPath $evidencePath
$evidenceSha256 = (Get-FileHash -LiteralPath $evidencePath -Algorithm SHA256).Hash.ToLowerInvariant()
Assert-Condition ($manifestFile.sizeBytes -eq $evidenceFile.Length `
        -and $manifestFile.sha256 -ceq $evidenceSha256) `
    "Studio evidence JSON differs from its exact manifest."

$evidence = Get-Content -LiteralPath $evidencePath -Raw | ConvertFrom-Json
Assert-SafePublicJson $evidence "evidence"
Assert-ExactProperties $evidence @(
    "schema", "schemaVersion", "verifiedAtUtc", "sourceStudioClosure", "release",
    "stagedExecutables", "coordinator", "agents", "windowsIdentity", "broker",
    "parallelExecution", "runs", "vendorExecution", "artifacts", "apiResponseProofs",
    "finalUnloadEvidence", "postgresFinalUnloadEvidence", "recovery", "persistence",
    "projections", "cleanup") "Studio two-Agent evidence"
Assert-Condition ($evidence.schema -ceq "openlineops.studio-two-agent-production-e2e" `
        -and $evidence.schemaVersion -eq 1) "Studio evidence identity is invalid."
Assert-UtcTimestamp ([string]$evidence.verifiedAtUtc) "Studio evidence verifiedAtUtc" | Out-Null

$source = $evidence.sourceStudioClosure
Assert-ExactProperties $source @(
    "productionEvidenceManifestSha256", "productionSummarySha256", "signingPublicKeySha256",
    "entryPackageContentSha256", "downstreamPackageContentSha256", "applicationPortability",
    "immutableRunTrace") `
    "Studio source closure"
foreach ($name in @("productionEvidenceManifestSha256", "productionSummarySha256", "signingPublicKeySha256", "entryPackageContentSha256", "downstreamPackageContentSha256")) {
    Assert-Sha256 $source.$name "Studio source closure $name"
}
$applicationPortability = $source.applicationPortability
Assert-ExactProperties $applicationPortability @(
    "sourceProjectId", "targetProjectId", "applicationId", "fileCount", "totalSizeBytes",
    "sourceBeforeCopyTreeSha256", "copiedTreeSha256", "afterImportTreeSha256",
    "afterPublishTreeSha256", "afterExecutionTreeSha256", "sourceAfterExecutionTreeSha256",
    "unchanged") "Studio source Application portability"
Assert-JsonBooleanProperties $applicationPortability ([ordered]@{
        unchanged = $true
    }) "Studio source Application portability"
Assert-Condition (-not [string]::IsNullOrWhiteSpace([string]$applicationPortability.sourceProjectId) `
        -and -not [string]::IsNullOrWhiteSpace([string]$applicationPortability.targetProjectId) `
        -and $applicationPortability.sourceProjectId -cne $applicationPortability.targetProjectId `
        -and -not [string]::IsNullOrWhiteSpace([string]$applicationPortability.applicationId)) `
    "Studio source Application portability must identify one Application copied across two Projects."
Assert-PositiveInteger $applicationPortability.fileCount `
    "Studio source Application portability fileCount"
Assert-PositiveInteger $applicationPortability.totalSizeBytes `
    "Studio source Application portability totalSizeBytes"
$applicationPortabilityHashes = @(
    $applicationPortability.sourceBeforeCopyTreeSha256,
    $applicationPortability.copiedTreeSha256,
    $applicationPortability.afterImportTreeSha256,
    $applicationPortability.afterPublishTreeSha256,
    $applicationPortability.afterExecutionTreeSha256,
    $applicationPortability.sourceAfterExecutionTreeSha256)
foreach ($hash in $applicationPortabilityHashes) {
    Assert-Sha256 $hash "Studio source Application portability phase hash"
}
Assert-Condition ($applicationPortability.unchanged -eq $true) `
    "Studio source Application portability must prove unchanged source and copied trees."
Assert-Condition (@($applicationPortabilityHashes | Select-Object -Unique).Count -eq 1) `
    "Studio source Application portability phase hashes must remain identical."
$immutable = $source.immutableRunTrace
Assert-ExactProperties $immutable @("before", "after", "unchanged", "terminalCompletedAtUtc", "unloadAtUtc") `
    "Studio source immutable Run Trace"
Assert-JsonBooleanProperties $immutable ([ordered]@{
        unchanged = $true
    }) "Studio source immutable Run Trace"
foreach ($phase in @("before", "after")) {
    Assert-ExactProperties $immutable.$phase @("sizeBytes", "sha256") "Studio immutable Run Trace $phase"
    Assert-Sha256 $immutable.$phase.sha256 "Studio immutable Run Trace $phase"
}
Assert-Condition ($immutable.unchanged -eq $true `
        -and $immutable.before.sha256 -ceq $immutable.after.sha256 `
        -and $immutable.before.sizeBytes -gt 0 `
        -and $immutable.before.sizeBytes -eq $immutable.after.sizeBytes) `
    "Studio source immutable Run Trace differs after unload."
$terminalCompletedAt = Assert-UtcTimestamp ([string]$immutable.terminalCompletedAtUtc) `
    "Studio immutable Run Trace terminalCompletedAtUtc"
$unloadAt = Assert-UtcTimestamp ([string]$immutable.unloadAtUtc) `
    "Studio immutable Run Trace unloadAtUtc"
Assert-Condition ($unloadAt -gt $terminalCompletedAt) `
    "Studio immutable Run Trace unload must occur after terminal completion."

$release = $evidence.release
Assert-ExactProperties $release @("version", "manifestSha256", "agent", "api", "samplePlugin") `
    "Studio release attestation"
Assert-Sha256 $release.manifestSha256 "Studio release manifest"
foreach ($kind in @("agent", "api", "samplePlugin")) {
    $artifact = $release.$kind
    Assert-ExactProperties $artifact @(
        "kind", "archiveSizeBytes", "archiveSha256", "bundleFileCount",
        "bundleContentSha256", "entrypointSha256") "Studio release $kind attestation"
    foreach ($hash in @("archiveSha256", "bundleContentSha256", "entrypointSha256")) {
        Assert-Sha256 $artifact.$hash "Studio release $kind $hash"
    }
    $expectedKind = if ($kind -ceq "samplePlugin") { "sample-plugin" } else { $kind }
    Assert-Condition ($artifact.kind -ceq $expectedKind) "Studio release $kind kind is invalid."
    Assert-PositiveInteger $artifact.archiveSizeBytes "Studio release $kind archive size"
    Assert-PositiveInteger $artifact.bundleFileCount "Studio release $kind bundle file count"
}

Assert-ExactProperties $evidence.stagedExecutables @("api", "agent") "Studio staged executables"
foreach ($name in @("api", "agent")) {
    Assert-ExactProperties $evidence.stagedExecutables.$name @("fileName", "sha256") `
        "Studio staged executable $name"
    Assert-Sha256 $evidence.stagedExecutables.$name.sha256 "Studio staged executable $name"
}
Assert-Condition ($evidence.stagedExecutables.api.fileName -ceq "OpenLineOps.Api.exe" `
        -and $evidence.stagedExecutables.agent.fileName -ceq "OpenLineOps.Agent.exe") `
    "Studio staged executable names are invalid."

$coordinator = $evidence.coordinator
Assert-ExactProperties $coordinator @(
    "processIds", "startOrdinals", "environmentSha256", "onlyApiWasRestarted",
    "persistentStateRestored", "runBeforeRestartSha256", "restoredRunSha256") `
    "Studio Coordinator evidence"
Assert-JsonBooleanProperties $coordinator ([ordered]@{
        onlyApiWasRestarted = $true
        persistentStateRestored = $true
    }) "Studio Coordinator evidence"
Assert-Condition (@($coordinator.processIds).Count -eq 3 `
        -and @($coordinator.processIds | Select-Object -Unique).Count -eq 3 `
        -and @($coordinator.startOrdinals).Count -eq 3 `
        -and @($coordinator.startOrdinals)[0] -eq 1 `
        -and @($coordinator.startOrdinals)[1] -eq 2 `
        -and @($coordinator.startOrdinals)[2] -eq 3 `
        -and $coordinator.onlyApiWasRestarted -eq $true `
        -and $coordinator.persistentStateRestored -eq $true `
        -and $coordinator.runBeforeRestartSha256 -ceq $coordinator.restoredRunSha256) `
    "Studio Coordinator restart evidence is invalid."
Assert-Sha256 $coordinator.environmentSha256 "Studio Coordinator environment"
Assert-Sha256 $coordinator.runBeforeRestartSha256 "Studio Coordinator pre-restart Run"
Assert-Sha256 $coordinator.restoredRunSha256 "Studio Coordinator restored Run"
foreach ($processId in @($coordinator.processIds)) {
    Assert-PositiveInteger $processId "Studio Coordinator process ID"
}

$agents = @($evidence.agents)
Assert-Condition ($agents.Count -eq 2) "Studio evidence must contain exactly two Agents."
foreach ($agent in $agents) {
    Assert-ExactProperties $agent @(
        "role", "agentId", "stationId", "stationSystemId", "processId",
        "credentialTokenSha256", "nonAdministrativeToken", "exitCode") `
        "Studio Agent evidence"
    Assert-JsonBooleanProperties $agent ([ordered]@{
            nonAdministrativeToken = $true
        }) "Studio Agent evidence"
    Assert-Sha256 $agent.credentialTokenSha256 "Studio Agent credential token hash"
    Assert-Condition ($agent.nonAdministrativeToken -eq $true -and $agent.exitCode -eq 0) `
        "Studio Agent did not run and exit under a non-administrative token."
    Assert-PositiveInteger $agent.processId "Studio Agent process ID"
    Assert-GuidText ([string]$agent.agentId) "Studio Agent ID"
    Assert-GuidText ([string]$agent.stationId) "Studio Station ID"
    Assert-GuidText ([string]$agent.stationSystemId) "Studio Station System ID"
}
Assert-Condition ((@($agents.role) -join ',') -ceq 'entry,downstream' `
        -and @($agents.role | Select-Object -Unique).Count -eq 2 `
        -and @($agents.agentId | Select-Object -Unique).Count -eq 2 `
        -and @($agents.stationId | Select-Object -Unique).Count -eq 2 `
        -and @($agents.stationSystemId | Select-Object -Unique).Count -eq 2 `
        -and @($agents.processId | Select-Object -Unique).Count -eq 2) `
    "Studio Agents are not distinct."

$identity = $evidence.windowsIdentity
Assert-ExactProperties $identity @(
    "sharedLocalServiceAccount", "serviceAccountName", "serviceAccountSid",
    "entryServiceSidSha256", "downstreamServiceSidSha256", "distinctRestrictedServiceSids",
    "entryServiceTokenConnected", "entryPipeExactAclVerified",
    "downstreamServiceTokenExplicitAccessDenied", "bothServicesRunningOnOriginalPids") `
    "Studio Windows identity evidence"
Assert-JsonBooleanProperties $identity ([ordered]@{
        sharedLocalServiceAccount = $true
        distinctRestrictedServiceSids = $true
        entryServiceTokenConnected = $true
        entryPipeExactAclVerified = $true
        downstreamServiceTokenExplicitAccessDenied = $true
        bothServicesRunningOnOriginalPids = $true
    }) "Studio Windows identity evidence"
Assert-Sha256 $identity.entryServiceSidSha256 "Studio entry Agent service SID"
Assert-Sha256 $identity.downstreamServiceSidSha256 "Studio downstream Agent service SID"
Assert-Condition ($identity.sharedLocalServiceAccount -eq $true `
        -and $identity.serviceAccountName -ceq "NT AUTHORITY\LocalService" `
        -and $identity.serviceAccountSid -ceq "S-1-5-19" `
        -and $identity.distinctRestrictedServiceSids -eq $true `
        -and $identity.entryServiceSidSha256 -cne $identity.downstreamServiceSidSha256 `
        -and $identity.entryServiceTokenConnected -eq $true `
        -and $identity.entryPipeExactAclVerified -eq $true `
        -and $identity.downstreamServiceTokenExplicitAccessDenied -eq $true `
        -and $identity.bothServicesRunningOnOriginalPids -eq $true) `
    "Studio Agents must share LocalService, retain distinct restricted service SIDs, and prove service-token material-arrival IPC isolation while remaining Running."

$broker = $evidence.broker
Assert-ExactProperties $broker @(
    "scheme", "host", "port", "tls", "queues", "snapshotSizeBytes", "snapshotSha256",
    "snapshotValidatedFromPrivateBrokerState", "allQueuesDrained") "Studio broker evidence"
Assert-Sha256 $broker.snapshotSha256 "Studio broker snapshot"
Assert-PositiveInteger $broker.port "Studio broker port"
Assert-PositiveInteger $broker.snapshotSizeBytes "Studio broker snapshot size"
$expectedBrokerTls = $broker.scheme -ceq 'amqps'
Assert-JsonBooleanProperties $broker ([ordered]@{
        tls = $expectedBrokerTls
        snapshotValidatedFromPrivateBrokerState = $true
        allQueuesDrained = $true
    }) "Studio broker evidence"
Assert-Condition ($broker.scheme -cin @('amqp', 'amqps') `
        -and -not [string]::IsNullOrWhiteSpace([string]$broker.host) `
        -and $broker.tls -eq ($broker.scheme -ceq 'amqps')) "Studio broker endpoint evidence is invalid."
$queues = @($broker.queues)
Assert-Condition ($queues.Count -eq 9 `
        -and $broker.snapshotValidatedFromPrivateBrokerState -eq $true `
        -and $broker.allQueuesDrained -eq $true) "Studio RabbitMQ cleanup evidence is invalid."
foreach ($queue in $queues) {
    Assert-ExactProperties $queue @("messages", "consumers") "Studio RabbitMQ queue snapshot"
    Assert-Condition ($queue.messages -eq 0) "Studio RabbitMQ queue was not drained."
    Assert-NonNegativeInteger $queue.consumers "Studio RabbitMQ queue consumer count"
}
Assert-Condition (@($queues | Where-Object { $_.consumers -eq 1 }).Count -eq 1 `
        -and @($queues | Where-Object { $_.consumers -gt 1 }).Count -eq 0) `
    "Studio RabbitMQ queue consumer snapshot is invalid."

$parallel = $evidence.parallelExecution
Assert-ExactProperties $parallel @(
    "observed", "lineStateSha256", "entryResourceCount", "downstreamResourceCount",
    "entryResourceIdentityHashes", "downstreamResourceIdentityHashes", "entryFencingTokens",
    "downstreamFencingTokens", "resourceIdentitiesDisjoint") "Studio parallel execution"
Assert-JsonBooleanProperties $parallel ([ordered]@{
        observed = $true
        resourceIdentitiesDisjoint = $true
    }) "Studio parallel execution"
Assert-Condition ($parallel.observed -eq $true `
        -and $parallel.resourceIdentitiesDisjoint -eq $true `
        -and $parallel.entryResourceCount -ge 2 `
        -and $parallel.downstreamResourceCount -ge 2) "Studio parallel execution proof is invalid."
Assert-Sha256 $parallel.lineStateSha256 "Studio parallel line state"
foreach ($hash in @($parallel.entryResourceIdentityHashes) + @($parallel.downstreamResourceIdentityHashes)) {
    Assert-Sha256 $hash "Studio parallel resource identity"
}
Assert-Condition (@($parallel.entryResourceIdentityHashes).Count -eq $parallel.entryResourceCount `
        -and @($parallel.downstreamResourceIdentityHashes).Count -eq $parallel.downstreamResourceCount `
        -and @($parallel.entryFencingTokens).Count -eq $parallel.entryResourceCount `
        -and @($parallel.downstreamFencingTokens).Count -eq $parallel.downstreamResourceCount) `
    "Studio parallel resource evidence counts differ."
foreach ($token in @($parallel.entryFencingTokens) + @($parallel.downstreamFencingTokens)) {
    Assert-PositiveInteger $token "Studio resource fencing token"
}

$runs = @($evidence.runs)
Assert-Condition ($runs.Count -eq 2) "Studio evidence must contain exactly two terminal pipeline Runs."
foreach ($run in $runs) {
    Assert-ExactProperties $run @(
        "productionRunId", "productionUnitId", "executionStatus", "judgement",
        "responseSha256", "traceSha256", "responseAfterRestartSha256") "Studio Run evidence"
    Assert-Condition ($run.executionStatus -ceq "Completed" -and $run.judgement -ceq "Passed") `
        "Studio pipeline Run axes are invalid."
    Assert-Sha256 $run.responseSha256 "Studio Run response"
    Assert-Sha256 $run.traceSha256 "Studio Run Trace"
    Assert-OptionalSha256 $run.responseAfterRestartSha256 "Studio Run response after restart"
    Assert-GuidText ([string]$run.productionRunId) "Studio production Run ID"
    Assert-GuidText ([string]$run.productionUnitId) "Studio Production Unit ID"
}
Assert-Condition ($null -eq $runs[0].responseAfterRestartSha256 `
        -and $runs[1].responseAfterRestartSha256 -ceq $runs[1].responseSha256 `
        -and @($runs.productionRunId | Select-Object -Unique).Count -eq 2 `
        -and @($runs.productionUnitId | Select-Object -Unique).Count -eq 2) `
    "Studio Run restart or identity evidence is invalid."

$vendor = $evidence.vendorExecution
Assert-ExactProperties $vendor @(
    "executableName", "boundStartCount", "uniqueProcessIds", "ledgerSizeBytes", "ledgerSha256",
    "starts", "rootInvocationCount", "noAutomaticReplayAfterActiveCoordinatorCrash") `
    "Studio vendor execution"
Assert-JsonBooleanProperties $vendor ([ordered]@{
        noAutomaticReplayAfterActiveCoordinatorCrash = $true
    }) "Studio vendor execution"
Assert-Sha256 $vendor.ledgerSha256 "Studio vendor process ledger"
$starts = @($vendor.starts)
Assert-Condition ($vendor.executableName -ceq "OpenLineOps.VendorTestHelper.exe" `
        -and $starts.Count -gt 0 `
        -and $vendor.boundStartCount -eq $starts.Count `
        -and $vendor.uniqueProcessIds -eq @($starts.processId | Select-Object -Unique).Count `
        -and $vendor.noAutomaticReplayAfterActiveCoordinatorCrash -eq $true) `
    "Studio vendor process evidence is invalid."
foreach ($start in $starts) {
    Assert-ExactProperties $start @(
        "sequence", "processId", "parentProcessId", "startedAtUtc", "ancestorDepth",
        "boundToDownstreamAgent", "boundToEntryAgent") "Studio vendor process start"
    Assert-PositiveInteger $start.sequence "Studio vendor process sequence"
    Assert-PositiveInteger $start.processId "Studio vendor process ID"
    Assert-NonNegativeInteger $start.parentProcessId "Studio vendor parent process ID"
    Assert-NonNegativeInteger $start.ancestorDepth "Studio vendor process ancestor depth"
    Assert-UtcTimestamp ([string]$start.startedAtUtc) "Studio vendor process start time" | Out-Null
    Assert-Condition ($start.boundToDownstreamAgent -is [bool] `
            -and $start.boundToEntryAgent -is [bool]) `
        "Studio vendor process binding fields must be JSON booleans."
    $expectedDownstreamBinding = [bool]$start.boundToDownstreamAgent
    Assert-JsonBooleanProperties $start ([ordered]@{
            boundToDownstreamAgent = $expectedDownstreamBinding
            boundToEntryAgent = -not $expectedDownstreamBinding
        }) "Studio vendor process start"
    Assert-Condition ($start.boundToDownstreamAgent -ne $start.boundToEntryAgent) `
        "Studio vendor process must be bound to exactly one staged Agent."
}

$requiredArtifactNames = @("measurements.csv", "inspection.png", "report.pdf", "stdout.log", "stderr.log")
$artifacts = @($evidence.artifacts)
Assert-Condition ($artifacts.Count -eq $requiredArtifactNames.Count) `
    "Studio evidence must contain the exact required vendor artifact set."
foreach ($artifact in $artifacts) {
    Assert-ExactProperties $artifact @(
        "name", "storageKeySha256", "sizeBytes", "sha256", "mediaType",
        "hashRecomputedFromCoordinatorDownload") "Studio artifact evidence"
    Assert-JsonBooleanProperties $artifact ([ordered]@{
            hashRecomputedFromCoordinatorDownload = $true
        }) "Studio artifact evidence"
    Assert-Condition ($requiredArtifactNames -ccontains $artifact.name `
            -and $artifact.hashRecomputedFromCoordinatorDownload -eq $true) `
        "Studio artifact was not recomputed from a Coordinator download."
    Assert-Sha256 $artifact.storageKeySha256 "Studio artifact storage key"
    Assert-Sha256 $artifact.sha256 "Studio artifact content"
}
Assert-Condition ((@($artifacts.name | Sort-Object) -join ',') `
        -ceq (@($requiredArtifactNames | Sort-Object) -join ',')) `
    "Studio evidence artifact names are not the exact required set."

$apiProofs = @($evidence.apiResponseProofs)
Assert-Condition ($apiProofs.Count -eq 13) "Studio evidence must contain 13 API response byte proofs."
foreach ($proof in $apiProofs) {
    Assert-ExactProperties $proof @("name", "sizeBytes", "sha256", "validatedFromPrivateResponseBytes") `
        "Studio API response proof"
    Assert-JsonBooleanProperties $proof ([ordered]@{
            validatedFromPrivateResponseBytes = $true
        }) "Studio API response proof"
    Assert-Sha256 $proof.sha256 "Studio API response proof"
    Assert-PositiveInteger $proof.sizeBytes "Studio API response proof size"
    Assert-Condition ($proof.validatedFromPrivateResponseBytes -eq $true) `
        "Studio API response proof was not validated from private bytes."
}
$requiredApiProofNames = @(
    'restored-run-a', 'terminal-run-a', 'terminal-run-b', 'terminal-run-c',
    'trace-run-a', 'trace-run-b', 'trace-run-c', 'parallel-line-state',
    'final-line-state', 'active-runs', 'recovery-required', 'reconcile-response',
    'replay-window-run')
Assert-Condition ((@($apiProofs.name | Sort-Object) -join ',') `
        -ceq (@($requiredApiProofNames | Sort-Object) -join ',')) `
    "Studio API response proof names are not the exact required set."

$unloads = @($evidence.finalUnloadEvidence)
$postgresUnloads = @($evidence.postgresFinalUnloadEvidence)
Assert-Condition ($unloads.Count -eq 3 -and $postgresUnloads.Count -eq 3) `
    "Studio evidence must contain three final unload API and PostgreSQL proofs."
foreach ($unload in $unloads) {
    Assert-ExactProperties $unload @(
        "productionUnitId", "productionRunId", "occurredAtUtc", "locationEvidenceId",
        "slotEvidenceId", "lifecycleResponseSizeBytes", "lifecycleResponseSha256") `
        "Studio final unload API evidence"
    Assert-Sha256 $unload.lifecycleResponseSha256 "Studio final unload API response"
    Assert-PositiveInteger $unload.lifecycleResponseSizeBytes "Studio final unload API response size"
    Assert-GuidText ([string]$unload.productionUnitId) "Studio final unload Production Unit ID"
    Assert-GuidText ([string]$unload.productionRunId) "Studio final unload Run ID"
    Assert-GuidText ([string]$unload.locationEvidenceId) "Studio final unload location evidence ID"
    Assert-GuidText ([string]$unload.slotEvidenceId) "Studio final unload Slot evidence ID"
    Assert-UtcTimestamp ([string]$unload.occurredAtUtc) "Studio final unload occurredAtUtc" | Out-Null
    $postgresUnload = @($postgresUnloads | Where-Object {
            $_.productionUnitId -ceq $unload.productionUnitId `
                -and $_.productionRunId -ceq $unload.productionRunId
        })
    Assert-Condition ($postgresUnload.Count -eq 1 `
            -and $postgresUnload[0].occurredAtUtc -ceq $unload.occurredAtUtc `
            -and $postgresUnload[0].locationEvidenceId -ceq $unload.locationEvidenceId `
            -and $postgresUnload[0].slotEvidenceId -ceq $unload.slotEvidenceId) `
        "Studio final unload API and PostgreSQL evidence differ."
}
foreach ($unload in $postgresUnloads) {
    Assert-ExactProperties $unload @(
        "productionUnitId", "productionRunId", "occurredAtUtc", "locationEvidenceId", "slotEvidenceId",
        "locationDocumentSha256", "slotDocumentSha256", "snapshotSizeBytes", "snapshotSha256") `
        "Studio final unload PostgreSQL evidence"
    foreach ($hash in @("locationDocumentSha256", "slotDocumentSha256", "snapshotSha256")) {
        Assert-Sha256 $unload.$hash "Studio final unload PostgreSQL $hash"
    }
    Assert-PositiveInteger $unload.snapshotSizeBytes "Studio final unload PostgreSQL snapshot size"
}
Assert-Condition (@($unloads.productionUnitId | Select-Object -Unique).Count -eq 3 `
        -and @($unloads.productionRunId | Select-Object -Unique).Count -eq 3) `
    "Studio final unload evidence does not cover three distinct Units and Runs."

$recovery = $evidence.recovery
Assert-ExactProperties $recovery @(
    "productionUnitId", "productionRunId", "operationRunId", "decisionId", "rootVendorProcessId",
    "childVendorProcessId", "recoveryRequiredResponseSha256", "reconcileResponseSha256",
    "terminalResponseSha256", "traceSha256", "operationCount", "rootInvocationCountBeforeReconcile",
    "rootInvocationCountAfterReconcile", "processStartCountBeforeReconcile",
    "processStartCountAfterReconcile", "persistedStationResultCountBeforeReconcile",
    "replayObservationWindowMilliseconds", "operationCountAfterReplayWindow",
    "stationJobCountAfterReplayWindow", "stationJobResultCountAfterReplayWindow",
    "auditEntryPresent", "noAutomaticReplay") "Studio recovery evidence"
Assert-JsonBooleanProperties $recovery ([ordered]@{
        auditEntryPresent = $true
        noAutomaticReplay = $true
    }) "Studio recovery evidence"
Assert-Condition ($recovery.operationCount -eq 1 `
        -and $recovery.operationCountAfterReplayWindow -eq 1 `
        -and $recovery.rootInvocationCountBeforeReconcile -eq $recovery.rootInvocationCountAfterReconcile `
        -and $recovery.processStartCountBeforeReconcile -eq $recovery.processStartCountAfterReconcile `
        -and $recovery.replayObservationWindowMilliseconds -ge 5000 `
        -and $recovery.auditEntryPresent -eq $true `
        -and $recovery.noAutomaticReplay -eq $true) "Studio recovery/no-replay proof is invalid."
foreach ($name in @('recoveryRequiredResponseSha256', 'reconcileResponseSha256', 'terminalResponseSha256', 'traceSha256')) {
    Assert-Sha256 $recovery.$name "Studio recovery $name"
}
Assert-GuidText ([string]$recovery.productionUnitId) "Studio recovery Production Unit ID"
Assert-GuidText ([string]$recovery.productionRunId) "Studio recovery Run ID"
Assert-GuidText ([string]$recovery.operationRunId) "Studio recovery Operation Run ID"
Assert-GuidText ([string]$recovery.decisionId) "Studio recovery decision ID"
Assert-PositiveInteger $recovery.rootVendorProcessId "Studio recovery root vendor process ID"
Assert-PositiveInteger $recovery.childVendorProcessId "Studio recovery child vendor process ID"
Assert-Condition ($recovery.persistedStationResultCountBeforeReconcile -ge 5 `
        -and $recovery.stationJobCountAfterReplayWindow -gt 0 `
        -and $recovery.stationJobResultCountAfterReplayWindow `
            -eq ($recovery.persistedStationResultCountBeforeReconcile + 1)) `
    "Studio recovery persistence evidence is invalid."

$persistence = $evidence.persistence
Assert-ExactProperties $persistence @(
    "productionRunCount", "terminalEvidenceCount", "stationJobCount", "publishedStationJobCount",
    "unpublishedStationJobCount", "quarantinedStationJobCount", "stationJobResultCount",
    "stationJobEventCount", "activeLeaseCount", "productionUnitCount", "availableSlotCount",
    "materialTimelineCount", "distinctTimelineRunCount") "Studio PostgreSQL persistence evidence"
Assert-Condition ($persistence.productionRunCount -eq 3 `
        -and $persistence.terminalEvidenceCount -eq 3 `
        -and $persistence.stationJobCount -eq 6 `
        -and $persistence.publishedStationJobCount -eq 6 `
        -and $persistence.unpublishedStationJobCount -eq 0 `
        -and $persistence.quarantinedStationJobCount -eq 0 `
        -and $persistence.stationJobResultCount -eq 6 `
        -and $persistence.productionUnitCount -eq 3 `
        -and $persistence.availableSlotCount -eq 2 `
        -and $persistence.activeLeaseCount -eq 0 `
        -and $persistence.materialTimelineCount -ge 30 `
        -and $persistence.distinctTimelineRunCount -eq 3) `
    "Studio PostgreSQL persistence counts are invalid."
foreach ($property in $persistence.PSObject.Properties) {
    Assert-NonNegativeInteger $property.Value "Studio PostgreSQL $($property.Name)"
}

Assert-ExactProperties $evidence.projections @(
    "activeRunsSha256", "finalLineStateSha256", "finalActiveRunCount") "Studio projections"
Assert-Condition ($evidence.projections.finalActiveRunCount -eq 0) "Studio final projection is not idle."
Assert-Sha256 $evidence.projections.activeRunsSha256 "Studio active Runs projection"
Assert-Sha256 $evidence.projections.finalLineStateSha256 "Studio final line projection"
$cleanup = $evidence.cleanup
Assert-ExactProperties $cleanup @(
    "privateStudioHandoffDeleted", "privateStudioProjectDeleted", "privateAgentHarnessDeleted",
    "postgresSchemaDropped", "rabbitQueueCleanupAttempted", "rabbitQueueCleanupSucceeded",
    "rabbitQueueCleanupCount") "Studio cleanup evidence"
Assert-JsonBooleanProperties $cleanup ([ordered]@{
        privateStudioHandoffDeleted = $true
        privateStudioProjectDeleted = $true
        privateAgentHarnessDeleted = $true
        postgresSchemaDropped = $true
        rabbitQueueCleanupAttempted = $true
        rabbitQueueCleanupSucceeded = $true
    }) "Studio cleanup evidence"
Assert-Condition ($cleanup.privateStudioHandoffDeleted -eq $true `
        -and $cleanup.privateStudioProjectDeleted -eq $true `
        -and $cleanup.privateAgentHarnessDeleted -eq $true `
        -and $cleanup.postgresSchemaDropped -eq $true `
        -and $cleanup.rabbitQueueCleanupAttempted -eq $true `
        -and $cleanup.rabbitQueueCleanupSucceeded -eq $true `
        -and $cleanup.rabbitQueueCleanupCount -eq 9) "Studio cleanup evidence is invalid."

Write-Host "Studio two-Agent production evidence verification passed."
Write-Host " - Evidence SHA-256: $evidenceSha256"
Write-Host " - Distinct Agents: $($agents.Count)"
Write-Host " - Final unload proofs: $($unloads.Count)"

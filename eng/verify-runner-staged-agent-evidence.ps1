param(
    [string] $EvidenceRoot = "output/runner-staged-agent-e2e",

    [switch] $RequirePassed
)

$ErrorActionPreference = "Stop"
$RepoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$ExactTest = "OpenLineOps.Runner.Tests.RunnerPublishedProjectProcessE2ETests.PublishedProjectRunsThroughPostgreSqlRabbitMqAndStagedAgent"

function Assert-Condition {
    param(
        [Parameter(Mandatory = $true)][bool] $Condition,
        [Parameter(Mandatory = $true)][string] $Message
    )
    if (-not $Condition) { throw $Message }
}

function Resolve-EvidenceRoot {
    param([Parameter(Mandatory = $true)][string] $Path)

    $resolved = if ([System.IO.Path]::IsPathRooted($Path)) {
        [System.IO.Path]::GetFullPath($Path)
    }
    else {
        [System.IO.Path]::GetFullPath((Join-Path $RepoRoot $Path))
    }
    Assert-Condition (Test-Path -LiteralPath $resolved -PathType Container) `
        "Runner staged-Agent evidence root does not exist."
    return $resolved
}

function Assert-ExactProperties {
    param(
        [Parameter(Mandatory = $true)] $Value,
        [Parameter(Mandatory = $true)][string[]] $Expected,
        [Parameter(Mandatory = $true)][string] $Description
    )

    $actual = @($Value.PSObject.Properties.Name)
    Assert-Condition ($actual.Count -eq $Expected.Count) `
        "$Description property count is not strict."
    foreach ($name in $Expected) {
        Assert-Condition ($actual -ccontains $name) `
            "$Description is missing property '$name'."
    }
}

function Assert-Sha256 {
    param(
        [Parameter(Mandatory = $true)] $Value,
        [Parameter(Mandatory = $true)][string] $Description
    )
    Assert-Condition ([string]$Value -cmatch '^[0-9a-f]{64}$') `
        "$Description must be lowercase hexadecimal SHA-256."
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

function Assert-CanonicalId {
    param(
        [Parameter(Mandatory = $true)] $Value,
        [Parameter(Mandatory = $true)][string] $Description
    )
    Assert-Condition ([string]$Value -cmatch '^[A-Za-z0-9][A-Za-z0-9._@-]{0,191}$') `
        "$Description is not a canonical identifier."
}

function Assert-Guid {
    param(
        [Parameter(Mandatory = $true)] $Value,
        [Parameter(Mandatory = $true)][string] $Description
    )
    $parsed = [System.Guid]::Empty
    Assert-Condition ([System.Guid]::TryParseExact([string]$Value, 'D', [ref]$parsed) `
            -and $parsed -ne [System.Guid]::Empty) `
        "$Description is not a non-empty canonical GUID."
}

function Assert-NoSensitiveOrLocalText {
    param(
        [Parameter(Mandatory = $true)][string] $Text,
        [Parameter(Mandatory = $true)][string] $Description
    )

    foreach ($pattern in @(
            '-----BEGIN (?:RSA |EC |DSA |OPENSSH )?PRIVATE KEY-----',
            '(?i)"(?:password|connectionString|artifactUploadBearerToken|bearerToken|authorization|clientSecret|privateKey)"\s*:',
            '(?i)\bBearer\s+[A-Za-z0-9._~+/=-]{8,}',
            '(?i)\bOPENLINEOPS_[A-Z0-9_]+',
            '(?i)(?:amqp|amqps|http|https)://[^\s/:@]+:[^\s/@]+@',
            '(?i)(?:(?<![A-Za-z0-9+.-])[A-Z]:[\\/]|\\\\[A-Za-z0-9._-]+\\|/(?:home|tmp|Users|workspace)/)')) {
        Assert-Condition ($Text -notmatch $pattern) `
            "$Description contains a secret, connection string, environment variable, or absolute local path."
    }
}

function Read-SafeXml {
    param([Parameter(Mandatory = $true)][string] $Path)

    $settings = [System.Xml.XmlReaderSettings]::new()
    $settings.DtdProcessing = [System.Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver = $null
    $reader = [System.Xml.XmlReader]::Create($Path, $settings)
    try {
        $document = [System.Xml.XmlDocument]::new()
        $document.XmlResolver = $null
        $document.Load($reader)
        return $document
    }
    finally {
        $reader.Dispose()
    }
}

$root = Resolve-EvidenceRoot $EvidenceRoot
$rootItem = Get-Item -LiteralPath $root -Force
Assert-Condition (($rootItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -eq 0) `
    "Runner staged-Agent evidence root cannot be a reparse point."
$expectedFiles = [System.Collections.Generic.HashSet[string]]::new(
    [System.StringComparer]::Ordinal)
[void]$expectedFiles.Add('evidence.json')
[void]$expectedFiles.Add('test-results/runner-staged-agent-e2e.trx')
$actualFiles = [System.Collections.Generic.HashSet[string]]::new(
    [System.StringComparer]::Ordinal)
$prefix = $root.TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
foreach ($entry in @(Get-ChildItem -LiteralPath $root -Force -Recurse)) {
    Assert-Condition (($entry.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -eq 0) `
        "Runner staged-Agent evidence contains a reparse point."
    Assert-Condition ($entry.FullName.StartsWith(
            $prefix,
            [System.StringComparison]::OrdinalIgnoreCase)) `
        "Runner staged-Agent evidence escaped its root."
    $relative = $entry.FullName.Substring($prefix.Length).Replace('\', '/')
    if ($entry -is [System.IO.DirectoryInfo]) {
        Assert-Condition ($relative -ceq 'test-results') `
            "Runner staged-Agent evidence contains an unknown directory."
        continue
    }
    Assert-Condition ($entry -is [System.IO.FileInfo] `
            -and $expectedFiles.Contains($relative) `
            -and $actualFiles.Add($relative)) `
        "Runner staged-Agent evidence contains an unknown or duplicate file."
}
Assert-Condition ($actualFiles.SetEquals($expectedFiles)) `
    "Runner staged-Agent public evidence membership is incomplete."

$jsonPath = Join-Path $root 'evidence.json'
$trxPath = Join-Path $root 'test-results/runner-staged-agent-e2e.trx'
$jsonFile = Get-Item -LiteralPath $jsonPath
$trxFile = Get-Item -LiteralPath $trxPath
Assert-Condition ($jsonFile.Length -gt 0 -and $jsonFile.Length -le 131072) `
    "Runner staged-Agent JSON evidence size is outside the public bound."
Assert-Condition ($trxFile.Length -gt 0 -and $trxFile.Length -le 2097152) `
    "Runner staged-Agent TRX evidence size is outside the public bound."
$jsonText = Get-Content -LiteralPath $jsonPath -Raw
$trxText = Get-Content -LiteralPath $trxPath -Raw
Assert-NoSensitiveOrLocalText $jsonText 'Runner staged-Agent evidence.json'
Assert-NoSensitiveOrLocalText $trxText 'Runner staged-Agent TRX'
try { $evidence = $jsonText | ConvertFrom-Json } catch {
    throw "Runner staged-Agent evidence.json is invalid JSON."
}

Assert-ExactProperties $evidence @(
    'schema', 'schemaVersion', 'outcome', 'testName', 'verifiedAtUtc',
    'project', 'execution', 'stationPackage', 'postgresql', 'agentSqlite',
    'rabbitMq', 'trace', 'artifactTransport', 'safetyChannel', 'cleanup',
    'testEvidence', 'releaseArtifacts') 'Top-level evidence'
Assert-Condition ($evidence.schema -ceq 'openlineops.runner-staged-agent-gate-evidence' `
        -and $evidence.schemaVersion -eq 1 `
        -and $evidence.testName -ceq $ExactTest) `
    "Runner staged-Agent evidence identity is invalid."
if ($RequirePassed) {
    Assert-Condition ($evidence.outcome -ceq 'Passed') `
        "Runner staged-Agent evidence is not Passed."
}
$verifiedAt = [System.DateTimeOffset]::MinValue
Assert-Condition ([System.DateTimeOffset]::TryParse(
        [string]$evidence.verifiedAtUtc,
        [System.Globalization.CultureInfo]::InvariantCulture,
        [System.Globalization.DateTimeStyles]::RoundtripKind,
        [ref]$verifiedAt) `
        -and $verifiedAt.Offset -eq [System.TimeSpan]::Zero) `
    "Runner staged-Agent verifiedAtUtc is not canonical UTC."

$project = $evidence.project
Assert-ExactProperties $project @(
    'projectId', 'applicationId', 'snapshotId', 'releaseContentSha256',
    'productionRunId', 'productionUnitId') 'Project evidence'
Assert-CanonicalId $project.projectId 'projectId'
Assert-CanonicalId $project.applicationId 'applicationId'
Assert-CanonicalId $project.snapshotId 'snapshotId'
Assert-Sha256 $project.releaseContentSha256 'releaseContentSha256'
Assert-Guid $project.productionRunId 'productionRunId'
Assert-Guid $project.productionUnitId 'productionUnitId'

$execution = $evidence.execution
Assert-ExactProperties $execution @(
    'stationExecutionProvider', 'coordinatorProcess', 'runner', 'agent', 'terminal') 'Execution evidence'
Assert-Condition ($execution.stationExecutionProvider -ceq 'Agent' `
        -and $execution.coordinatorProcess -ceq 'OpenLineOps.Runner.exe') `
    "Execution did not use the staged Runner and Agent provider."
$runner = $execution.runner
Assert-ExactProperties $runner @(
    'processId', 'executableFileName', 'executableSha256', 'runningImageSha256',
    'bundleManifestSha256', 'bundleChecksumsSha256', 'manifestBound',
    'mainModuleBound', 'jobObjectBound', 'processTreeTerminated', 'exitCode') 'Runner evidence'
$agent = $execution.agent
Assert-ExactProperties $agent @(
    'processId', 'executableFileName', 'executableSha256', 'runningImageSha256',
    'bundleManifestSha256', 'bundleChecksumsSha256', 'manifestBound',
    'mainModuleBound', 'jobObjectBound', 'processTreeTerminated') 'Agent evidence'
Assert-Condition ($runner.processId -gt 0 -and $agent.processId -gt 0 `
        -and $runner.processId -ne $agent.processId `
        -and $runner.executableFileName -ceq 'OpenLineOps.Runner.exe' `
        -and $agent.executableFileName -ceq 'OpenLineOps.Agent.exe' `
        -and $runner.manifestBound -eq $true -and $agent.manifestBound -eq $true `
        -and $runner.mainModuleBound -eq $true -and $agent.mainModuleBound -eq $true `
        -and $runner.jobObjectBound -eq $true -and $agent.jobObjectBound -eq $true `
        -and $runner.processTreeTerminated -eq $true `
        -and $agent.processTreeTerminated -eq $true `
        -and $runner.runningImageSha256 -ceq $runner.executableSha256 `
        -and $agent.runningImageSha256 -ceq $agent.executableSha256 `
        -and $runner.exitCode -eq 0) `
    "Staged process identity or manifest binding is invalid."
foreach ($value in @(
        $runner.executableSha256, $runner.runningImageSha256, $runner.bundleManifestSha256,
        $runner.bundleChecksumsSha256, $agent.executableSha256, $agent.runningImageSha256,
        $agent.bundleManifestSha256, $agent.bundleChecksumsSha256)) {
    Assert-Sha256 $value 'Staged executable evidence hash'
}
$terminal = $execution.terminal
Assert-ExactProperties $terminal @(
    'executionStatus', 'resultJudgement', 'operationCount', 'completedOperationCount',
    'completedStepCount', 'commandCount', 'incidentCount') 'Runner terminal evidence'
Assert-Condition ($terminal.executionStatus -ceq 'Completed' `
        -and $terminal.resultJudgement -ceq 'NotApplicable' `
        -and $terminal.operationCount -eq 1 `
        -and $terminal.completedOperationCount -eq 1 `
        -and $terminal.completedStepCount -gt 0 `
        -and $terminal.commandCount -eq 1 `
        -and $terminal.incidentCount -eq 0) `
    "Runner JSON terminal evidence is invalid."

$package = $evidence.stationPackage
Assert-ExactProperties $package @(
    'packageFileName', 'packageContentSha256', 'packageFileSha256', 'catalogFileName',
    'catalogFileSha256', 'signingKeyId', 'signatureAlgorithm', 'manifestBound',
    'deploymentBound') 'Station package evidence'
Assert-Sha256 $package.packageContentSha256 'Station package content SHA-256'
Assert-Sha256 $package.packageFileSha256 'Station package file SHA-256'
Assert-Sha256 $package.catalogFileSha256 'Station package catalog SHA-256'
Assert-Condition ($package.packageFileName -ceq "$($package.packageContentSha256).olopkg" `
        -and $package.catalogFileName -cmatch '^[A-Za-z0-9._-]+\.json$' `
        -and $package.signingKeyId -ceq 'runner-process-e2e-signing' `
        -and $package.signatureAlgorithm -ceq 'RSA-PSS-SHA256' `
        -and $package.manifestBound -eq $true `
        -and $package.deploymentBound -eq $true) `
    "Signed Station package evidence is not fully bound."

$postgres = $evidence.postgresql
Assert-ExactProperties $postgres @(
    'isolatedSchema', 'productionRunCount', 'terminalEvidenceCount', 'stationJobCount',
    'stationResultCount', 'unpublishedJobCount', 'terminalOutboxCount',
    'createdOutboxCount', 'executionStatus', 'resultArtifactCount', 'rawSnapshot',
    'rawSnapshotSha256') 'PostgreSQL evidence'
Assert-Condition ($postgres.isolatedSchema -eq $true `
        -and $postgres.productionRunCount -eq 1 `
        -and $postgres.terminalEvidenceCount -eq 1 `
        -and $postgres.stationJobCount -eq 1 `
        -and $postgres.stationResultCount -eq 1 `
        -and $postgres.unpublishedJobCount -eq 0 `
        -and $postgres.terminalOutboxCount -eq 0 `
        -and $postgres.createdOutboxCount -eq 0 `
        -and $postgres.executionStatus -ceq 'Completed' `
        -and $postgres.resultArtifactCount -eq 0) `
    "PostgreSQL once-only production evidence is invalid."
Assert-Sha256 $postgres.rawSnapshotSha256 'PostgreSQL raw snapshot SHA-256'
$postgresRaw = $postgres.rawSnapshot
Assert-ExactProperties $postgresRaw @(
    'productionRunId', 'productionRunCount', 'terminalEvidenceCount', 'stationJobId',
    'stationJobCount', 'stationResultMessageId', 'stationResultCount',
    'executionStatus', 'resultArtifactCount') 'PostgreSQL raw snapshot'
Assert-Guid $postgresRaw.productionRunId 'PostgreSQL raw productionRunId'
Assert-Guid $postgresRaw.stationJobId 'PostgreSQL raw Station job id'
Assert-Guid $postgresRaw.stationResultMessageId 'PostgreSQL raw result message id'
Assert-Condition ($postgresRaw.productionRunId -ceq $project.productionRunId `
        -and $postgresRaw.productionRunCount -eq $postgres.productionRunCount `
        -and $postgresRaw.terminalEvidenceCount -eq $postgres.terminalEvidenceCount `
        -and $postgresRaw.stationJobCount -eq $postgres.stationJobCount `
        -and $postgresRaw.stationResultCount -eq $postgres.stationResultCount `
        -and $postgresRaw.executionStatus -ceq $postgres.executionStatus `
        -and $postgresRaw.resultArtifactCount -eq $postgres.resultArtifactCount) `
    "PostgreSQL raw snapshot does not bind its summarized evidence."
$postgresCanonical = @(
    "productionRunId=$($postgresRaw.productionRunId)",
    "productionRunCount=$($postgresRaw.productionRunCount)",
    "terminalEvidenceCount=$($postgresRaw.terminalEvidenceCount)",
    "stationJobId=$($postgresRaw.stationJobId)",
    "stationJobCount=$($postgresRaw.stationJobCount)",
    "stationResultMessageId=$($postgresRaw.stationResultMessageId)",
    "stationResultCount=$($postgresRaw.stationResultCount)",
    "executionStatus=$($postgresRaw.executionStatus)",
    "resultArtifactCount=$($postgresRaw.resultArtifactCount)"
) -join "`n"
Assert-Condition ((Get-TextSha256 $postgresCanonical) -ceq [string]$postgres.rawSnapshotSha256) `
    "PostgreSQL sanitized raw snapshot hash cannot be recomputed."

$sqlite = $evidence.agentSqlite
Assert-ExactProperties $sqlite @(
    'jobCount', 'inboxCount', 'completionOutboxCount', 'acknowledgedCompletionCount',
    'pendingOutboxCount', 'safetyInboxCount', 'status', 'terminalCheckpointRevision', 'commandCount',
    'databaseSha256', 'onceOnly') 'Agent SQLite evidence'
Assert-Sha256 $sqlite.databaseSha256 'Agent SQLite SHA-256'
Assert-Condition ($sqlite.jobCount -eq 1 -and $sqlite.inboxCount -eq 1 `
        -and $sqlite.completionOutboxCount -eq 1 `
        -and $sqlite.acknowledgedCompletionCount -eq 1 `
        -and $sqlite.pendingOutboxCount -eq 0 `
        -and $sqlite.safetyInboxCount -eq 0 `
        -and $sqlite.status -ceq 'Completed' `
        -and $sqlite.terminalCheckpointRevision -gt 0 `
        -and $sqlite.commandCount -eq 1 `
        -and $sqlite.onceOnly -eq $true) `
    "Agent SQLite inbox/outbox/checkpoint evidence is not once-only."

$rabbit = $evidence.rabbitMq
Assert-ExactProperties $rabbit @(
    'jobQueueMessageCount', 'resultQueueMessageCount', 'safetyQueueMessageCount',
    'jobQueueConsumerCount', 'resultQueueConsumerCount', 'queueIdentitySha256',
    'rawSnapshotSha256', 'drained') `
    'RabbitMQ evidence'
Assert-Condition ($rabbit.jobQueueMessageCount -eq 0 `
        -and $rabbit.resultQueueMessageCount -eq 0 `
        -and $rabbit.safetyQueueMessageCount -eq 0 `
        -and $rabbit.jobQueueConsumerCount -eq 0 `
        -and $rabbit.resultQueueConsumerCount -eq 0 `
        -and $rabbit.drained -eq $true) `
    "RabbitMQ queues were not drained."
Assert-Sha256 $rabbit.queueIdentitySha256 'RabbitMQ queue identity SHA-256'
Assert-Sha256 $rabbit.rawSnapshotSha256 'RabbitMQ raw snapshot SHA-256'
$rabbitCanonical = @(
    "jobQueueMessageCount=$($rabbit.jobQueueMessageCount)",
    "jobQueueConsumerCount=$($rabbit.jobQueueConsumerCount)",
    "resultQueueMessageCount=$($rabbit.resultQueueMessageCount)",
    "resultQueueConsumerCount=$($rabbit.resultQueueConsumerCount)",
    "safetyQueueMessageCount=$($rabbit.safetyQueueMessageCount)",
    "queueIdentitySha256=$($rabbit.queueIdentitySha256)"
) -join "`n"
Assert-Condition ((Get-TextSha256 $rabbitCanonical) -ceq [string]$rabbit.rawSnapshotSha256) `
    "RabbitMQ sanitized raw snapshot hash cannot be recomputed."

$trace = $evidence.trace
Assert-ExactProperties $trace @(
    'recordCount', 'operationCount', 'commandCount', 'artifactCount', 'executionStatus',
    'judgement', 'disposition', 'databaseSha256') 'Trace evidence'
Assert-Sha256 $trace.databaseSha256 'Trace database SHA-256'
Assert-Condition ($trace.recordCount -eq 1 -and $trace.operationCount -eq 1 `
        -and $trace.commandCount -eq 1 -and $trace.artifactCount -eq 0 `
        -and $trace.executionStatus -ceq 'Completed' `
        -and $trace.judgement -ceq 'NotApplicable' `
        -and $trace.disposition -ceq 'Completed') `
    "Project Trace evidence is invalid."

$artifact = $evidence.artifactTransport
Assert-ExactProperties $artifact @(
    'endpointClass', 'endpointReachable', 'artifactCount', 'artifactUploadAttemptCount') `
    'Artifact transport evidence'
Assert-Condition ($artifact.endpointClass -ceq 'closed-loopback-http' `
        -and $artifact.endpointReachable -eq $false `
        -and $artifact.artifactCount -eq 0 `
        -and $artifact.artifactUploadAttemptCount -eq 0) `
    "Artifact-free Simulator flow contacted or required the unreachable endpoint."

$safety = $evidence.safetyChannel
Assert-ExactProperties $safety @(
    'configured', 'commandCount', 'queueMessageCount', 'actuatorInvoked',
    'executableFileName', 'executableSha256', 'independentFromStationRuntime') `
    'Safety channel evidence'
Assert-Sha256 $safety.executableSha256 'Safety placeholder executable SHA-256'
Assert-Condition ($safety.configured -eq $true `
        -and $safety.commandCount -eq 0 `
        -and $safety.queueMessageCount -eq 0 `
        -and $safety.actuatorInvoked -eq $false `
        -and $safety.executableFileName -ceq 'where.exe' `
        -and $safety.independentFromStationRuntime -eq $true) `
    "The independent safety channel was invoked or was not configured validly."

$cleanup = $evidence.cleanup
Assert-ExactProperties $cleanup @(
    'postgresSchemaDropped', 'rabbitQueuesDeleted', 'runnerTreeTerminated',
    'agentTreeTerminated', 'temporaryRootDeleted', 'reparsePointsTraversed') 'Cleanup evidence'
Assert-Condition ($cleanup.postgresSchemaDropped -eq $true `
        -and $cleanup.rabbitQueuesDeleted -eq $true `
        -and $cleanup.runnerTreeTerminated -eq $true `
        -and $cleanup.agentTreeTerminated -eq $true `
        -and $cleanup.temporaryRootDeleted -eq $true `
        -and $cleanup.reparsePointsTraversed -eq $false) `
    "Bounded process or infrastructure cleanup evidence is incomplete."

$test = $evidence.testEvidence
Assert-ExactProperties $test @(
    'fullyQualifiedName', 'outcome', 'total', 'executed', 'passed', 'failed',
    'skipped', 'trxRelativePath', 'trxSha256') 'Exact-test evidence'
Assert-Sha256 $test.trxSha256 'TRX SHA-256'
Assert-Condition ($test.fullyQualifiedName -ceq $ExactTest `
        -and $test.outcome -ceq 'Passed' `
        -and $test.total -eq 1 -and $test.executed -eq 1 -and $test.passed -eq 1 `
        -and $test.failed -eq 0 -and $test.skipped -eq 0 `
        -and $test.trxRelativePath -ceq 'test-results/runner-staged-agent-e2e.trx' `
        -and (Get-FileHash -LiteralPath $trxPath -Algorithm SHA256).Hash.ToLowerInvariant() `
            -ceq [string]$test.trxSha256) `
    "Exact-test evidence does not bind one Passed and zero Skipped TRX."

$release = $evidence.releaseArtifacts
Assert-ExactProperties $release @(
    'releaseManifestSha256', 'runner', 'agent', 'attestationSha256') `
    'Release artifact attestation'
Assert-Sha256 $release.releaseManifestSha256 'Release manifest SHA-256'
Assert-Sha256 $release.attestationSha256 'Release attestation SHA-256'
foreach ($kind in @('runner', 'agent')) {
    $item = $release.$kind
    Assert-ExactProperties $item @(
        'archiveRelativePath', 'archiveSizeBytes', 'archiveSha256',
        'bundleManifestSha256', 'executableSha256') "Release $kind attestation"
    Assert-Condition ($item.archiveRelativePath -cmatch "^$kind/[A-Za-z0-9._-]+\.zip$" `
            -and $item.archiveSizeBytes -gt 0) `
        "Release $kind archive identity is invalid."
    Assert-Sha256 $item.archiveSha256 "Release $kind archive SHA-256"
    Assert-Sha256 $item.bundleManifestSha256 "Release $kind bundle manifest SHA-256"
    Assert-Sha256 $item.executableSha256 "Release $kind executable SHA-256"
}
Assert-Condition ($release.runner.bundleManifestSha256 -ceq $runner.bundleManifestSha256 `
        -and $release.runner.executableSha256 -ceq $runner.executableSha256 `
        -and $release.runner.executableSha256 -ceq $runner.runningImageSha256 `
        -and $release.agent.bundleManifestSha256 -ceq $agent.bundleManifestSha256 `
        -and $release.agent.executableSha256 -ceq $agent.executableSha256 `
        -and $release.agent.executableSha256 -ceq $agent.runningImageSha256) `
    "Release archives, inner manifests, and running images are not transitively bound."
$releaseCanonical = @(
    "releaseManifestSha256=$($release.releaseManifestSha256)",
    "runnerArchiveRelativePath=$($release.runner.archiveRelativePath)",
    "runnerArchiveSizeBytes=$($release.runner.archiveSizeBytes)",
    "runnerArchiveSha256=$($release.runner.archiveSha256)",
    "runnerBundleManifestSha256=$($release.runner.bundleManifestSha256)",
    "runnerExecutableSha256=$($release.runner.executableSha256)",
    "agentArchiveRelativePath=$($release.agent.archiveRelativePath)",
    "agentArchiveSizeBytes=$($release.agent.archiveSizeBytes)",
    "agentArchiveSha256=$($release.agent.archiveSha256)",
    "agentBundleManifestSha256=$($release.agent.bundleManifestSha256)",
    "agentExecutableSha256=$($release.agent.executableSha256)"
) -join "`n"
Assert-Condition ((Get-TextSha256 $releaseCanonical) -ceq [string]$release.attestationSha256) `
    "Release artifact attestation hash cannot be recomputed."

$trx = Read-SafeXml $trxPath
$definitions = @($trx.TestRun.TestDefinitions.UnitTest)
$results = @($trx.TestRun.Results.UnitTestResult)
$counters = $trx.TestRun.ResultSummary.Counters
Assert-Condition ($definitions.Count -eq 1 -and $results.Count -eq 1 `
        -and "$($definitions[0].TestMethod.className).$($definitions[0].TestMethod.name)" -ceq $ExactTest `
        -and $definitions[0].storage -ceq 'OpenLineOps.Runner.Tests.dll' `
        -and $definitions[0].TestMethod.codeBase -ceq 'OpenLineOps.Runner.Tests.dll' `
        -and $results[0].outcome -ceq 'Passed' `
        -and $results[0].computerName -ceq 'redacted' `
        -and [int]$counters.total -eq 1 -and [int]$counters.executed -eq 1 `
        -and [int]$counters.passed -eq 1 -and [int]$counters.failed -eq 0 `
        -and [int]$counters.notExecuted -eq 0) `
    "Sanitized TRX does not prove exactly one Passed and zero Skipped test."

Write-Host "Runner staged-Agent evidence passed strict validation."
Write-Host " - Evidence: $jsonPath"
Write-Host " - TRX: $trxPath"

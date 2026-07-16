param(
    [string] $AgentBundleRoot = $env:OPENLINEOPS_STAGED_AGENT_BUNDLE_ROOT,

    [string] $SamplePluginRoot = $env:OPENLINEOPS_STAGED_SAMPLE_PLUGIN_ROOT,

    [string] $ApiBundleRoot = $env:OPENLINEOPS_STAGED_API_BUNDLE_ROOT,

    [string] $BrokerUri = $env:OPENLINEOPS_RABBITMQ_URI,

    [string] $WorkRoot = "output/staged-agent-rabbitmq-e2e",

    [string] $Configuration = "Release",

    [switch] $NoBuild,

    [switch] $NoRestore,

    [ValidateRange(30, 1800)]
    [int] $TestTimeoutSeconds = 900
)

$ErrorActionPreference = "Stop"
$RepoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))

function Resolve-CanonicalDirectory {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][string] $Name
    )

    if ([string]::IsNullOrWhiteSpace($Path) `
        -or [char]::IsWhiteSpace($Path[0]) `
        -or [char]::IsWhiteSpace($Path[$Path.Length - 1]) `
        -or -not [System.IO.Path]::IsPathRooted($Path)) {
        throw "$Name must be a canonical absolute directory path."
    }

    $resolved = [System.IO.Path]::GetFullPath($Path)
    if (-not (Test-Path -LiteralPath $resolved -PathType Container)) {
        throw "$Name does not exist: $resolved"
    }

    return $resolved
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

    $normalizedRoot = $RepoRoot.TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
    $rootPrefix = $normalizedRoot + [System.IO.Path]::DirectorySeparatorChar
    if (-not $Path.StartsWith(
            $rootPrefix,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing staged Agent RabbitMQ E2E work outside the repository root: $Path"
    }
}

function Assert-DirectFile {
    param(
        [Parameter(Mandatory = $true)][string] $Root,
        [Parameter(Mandatory = $true)][string] $FileName
    )

    if ([System.IO.Path]::GetFileName($FileName) -cne $FileName) {
        throw "Required staged dependency must be a direct-child file: $FileName"
    }

    $path = [System.IO.Path]::GetFullPath((Join-Path $Root $FileName))
    if ([System.IO.Path]::GetDirectoryName($path) -cne $Root) {
        throw "Required staged dependency resolves outside its root: $FileName"
    }
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required staged dependency does not exist: $path"
    }
}

function Stop-ProcessTree {
    param([Parameter(Mandatory = $true)][int] $ProcessId)

    if ($null -eq (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue)) {
        return
    }

    $taskKillPath = Join-Path $env:SystemRoot "System32/taskkill.exe"
    $taskKillOutput = @(& $taskKillPath /PID $ProcessId /T /F 2>&1)
    $taskKillExitCode = $LASTEXITCODE
    $taskKillOutput | ForEach-Object { Write-Host $_ }
    if ($taskKillExitCode -ne 0 `
        -and $null -ne (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue)) {
        throw "Failed to terminate timed-out staged Agent RabbitMQ test process tree $ProcessId."
    }
}

if (-not [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
        [System.Runtime.InteropServices.OSPlatform]::Windows)) {
    throw "The staged Agent RabbitMQ process E2E gate requires Windows."
}

if ([string]::IsNullOrWhiteSpace($BrokerUri) `
    -or [char]::IsWhiteSpace($BrokerUri[0]) `
    -or [char]::IsWhiteSpace($BrokerUri[$BrokerUri.Length - 1])) {
    throw "BrokerUri is required; this gate cannot silently skip its real RabbitMQ boundary."
}

$parsedBrokerUri = $null
if (-not [System.Uri]::TryCreate(
        $BrokerUri,
        [System.UriKind]::Absolute,
        [ref]$parsedBrokerUri) `
    -or ($parsedBrokerUri.Scheme -cne "amqp" `
        -and $parsedBrokerUri.Scheme -cne "amqps")) {
    throw "BrokerUri must be an absolute amqp or amqps URI."
}

$resolvedAgentBundleRoot = Resolve-CanonicalDirectory `
    -Path $AgentBundleRoot `
    -Name "AgentBundleRoot"
$resolvedSamplePluginRoot = Resolve-CanonicalDirectory `
    -Path $SamplePluginRoot `
    -Name "SamplePluginRoot"
$resolvedApiBundleRoot = Resolve-CanonicalDirectory `
    -Path $ApiBundleRoot `
    -Name "ApiBundleRoot"
foreach ($fileName in @(
        "OpenLineOps.Agent.exe",
        "OpenLineOps.StationRuntime.exe",
        "OpenLineOps.PluginHost.exe",
        "OpenLineOps.ScriptWorker.exe",
        "OpenLineOps.LeastPrivilegeLauncher.exe")) {
    Assert-DirectFile -Root $resolvedAgentBundleRoot -FileName $fileName
}
Assert-DirectFile -Root $resolvedSamplePluginRoot -FileName "manifest.json"
Assert-DirectFile `
    -Root $resolvedSamplePluginRoot `
    -FileName "OpenLineOps.SamplePlugins.LoopbackDevice.dll"
Assert-DirectFile -Root $resolvedApiBundleRoot -FileName "OpenLineOps.Api.exe"

$resolvedWorkRoot = Resolve-RepoPath $WorkRoot
Assert-UnderRepoRoot $resolvedWorkRoot
if (Test-Path -LiteralPath $resolvedWorkRoot) {
    Remove-Item -LiteralPath $resolvedWorkRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $resolvedWorkRoot -Force | Out-Null
$evidencePath = Join-Path $resolvedWorkRoot "evidence.json"

$testProject = Join-Path $RepoRoot "tests/OpenLineOps.Agent.Tests/OpenLineOps.Agent.Tests.csproj"
$testFilter = "FullyQualifiedName=OpenLineOps.Agent.Tests.StagedAgentRabbitMqProcessE2ETests.StagedAgentBuffersSignedVendorResultDuringBrokerOutageAndDeduplicatesAcrossRestart"
$testArguments = @(
    "test",
    $testProject,
    "--configuration",
    $Configuration,
    "--filter",
    $testFilter,
    "--logger",
    "console;verbosity=minimal"
)
if ($NoBuild) {
    $testArguments += "--no-build"
}
if ($NoRestore) {
    $testArguments += "--no-restore"
}

$previousAgentBundleRoot = $env:OPENLINEOPS_STAGED_AGENT_BUNDLE_ROOT
$previousSamplePluginRoot = $env:OPENLINEOPS_STAGED_SAMPLE_PLUGIN_ROOT
$previousApiBundleRoot = $env:OPENLINEOPS_STAGED_API_BUNDLE_ROOT
$previousBrokerUri = $env:OPENLINEOPS_RABBITMQ_URI
$previousEvidencePath = $env:OPENLINEOPS_STAGED_AGENT_RABBITMQ_EVIDENCE_PATH
$previousOutageControl = $env:OPENLINEOPS_RABBITMQ_OUTAGE_CONTROL
$previousDotnetCliLanguage = $env:DOTNET_CLI_UI_LANGUAGE
$previousVsLanguage = $env:VSLANG
try {
    $env:OPENLINEOPS_STAGED_AGENT_BUNDLE_ROOT = $resolvedAgentBundleRoot
    $env:OPENLINEOPS_STAGED_SAMPLE_PLUGIN_ROOT = $resolvedSamplePluginRoot
    $env:OPENLINEOPS_STAGED_API_BUNDLE_ROOT = $resolvedApiBundleRoot
    $env:OPENLINEOPS_RABBITMQ_URI = $parsedBrokerUri.AbsoluteUri
    $env:OPENLINEOPS_STAGED_AGENT_RABBITMQ_EVIDENCE_PATH = $evidencePath
    $env:DOTNET_CLI_UI_LANGUAGE = "en-US"
    $env:VSLANG = "1033"
    if ([string]::IsNullOrWhiteSpace($previousOutageControl)) {
        $env:OPENLINEOPS_RABBITMQ_OUTAGE_CONTROL = "windows-service:RabbitMQ"
    }
    $previousErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    $testLogPath = Join-Path $resolvedWorkRoot "dotnet-test.log"
    $testErrorLogPath = Join-Path $resolvedWorkRoot "dotnet-test-error.log"
    $testProcess = $null
    $testExitCode = $null
    $testTimedOut = $false
    try {
        $testProcess = Start-Process `
            -FilePath "dotnet" `
            -ArgumentList $testArguments `
            -RedirectStandardOutput $testLogPath `
            -RedirectStandardError $testErrorLogPath `
            -WindowStyle Hidden `
            -PassThru
        $timeoutMilliseconds = [int]($TestTimeoutSeconds * 1000)
        if (-not $testProcess.WaitForExit($timeoutMilliseconds)) {
            $testTimedOut = $true
        }
        else {
            $testProcess.Refresh()
            $testExitCode = [int]$testProcess.ExitCode
        }
    }
    finally {
        if ($null -ne $testProcess) {
            if (-not $testProcess.HasExited) {
                Stop-ProcessTree -ProcessId $testProcess.Id
                if (-not $testProcess.WaitForExit(15000)) {
                    throw "The staged Agent RabbitMQ test process remained alive after taskkill cleanup."
                }
            }
            $testProcess.Dispose()
        }
        $ErrorActionPreference = $previousErrorActionPreference
    }

    if (Test-Path -LiteralPath $testLogPath -PathType Leaf) {
        Get-Content -LiteralPath $testLogPath -Encoding utf8 |
            ForEach-Object { Write-Host $_ }
    }
    if (Test-Path -LiteralPath $testErrorLogPath -PathType Leaf) {
        Get-Content -LiteralPath $testErrorLogPath -Encoding utf8 |
            ForEach-Object { Write-Host $_ }
    }
    if ($testTimedOut) {
        throw "Staged Agent RabbitMQ process E2E timed out after $TestTimeoutSeconds seconds; its complete process tree was terminated."
    }
    if ($null -eq $testExitCode -or $testExitCode -ne 0) {
        throw "Staged Agent RabbitMQ process E2E failed with exit code $testExitCode."
    }
}
finally {
    $env:OPENLINEOPS_STAGED_AGENT_BUNDLE_ROOT = $previousAgentBundleRoot
    $env:OPENLINEOPS_STAGED_SAMPLE_PLUGIN_ROOT = $previousSamplePluginRoot
    $env:OPENLINEOPS_STAGED_API_BUNDLE_ROOT = $previousApiBundleRoot
    $env:OPENLINEOPS_RABBITMQ_URI = $previousBrokerUri
    $env:OPENLINEOPS_STAGED_AGENT_RABBITMQ_EVIDENCE_PATH = $previousEvidencePath
    $env:OPENLINEOPS_RABBITMQ_OUTAGE_CONTROL = $previousOutageControl
    $env:DOTNET_CLI_UI_LANGUAGE = $previousDotnetCliLanguage
    $env:VSLANG = $previousVsLanguage
}

if (-not (Test-Path -LiteralPath $evidencePath -PathType Leaf)) {
    throw "Staged Agent RabbitMQ process E2E did not produce its required evidence: $evidencePath"
}

$evidence = Get-Content -LiteralPath $evidencePath -Raw | ConvertFrom-Json
$allowedIdentityStrategies = @(
    "temporary-standard-account",
    "inherited-uac-filtered-token"
)
$identityStrategyInvalid = `
    $allowedIdentityStrategies -cnotcontains $evidence.agentHostIdentity.identityStrategy `
    -or $evidence.restartedAgentHostIdentity.identityStrategy -cne `
        $evidence.agentHostIdentity.identityStrategy
$inheritedIdentityInvalid = `
    $evidence.agentHostIdentity.identityStrategy -ceq "inherited-uac-filtered-token" `
    -and ($evidence.agentHostIdentity.administratorGroupPresent -ne $true `
        -or $evidence.agentHostIdentity.administratorGroupDenyOnly -ne $true)
$vendorArtifacts = @($evidence.vendorArtifacts)
$requiredVendorArtifacts = [ordered]@{
    "measurements.csv" = "Csv"
    "inspection.png" = "Image"
    "report.pdf" = "Report"
    "stdout.log" = "Log"
    "stderr.log" = "Log"
}
$artifactEvidenceInvalid = $vendorArtifacts.Count -lt $requiredVendorArtifacts.Count `
    -or @($vendorArtifacts | Where-Object {
            $_.name -isnot [string] `
            -or [string]::IsNullOrWhiteSpace($_.name) `
            -or $_.kind -isnot [string] `
            -or [string]::IsNullOrWhiteSpace($_.kind) `
            -or $_.storageKey -isnot [string] `
            -or $_.storageKey -notmatch '^station-artifacts/' `
            -or $_.receiptId -isnot [string] `
            -or $_.receiptId -cnotmatch '^[0-9a-f]{64}$' `
            -or $_.sha256 -isnot [string] `
            -or $_.sha256 -cnotmatch '^[0-9a-f]{64}$' `
            -or $_.sizeBytes -lt 0
        }).Count -ne 0 `
    -or @($requiredVendorArtifacts.Keys | Where-Object {
            $name = $_
            @($vendorArtifacts | Where-Object {
                    $_.name -ceq $name `
                        -and $_.kind -ceq $requiredVendorArtifacts[$name]
                }).Count -ne 1
        }).Count -ne 0
if ($evidence.schema -cne "openlineops.staged-agent-rabbitmq-e2e-evidence" `
    -or $evidence.schemaVersion -ne 1 `
    -or $evidence.executionStatus -cne "Completed" `
    -or $evidence.judgement -cne "Passed" `
    -or $evidence.vendorProgram -cne "OpenLineOps.VendorTestHelper.exe" `
    -or $evidence.centralArtifactTransport -cne "authenticated-http-stream" `
    -or $evidence.operatorTraceGetVerified -ne $true `
    -or $artifactEvidenceInvalid `
    -or $evidence.brokerOutageVerified -ne $true `
    -or $evidence.coordinatorTransportResultInboxRestartedAfterBrokerRecovery -ne $true `
    -or $evidence.agentHostIdentity.nonAdministrative -ne $true `
    -or $evidence.agentHostIdentity.isPrimaryToken -ne $true `
    -or $evidence.agentHostIdentity.isElevated -ne $false `
    -or $evidence.agentHostIdentity.administratorGroupEnabled -ne $false `
    -or $evidence.agentHostIdentity.principalAdministratorMembership -ne $false `
    -or $identityStrategyInvalid `
    -or $inheritedIdentityInvalid `
    -or $evidence.restartedAgentHostIdentity.nonAdministrative -ne $true `
    -or $evidence.restartedAgentHostIdentity.isPrimaryToken -ne $true `
    -or $evidence.restartedAgentHostIdentity.isElevated -ne $false `
    -or $evidence.offlinePendingOutboxCount -lt 1 `
    -or $evidence.offlineCompletionWasNotDelivered -ne $true `
    -or $evidence.completionDeliveredOnceAfterReconnect -ne $true `
    -or $evidence.duplicateRedeliveryRejected -ne $true `
    -or $evidence.duplicateAfterRestartRejected -ne $true `
    -or @($evidence.presence.persistedStates) -cnotcontains "Started" `
    -or @($evidence.presence.persistedStates) -cnotcontains "Heartbeat" `
    -or $evidence.presence.startedAndHeartbeatPersisted -ne $true `
    -or $evidence.presence.expiredOfflineDuringBrokerOutage -ne $true `
    -or $evidence.presence.offlineDuringBrokerOutage.status -cne "Offline" `
    -or $evidence.presence.offlineDuringBrokerOutage.health -cne "Expired" `
    -or $evidence.presence.freshOnlineAfterReconnect -ne $true `
    -or $evidence.presence.onlineAfterReconnect.status -cne "Idle" `
    -or $evidence.presence.onlineAfterReconnect.health -cne "Online" `
    -or $evidence.cleanShutdownVerified -ne $true) {
    throw "Staged Agent RabbitMQ process E2E evidence is incomplete or invalid."
}

Write-Host "Staged Agent RabbitMQ process E2E passed."
Write-Host " - Broker: $($parsedBrokerUri.Host):$($parsedBrokerUri.Port) (TLS: $($parsedBrokerUri.Scheme -ceq 'amqps'))"
Write-Host " - True staged Agent process completed a signed frozen vendor helper while RabbitMQ was offline."
Write-Host " - SQLite buffered the result; reconnect delivered it once; redelivery and restart did not replay hardware."
Write-Host " - Coordinator transport/result inbox restarted after broker recovery; child-token inspection proved the Agent non-administrative."
Write-Host " - Persisted Started/Heartbeat presence expired to Offline during outage and recovered to fresh Online."
Write-Host " - Evidence: $evidencePath"

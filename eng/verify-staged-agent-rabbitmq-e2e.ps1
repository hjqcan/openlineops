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

function Assert-ExactProperties {
    param(
        [Parameter(Mandatory = $true)] $Value,
        [Parameter(Mandatory = $true)][string[]] $Expected,
        [Parameter(Mandatory = $true)][string] $Description
    )

    $actual = @($Value.PSObject.Properties.Name)
    if ($actual.Count -ne $Expected.Count) {
        throw "$Description must contain exactly: $($Expected -join ', ')."
    }
    foreach ($name in $Expected) {
        if ($actual -cnotcontains $name) {
            throw "$Description is missing '$name'."
        }
    }
}

function Assert-JsonBooleanProperties {
    param(
        [Parameter(Mandatory = $true)] $Value,
        [Parameter(Mandatory = $true)][System.Collections.IDictionary] $Expected,
        [Parameter(Mandatory = $true)][string] $Description
    )

    foreach ($field in $Expected.Keys) {
        if ($Value.$field -isnot [bool] -or $Value.$field -ne $Expected[$field]) {
            throw "$Description field '$field' must be the JSON boolean $($Expected[$field].ToString().ToLowerInvariant())."
        }
    }
}

function Get-ServiceSidFromName {
    param([Parameter(Mandatory = $true)][string] $ServiceName)

    $algorithm = [System.Security.Cryptography.SHA1]::Create()
    try {
        $hash = $algorithm.ComputeHash(
            [System.Text.Encoding]::Unicode.GetBytes($ServiceName.ToUpperInvariant()))
        $subAuthorities = @(for ($offset = 0; $offset -lt 20; $offset += 4) {
                [System.BitConverter]::ToUInt32($hash, $offset).ToString(
                    [System.Globalization.CultureInfo]::InvariantCulture)
            })
        return "S-1-5-80-$($subAuthorities -join '-')"
    }
    finally {
        $algorithm.Dispose()
    }
}

function Assert-RestrictedServiceIdentity {
    param(
        [Parameter(Mandatory = $true)] $Identity,
        [Parameter(Mandatory = $true)][string] $ServiceName,
        [Parameter(Mandatory = $true)][string] $Description
    )

    Assert-ExactProperties $Identity @(
        "serviceAccountName",
        "serviceAccountSid",
        "serviceSid",
        "AccountName",
        "UserSid",
        "IsPrimaryToken",
        "HasLinkedToken",
        "IsRestrictedToken",
        "AdministratorGroupPresent",
        "AdministratorGroupEnabled",
        "AdministratorGroupDenyOnly",
        "ServiceLogonSidPresent",
        "ServiceLogonSidEnabled",
        "ExactServiceSidPresent",
        "ExactServiceSidEnabled",
        "ExactServiceSidRestricted",
        "IsAuthenticated",
        "IsSystem",
        "NonAdministrative",
        "identityStrategy") $Description
    Assert-JsonBooleanProperties $Identity ([ordered]@{
            IsPrimaryToken = $true
            HasLinkedToken = $false
            IsRestrictedToken = $true
            AdministratorGroupPresent = $false
            AdministratorGroupEnabled = $false
            AdministratorGroupDenyOnly = $false
            ServiceLogonSidPresent = $true
            ServiceLogonSidEnabled = $true
            ExactServiceSidPresent = $true
            ExactServiceSidEnabled = $true
            ExactServiceSidRestricted = $true
            IsAuthenticated = $true
            IsSystem = $false
            NonAdministrative = $true
        }) $Description
    if ($Identity.serviceAccountName -cne "NT AUTHORITY\LocalService" `
        -or $Identity.serviceAccountSid -cne "S-1-5-19" `
        -or $Identity.serviceSid -cnotmatch '^S-1-5-80-(?:[0-9]+-){4}[0-9]+$' `
        -or $Identity.serviceSid -cne (Get-ServiceSidFromName $ServiceName) `
        -or [string]::IsNullOrWhiteSpace([string]$Identity.AccountName) `
        -or $Identity.UserSid -cne "S-1-5-19" `
        -or $Identity.IsPrimaryToken -ne $true `
        -or $Identity.HasLinkedToken -ne $false `
        -or $Identity.IsRestrictedToken -ne $true `
        -or $Identity.AdministratorGroupPresent -ne $false `
        -or $Identity.AdministratorGroupEnabled -ne $false `
        -or $Identity.AdministratorGroupDenyOnly -ne $false `
        -or $Identity.ServiceLogonSidPresent -ne $true `
        -or $Identity.ServiceLogonSidEnabled -ne $true `
        -or $Identity.ExactServiceSidPresent -ne $true `
        -or $Identity.ExactServiceSidEnabled -ne $true `
        -or $Identity.ExactServiceSidRestricted -ne $true `
        -or $Identity.IsAuthenticated -ne $true `
        -or $Identity.IsSystem -ne $false `
        -or $Identity.NonAdministrative -ne $true `
        -or $Identity.identityStrategy -cne "local-service-restricted-service-sid") {
        throw "$Description does not prove the exact LocalService restricted service-SID token."
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
$requestedServiceScope = $env:OPENLINEOPS_STAGED_AGENT_SERVICE_SCOPE
$serviceScope = if ([string]::IsNullOrWhiteSpace($requestedServiceScope)) {
    [System.Guid]::NewGuid().ToString("N")
}
else {
    $requestedServiceScope
}
if ($serviceScope -cnotmatch '^[0-9a-f]{32}$') {
    throw "OPENLINEOPS_STAGED_AGENT_SERVICE_SCOPE must contain exactly 32 lowercase hexadecimal characters."
}
$requestedCleanupManifestPath = $env:OPENLINEOPS_AGENT_SERVICE_CLEANUP_MANIFEST_PATH
$cleanupManifestPath = if ([string]::IsNullOrWhiteSpace($requestedCleanupManifestPath)) {
    [System.IO.Path]::GetFullPath((Join-Path `
            ([System.IO.Path]::GetTempPath()) `
            "openlineops-agent-service-cleanup/rabbitmq-$serviceScope.json"))
}
else {
    [System.IO.Path]::GetFullPath($requestedCleanupManifestPath)
}
$cleanupScript = Join-Path $PSScriptRoot "invoke-run-scoped-agent-service-cleanup.ps1"

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
$previousServiceScope = $env:OPENLINEOPS_STAGED_AGENT_SERVICE_SCOPE
$previousCleanupManifestPath = $env:OPENLINEOPS_AGENT_SERVICE_CLEANUP_MANIFEST_PATH
$previousCleanupGate = $env:OPENLINEOPS_AGENT_SERVICE_CLEANUP_GATE
$previousDotnetCliLanguage = $env:DOTNET_CLI_UI_LANGUAGE
$previousVsLanguage = $env:VSLANG
$primaryFailure = $null
$cleanupFailure = $null
try {
    & $cleanupScript `
        -Kind rabbitmq `
        -Scope $serviceScope `
        -AgentBundleRoot $resolvedAgentBundleRoot `
        -ManifestPath $cleanupManifestPath `
        -Configuration $Configuration `
        -PrepareManifest `
        -NoBuild:$NoBuild `
        -NoRestore:$NoRestore
    $env:OPENLINEOPS_STAGED_AGENT_BUNDLE_ROOT = $resolvedAgentBundleRoot
    $env:OPENLINEOPS_STAGED_SAMPLE_PLUGIN_ROOT = $resolvedSamplePluginRoot
    $env:OPENLINEOPS_STAGED_API_BUNDLE_ROOT = $resolvedApiBundleRoot
    $env:OPENLINEOPS_RABBITMQ_URI = $parsedBrokerUri.AbsoluteUri
    $env:OPENLINEOPS_STAGED_AGENT_RABBITMQ_EVIDENCE_PATH = $evidencePath
    $env:OPENLINEOPS_STAGED_AGENT_SERVICE_SCOPE = $serviceScope
    $env:OPENLINEOPS_AGENT_SERVICE_CLEANUP_MANIFEST_PATH = $cleanupManifestPath
    $env:OPENLINEOPS_AGENT_SERVICE_CLEANUP_GATE = $null
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
catch {
    $primaryFailure = $_.Exception
}
finally {
    try {
        & $cleanupScript `
            -Kind rabbitmq `
            -Scope $serviceScope `
            -AgentBundleRoot $resolvedAgentBundleRoot `
            -ManifestPath $cleanupManifestPath `
            -Configuration $Configuration `
            -NoBuild:$NoBuild `
            -NoRestore:$NoRestore
    }
    catch {
        $cleanupFailure = $_.Exception
    }
    $env:OPENLINEOPS_STAGED_AGENT_BUNDLE_ROOT = $previousAgentBundleRoot
    $env:OPENLINEOPS_STAGED_SAMPLE_PLUGIN_ROOT = $previousSamplePluginRoot
    $env:OPENLINEOPS_STAGED_API_BUNDLE_ROOT = $previousApiBundleRoot
    $env:OPENLINEOPS_RABBITMQ_URI = $previousBrokerUri
    $env:OPENLINEOPS_STAGED_AGENT_RABBITMQ_EVIDENCE_PATH = $previousEvidencePath
    $env:OPENLINEOPS_RABBITMQ_OUTAGE_CONTROL = $previousOutageControl
    $env:OPENLINEOPS_STAGED_AGENT_SERVICE_SCOPE = $previousServiceScope
    $env:OPENLINEOPS_AGENT_SERVICE_CLEANUP_MANIFEST_PATH = $previousCleanupManifestPath
    $env:OPENLINEOPS_AGENT_SERVICE_CLEANUP_GATE = $previousCleanupGate
    $env:DOTNET_CLI_UI_LANGUAGE = $previousDotnetCliLanguage
    $env:VSLANG = $previousVsLanguage
}

if ($null -ne $primaryFailure -and $null -ne $cleanupFailure) {
    throw [System.AggregateException]::new(
        "Staged Agent RabbitMQ process E2E failed and run-scoped Windows service cleanup was incomplete.",
        @($primaryFailure, $cleanupFailure))
}
if ($null -ne $primaryFailure) { throw $primaryFailure }
if ($null -ne $cleanupFailure) { throw $cleanupFailure }

if (-not (Test-Path -LiteralPath $evidencePath -PathType Leaf)) {
    throw "Staged Agent RabbitMQ process E2E did not produce its required evidence: $evidencePath"
}

$evidence = Get-Content -LiteralPath $evidencePath -Raw | ConvertFrom-Json
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
Assert-RestrictedServiceIdentity `
    -Identity $evidence.agentHostIdentity `
    -ServiceName $evidence.windowsServiceName `
    -Description "Initial staged Agent host identity"
Assert-RestrictedServiceIdentity `
    -Identity $evidence.restartedAgentHostIdentity `
    -ServiceName $evidence.windowsServiceName `
    -Description "Restarted staged Agent host identity"
if ($evidence.agentHostIdentity.serviceSid -cne $evidence.restartedAgentHostIdentity.serviceSid) {
    throw "Restarted staged Agent service SID differs from its initial SCM service identity."
}
$materialArrivalIpcFields = @(
    "serviceTokenConnected",
    "pipeExactAclVerified",
    "durablePublicationVerified",
    "ordinaryCiTokenExplicitAccessDenied")
Assert-ExactProperties `
    -Value $evidence.materialArrivalIpc `
    -Expected $materialArrivalIpcFields `
    -Description "Staged Agent material-arrival IPC evidence"
foreach ($field in $materialArrivalIpcFields) {
    if ($evidence.materialArrivalIpc.$field -isnot [bool] `
        -or $evidence.materialArrivalIpc.$field -ne $true) {
        throw "Staged Agent material-arrival IPC evidence field '$field' must be the JSON boolean true."
    }
}
$immutableContentCacheFields = @(
    "packagedProvisionCommandVerified",
    "runningServiceAdministrationRejected",
    "serviceTokenReadExecuteVerified",
    "sealedMutationAccessDenied",
    "deepAncestorMutationAccessDenied",
    "preSealRecoveryVerified",
    "cleanupCrashResumeVerified",
    "committedAdminRemovalVerified",
    "packagedRemovalCommandVerified",
    "cacheNamespaceRemoved")
Assert-ExactProperties `
    -Value $evidence.immutableContentCache `
    -Expected $immutableContentCacheFields `
    -Description "Staged Agent immutable content-cache evidence"
foreach ($field in $immutableContentCacheFields) {
    if ($evidence.immutableContentCache.$field -isnot [bool] `
        -or $evidence.immutableContentCache.$field -ne $true) {
        throw "Staged Agent immutable content-cache evidence field '$field' must be the JSON boolean true."
    }
}
Assert-JsonBooleanProperties $evidence ([ordered]@{
        operatorTraceGetVerified = $true
        brokerOutageVerified = $true
        coordinatorTransportResultInboxRestartedAfterBrokerRecovery = $true
        offlineCompletionWasNotDelivered = $true
        completionDeliveredOnceAfterReconnect = $true
        duplicateRedeliveryRejected = $true
        duplicateAfterRestartRejected = $true
        windowsServiceLifecycleVerified = $true
        cleanShutdownVerified = $true
    }) "Staged Agent RabbitMQ evidence"
Assert-JsonBooleanProperties $evidence.presence ([ordered]@{
        startedAndHeartbeatPersisted = $true
        expiredOfflineDuringBrokerOutage = $true
        freshOnlineAfterReconnect = $true
    }) "Staged Agent presence evidence"
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
    -or [string]$evidence.windowsServiceName -cnotmatch '^OpenLineOpsAgentE2E-[0-9a-f]{32}$' `
    -or $evidence.windowsServiceLifecycleVerified -isnot [bool] `
    -or $evidence.windowsServiceLifecycleVerified -ne $true `
    -or $evidence.cleanShutdownVerified -ne $true) {
    throw "Staged Agent RabbitMQ process E2E evidence is incomplete or invalid."
}

Write-Host "Staged Agent RabbitMQ process E2E passed."
Write-Host " - Broker: $($parsedBrokerUri.Host):$($parsedBrokerUri.Port) (TLS: $($parsedBrokerUri.Scheme -ceq 'amqps'))"
Write-Host " - A run-scoped Windows service completed a signed frozen vendor helper while RabbitMQ was offline."
Write-Host " - SQLite buffered the result; reconnect delivered it once; redelivery and restart did not replay hardware."
Write-Host " - SCM start, stop, restart, and deletion were verified under LocalService with an exact restricted service SID."
Write-Host " - Coordinator transport/result inbox restarted after broker recovery; actual token groups and restricting SIDs proved least privilege."
Write-Host " - Persisted Started/Heartbeat presence expired to Offline during outage and recovered to fresh Online."
Write-Host " - Evidence: $evidencePath"

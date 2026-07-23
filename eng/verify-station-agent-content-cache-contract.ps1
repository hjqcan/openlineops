param()

$ErrorActionPreference = "Stop"
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))

function Read-RequiredText {
    param([Parameter(Mandatory = $true)][string] $RelativePath)

    $path = Join-Path $repoRoot $RelativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required Station content-cache contract file is missing: $RelativePath"
    }

    return Get-Content -LiteralPath $path -Raw
}

function Assert-ContainsLiteral {
    param(
        [Parameter(Mandatory = $true)][string] $Text,
        [Parameter(Mandatory = $true)][string] $Literal,
        [Parameter(Mandatory = $true)][string] $Failure
    )

    if ($Text -cnotmatch [regex]::Escape($Literal)) {
        throw $Failure
    }
}

$program = Read-RequiredText "src/OpenLineOps.Agent/Program.cs"
$command = Read-RequiredText "src/OpenLineOps.Agent/StationAgentContentCacheProvisioningCommand.cs"
$commandLine = Read-RequiredText "src/OpenLineOps.Agent/StationAgentCommandLine.cs"
$hostOptions = Read-RequiredText "src/OpenLineOps.Agent/StationAgentHostOptions.cs"
$executableContract = Read-RequiredText "tests/OpenLineOps.Agent.Tests/StationAgentExecutableContractTests.cs"
$contentProtector = Read-RequiredText "shared/OpenLineOps.ContentProtection/ImmutableContentProtector.cs"
$contentProtectorTests = Read-RequiredText "tests/OpenLineOps.ContentProtection.Tests/ImmutableContentProtectorTests.cs"
$agentStagedE2E = Read-RequiredText "tests/OpenLineOps.Agent.Tests/StagedAgentRabbitMqProcessE2ETests.cs"
$runnerStagedE2E = Read-RequiredText "tests/OpenLineOps.Runner.Tests/RunnerStagedAgentProcessE2ETests.cs"
$transactionLock = Read-RequiredText "shared/OpenLineOps.ContentProtection/ImmutableContentCacheTransactionLock.cs"
$studioHarness = Read-RequiredText "tests/OpenLineOps.Agent.Tests/StudioTwoAgentExternalProcessHarness.cs"
$serviceTokenBridge = Read-RequiredText "tests/OpenLineOps.Agent.Tests/WindowsServiceTokenTestBridge.cs"
$serviceTokenKernelLease = Read-RequiredText "tests/OpenLineOps.Agent.Tests/WindowsKernelObjectAccessLease.cs"
$serviceTokenProcessLease = Read-RequiredText "tests/OpenLineOps.Agent.Tests/WindowsProcessAccessLease.cs"
$serviceTokenContractTests = Read-RequiredText "tests/OpenLineOps.Agent.Tests/WindowsServiceTokenTestHelperContractTests.cs"
$serviceTokenHelperProject = Read-RequiredText "tests/OpenLineOps.WindowsServiceToken.TestHelper/OpenLineOps.WindowsServiceToken.TestHelper.csproj"
$serviceTokenHelperProtocol = Read-RequiredText "tests/OpenLineOps.WindowsServiceToken.TestHelper/TokenTransferProtocol.cs"
$serviceTokenHelperNative = Read-RequiredText "tests/OpenLineOps.WindowsServiceToken.TestHelper/WindowsNative.cs"
$serviceTokenHelperOperation = Read-RequiredText "tests/OpenLineOps.WindowsServiceToken.TestHelper/WindowsServiceTokenTransferOperation.cs"
$serviceTokenRelayProcess = Read-RequiredText "tests/OpenLineOps.WindowsServiceToken.TestHelper/SourceTokenRelayProcess.cs"
$serviceTokenRelayOperation = Read-RequiredText "tests/OpenLineOps.WindowsServiceToken.TestHelper/SourceTokenRelayOperation.cs"
$serviceTokenHelperRoot = Join-Path $repoRoot "tests/OpenLineOps.WindowsServiceToken.TestHelper"
$serviceTokenHelperSourceFiles = @(Get-ChildItem `
    -LiteralPath $serviceTokenHelperRoot `
    -Recurse `
    -File `
    -Filter "*.cs" | Where-Object {
        $relative = $_.FullName.Substring($serviceTokenHelperRoot.Length).TrimStart(
            [char[]]@([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar))
        $segments = $relative -split '[\\/]'
        -not ($segments -contains "bin" -or $segments -contains "obj")
    } | Sort-Object FullName)
if ($serviceTokenHelperSourceFiles.Count -eq 0) {
    throw "The Windows service-token helper project contains no controlled C# source files."
}
$reparseHelperSource = @($serviceTokenHelperSourceFiles | Where-Object {
        ($_.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0
    })
if ($reparseHelperSource.Count -ne 0) {
    throw "The Windows service-token helper project contains a reparse-point C# source file."
}
$serviceTokenHelperAllSource = [string]::Join(
    "`n",
    @($serviceTokenHelperSourceFiles | ForEach-Object {
            Get-Content -LiteralPath $_.FullName -Raw
        }))
$agentTestsProject = Read-RequiredText "tests/OpenLineOps.Agent.Tests/OpenLineOps.Agent.Tests.csproj"
$deployment = Read-RequiredText "docs/station-agent-deployment.md"
$security = Read-RequiredText "docs/station-agent-security.md"
$release = Read-RequiredText "docs/release-packaging.md"
$staging = Read-RequiredText "eng/stage-release-artifacts.ps1"
$inspection = Read-RequiredText "eng/inspect-release-candidate.ps1"
$scalarReaderMarker = "private static int ReadTokenScalar("
$groupsReaderMarker = "private static List<TokenGroupEvidence> ReadTokenGroups("
$runnerScalarReaderMarker = "private static int ReadTokenInt32("
$runnerBufferReaderMarker = "private static SafeHGlobalHandle ReadTokenBuffer("
$scalarReaderStart = $agentStagedE2E.IndexOf(
    $scalarReaderMarker,
    [System.StringComparison]::Ordinal)
$groupsReaderStart = $agentStagedE2E.IndexOf(
    $groupsReaderMarker,
    [System.StringComparison]::Ordinal)
if ($scalarReaderStart -lt 0 -or $groupsReaderStart -le $scalarReaderStart) {
    throw "Agent staged evidence is missing the bounded scalar token reader."
}
$scalarReader = $agentStagedE2E.Substring(
    $scalarReaderStart,
    $groupsReaderStart - $scalarReaderStart)
$runnerScalarReaderStart = $runnerStagedE2E.IndexOf(
    $runnerScalarReaderMarker,
    [System.StringComparison]::Ordinal)
$runnerBufferReaderStart = $runnerStagedE2E.IndexOf(
    $runnerBufferReaderMarker,
    [System.StringComparison]::Ordinal)
if ($runnerScalarReaderStart -lt 0 `
    -or $runnerBufferReaderStart -le $runnerScalarReaderStart) {
    throw "Runner staged evidence is missing the bounded scalar token reader."
}
$runnerScalarReader = $runnerStagedE2E.Substring(
    $runnerScalarReaderStart,
    $runnerBufferReaderStart - $runnerScalarReaderStart)
$helperIdentityMarker = "public static void ValidateHelperIdentity("
$sourceServiceMarker = "public static ValidatedSourceService OpenValidatedSourceService("
$sourceTokenIdentityMarker = "public static void ValidateCurrentSourceToken("
$sourceTokenIdentityEndMarker = "private static SafeServiceHandle OpenRequiredServiceControlManager("
$unrestrictedServiceSidMarker = "private static bool HasUnrestrictedVirtualServiceSidGroup("
$nativeFailureMarker = "private static Win32Exception NativeFailure("
$helperIdentityStart = $serviceTokenHelperNative.IndexOf(
    $helperIdentityMarker,
    [System.StringComparison]::Ordinal)
$sourceServiceStart = $serviceTokenHelperNative.IndexOf(
    $sourceServiceMarker,
    [System.StringComparison]::Ordinal)
if ($helperIdentityStart -lt 0 -or $sourceServiceStart -le $helperIdentityStart) {
    throw "The one-shot helper is missing its bounded virtual-service identity validator."
}
$helperIdentityValidation = $serviceTokenHelperNative.Substring(
    $helperIdentityStart,
    $sourceServiceStart - $helperIdentityStart)
$sourceTokenIdentityStart = $serviceTokenHelperNative.IndexOf(
    $sourceTokenIdentityMarker,
    [System.StringComparison]::Ordinal)
$sourceTokenIdentityEnd = $serviceTokenHelperNative.IndexOf(
    $sourceTokenIdentityEndMarker,
    [System.StringComparison]::Ordinal)
if ($sourceTokenIdentityStart -lt 0 `
    -or $sourceTokenIdentityEnd -le $sourceTokenIdentityStart) {
    throw "The one-shot helper is missing its bounded restricted Station-token validator."
}
$sourceTokenIdentityValidation = $serviceTokenHelperNative.Substring(
    $sourceTokenIdentityStart,
    $sourceTokenIdentityEnd - $sourceTokenIdentityStart)
$unrestrictedServiceSidStart = $serviceTokenHelperNative.IndexOf(
    $unrestrictedServiceSidMarker,
    [System.StringComparison]::Ordinal)
$nativeFailureStart = $serviceTokenHelperNative.IndexOf(
    $nativeFailureMarker,
    [System.StringComparison]::Ordinal)
if ($unrestrictedServiceSidStart -lt 0 `
    -or $nativeFailureStart -le $unrestrictedServiceSidStart) {
    throw "The one-shot helper is missing its bounded unrestricted service-SID predicate."
}
$unrestrictedServiceSidValidation = $serviceTokenHelperNative.Substring(
    $unrestrictedServiceSidStart,
    $nativeFailureStart - $unrestrictedServiceSidStart)

Assert-ContainsLiteral $commandLine "--provision-content-cache" `
    "Station Agent is missing the explicit content-cache provisioning switch."
Assert-ContainsLiteral $commandLine "--remove-content-cache-package" `
    "Station Agent is missing the explicit protected-package removal switch."
Assert-ContainsLiteral $commandLine "mutually exclusive" `
    "Station Agent does not reject simultaneous provisioning and removal modes."
Assert-ContainsLiteral $program "if (commandLine.ProvisionContentCache)" `
    "Station Agent does not dispatch provisioning before normal service startup."
Assert-ContainsLiteral $program "StationAgentContentCacheProvisioningCommand.Execute(builder.Configuration);" `
    "Station Agent provisioning mode is not connected to its administrative command."
Assert-ContainsLiteral $program "StationAgentContentCacheProvisioningCommand.RemovePackageAsync(" `
    "Station Agent protected-package removal mode is not connected to its administrative command."
Assert-ContainsLiteral $program "EventLog.WriteEntry(" `
    "Station Agent startup failures are not written to the registered Windows service EventLog source."
Assert-ContainsLiteral $program "StationAgentStartupDiagnostics.CreateEventLogFailureMessage(exception)" `
    "Station Agent startup failures are written to EventLog without the bounded credential-redaction boundary."
Assert-ContainsLiteral $executableContract "WindowsServiceStartupDiagnosticRedactsCredentialsAndBoundsEventLogPayload" `
    "Station Agent startup EventLog credential redaction lacks a regression test."
Assert-ContainsLiteral $agentStagedE2E "Startup diagnostic: {startupDiagnostic}" `
    "Staged Agent service startup failures do not preserve the EventLog diagnostic in CI output."
Assert-ContainsLiteral $command "OperatingSystem.IsWindows()" `
    "Content-cache provisioning does not fail closed outside Windows."
Assert-ContainsLiteral $command "EnsureAdministrativeCaller();" `
    "Content-cache provisioning does not require an administrative token."
Assert-ContainsLiteral $command "TokenAccessLevels.Query | TokenAccessLevels.Duplicate" `
    "Content-cache provisioning administrator classification does not request the token duplication right required by WindowsPrincipal role checks."
Assert-ContainsLiteral $command "ServiceSidFromNameRequired" `
    "Content-cache provisioning does not derive the Station SID from WindowsServiceName."
Assert-ContainsLiteral $command "ExternalProgramContentCapabilityName" `
    "Content-cache provisioning does not derive the runtime content capability SID."
Assert-ContainsLiteral $command "ProvisionCacheNamespace(" `
    "Content-cache provisioning does not invoke the immutable namespace API."
Assert-ContainsLiteral $command "RemoveProtectedPackageInstallationAsync(" `
    "Protected-package removal does not invoke the paired immutable cleanup API."
Assert-ContainsLiteral $command "Path.IsPathFullyQualified(configuredPath)" `
    "Content-cache provisioning does not reject relative cache paths."
Assert-ContainsLiteral $command "Path.GetFullPath(configuredPath)" `
    "Content-cache provisioning does not require the configured cache path to already be canonical."
Assert-ContainsLiteral $command "Path.EndsInDirectorySeparator(configuredPath)" `
    "Content-cache provisioning does not explicitly normalize one trailing separator."
Assert-ContainsLiteral $command "DriveType.Fixed" `
    "Content-cache provisioning does not require local fixed storage."
Assert-ContainsLiteral $command 'string.Equals(drive.DriveFormat, "NTFS"' `
    "Content-cache provisioning does not require the NTFS security boundary."
Assert-ContainsLiteral $contentProtector "public void ProvisionCacheNamespace(" `
    "Immutable content protection is missing its formal namespace provisioning API."
Assert-ContainsLiteral $contentProtector "ValueTask RemoveProtectedPackageInstallationAsync(" `
    "Immutable content protection is missing its formal protected-package removal API."
Assert-ContainsLiteral $contentProtector "must be fully stopped" `
    "Immutable content administration does not fail closed while the Station service can run."
Assert-ContainsLiteral $contentProtector "TokenAccessLevels.Query | TokenAccessLevels.Duplicate" `
    "Immutable content cleanup administrator classification does not request the token duplication right required by WindowsPrincipal role checks."
Assert-ContainsLiteral $transactionLock "TokenAccessLevels.Query | TokenAccessLevels.Duplicate" `
    "Immutable content transaction-lock owner classification does not request the token duplication right required by WindowsPrincipal role checks."
Assert-ContainsLiteral $agentStagedE2E "WindowsServiceTokenTestBridge.Run(" `
    "Staged Agent exact-service-token checks do not use the test-only reverse-pipe bridge."
Assert-ContainsLiteral $studioHarness ".RunAsService(" `
    "The packaged two-Agent gate does not route exact service identities through the shared reverse-pipe bridge."
foreach ($directTokenConsumer in @($agentStagedE2E, $studioHarness)) {
    if ($directTokenConsumer -cmatch 'DuplicateTokenEx|TokenDuplicate|DuplicateHandle|SeDebugPrivilege|AdjustTokenPrivileges') {
        throw "Staged Agent and Studio E2E harnesses must not duplicate service tokens or enable debug privilege directly."
    }
}
foreach ($bridgeLiteral in @(
        "NamedPipeServerStreamAcl.Create(",
        "PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance",
        "AuthenticatedPipeClientRights",
        "PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize",
        "pipe.RunAsClient(",
        "controlPipe.RunAsClient(",
        "ProtectOneShotServiceObject(",
        "SetServiceObjectSecurity(",
        "ServiceSidType = ServiceSidTypeUnrestricted",
        "CreateProtectedDirectory(bridgeRoot, security,",
        "PrepareResultRoot(resultRoot, bridgeServiceSid)",
        "AssertBridgeTreeSecurity(",
        "VerifyHelperBundle(",
        "SearchOption.TopDirectoryOnly",
        "CaptureCleanupFailure(",
        "CaptureHelperProcess(",
        "ValidateCapturedHelperProcess(",
        "ValidateCoordinationClient(",
        "OpenObservedRelayProcess(",
        "ValidateRelayControlClient(",
        "ValidateSourceServiceAndProcessRunning(",
        "GetNamedPipeClientProcessId(",
        "EnsureHelperProcessTerminated(",
        "WaitForProcessExit(",
        "WaitForDeletion(manager, bridgeServiceName, TransitionTimeout)",
        "DeleteBridgeDirectoryWithoutFollowingReparsePoints(",
        '$@"NT SERVICE\{bridgeServiceName}"',
        "DeleteServiceRequired(service, bridgeServiceName)")) {
    Assert-ContainsLiteral $serviceTokenBridge $bridgeLiteral `
        "The test-only Windows service-token bridge is missing strict boundary '$bridgeLiteral'."
}
if ($serviceTokenBridge -cmatch 'SearchOption\.AllDirectories') {
    throw "The service-token bridge must not recursively traverse helper or cleanup reparse points."
}
if ([regex]::Matches(
        $serviceTokenBridge,
        [regex]::Escape("VerifyHelperBundle(")).Count -lt 5) {
    throw "The complete self-contained helper bundle must be inventoried and reverified before service start, after helper capture, before relay resume, and after completion."
}
foreach ($bundleBoundary in @(
        'var protocolRoot = Path.Combine(bridgeRoot, "protocol")',
        'var resultRoot = Path.Combine(bridgeRoot, "result")',
        "FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize",
        "FileSystemRights.Modify",
        "HasReportedRelayProcess(result)",
        "HasReportedWithoutCreationTime(result)",
        "CaptureReportedRelayForCleanup(")) {
    Assert-ContainsLiteral $serviceTokenBridge $bundleBoundary `
        "The helper execution/result split or failed-relay recovery is missing boundary '$bundleBoundary'."
}
foreach ($leaseLiteral in @(
        "new CommonAce(",
        "AceQualifier.AccessAllowed",
        "SetKernelObjectSecurity(",
        "VerifyTemporaryDacl(",
        "RestoreRequired();",
        "AssertEquivalentDacl(",
        "RemoveScopedAccessAfterDaclDrift(",
        ".OfType<KnownAce>()",
        "if (!removedScopedAce")) {
    Assert-ContainsLiteral $serviceTokenKernelLease $leaseLiteral `
        "The shared kernel-object DACL lease is missing strict boundary '$leaseLiteral'."
}
$kernelLeaseDisposeIndex = $serviceTokenKernelLease.IndexOf(
    "public void Dispose()",
    [System.StringComparison]::Ordinal)
$kernelLeaseRestoreIndex = $serviceTokenKernelLease.IndexOf(
    "RestoreRequired();",
    $kernelLeaseDisposeIndex,
    [System.StringComparison]::Ordinal)
$kernelLeaseHandleCloseIndex = $serviceTokenKernelLease.IndexOf(
    "_handle.Dispose();",
    $kernelLeaseRestoreIndex,
    [System.StringComparison]::Ordinal)
$kernelLeaseDisposedIndex = $serviceTokenKernelLease.IndexOf(
    "_disposed = true;",
    $kernelLeaseHandleCloseIndex,
    [System.StringComparison]::Ordinal)
if ($kernelLeaseDisposeIndex -lt 0 `
    -or $kernelLeaseRestoreIndex -le $kernelLeaseDisposeIndex `
    -or $kernelLeaseHandleCloseIndex -le $kernelLeaseRestoreIndex `
    -or $kernelLeaseDisposedIndex -le $kernelLeaseHandleCloseIndex) {
    throw "A failed kernel-object DACL restoration must retain a live retryable lease until exact restoration succeeds."
}
foreach ($processLeaseLiteral in @(
        "ReadControl | WriteDac | ProcessQueryLimitedInformation | Synchronize",
        "ProcessCreateProcess",
        "unchecked((int)(ProcessCreateProcess",
        "WindowsKernelObjectAccessLease.PrepareOwnedHandle(",
        "ApplyRequired()")) {
    Assert-ContainsLiteral $serviceTokenProcessLease $processLeaseLiteral `
        "The source-process query lease is missing strict boundary '$processLeaseLiteral'."
}
if (Test-Path -LiteralPath (Join-Path $repoRoot "tests/OpenLineOps.Agent.Tests/WindowsTokenAccessLease.cs")) {
    throw "The retired source-token DACL lease must not exist."
}
if (($serviceTokenKernelLease + $serviceTokenProcessLease) `
    -cmatch 'LocalServiceSid|ServiceLogonSid|AdministratorsSid|TokenAllAccess|MaximumAllowed|TokenImpersonate|ProcessDupHandle|SeDebugPrivilege|SeTakeOwnershipPrivilege|AdjustTokenPrivileges') {
    throw "The service-token helper lease must grant only the random helper service SID exact relay-creation process access."
}
Assert-ContainsLiteral $serviceTokenBridge "WindowsProcessAccessLease.Prepare(" `
    "The reverse-pipe bridge does not prepare the exact scoped source-process relay-creation lease before mutation."
Assert-ContainsLiteral $serviceTokenBridge "sourceProcessAccessLease.ApplyRequired();" `
    "The reverse-pipe bridge does not explicitly activate the retained source-process relay-creation lease."
foreach ($handshakeLiteral in @(
        "WriteProtocolByte(coordinationPipe, RelayCreationGrant)",
        "ParseObservedRelayFrame(observedRelayFrame)",
        "relayProcessHandle = OpenObservedRelayProcess(relayProcessId)",
        "WriteRelayCaptureAcknowledgement(",
        "preparedRelayMarker[0] != PreparedRelayMarker",
        "sourceProcessAccessLease.Dispose();",
        "WriteProtocolByte(coordinationPipe, RelayResumeAcknowledgement)",
        "ValidateRelayControlClient(",
        "HasCapturedRelayBinding(")) {
    Assert-ContainsLiteral $serviceTokenBridge $handshakeLiteral `
        "The reverse-pipe bridge is missing authenticated pre-resume handshake '$handshakeLiteral'."
}
$coordinationValidationIndex = $serviceTokenBridge.IndexOf(
    "ValidateCoordinationClient(",
    [System.StringComparison]::Ordinal)
$leaseAcquireIndex = $serviceTokenBridge.IndexOf(
    "sourceProcessAccessLease = WindowsProcessAccessLease.Prepare(",
    [System.StringComparison]::Ordinal)
$leaseApplyIndex = $serviceTokenBridge.IndexOf(
    "sourceProcessAccessLease.ApplyRequired();",
    [System.StringComparison]::Ordinal)
$creationGrantIndex = $serviceTokenBridge.IndexOf(
    "WriteProtocolByte(coordinationPipe, RelayCreationGrant)",
    [System.StringComparison]::Ordinal)
$relayObservedIndex = $serviceTokenBridge.IndexOf(
    "ParseObservedRelayFrame(observedRelayFrame)",
    [System.StringComparison]::Ordinal)
$relayCaptureIndex = $serviceTokenBridge.IndexOf(
    "relayProcessHandle = OpenObservedRelayProcess(relayProcessId)",
    [System.StringComparison]::Ordinal)
$runnerRelayCreationReadIndex = $serviceTokenBridge.IndexOf(
    "relayProcessCreatedAtUtcTicks = ReadProcessCreationUtcTicks(",
    $relayCaptureIndex,
    [System.StringComparison]::Ordinal)
$runnerRelayValidationIndex = $serviceTokenBridge.IndexOf(
    "ValidateCapturedRelayProcess(",
    $runnerRelayCreationReadIndex,
    [System.StringComparison]::Ordinal)
$captureAcknowledgementIndex = $serviceTokenBridge.IndexOf(
    "WriteRelayCaptureAcknowledgement(",
    [System.StringComparison]::Ordinal)
$relayReadyIndex = $serviceTokenBridge.IndexOf(
    "preparedRelayMarker[0] != PreparedRelayMarker",
    [System.StringComparison]::Ordinal)
$preResumeRestoreIndex = $serviceTokenBridge.IndexOf(
    "sourceProcessAccessLease.Dispose();",
    [System.StringComparison]::Ordinal)
$controlPipeCreateIndex = $serviceTokenBridge.IndexOf(
    "controlPipe = CreateAuthenticatedPipe(",
    [System.StringComparison]::Ordinal)
$resumeAcknowledgementIndex = $serviceTokenBridge.IndexOf(
    "WriteProtocolByte(coordinationPipe, RelayResumeAcknowledgement)",
    [System.StringComparison]::Ordinal)
$controlClientValidationIndex = $serviceTokenBridge.IndexOf(
    "ValidateRelayControlClient(",
    [System.StringComparison]::Ordinal)
$actionImpersonationIndex = $serviceTokenBridge.IndexOf(
    "controlPipe.RunAsClient(",
    [System.StringComparison]::Ordinal)
if ($coordinationValidationIndex -lt 0 `
    -or $leaseAcquireIndex -le $coordinationValidationIndex `
    -or $leaseApplyIndex -le $leaseAcquireIndex `
    -or $creationGrantIndex -le $leaseApplyIndex `
    -or $relayObservedIndex -le $creationGrantIndex `
    -or $relayCaptureIndex -le $relayObservedIndex `
    -or $runnerRelayCreationReadIndex -le $relayCaptureIndex `
    -or $runnerRelayValidationIndex -le $runnerRelayCreationReadIndex `
    -or $captureAcknowledgementIndex -le $runnerRelayValidationIndex `
    -or $relayReadyIndex -le $captureAcknowledgementIndex `
    -or $preResumeRestoreIndex -le $relayReadyIndex `
    -or $controlPipeCreateIndex -le $preResumeRestoreIndex `
    -or $resumeAcknowledgementIndex -le $controlPipeCreateIndex `
    -or $controlClientValidationIndex -le $resumeAcknowledgementIndex `
    -or $actionImpersonationIndex -le $controlClientValidationIndex) {
    throw "The exact helper/relay handshake does not authenticate, close relay-creation access, restore the DACL, and bind the control client before action impersonation."
}
foreach ($frameLiteral in @(
        "ObservedRelayFrameBytes = 1 + sizeof(uint)",
        "RelayCaptureAcknowledgementFrameBytes = 1 + sizeof(long)",
        "HasFailureResultRelayBinding(")) {
    Assert-ContainsLiteral $serviceTokenBridge $frameLiteral `
        "The runner is missing PID-first retained-handle handshake boundary '$frameLiteral'."
}
foreach ($frameLiteral in @(
        "RelayObservedBytes = 1 + sizeof(uint)",
        "RunnerCaptureAcknowledgementBytes = 1 + sizeof(long)",
        "SourceTokenRelayCreationException")) {
    Assert-ContainsLiteral $serviceTokenHelperOperation $frameLiteral `
        "The helper is missing PID-first retained-handle handshake boundary '$frameLiteral'."
}
Assert-ContainsLiteral $serviceTokenBridge "if (before.CurrentState == ServiceStartPending)" `
    "The reverse-pipe bridge does not wait out SCM's non-authoritative start-pending process identifier."
Assert-ContainsLiteral $serviceTokenBridge "if (before.CurrentState != ServiceRunning" `
    "The reverse-pipe bridge does not require a running own-process service before capturing its exact process handle."
if ($serviceTokenBridge -cmatch 'CurrentState is not \(ServiceStartPending or ServiceRunning\)') {
    throw "The reverse-pipe bridge must not trust an SCM process identifier while the helper service is start-pending."
}
foreach ($provisionalRelayLiteral in @(
        "var relayProcessIdentityConfirmed = false;",
        "relayProcessIdentityConfirmed = true;",
        "&& relayProcessIdentityConfirmed",
        "&& helperProcessTerminationProven")) {
    Assert-ContainsLiteral $serviceTokenBridge $provisionalRelayLiteral `
        "The runner is missing provisional-relay cleanup boundary '$provisionalRelayLiteral'."
}
$reportedRelayCleanupIndex = $serviceTokenBridge.IndexOf(
    "private static SafeProcessHandle? CaptureReportedRelayForCleanup(",
    [System.StringComparison]::Ordinal)
$pidOnlyCleanupRejectionIndex = $serviceTokenBridge.IndexOf(
    "if (expectedCreatedAtUtcTicks == 0)",
    $reportedRelayCleanupIndex,
    [System.StringComparison]::Ordinal)
$reportedRelayOpenIndex = $serviceTokenBridge.IndexOf(
    "var process = OpenProcess(",
    $reportedRelayCleanupIndex,
    [System.StringComparison]::Ordinal)
if ($reportedRelayCleanupIndex -lt 0 `
    -or $pidOnlyCleanupRejectionIndex -le $reportedRelayCleanupIndex `
    -or $reportedRelayOpenIndex -le $pidOnlyCleanupRejectionIndex) {
    throw "PID-only relay diagnostics must fail before reopening any unbound process with termination rights."
}
Assert-ContainsLiteral $serviceTokenBridge "var postTerminationWait = WaitForSingleObject(" `
    "The exact helper-process cleanup does not close the normal-exit race after TerminateProcess failure."
Assert-ContainsLiteral $serviceTokenContractTests "SourceProcessAccessLeaseGrantsOnlyRelayCreationQueryAndWaitAndRestoresDacl" `
    "The scoped source-process relay-creation lease lacks an apply-and-exact-restore regression test."
Assert-ContainsLiteral $serviceTokenContractTests "OverlappingKernelObjectLeasesRemoveOnlyTheirOwnSidAndRejectDaclDrift" `
    "The shared kernel-object lease does not behavior-test fail-closed DACL drift cleanup."
Assert-ContainsLiteral $serviceTokenContractTests "SourceProcessAccessLeaseRejectsExitedProcessWithStillActiveExitCode" `
    "The source-process lease does not regression-test exit code 259 against an exact wait handle."
Assert-ContainsLiteral $serviceTokenContractTests "HelperProcessCleanupTerminatesOnlyTheExactCapturedProcess" `
    "The one-shot helper cleanup does not behavior-test forced termination of its exact captured process."
$helperTerminationIndex = $serviceTokenBridge.IndexOf(
    "EnsureHelperProcessTerminated(",
    [System.StringComparison]::Ordinal)
$sourceDaclRestoreIndex = $serviceTokenBridge.LastIndexOf(
    "sourceProcessAccessLease.Dispose();",
    [System.StringComparison]::Ordinal)
$relayTerminationIndex = $serviceTokenBridge.IndexOf(
    "if (relayProcessHandle is not null)",
    [System.StringComparison]::Ordinal)
if ($helperTerminationIndex -lt 0 `
    -or $relayTerminationIndex -le $helperTerminationIndex `
    -or $sourceDaclRestoreIndex -le $relayTerminationIndex) {
    throw "The source process DACL is not restored after exact helper and relay termination are attempted."
}
foreach ($helperLiteral in @(
        "ProcessCreateProcess",
        "ServiceSidTypeRestricted",
        "BorrowedCurrentProcessTokenHandle",
        "ValidateHelperIdentity",
        "ValidateCurrentSourceToken")) {
    Assert-ContainsLiteral $serviceTokenHelperNative $helperLiteral `
        "The one-shot helper is missing strict source-token boundary '$helperLiteral'."
}
foreach ($unrestrictedServiceSidConstant in @(
        "private const uint GroupEnabledByDefault = 0x00000002;",
        "private const uint GroupOwner = 0x00000008;")) {
    Assert-ContainsLiteral $serviceTokenHelperNative $unrestrictedServiceSidConstant `
        "The one-shot helper is missing Windows' documented unrestricted service-SID attribute '$unrestrictedServiceSidConstant'."
}
foreach ($helperIdentityLiteral in @(
        'var helperServiceSid = DeriveServiceSid(helperServiceName, "helper");',
        "identity.TokenType != TokenPrimary",
        "identity.ElevationType != TokenElevationTypeDefault",
        "identity.IsRestricted",
        "HasEnabledGroup(identity.Groups, ServiceLogonSid)",
        "HasUnrestrictedVirtualServiceSidGroup(",
        "identity.RestrictedSids.Any(group => string.Equals(",
        "AdministratorsSid")) {
    Assert-ContainsLiteral $helperIdentityValidation $helperIdentityLiteral `
        "The one-shot helper identity validator is missing exact virtual-account boundary '$helperIdentityLiteral'."
}
if ([regex]::Matches(
        $helperIdentityValidation,
        [regex]::Escape("identity.UserSid")).Count -ne 1 `
    -or $helperIdentityValidation -cnotmatch '(?s)!\s*string\.Equals\(\s*identity\.UserSid,\s*helperServiceSid,\s*StringComparison\.Ordinal\)') {
    throw "The one-shot helper must accept only its exact random virtual service account as TokenUser, never LocalService or a shared service SID."
}
if ($helperIdentityValidation -cmatch '(?s)HasEnabledGroup\(\s*identity\.Groups,\s*helperServiceSid\s*\)') {
    throw "The helper service SID must use its dedicated unrestricted-service attribute predicate, not the enabled-group predicate used by the Station relay."
}
if ($helperIdentityValidation -cnotmatch '(?s)HasUnrestrictedVirtualServiceSidGroup\(\s*identity\.Groups,\s*helperServiceSid\s*\)') {
    throw "The helper identity validator must apply its unrestricted-service attribute predicate to the exact derived helper service SID."
}
if ($helperIdentityValidation -cnotmatch '(?s)identity\.Groups\.Any\(group => string\.Equals\(\s*group\.Sid,\s*AdministratorsSid,\s*StringComparison\.Ordinal\)\)') {
    throw "The one-shot helper identity validator must reject an Administrators SID regardless of that SID's group attributes."
}
if ($helperIdentityValidation -cnotmatch '(?s)identity\.RestrictedSids\.Any\(group => string\.Equals\(\s*group\.Sid,\s*helperServiceSid,\s*StringComparison\.Ordinal\)\)') {
    throw "The unrestricted helper identity validator must reject its exact service SID from TokenRestrictedSids."
}
foreach ($unrestrictedServiceSidLiteral in @(
        "const uint requiredAttributes = GroupEnabledByDefault | GroupOwner;",
        "string.Equals(group.Sid, sid, StringComparison.Ordinal)",
        "(group.Attributes & requiredAttributes) == requiredAttributes",
        "(group.Attributes & GroupUseForDenyOnly) == 0")) {
    Assert-ContainsLiteral $unrestrictedServiceSidValidation $unrestrictedServiceSidLiteral `
        "The helper unrestricted service-SID predicate is missing documented boundary '$unrestrictedServiceSidLiteral'."
}
if ($unrestrictedServiceSidValidation -cmatch '\bGroupEnabled\b') {
    throw "The unrestricted helper service SID must not require SE_GROUP_ENABLED; Windows documents ENABLED_BY_DEFAULT and OWNER for that entry."
}
foreach ($sourceTokenIdentityLiteral in @(
        "string.Equals(evidence.UserSid, LocalServiceSid, StringComparison.Ordinal)",
        "evidence.IsRestricted",
        "HasEnabledGroup(evidence.Groups, ServiceLogonSid)",
        "HasEnabledGroup(evidence.Groups, expectedServiceSid)",
        "evidence.RestrictedSids.Any(group => string.Equals(",
        "AdministratorsSid")) {
    Assert-ContainsLiteral $sourceTokenIdentityValidation $sourceTokenIdentityLiteral `
        "The restricted Station relay identity validator is missing boundary '$sourceTokenIdentityLiteral'."
}
if ($sourceTokenIdentityValidation -cmatch 'HasUnrestrictedVirtualServiceSidGroup') {
    throw "The restricted LocalService Station relay must not reuse the virtual-account helper SID predicate."
}
if ($sourceTokenIdentityValidation -cnotmatch '(?s)evidence\.RestrictedSids\.Any\(group => string\.Equals\(\s*group\.Sid,\s*expectedServiceSid,\s*StringComparison\.Ordinal\)\)') {
    throw "The restricted LocalService Station relay must retain its exact service SID in TokenRestrictedSids."
}
if (($serviceTokenBridge + $serviceTokenHelperAllSource) `
    -cmatch 'DuplicateTokenEx|DuplicateHandle|OpenProcessToken|OpenThreadToken|TokenDuplicate|TokenImpersonate|ProcessDupHandle|TokenAllAccess|MaximumAllowed|SeDebugPrivilege|SeTakeOwnershipPrivilege|AdjustTokenPrivileges|CreateRemoteThread|ProcessVm(Read|Write|Operation)|CreateBreakawayFromJob') {
    throw "The reverse-pipe bridge must inherit the exact source process token without exporting handles or widening process/token privileges."
}
if ($serviceTokenHelperAllSource -cmatch 'IsTokenRestricted\(') {
    throw "Token restriction evidence must come from the already validated TokenRestrictedSids inventory, not the ambiguous IsTokenRestricted FALSE result."
}
Assert-ContainsLiteral $serviceTokenHelperProtocol "args.Count != 2" `
    "The one-shot helper does not reject any invocation other than its fixed request protocol."
Assert-ContainsLiteral $serviceTokenHelperProtocol "unknown property" `
    "The one-shot helper request protocol does not reject unknown fields."
Assert-ContainsLiteral $serviceTokenHelperProtocol "ReadServiceRequest(" `
    "The SCM helper lacks role-specific validation of its writable result destination."
Assert-ContainsLiteral $serviceTokenHelperProtocol "ReadRelayRequest(" `
    "The source-token relay must parse the shared request without touching its inaccessible result directory."
Assert-ContainsLiteral $serviceTokenHelperOperation "SourceTokenRelayProcess.CreateSuspended(" `
    "The helper does not launch the fixed relay through the exact source Station process."
foreach ($coordinationLiteral in @(
        "ConnectAndAwaitGrantAsync(",
        "using (var creationSourceProcess = WindowsNative.OpenRequiredProcess(",
        "using var weakSourceProcess = WindowsNative.OpenRequiredProcess(",
        "SendObservedAndAwaitCaptureAsync(",
        "relay.BindCreatedAtUtcTicks(runnerCapturedCreatedAtUtcTicks)",
        "relayProcessCreatedAtUtcTicks = relay.CreatedAtUtcTicks;",
        "relay.ValidateCreated(request, creationSourceProcess)",
        "SendReadyAndAwaitResumeAsync(",
        "relay.Resume();")) {
    Assert-ContainsLiteral $serviceTokenHelperOperation $coordinationLiteral `
        "The helper operation is missing strict suspended-relay coordination '$coordinationLiteral'."
}
$createSuspendedIndex = $serviceTokenHelperOperation.IndexOf(
    "SourceTokenRelayProcess.CreateSuspended(",
    [System.StringComparison]::Ordinal)
$openWeakProcessIndex = $serviceTokenHelperOperation.IndexOf(
    "using var weakSourceProcess = WindowsNative.OpenRequiredProcess(",
    [System.StringComparison]::Ordinal)
$sendObservedIndex = $serviceTokenHelperOperation.IndexOf(
    "SendObservedAndAwaitCaptureAsync(",
    [System.StringComparison]::Ordinal)
$validateRelayIndex = $serviceTokenHelperOperation.IndexOf(
    "relay.ValidateCreated(request, creationSourceProcess)",
    [System.StringComparison]::Ordinal)
$bindRelayCreationTimeIndex = $serviceTokenHelperOperation.IndexOf(
    "relay.BindCreatedAtUtcTicks(runnerCapturedCreatedAtUtcTicks)",
    [System.StringComparison]::Ordinal)
$publishRelayCreationTimeIndex = $serviceTokenHelperOperation.IndexOf(
    "relayProcessCreatedAtUtcTicks = relay.CreatedAtUtcTicks;",
    [System.StringComparison]::Ordinal)
$sendReadyIndex = $serviceTokenHelperOperation.IndexOf(
    "SendReadyAndAwaitResumeAsync(",
    [System.StringComparison]::Ordinal)
$resumeRelayIndex = $serviceTokenHelperOperation.IndexOf(
    "relay.Resume();",
    [System.StringComparison]::Ordinal)
if ($createSuspendedIndex -lt 0 `
    -or $sendObservedIndex -le $createSuspendedIndex `
    -or $bindRelayCreationTimeIndex -le $sendObservedIndex `
    -or $publishRelayCreationTimeIndex -le $bindRelayCreationTimeIndex `
    -or $validateRelayIndex -le $publishRelayCreationTimeIndex `
    -or $openWeakProcessIndex -le $validateRelayIndex `
    -or $sendReadyIndex -le $openWeakProcessIndex `
    -or $resumeRelayIndex -le $sendReadyIndex) {
    throw "The helper does not create suspended, downgrade to a weak source handle, authenticate readiness, and only then resume the relay."
}
Assert-ContainsLiteral $serviceTokenHelperOperation "WaitForSuccessfulExitAsync(" `
    "The helper does not wait for exact source-token relay termination."
foreach ($relayProcessLiteral in @(
        "ProcThreadAttributeParentProcess",
        "ProcThreadAttributeJobList",
        "JobObjectLimitActiveProcess | JobObjectLimitKillOnJobClose",
        "CreateNoWindow",
        "CreateSuspendedFlag",
        "CreateUnicodeEnvironment",
        "ExtendedStartupInfoPresent",
        "inheritHandles: false",
        "ResumeThread(_thread)",
        "previousSuspendCount != 1",
        "IsProcessInJob(sourceProcess, IntPtr.Zero",
        "IsProcessInJob(_process, _job",
        "TerminateJobObject(job, 70)",
        "TerminateProcess(process, 70)",
        "BindCreatedAtUtcTicks(",
        "ValidateCreated(")) {
    Assert-ContainsLiteral $serviceTokenRelayProcess $relayProcessLiteral `
        "The fixed source-token relay is missing process containment boundary '$relayProcessLiteral'."
}
$createRelayMethodIndex = $serviceTokenRelayProcess.IndexOf(
    "public static SourceTokenRelayProcess CreateSuspended(",
    [System.StringComparison]::Ordinal)
$createRelayReturnIndex = $serviceTokenRelayProcess.IndexOf(
    "return relay;",
    $createRelayMethodIndex,
    [System.StringComparison]::Ordinal)
$firstCreationTimeReadIndex = $serviceTokenRelayProcess.IndexOf(
    "ReadCreatedAtUtcTicks(",
    [System.StringComparison]::Ordinal)
if ($createRelayMethodIndex -lt 0 `
    -or $createRelayReturnIndex -le $createRelayMethodIndex `
    -or $firstCreationTimeReadIndex -le $createRelayReturnIndex) {
    throw "CreateSuspended must return ownership immediately after native process creation; creation-time reads belong to the acknowledged binding phase."
}
foreach ($relayOperationLiteral in @(
        "ValidateCurrentSourceToken(request.ExpectedSourceServiceSid)",
        "ValidateCurrentRelayExecutable(request)",
        "ValidateSourceExecutableFile(request)",
        "TokenImpersonationLevel.Impersonation",
        "request.ControlPipeName")) {
    Assert-ContainsLiteral $serviceTokenRelayOperation $relayOperationLiteral `
        "The fixed source-token relay is missing identity boundary '$relayOperationLiteral'."
}
Assert-ContainsLiteral $serviceTokenHelperOperation "RelayCompletionTimeout = TimeSpan.FromSeconds(90)" `
    "The helper does not leave a distinct receipt grace period beyond the bounded Agent actions."
Assert-ContainsLiteral $serviceTokenHelperOperation 'failurePhase = "helper-identity"' `
    "The helper does not publish bounded failure-phase diagnostics."
Assert-ContainsLiteral $serviceTokenHelperOperation 'failureReason = "open-process"' `
    "The helper does not distinguish its bounded source-process failure reason."
Assert-ContainsLiteral $serviceTokenHelperOperation "FindWin32Error(failureDiagnosticException ?? operationFailure)" `
    "The helper does not publish a numeric Win32 failure code."
Assert-ContainsLiteral $serviceTokenHelperOperation 'failurePhase = "source-relay-cleanup"' `
    "The helper cannot distinguish exact relay cleanup failure from operation failure."
Assert-ContainsLiteral $serviceTokenHelperProtocol "string FailureReason" `
    "The strict helper result schema is missing its bounded failure reason."
Assert-ContainsLiteral $serviceTokenHelperProtocol "int Win32Error" `
    "The strict helper result schema is missing its numeric Win32 error."
Assert-ContainsLiteral $serviceTokenBridge "AllowDuplicateProperties = false" `
    "The strict helper result parser does not reject duplicate JSON properties."
Assert-ContainsLiteral $serviceTokenBridge "HasValidResultContract(result)" `
    "The strict helper result parser does not validate phase, reason, flags, and Win32 error as one contract."
Assert-ContainsLiteral $serviceTokenContractTests "BridgeResultProtocolRejectsDuplicateUnknownAndInconsistentFields" `
    "The strict helper result parser lacks malformed-protocol regression coverage."
Assert-ContainsLiteral $serviceTokenContractTests "BridgeResultProtocolAcceptsEveryBoundedFailurePhaseAndReason" `
    "The strict helper result parser lacks exhaustive valid phase/reason regression coverage."
Assert-ContainsLiteral $serviceTokenContractTests "HelperBundleInventoryRejectsMutationAndUnexpectedFiles" `
    "The complete helper bundle inventory lacks mutation and extra-file regression coverage."
Assert-ContainsLiteral $serviceTokenContractTests "CapturedRelayBindingRejectsMismatchedPidOrCreationTime" `
    "The exact relay PID and creation-time binding lacks mismatch regression coverage."
Assert-ContainsLiteral $serviceTokenContractTests "WindowsServiceTokenTestBridge.HasCapturedRelayBinding(" `
    "The exact relay PID and creation-time binding lacks a pure contract predicate."
Assert-ContainsLiteral $serviceTokenContractTests "ResultRelayBindingRejectsMismatchedPidOrCreationTime" `
    "Helper success and diagnostic results lack exact relay PID/time binding regression coverage."
Assert-ContainsLiteral $serviceTokenContractTests "FailureResultRelayBindingAllowsPidOnlyBeforeRelayReady" `
    "The PID-first failure-result binding lacks pre/post ready-marker regression coverage."
Assert-ContainsLiteral $serviceTokenContractTests "AuthenticatedPipeClientRightsExcludeAdministrativeAndInstanceCreationRights" `
    "The helper and relay pipe client masks lack a regression test against administrative and instance-creation rights."
foreach ($relayBindingLiteral in @(
        "HasResultRelayBinding(",
        "bridgeResult.RelayProcessId",
        "bridgeResult.RelayProcessCreatedAtUtcTicks")) {
    Assert-ContainsLiteral $serviceTokenBridge $relayBindingLiteral `
        "The runtime bridge is missing captured relay binding '$relayBindingLiteral'."
}
Assert-ContainsLiteral $serviceTokenHelperProtocol "var sourceExecutablePath = RequireCanonicalAbsolutePath(" `
    "The helper protocol still touches the frozen Agent executable before source-token impersonation."
$sourceExecutablePathStart = $serviceTokenHelperProtocol.IndexOf(
    "var sourceExecutablePath =",
    [System.StringComparison]::Ordinal)
$controlPipePathStart = $serviceTokenHelperProtocol.IndexOf(
    "var helperBundleRoot =",
    [System.StringComparison]::Ordinal)
if ($sourceExecutablePathStart -lt 0 `
    -or $controlPipePathStart -le $sourceExecutablePathStart `
    -or $serviceTokenHelperProtocol.Substring(
        $sourceExecutablePathStart,
        $controlPipePathStart - $sourceExecutablePathStart) -cmatch `
        'File\.(Exists|GetAttributes)|Directory\.(Exists|GetAttributes)') {
    throw "The helper protocol accesses the frozen Agent executable before the source-token relay starts."
}
foreach ($projectLiteral in @(
        '<RuntimeIdentifier Condition="$([MSBuild]::IsOSPlatform(''Windows''))">win-x64</RuntimeIdentifier>',
        '<SelfContained Condition="$([MSBuild]::IsOSPlatform(''Windows''))">true</SelfContained>',
        "<IsPublishable>false</IsPublishable>")) {
    Assert-ContainsLiteral $serviceTokenHelperProject $projectLiteral `
        "The Windows service-token helper project is missing release isolation '$projectLiteral'."
}
$agentTestsProjectXml = [xml]$agentTestsProject
$serviceTokenHelperReferences = @(
    $agentTestsProjectXml.Project.ItemGroup.ProjectReference | Where-Object {
        $_.Include -ceq `
            '..\OpenLineOps.WindowsServiceToken.TestHelper\OpenLineOps.WindowsServiceToken.TestHelper.csproj'
    })
if ($serviceTokenHelperReferences.Count -ne 1 `
    -or $serviceTokenHelperReferences[0].ReferenceOutputAssembly -cne 'false') {
    throw "Agent tests must reference exactly one Windows service-token helper project with ReferenceOutputAssembly=false."
}
Assert-ContainsLiteral $agentTestsProject "windows-service-token-test-helper" `
    "Agent tests do not stage the self-contained Windows service-token helper in a test-only directory."
Assert-ContainsLiteral $contentProtector "ReadIsRestrictedToken(identity.AccessToken);" `
    "Station service identity validation does not use the native restricted-token predicate."
Assert-ContainsLiteral $contentProtector "IsTokenRestricted(token);" `
    "Station service identity validation does not call the Windows restricted-token predicate."
Assert-ContainsLiteral $contentProtectorTests "WindowsRestrictedTokenPredicateUsesTheNativeSecurityBoundary" `
    "Content protection tests do not exercise the native restricted-token predicate on Windows."
Assert-ContainsLiteral $runnerStagedE2E "IsRestrictedToken: IsTokenRestricted(token)," `
    "Runner staged Agent evidence does not use the native restricted-token predicate."
Assert-ContainsLiteral $runnerStagedE2E "TokenInformationClass.TokenElevationType" `
    "Runner staged Agent evidence does not inspect the UAC linked-token boundary."
Assert-ContainsLiteral $runnerStagedE2E "HasLinkedToken" `
    "Runner staged Agent evidence does not expose the linked-token boundary."
Assert-ContainsLiteral $runnerScalarReader "const int bufferLength = sizeof(int);" `
    "Runner staged Agent scalar token evidence does not use the exact native integer width."
Assert-ContainsLiteral $runnerScalarReader "returnedLength != bufferLength" `
    "Runner staged Agent scalar token evidence does not require the returned width to match its exact native integer buffer."
if ($runnerScalarReader -cmatch 'ReadTokenBuffer|IntPtr\.Zero|ErrorInsufficientBuffer|requiredBytes') {
    throw "Runner staged Agent scalar token evidence must call GetTokenInformation with the exact fixed buffer instead of a variable-length sizing probe."
}
Assert-ContainsLiteral $agentStagedE2E "IsTokenRestricted(token.DangerousGetHandle())," `
    "Agent staged evidence does not use the native restricted-token predicate."
Assert-ContainsLiteral $agentStagedE2E "TokenInformationClass.TokenElevationType" `
    "Agent staged evidence does not inspect the UAC linked-token boundary."
Assert-ContainsLiteral $agentStagedE2E "HasLinkedToken" `
    "Agent staged evidence does not expose the linked-token boundary."
Assert-ContainsLiteral $scalarReader "const int bufferLength = sizeof(int);" `
    "Agent staged scalar token evidence does not use the exact native integer width."
Assert-ContainsLiteral $scalarReader "returnedLength != bufferLength" `
    "Agent staged scalar token evidence does not require the returned width to match its exact native integer buffer."
if ($scalarReader -cmatch 'IntPtr\.Zero|ErrorInsufficientBuffer|requiredLength') {
    throw "Agent staged scalar token evidence must call GetTokenInformation with the exact fixed buffer instead of a variable-length sizing probe."
}
if ($contentProtector -cmatch 'TokenHasRestrictions') {
    throw "Station identity validation must not use TokenHasRestrictions as the restricted-token predicate."
}
if ($runnerStagedE2E -cmatch 'TokenHasRestrictions') {
    throw "Runner staged Agent evidence must not use TokenHasRestrictions as the restricted-token predicate."
}
if ($agentStagedE2E -cmatch 'TokenHasRestrictions') {
    throw "Agent staged evidence must not use TokenHasRestrictions as the restricted-token predicate."
}
Assert-ContainsLiteral $hostOptions 'section["PackageCacheDirectory"]' `
    "Station Agent host options do not read PackageCacheDirectory."
Assert-ContainsLiteral $hostOptions '"OpenLineOps:Agent:PackageCacheDirectory"' `
    "Station Agent host options do not require the explicit PackageCacheDirectory setting."
Assert-ContainsLiteral $hostOptions "StationAgentPackageCachePath.RequireCanonicalAbsolute" `
    "Normal Station Agent startup does not enforce the same canonical absolute cache path as provisioning."
Assert-ContainsLiteral $executableContract "AdministrativeContentCacheModesAreExposedByAgentExecutable" `
    "The built Station Agent executable has no regression proof for both administrative cache modes."
Assert-ContainsLiteral $executableContract "ProvisioningModeClassifiesCallerWithoutGenericTokenAccessFailure" `
    "The built Station Agent executable has no process-level regression proof for administrative token classification."
if ($hostOptions -cmatch 'Path\.Combine\(dataDirectory,\s*"(?:content|cache)"\)') {
    throw "Station Agent host options still contain an implicit data-directory package-cache fallback."
}

$configurationPath = Join-Path $repoRoot "src/OpenLineOps.Agent/appsettings.json"
$configuration = Get-Content -LiteralPath $configurationPath -Raw | ConvertFrom-Json
$openLineOpsProperties = @($configuration.OpenLineOps.PSObject.Properties.Name)
if (-not ($openLineOpsProperties -ccontains "WindowsServiceName") `
    -or $configuration.OpenLineOps.WindowsServiceName -isnot [string] `
    -or $configuration.OpenLineOps.WindowsServiceName -cne "") {
    throw "Station Agent release configuration must expose an explicit empty WindowsServiceName."
}
$agentProperties = @($configuration.OpenLineOps.Agent.PSObject.Properties.Name)
if (-not ($agentProperties -ccontains "PackageCacheDirectory") `
    -or $configuration.OpenLineOps.Agent.PackageCacheDirectory -isnot [string] `
    -or $configuration.OpenLineOps.Agent.PackageCacheDirectory -cne "") {
    throw "Station Agent release configuration must expose an explicit empty PackageCacheDirectory."
}

foreach ($document in @($deployment, $security, $release)) {
    Assert-ContainsLiteral $document "--provision-content-cache" `
        "Station Agent deployment, security, and release docs must all name the provisioning command."
    Assert-ContainsLiteral $document "--remove-content-cache-package" `
        "Station Agent deployment, security, and release docs must all name protected-package removal."
}
foreach ($literal in @(
        "dedicated content-cache namespace",
        "OpenLineOps:WindowsServiceName",
        "OpenLineOps:Agent:PackageCacheDirectory",
        "fixed NTFS volume",
        "Normal startup only verifies")) {
    Assert-ContainsLiteral $deployment $literal `
        "Station Agent deployment documentation is missing '$literal'."
}
Assert-ContainsLiteral $security "immediate parent is a dedicated namespace anchor" `
    "Station Agent security documentation is missing the dedicated-anchor contract."
Assert-ContainsLiteral $security "commit marker records transaction state" `
    "Station Agent security documentation must state that commit markers are not authentication."

Assert-ContainsLiteral $staging "PackageCacheDirectory must be present and empty" `
    "Release staging does not enforce the empty PackageCacheDirectory template."
Assert-ContainsLiteral $staging "WindowsServiceName must be present and empty" `
    "Release staging does not enforce the empty WindowsServiceName template."
Assert-ContainsLiteral $staging "--provision-content-cache" `
    "Release staging does not enforce deployment documentation for the provisioning entry point."
Assert-ContainsLiteral $staging "--remove-content-cache-package" `
    "Release staging does not enforce deployment documentation for protected-package removal."
Assert-ContainsLiteral $inspection "PackageCacheDirectory release template must be present and empty" `
    "Release candidate inspection does not enforce the package-cache template contract."
Assert-ContainsLiteral $inspection "WindowsServiceName release template must be present and empty" `
    "Release candidate inspection does not enforce the deployment-time service-name template contract."
Assert-ContainsLiteral $inspection "DEPLOYMENT.md is missing content-cache provisioning contract" `
    "Release candidate inspection does not enforce the packaged provisioning instructions."
Assert-ContainsLiteral $inspection "--remove-content-cache-package" `
    "Release candidate inspection does not enforce the packaged protected-package removal command."
Assert-ContainsLiteral $staging "The `$ArtifactKind release payload contains the test-only Windows service-token helper" `
    "Release staging does not reject the test-only Windows service-token helper from deployable payloads."
Assert-ContainsLiteral $staging "Test-PortableExecutableContainsAsciiMarker" `
    "Release staging does not reject renamed portable executables carrying the test-only helper identity."
Assert-ContainsLiteral $inspection "contains the test-only Windows service-token helper in a deployable artifact" `
    "Release candidate inspection does not independently reject the test-only Windows service-token helper."
Assert-ContainsLiteral $inspection "Test-ZipEntryPortableExecutableContainsAsciiMarker" `
    "Release candidate inspection does not inspect renamed portable executables for the test-only helper identity."

Write-Host "Station Agent content-cache provisioning contract verification passed."
exit 0

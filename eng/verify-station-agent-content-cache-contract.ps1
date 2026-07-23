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

function Assert-ForbiddenPattern {
    param(
        [Parameter(Mandatory = $true)][string] $Text,
        [Parameter(Mandatory = $true)][string] $Pattern,
        [Parameter(Mandatory = $true)][string] $Failure
    )

    if ($Text -cmatch $Pattern) {
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
$relayBridge = Read-RequiredText "tests/OpenLineOps.Agent.Tests/WindowsServiceTokenTestBridge.cs"
$relayController = Read-RequiredText "tests/OpenLineOps.Agent.Tests/WindowsSourceTokenRelayProcess.cs"
$relayContractTests = Read-RequiredText "tests/OpenLineOps.Agent.Tests/WindowsServiceTokenTestRelayContractTests.cs"
$relayProject = Read-RequiredText "tests/OpenLineOps.WindowsServiceToken.TestRelay/OpenLineOps.WindowsServiceToken.TestRelay.csproj"
$relayProgram = Read-RequiredText "tests/OpenLineOps.WindowsServiceToken.TestRelay/Program.cs"
$relayProtocol = Read-RequiredText "tests/OpenLineOps.WindowsServiceToken.TestRelay/RelayProtocol.cs"
$relayNative = Read-RequiredText "tests/OpenLineOps.WindowsServiceToken.TestRelay/WindowsNative.cs"
$relayOperation = Read-RequiredText "tests/OpenLineOps.WindowsServiceToken.TestRelay/SourceTokenRelayOperation.cs"
$agentTestsProject = Read-RequiredText "tests/OpenLineOps.Agent.Tests/OpenLineOps.Agent.Tests.csproj"
$deployment = Read-RequiredText "docs/station-agent-deployment.md"
$security = Read-RequiredText "docs/station-agent-security.md"
$release = Read-RequiredText "docs/release-packaging.md"
$staging = Read-RequiredText "eng/stage-release-artifacts.ps1"
$inspection = Read-RequiredText "eng/inspect-release-candidate.ps1"

foreach ($retiredPath in @(
        "tests/OpenLineOps.Agent.Tests/WindowsKernelObjectAccessLease.cs",
        "tests/OpenLineOps.Agent.Tests/WindowsProcessAccessLease.cs",
        "tests/OpenLineOps.Agent.Tests/WindowsServiceTokenTestHelperContractTests.cs")) {
    if (Test-Path -LiteralPath (Join-Path $repoRoot $retiredPath)) {
        throw "Retired service-token helper or source-process DACL implementation still exists: $retiredPath"
    }
}

$retiredHelperRoot = Join-Path $repoRoot "tests/OpenLineOps.WindowsServiceToken.TestHelper"
if (Test-Path -LiteralPath $retiredHelperRoot -PathType Container) {
    $retiredSourceEntries = @(Get-ChildItem -LiteralPath $retiredHelperRoot -Recurse -File | Where-Object {
            $relative = $_.FullName.Substring($retiredHelperRoot.Length).TrimStart(
                [char[]]@(
                    [System.IO.Path]::DirectorySeparatorChar,
                    [System.IO.Path]::AltDirectorySeparatorChar))
            $segments = $relative -split '[\\/]'
            -not ($segments -contains "bin" -or $segments -contains "obj")
        })
    if ($retiredSourceEntries.Count -ne 0) {
        throw "Retired service-token TestHelper source still exists beneath tests/OpenLineOps.WindowsServiceToken.TestHelper."
    }
}

$relayRoot = Join-Path $repoRoot "tests/OpenLineOps.WindowsServiceToken.TestRelay"
$relaySourceFiles = @(Get-ChildItem -LiteralPath $relayRoot -Recurse -File -Filter "*.cs" | Where-Object {
        $relative = $_.FullName.Substring($relayRoot.Length).TrimStart(
            [char[]]@(
                [System.IO.Path]::DirectorySeparatorChar,
                [System.IO.Path]::AltDirectorySeparatorChar))
        $segments = $relative -split '[\\/]'
        -not ($segments -contains "bin" -or $segments -contains "obj")
    } | Sort-Object Name)
$relaySourceNames = [string]::Join("|", @($relaySourceFiles | ForEach-Object Name))
if ($relaySourceNames -cne "Program.cs|RelayProtocol.cs|SourceTokenRelayOperation.cs|WindowsNative.cs") {
    throw "The Test Relay source tree must contain exactly Program.cs, RelayProtocol.cs, SourceTokenRelayOperation.cs, and WindowsNative.cs."
}
if (@($relaySourceFiles | Where-Object {
            ($_.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0
        }).Count -ne 0) {
    throw "The Test Relay source tree contains a reparse-point C# file."
}
$relayAllSource = [string]::Join(
    [Environment]::NewLine,
    @($relaySourceFiles | ForEach-Object {
            Get-Content -LiteralPath $_.FullName -Raw
        }))

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
Assert-ContainsLiteral $agentStagedE2E "WindowsServiceTokenTestBridge.Run(" "Staged Agent exact-service-token checks do not use the direct source-token relay bridge."
Assert-ContainsLiteral $studioHarness ".RunAsService(" "The packaged two-Agent gate does not route exact Station identities through the shared direct relay bridge."
foreach ($directTokenConsumer in @($agentStagedE2E, $studioHarness)) {
    Assert-ForbiddenPattern $directTokenConsumer 'DuplicateTokenEx|TokenDuplicate|DuplicateHandle|SeDebugPrivilege|AdjustTokenPrivileges' "Staged Agent and Studio E2E harnesses must not duplicate service tokens or enable debug privilege directly."
}

$directRelayImplementation = [string]::Join([Environment]::NewLine, @($relayBridge, $relayController, $relayAllSource))
Assert-ForbiddenPattern $directRelayImplementation 'WindowsKernelObjectAccessLease|WindowsProcessAccessLease|WindowsServiceTokenTestHelper|OpenLineOps\.WindowsServiceToken\.TestHelper' "The direct Test Relay implementation still references a retired helper or source-process DACL lease."
Assert-ForbiddenPattern $directRelayImplementation 'DuplicateToken(?:Ex)?|TokenDuplicate|DuplicateHandle|SeDebugPrivilege|AdjustTokenPrivileges|SetKernelObjectSecurity|SetNamedSecurityInfo|WRITE_DAC|WriteDac' "The direct Test Relay must not copy tokens, enable debug privilege, or rewrite the Station process/token DACL."
Assert-ForbiddenPattern $directRelayImplementation '\b(?:V|v)[0-9]+\b' "The direct Test Relay contains a version-suffixed implementation name."

foreach ($bridgeLiteral in @(
        "ProcessCreateProcess = 0x00000080",
        "OpenExactSourceCreationProcess(",
        "CompareObjectHandles(retainedSourceProcess, process)",
        "using (var createOnlySource = OpenExactSourceCreationProcess(",
        "WindowsSourceTokenRelayProcess.CreateSuspended(",
        "relay.ValidateCreated(request);",
        "relay.Resume();",
        "ValidateSourceServiceAndProcessRunning(",
        "ValidateCapturedProcessImageAndHash(",
        "ValidateSourceProcessOutsideJob(",
        "PrepareRelayRoot(",
        "CreateProtectedDirectory(relayRoot, security)",
        "VerifyRelayBundle(",
        "AssertRelayTreeSecurity(",
        "SearchOption.TopDirectoryOnly",
        "NamedPipeServerStreamAcl.Create(",
        "PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance",
        "PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize",
        "GetNamedPipeClientProcessId(",
        "controlPipe.RunAsClient(",
        "ValidateImpersonatedSourceIdentity(",
        "CryptographicOperations.FixedTimeEquals(",
        "WriteReceipt(controlPipe)",
        "relay.WaitForSuccessfulExit(TransitionTimeout)",
        "relay.Dispose();",
        "controlPipe.Dispose();",
        "DeleteRelayRoot(bridgeRoot)",
        "case IDisposable disposable:",
        "case IAsyncDisposable asyncDisposable:",
        "cleanupFailures.Add(exception)")) {
    Assert-ContainsLiteral $relayBridge $bridgeLiteral "The direct Windows source-token bridge is missing strict boundary '$bridgeLiteral'."
}
Assert-ForbiddenPattern $relayBridge 'SearchOption\.AllDirectories|PipeAccessRights\.(?:ChangePermissions|TakeOwnership|CreateNewInstance)' "The direct bridge must not follow recursive reparse paths or grant administrative pipe rights."

$openExactStart = $relayBridge.IndexOf(
    "private static SafeProcessHandle OpenExactSourceCreationProcess(",
    [System.StringComparison]::Ordinal)
$openExactEnd = $relayBridge.IndexOf(
    "private static void PrepareRelayRoot(",
    $openExactStart,
    [System.StringComparison]::Ordinal)
if ($openExactStart -lt 0 -or $openExactEnd -le $openExactStart) {
    throw "The direct bridge is missing its bounded create-only source process opener."
}
$openExactSource = $relayBridge.Substring($openExactStart, $openExactEnd - $openExactStart)
Assert-ContainsLiteral $openExactSource "OpenProcess(" "The direct bridge does not open the exact source process."
Assert-ContainsLiteral $openExactSource "ProcessCreateProcess" "The direct bridge does not request only PROCESS_CREATE_PROCESS."
Assert-ContainsLiteral $openExactSource "inheritHandle: false" "The direct bridge source capability is inheritable."
Assert-ContainsLiteral $openExactSource "CompareObjectHandles(retainedSourceProcess, process)" "The direct bridge does not bind the create-only capability to the retained Station process object."
Assert-ContainsLiteral $openExactSource "throw new Win32Exception(" "The direct bridge does not fail closed when exact create-only access is denied."
Assert-ForbiddenPattern $openExactSource 'catch|ProcessCreateProcess\s*\|' "The exact source process opener contains a fallback or adds rights beyond PROCESS_CREATE_PROCESS."
if ([regex]::Matches($relayBridge, [regex]::Escape("OpenProcess(")).Count -ne 2) {
    throw "The direct bridge must contain exactly one OpenProcess call plus its P/Invoke declaration, with no fallback reopen."
}

foreach ($threeWayValidation in @(
        "ValidateSourceProcessOutsideJob(sourceProcessHandle, sourceProcessId);",
        "VerifyRelayBundle(relayBundleRoot, relayBundleInventory);",
        "AssertRelayTreeSecurity(bridgeRoot, canonicalSourceServiceSid);")) {
    if ([regex]::Matches($relayBridge, [regex]::Escape($threeWayValidation)).Count -ne 3) {
        throw "The direct bridge must enforce '$threeWayValidation' before creation, before resume, and after completion."
    }
}
if ([regex]::Matches(
        $relayBridge,
        [regex]::Escape("ValidateCapturedProcessImageAndHash(")).Count -lt 4) {
    throw "The direct bridge must repeatedly bind the retained Station/Relay process handles to canonical image hashes."
}

foreach ($controllerLiteral in @(
        "ProcThreadAttributeParentProcess = 0x00020000",
        "ProcThreadAttributeJobList = 0x0002000D",
        "JobObjectLimitActiveProcess | JobObjectLimitKillOnJobClose",
        "ActiveProcessLimit = 1",
        "CreateNoWindow",
        "CreateSuspendedFlag",
        "CreateUnicodeEnvironment",
        "ExtendedStartupInfoPresent",
        "inheritHandles: false",
        "ProcessSecurityDescriptor(runnerSid)",
        "ProcessTerminate | ProcessQueryLimitedInformation | Synchronize",
        "ResumeThread(_thread)",
        "previousSuspendCount != 1",
        "IsProcessInJob(_process, _job",
        "TerminateJobObject(job, 70)",
        "TerminateProcess(process, 70)",
        "ReadCreatedAtUtcTicks(_process)",
        "ValidateCreated(",
        "ValidateRunning(")) {
    Assert-ContainsLiteral $relayController $controllerLiteral "The direct Test Relay controller is missing containment boundary '$controllerLiteral'."
}
Assert-ForbiddenPattern $relayController 'CREATE_BREAKAWAY_FROM_JOB|CreateBreakawayFromJob|Process\.GetProcessById|OpenProcess\(|WellKnownSidType|BuiltinAdministrators|LocalSystemSid' "The direct Test Relay controller contains breakaway, PID reopen, fallback, or an over-broad process DACL."
if ([regex]::Matches($relayController, [regex]::Escape("CreateProcess(")).Count -ne 2) {
    throw "The direct Test Relay controller must contain one CreateProcess call and one P/Invoke declaration."
}

foreach ($programLiteral in @(
        "RelayProtocol.ParseInvocation(args)",
        "SourceTokenRelayOperation.ExecuteAsync(requestPath)",
        "InvalidInvocationExitCode",
        "OperationFailureExitCode")) {
    Assert-ContainsLiteral $relayProgram $programLiteral "The fixed Test Relay entry point is missing '$programLiteral'."
}
foreach ($protocolLiteral in @(
        "args.Count != 2",
        '"--request"',
        "RequestPropertyNames",
        "AllowTrailingCommas = false",
        "CommentHandling = JsonCommentHandling.Disallow",
        "MaxDepth = 4",
        "contains unknown property",
        "duplicates property",
        "is missing properties",
        "sourceProcessCreatedAtUtcTicks",
        "RequireCanonicalAbsoluteFile(",
        "RequireCanonicalAbsoluteDirectory(",
        "RequireLowerHex(",
        "RequireServiceSid(",
        "RequirePipeName(",
        "RejectReparsePoint(",
        "OpenLineOps.WindowsServiceToken.TestRelay.exe",
        "openlineops-source-token-relay-")) {
    Assert-ContainsLiteral $relayProtocol $protocolLiteral "The strict Test Relay request protocol is missing '$protocolLiteral'."
}

foreach ($nativeLiteral in @(
        "ValidateCurrentSourceToken(",
        "LocalServiceSid",
        "TokenPrimary",
        "TokenElevationTypeDefault",
        "AdministratorsSid",
        "ServiceLogonSid",
        "TokenRestrictedSids",
        "ValidateCanonicalExecutableHandle(",
        "FileAttributeReparsePoint",
        "GetFinalPathNameByHandle(",
        "BorrowedCurrentProcessTokenHandle")) {
    Assert-ContainsLiteral $relayNative $nativeLiteral "The Test Relay token/image self-attestation is missing '$nativeLiteral'."
}
Assert-ForbiddenPattern $relayAllSource 'OpenProcessToken|CreateRestrictedToken|CreateProcessAsUser|CreateProcessWithToken|OpenSCManager|OpenService|CreateService|UseWindowsService|BackgroundService' "The fixed Test Relay must not acquire/copy tokens or host an SCM service."

foreach ($operationLiteral in @(
        "PipeConnectionTimeout = TimeSpan.FromSeconds(15)",
        "ReceiptTimeout = TimeSpan.FromSeconds(60)",
        "TokenImpersonationLevel.Impersonation",
        "Convert.FromHexString(request.Nonce)",
        "pipe.WriteAsync(nonce",
        "AcceptedReceipt",
        "bytesRead != 1",
        "ValidateCanonicalExecutableHandle(",
        "SHA256.HashData(stream)")) {
    Assert-ContainsLiteral $relayOperation $operationLiteral "The Test Relay operation is missing '$operationLiteral'."
}
foreach ($twiceValidated in @(
        "WindowsNative.ValidateCurrentSourceToken(request.ExpectedSourceServiceSid);",
        "ValidateCurrentRelayExecutable(request);",
        "ValidateSourceExecutableFile(request);")) {
    if ([regex]::Matches($relayOperation, [regex]::Escape($twiceValidated)).Count -ne 2) {
        throw "The Test Relay must validate '$twiceValidated' both before pipe access and after the authenticated receipt."
    }
}

foreach ($testLiteral in @(
        "RelayBundleCopyIsFrozenAndRejectsChangedOrAddedFiles",
        "RelayBundleCopyRejectsAnEmptyBundle",
        "RelayTreeOwnerCanonicalizationRestoresEveryEntry",
        "ExactProcessHandleRejectsPidAndCreationTimeDrift",
        "PipeClientRightsContainOnlyProtocolAccess",
        "RelayExecutableRejectsEveryInvocationExceptOneCanonicalRequest",
        "RelayExecutableRejectsUnknownAndMissingRequestProperties",
        "CanonicalRelayRequestReachesTokenSelfAttestation",
        "SuspendedRelayIsBoundToItsImageAndKilledByJobDisposal",
        "ResumedRelayRejectsAnOrdinaryRunnerTokenBeforePipeAccess")) {
    Assert-ContainsLiteral $relayContractTests $testLiteral "The direct Test Relay contract suite is missing '$testLiteral'."
}

foreach ($projectLiteral in @(
        "<IsPackable>false</IsPackable>",
        "<IsPublishable>false</IsPublishable>",
        "<IsTestProject>false</IsTestProject>",
        '<RuntimeIdentifier Condition="$([MSBuild]::IsOSPlatform(''Windows''))">win-x64</RuntimeIdentifier>',
        '<SelfContained Condition="$([MSBuild]::IsOSPlatform(''Windows''))">true</SelfContained>')) {
    Assert-ContainsLiteral $relayProject $projectLiteral "The Test Relay project is missing release isolation '$projectLiteral'."
}
$agentTestsProjectXml = [xml]$agentTestsProject
$relayReferences = @(
    $agentTestsProjectXml.Project.ItemGroup.ProjectReference | Where-Object {
        $_.Include -ceq '..\OpenLineOps.WindowsServiceToken.TestRelay\OpenLineOps.WindowsServiceToken.TestRelay.csproj'
    })
if ($relayReferences.Count -ne 1 -or $relayReferences[0].ReferenceOutputAssembly -cne 'false') {
    throw "Agent tests must reference exactly one Test Relay project with ReferenceOutputAssembly=false."
}
foreach ($stagingLiteral in @(
        "StageWindowsServiceTokenTestRelayBundle",
        "windows-service-token-test-relay",
        "OpenLineOps.WindowsServiceToken.TestRelay.exe",
        '<FileWrites Include="$(_ServiceTokenTestRelayBundleDir)**\*" />')) {
    Assert-ContainsLiteral $agentTestsProject $stagingLiteral "Agent tests are missing Test Relay staging boundary '$stagingLiteral'."
}

foreach ($documentationLiteral in @(
        "PROCESS_CREATE_PROCESS",
        "CompareObjectHandles",
        "PROC_THREAD_ATTRIBUTE_PARENT_PROCESS",
        "PROC_THREAD_ATTRIBUTE_JOB_LIST")) {
    Assert-ContainsLiteral $security $documentationLiteral "Station security documentation is missing direct relay boundary '$documentationLiteral'."
    Assert-ContainsLiteral $release $documentationLiteral "Release documentation is missing direct relay boundary '$documentationLiteral'."
}
Assert-ForbiddenPattern $security 'one-shot virtual service|temporary process DACL|WindowsProcessAccessLease|WindowsKernelObjectAccessLease|OpenLineOps\.WindowsServiceToken\.TestHelper' "Station security documentation retains the retired helper-service/source-DACL design."
Assert-ForbiddenPattern $release 'one-shot virtual service|temporary process DACL|WindowsProcessAccessLease|WindowsKernelObjectAccessLease|OpenLineOps\.WindowsServiceToken\.TestHelper' "Release documentation retains the retired helper-service/source-DACL design."

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
Assert-ContainsLiteral $staging 'The $ArtifactKind release payload contains the test-only Windows service-token Test Relay' "Release staging does not reject the test-only Test Relay from deployable payloads."
Assert-ContainsLiteral $staging "Test-PortableExecutableContainsAsciiMarker" "Release staging does not reject renamed portable executables carrying the Test Relay identity."
Assert-ContainsLiteral $staging "Production project" "Release staging does not reject production references to the Test Relay."
Assert-ContainsLiteral $inspection "contains the test-only Windows service-token Test Relay in a deployable artifact" "Release candidate inspection does not independently reject the Test Relay."
Assert-ContainsLiteral $inspection "Test-ZipEntryPortableExecutableContainsAsciiMarker" "Release candidate inspection does not inspect renamed portable executables for the Test Relay identity."

Write-Host "Station Agent content-cache provisioning and direct source-token Relay contract verification passed."
exit 0

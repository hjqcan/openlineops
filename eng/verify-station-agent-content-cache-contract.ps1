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
$serviceTokenHelperProject = Read-RequiredText "tests/OpenLineOps.WindowsServiceToken.TestHelper/OpenLineOps.WindowsServiceToken.TestHelper.csproj"
$serviceTokenHelperProtocol = Read-RequiredText "tests/OpenLineOps.WindowsServiceToken.TestHelper/TokenTransferProtocol.cs"
$serviceTokenHelperNative = Read-RequiredText "tests/OpenLineOps.WindowsServiceToken.TestHelper/WindowsNative.cs"
$serviceTokenHelperOperation = Read-RequiredText "tests/OpenLineOps.WindowsServiceToken.TestHelper/WindowsServiceTokenTransferOperation.cs"
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
        "controlPipe.RunAsClient(",
        "ProtectOneShotServiceObject(",
        "SetServiceObjectSecurity(",
        "ServiceSidType = ServiceSidTypeUnrestricted",
        "CreateDirectory(bridgeRoot, ref attributes)",
        "AssertBridgeTreeSecurity(",
        "SearchOption.TopDirectoryOnly",
        "CaptureCleanupFailure(",
        "WaitForDeletion(manager, bridgeServiceName, TransitionTimeout)",
        "DeleteBridgeDirectoryWithoutFollowingReparsePoints(",
        '@"NT AUTHORITY\LocalService"',
        "DeleteServiceRequired(service, bridgeServiceName)")) {
    Assert-ContainsLiteral $serviceTokenBridge $bridgeLiteral `
        "The test-only Windows service-token bridge is missing strict boundary '$bridgeLiteral'."
}
if ($serviceTokenBridge -cmatch 'SearchOption\.AllDirectories') {
    throw "The service-token bridge must not recursively traverse helper or cleanup reparse points."
}
foreach ($helperLiteral in @(
        "TokenQuery | TokenDuplicate",
        "DuplicateTokenEx(",
        "ServiceSidTypeUnrestricted",
        "ServiceSidTypeRestricted",
        "TokenImpersonation",
        "SecurityImpersonation")) {
    Assert-ContainsLiteral $serviceTokenHelperNative $helperLiteral `
        "The one-shot helper is missing strict source-token boundary '$helperLiteral'."
}
if ($serviceTokenBridge -cmatch 'DuplicateTokenEx|DuplicateHandle|SeDebugPrivilege|AdjustTokenPrivileges' `
    -or $serviceTokenHelperNative -cmatch 'DuplicateHandle|SeDebugPrivilege|AdjustTokenPrivileges') {
    throw "The reverse-pipe bridge must not export token handles or depend on debug privilege."
}
Assert-ContainsLiteral $serviceTokenHelperProtocol "args.Count != 2" `
    "The one-shot helper does not reject any invocation other than its fixed request protocol."
Assert-ContainsLiteral $serviceTokenHelperProtocol "unknown property" `
    "The one-shot helper request protocol does not reject unknown fields."
Assert-ContainsLiteral $serviceTokenHelperOperation "WindowsIdentity.RunImpersonated(" `
    "The helper does not hash and connect within the exact source Agent identity."
Assert-ContainsLiteral $serviceTokenHelperOperation "ValidateSourceExecutableFile" `
    "The helper does not bind token transfer to the frozen Agent executable hash."
Assert-ContainsLiteral $serviceTokenHelperOperation "ValidateCanonicalSourceExecutableHandle" `
    "The helper does not bind executable hashing to a canonical non-reparse file handle."
Assert-ContainsLiteral $serviceTokenHelperOperation "ReceiptTimeout = TimeSpan.FromSeconds(60)" `
    "The helper does not leave a distinct receipt grace period beyond the bounded Agent actions."
Assert-ContainsLiteral $serviceTokenHelperOperation 'failurePhase = "helper-identity"' `
    "The helper does not publish bounded phase-only failure diagnostics."
Assert-ContainsLiteral $serviceTokenHelperProtocol "var sourceExecutablePath = RequireCanonicalAbsolutePath(" `
    "The helper protocol still touches the frozen Agent executable before source-token impersonation."
$sourceExecutablePathStart = $serviceTokenHelperProtocol.IndexOf(
    "var sourceExecutablePath =",
    [System.StringComparison]::Ordinal)
$controlPipePathStart = $serviceTokenHelperProtocol.IndexOf(
    "var controlPipeName =",
    [System.StringComparison]::Ordinal)
if ($sourceExecutablePathStart -lt 0 `
    -or $controlPipePathStart -le $sourceExecutablePathStart `
    -or $serviceTokenHelperProtocol.Substring(
        $sourceExecutablePathStart,
        $controlPipePathStart - $sourceExecutablePathStart) -cmatch `
        'File\.(Exists|GetAttributes)|Directory\.(Exists|GetAttributes)') {
    throw "The helper protocol accesses the frozen Agent executable before source-token impersonation."
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

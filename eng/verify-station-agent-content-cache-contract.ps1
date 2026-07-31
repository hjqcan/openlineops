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
$processLifecycle = Read-RequiredText "src/OpenLineOps.Agent/StationAgentProcess.cs"
$command = Read-RequiredText "src/OpenLineOps.Agent/StationAgentContentCacheProvisioningCommand.cs"
$commandLine = Read-RequiredText "src/OpenLineOps.Agent/StationAgentCommandLine.cs"
$hostOptions = Read-RequiredText "src/OpenLineOps.Agent/StationAgentHostOptions.cs"
$executableContract = Read-RequiredText "tests/OpenLineOps.Agent.Tests/StationAgentExecutableContractTests.cs"
$contentProtector = Read-RequiredText "shared/OpenLineOps.ContentProtection/ImmutableContentProtector.cs"
$contentProtectionProject = Read-RequiredText "shared/OpenLineOps.ContentProtection/OpenLineOps.ContentProtection.csproj"
$processIsolationProject = Read-RequiredText "shared/OpenLineOps.ProcessIsolation/OpenLineOps.ProcessIsolation.csproj"
$windowsSecurity = Read-RequiredText "shared/OpenLineOps.WindowsSecurity/WindowsStationServiceIdentityReader.cs"
$contentProtectorTests = Read-RequiredText "tests/OpenLineOps.ContentProtection.Tests/ImmutableContentProtectorTests.cs"
$agentStagedE2E = Read-RequiredText "tests/OpenLineOps.Agent.Tests/StagedAgentRabbitMqProcessE2ETests.cs"
$runnerStagedE2E = Read-RequiredText "tests/OpenLineOps.Runner.Tests/RunnerStagedAgentProcessE2ETests.cs"
$transactionLock = Read-RequiredText "shared/OpenLineOps.ContentProtection/ImmutableContentCacheTransactionLock.cs"
$studioHarness = Read-RequiredText "tests/OpenLineOps.Agent.Tests/StudioTwoAgentExternalProcessHarness.cs"
$studioE2E = Read-RequiredText "tests/OpenLineOps.Agent.Tests/StudioTwoAgentRealCoordinatorE2ETests.cs"
$stagedEvidenceValidator = Read-RequiredText "eng/verify-staged-agent-evidence.ps1"
$studioEvidenceValidator = Read-RequiredText "eng/verify-studio-two-agent-production-evidence.ps1"
$runnerEvidenceValidator = Read-RequiredText "eng/verify-runner-staged-agent-evidence.ps1"
$stagedEvidenceMutations = Read-RequiredText "eng/verify-evidence-validation.tests.ps1"
$studioEvidenceMutations = Read-RequiredText "eng/verify-studio-two-agent-production-evidence.tests.ps1"
$runnerEvidenceMutations = Read-RequiredText "eng/verify-runner-staged-agent-evidence.tests.ps1"
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
Assert-ContainsLiteral $processLifecycle "EventLog.WriteEntry(" `
    "Station Agent startup failures are not written to the registered Windows service EventLog source."
Assert-ContainsLiteral $processLifecycle "StationAgentDiagnostics.FormatFailureMessage(" `
    "Station Agent startup failures are written to EventLog without the bounded credential-redaction boundary."
Assert-ContainsLiteral $program "StationAgentDiagnostics.ProtectLoggingProviders(builder.Services);" `
    "Station Agent runtime logging providers are not protected by the unified credential-redaction boundary."
Assert-ContainsLiteral $executableContract "DiagnosticFormatterRedactsCredentialsAuthorizationAndBoundsEveryPayload" `
    "Station Agent startup diagnostic credential redaction lacks a regression test."
Assert-ContainsLiteral $executableContract "AgentLoggingBoundarySanitizesExceptionAndMessageCredentials" `
    "Station Agent runtime message, exception, and scope redaction lacks a regression test."
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
if ($contentProtector -cnotmatch 'internal static FileSystemRights CacheWriterPreSealRights =>\s*FileSystemRights\.Modify\s*\|\s*FileSystemRights\.TakeOwnership;') {
    throw "The pre-seal cache writer must use only Modify and TakeOwnership so it can canonicalize LocalService-owned content to the exact owner-eligible service SID."
}
$cacheWriterPreSealRightsReferences = [regex]::Matches(
    $contentProtector,
    "\bCacheWriterPreSealRights\b").Count
if ($cacheWriterPreSealRightsReferences -ne 5) {
    throw "Every pre-seal cache-writer ACL application and verifier must use the single canonical rights boundary."
}
Assert-ContainsLiteral $contentProtectorTests "CacheWriterPreSealRightsPermitOnlyContentMutationAndOwnerCanonicalization" `
    "Content protection tests do not lock the bounded service-SID ownership canonicalization right."
Assert-ContainsLiteral $transactionLock "TokenAccessLevels.Query | TokenAccessLevels.Duplicate" `
    "Immutable content transaction-lock owner classification does not request the token duplication right required by WindowsPrincipal role checks."
foreach ($directTokenConsumer in @($agentStagedE2E, $studioHarness)) {
    Assert-ForbiddenPattern $directTokenConsumer 'DuplicateTokenEx|TokenDuplicate|DuplicateHandle|SeDebugPrivilege|AdjustTokenPrivileges|CreateProcessAsUser|CreateProcessWithToken' "Staged Agent and Studio E2E harnesses must not duplicate service tokens, enable debug privilege, or launch a copied token."
}
Assert-ContainsLiteral $agentStagedE2E "productionRuntimeReadExecuteVerified" `
    "Staged Agent evidence does not bind immutable package access to a real production Runtime job."
Assert-ContainsLiteral $agentStagedE2E "serviceSidReadOnlyAclVerified" `
    "Staged Agent evidence does not verify the exact service-SID read-only cache ACL."
Assert-ContainsLiteral $agentStagedE2E "nestedServiceSidReadOnlyAclVerified" `
    "Staged Agent evidence does not verify nested service-SID read-only cache ACLs."
Assert-ContainsLiteral $agentStagedE2E "administratorPreSealRecoveryFixtureVerified" `
    "Staged Agent evidence does not bind pre-seal recovery to its administrator fixture."
Assert-ContainsLiteral $agentStagedE2E "WaitForRestrictedServiceAppContainerProfileLifecycleAsync" `
    "Staged Agent SCM E2E does not inspect the live restricted-service AppContainer profile."
Assert-ContainsLiteral $agentStagedE2E "VerifyAppContainerProfileDirectoryLifecycleAccess" `
    "Staged Agent SCM E2E does not verify the profile directory owner and exact FullControl rule."
Assert-ContainsLiteral $agentStagedE2E "VerifyAppContainerProfileRegistryLifecycleAccess" `
    "Staged Agent SCM E2E does not verify all AppContainer registry lifecycle leaves."
Assert-ContainsLiteral $agentStagedE2E 'mappingPath + "\\Children"' `
    "Staged Agent SCM E2E does not verify the AppContainer mapping Children leaf."
Assert-ContainsLiteral $agentStagedE2E 'storagePath + "\\Children"' `
    "Staged Agent SCM E2E does not verify the AppContainer storage Children leaf."
Assert-ContainsLiteral $agentStagedE2E "VerifyRestrictedServiceAppContainerProfileRemovedAsync" `
    "Staged Agent SCM E2E does not prove the AppContainer directory and registry leaves are deleted."

Assert-ContainsLiteral $windowsSecurity "ReadIsRestrictedToken(identity.AccessToken);" `
    "Station service identity validation does not use the native restricted-token predicate."
Assert-ContainsLiteral $windowsSecurity "IsTokenRestricted(token);" `
    "Station service identity validation does not call the Windows restricted-token predicate."
Assert-ContainsLiteral $contentProtectionProject "OpenLineOps.WindowsSecurity.csproj" `
    "Content protection does not depend on the neutral Windows security foundation."
Assert-ContainsLiteral $processIsolationProject "OpenLineOps.WindowsSecurity.csproj" `
    "Process isolation does not depend on the neutral Windows security foundation."
Assert-ForbiddenPattern $processIsolationProject "OpenLineOps.ContentProtection" `
    "Process isolation must not reverse-depend on the higher-level content-protection assembly."
Assert-ContainsLiteral $contentProtectorTests "WindowsRestrictedTokenPredicateUsesTheNativeSecurityBoundary" `
    "Content protection tests do not exercise the native restricted-token predicate on Windows."
Assert-ContainsLiteral $runnerStagedE2E "IsRestrictedToken: IsTokenRestricted(token)," `
    "Runner staged Agent evidence does not use the native restricted-token predicate."
Assert-ContainsLiteral $runnerStagedE2E "TokenInformationClass.TokenElevationType" `
    "Runner staged Agent evidence does not inspect the UAC linked-token boundary."
Assert-ContainsLiteral $runnerStagedE2E "HasLinkedToken" `
    "Runner staged Agent evidence does not expose the linked-token boundary."
Assert-ContainsLiteral $runnerStagedE2E "ProcessIdToSessionId(running.ProcessId, out var sessionId)" `
    "Runner staged Agent evidence does not bind the SCM PID to Windows Session 0."
Assert-ContainsLiteral $runnerStagedE2E "if (sessionId != 0)" `
    "Runner staged Agent evidence does not hard-reject an interactive SCM PID."
Assert-ContainsLiteral $runnerStagedE2E "session0Verified: true" `
    "Runner staged Agent evidence does not record the successful Session 0 assertion."
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
Assert-ContainsLiteral $agentStagedE2E "ProcessIdToSessionId(processId, out var sessionId)" `
    "Agent staged evidence does not bind each SCM PID to Windows Session 0."
Assert-ContainsLiteral $agentStagedE2E "if (sessionId != 0)" `
    "Agent staged evidence does not hard-reject an interactive SCM PID."
Assert-ContainsLiteral $agentStagedE2E "Session0Verified: true" `
    "Agent staged evidence does not retain the successful Session 0 assertion."
Assert-ContainsLiteral $studioHarness "EntryAgentSession0Verified" `
    "Studio two-Agent harness does not expose the entry Station Session 0 assertion."
Assert-ContainsLiteral $studioHarness "DownstreamAgentSession0Verified" `
    "Studio two-Agent harness does not expose the downstream Station Session 0 assertion."
Assert-ContainsLiteral $studioE2E "session0Verified = entryAgentSession0Verified" `
    "Studio two-Agent evidence does not record the entry Station Session 0 assertion."
Assert-ContainsLiteral $studioE2E "session0Verified = downstreamAgentSession0Verified" `
    "Studio two-Agent evidence does not record the downstream Station Session 0 assertion."
foreach ($validator in @(
        $stagedEvidenceValidator,
        $studioEvidenceValidator,
        $runnerEvidenceValidator)) {
    Assert-ContainsLiteral $validator "session0Verified" `
        "A public Agent evidence validator does not require Session 0 proof."
}
foreach ($mutations in @(
        $stagedEvidenceMutations,
        $studioEvidenceMutations,
        $runnerEvidenceMutations)) {
    Assert-ContainsLiteral $mutations "session0Verified" `
        "An Agent evidence mutation suite does not reject invalid Session 0 proof."
}
Assert-ContainsLiteral $scalarReader "const int bufferLength = sizeof(int);" `
    "Agent staged scalar token evidence does not use the exact native integer width."
Assert-ContainsLiteral $scalarReader "returnedLength != bufferLength" `
    "Agent staged scalar token evidence does not require the returned width to match its exact native integer buffer."
if ($scalarReader -cmatch 'IntPtr\.Zero|ErrorInsufficientBuffer|requiredLength') {
    throw "Agent staged scalar token evidence must call GetTokenInformation with the exact fixed buffer instead of a variable-length sizing probe."
}
if ($windowsSecurity -cmatch 'TokenHasRestrictions') {
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
Assert-ContainsLiteral $staging "Assert-NoDevelopmentOnlyPayload" "Release staging does not reject source, project, and test-only deployable payloads."
Assert-ContainsLiteral $staging "Test-PortableExecutableContainsAsciiMarker" "Release staging does not reject renamed portable executables carrying test-framework identity."
Assert-ContainsLiteral $inspection "Test-NoDevelopmentOnlyPayloadEntries" "Release candidate inspection does not independently reject source, project, and test-only deployable payloads."
Assert-ContainsLiteral $inspection "Test-ZipEntryPortableExecutableContainsAsciiMarker" "Release candidate inspection does not inspect renamed portable executables for test-framework identity."
foreach ($extension in @(".cs", ".fs", ".vb", ".csproj", ".fsproj", ".vbproj", ".sln", ".slnx")) {
    Assert-ContainsLiteral $staging ('"' + $extension + '"') `
        "Release staging does not reject '$extension' files from deployable payloads."
    Assert-ContainsLiteral $inspection ('"' + $extension + '"') `
        "Release candidate inspection does not reject '$extension' files from deployable payloads."
}

Write-Host "Station Agent content-cache provisioning and production evidence contract verification passed."
exit 0

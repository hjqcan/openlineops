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
$transactionLock = Read-RequiredText "shared/OpenLineOps.ContentProtection/ImmutableContentCacheTransactionLock.cs"
$studioHarness = Read-RequiredText "tests/OpenLineOps.Agent.Tests/StudioTwoAgentExternalProcessHarness.cs"
$deployment = Read-RequiredText "docs/station-agent-deployment.md"
$security = Read-RequiredText "docs/station-agent-security.md"
$release = Read-RequiredText "docs/release-packaging.md"
$staging = Read-RequiredText "eng/stage-release-artifacts.ps1"
$inspection = Read-RequiredText "eng/inspect-release-candidate.ps1"

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
Assert-ContainsLiteral $studioHarness "TokenAccessLevels.Query | TokenAccessLevels.Duplicate" `
    "The packaged two-Agent elevation gate does not request the token duplication right required by WindowsPrincipal role checks."
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

Write-Host "Station Agent content-cache provisioning contract verification passed."
exit 0

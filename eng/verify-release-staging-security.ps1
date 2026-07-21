param(
    [string] $WorkRoot = "output/release-staging-security"
)

$ErrorActionPreference = "Stop"
$RepoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$StageScript = Join-Path $PSScriptRoot "stage-release-artifacts.ps1"
$PrepareScript = Join-Path $PSScriptRoot "prepare-final-publication.ps1"
$StagedAgentBundleScript = Join-Path $PSScriptRoot "verify-staged-agent-bundle-e2e.ps1"
$RabbitMqScript = Join-Path $PSScriptRoot "verify-staged-agent-rabbitmq-e2e.ps1"
$StudioScript = Join-Path $PSScriptRoot "verify-studio-two-agent-production-closure.ps1"
$RunnerScript = Join-Path $PSScriptRoot "verify-runner-staged-agent-e2e.ps1"
$AgentServiceCleanupScript = Join-Path $PSScriptRoot "invoke-run-scoped-agent-service-cleanup.ps1"
$ExternalAbortScript = Join-Path $PSScriptRoot "verify-agent-service-external-abort-cleanup.ps1"
. (Join-Path $PSScriptRoot "github-fixture-process.ps1")

function Resolve-RepoPath {
    param([Parameter(Mandatory = $true)][string] $Path)

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }

    return [System.IO.Path]::GetFullPath((Join-Path $RepoRoot $Path))
}

function Get-ScriptAst {
    param([Parameter(Mandatory = $true)][string] $Path)

    $tokens = $null
    $parseErrors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile(
        $Path,
        [ref]$tokens,
        [ref]$parseErrors)
    if ($parseErrors.Count -ne 0) {
        throw "PowerShell parsing failed for $Path`: $($parseErrors[0].Message)"
    }

    return $ast
}

function Get-FunctionDefinition {
    param(
        [Parameter(Mandatory = $true)]$Ast,
        [Parameter(Mandatory = $true)][string] $Name
    )

    $definition = $Ast.Find({
            param($node)
            $node -is [System.Management.Automation.Language.FunctionDefinitionAst] `
                -and $node.Name -ceq $Name
        }, $true)
    if ($null -eq $definition) {
        throw "Required function '$Name' was not found."
    }

    return $definition
}

function Assert-ParameterAbsent {
    param(
        [Parameter(Mandatory = $true)]$Ast,
        [Parameter(Mandatory = $true)][string] $Name,
        [Parameter(Mandatory = $true)][string] $Description
    )

    $parameterNames = @($Ast.ParamBlock.Parameters | ForEach-Object {
            $_.Name.VariablePath.UserPath
        })
    if ($parameterNames -ccontains $Name) {
        throw "$Description still exposes removed parameter '$Name'."
    }
}

function Invoke-Git {
    param(
        [Parameter(Mandatory = $true)][string] $WorkingDirectory,
        [Parameter(Mandatory = $true)][string[]] $Arguments
    )

    $previousErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $output = @(& git -C $WorkingDirectory @Arguments 2>&1)
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }
    if ($exitCode -ne 0) {
        throw "Git command failed in release staging regression: $($output -join [Environment]::NewLine)"
    }
}

$stageAst = Get-ScriptAst $StageScript
$prepareAst = Get-ScriptAst $PrepareScript
$stagedAgentBundleAst = Get-ScriptAst $StagedAgentBundleScript
$rabbitMqAst = Get-ScriptAst $RabbitMqScript
$studioAst = Get-ScriptAst $StudioScript
$runnerAst = Get-ScriptAst $RunnerScript
$agentServiceCleanupAst = Get-ScriptAst $AgentServiceCleanupScript
$externalAbortAst = Get-ScriptAst $ExternalAbortScript

Assert-ParameterAbsent `
    -Ast $stageAst `
    -Name "CodeSigningCertificatePassword" `
    -Description "Release staging"
Assert-ParameterAbsent `
    -Ast $stageAst `
    -Name "CodeSigningCertificatePath" `
    -Description "Release staging"
Assert-ParameterAbsent `
    -Ast $prepareAst `
    -Name "CodeSigningCertificatePassword" `
    -Description "Final publication"
Assert-ParameterAbsent `
    -Ast $prepareAst `
    -Name "CodeSigningCertificatePath" `
    -Description "Final publication"

$stageText = Get-Content -LiteralPath $StageScript -Raw
$prepareText = Get-Content -LiteralPath $PrepareScript -Raw
$stagedAgentBundleText = Get-Content -LiteralPath $StagedAgentBundleScript -Raw
$rabbitMqText = Get-Content -LiteralPath $RabbitMqScript -Raw
$studioText = Get-Content -LiteralPath $StudioScript -Raw
$runnerText = Get-Content -LiteralPath $RunnerScript -Raw
$agentServiceCleanupText = Get-Content -LiteralPath $AgentServiceCleanupScript -Raw
$externalAbortText = Get-Content -LiteralPath $ExternalAbortScript -Raw
foreach ($expectedStageBoundary in @(
        "git -c core.quotePath=false ls-files --cached --full-name",
        "RequireCleanGitWorkTree",
        "ExpectedGitCommit",
        "CoordinatorBaseUri",
        "ArtifactUploadBearerToken",
        "WindowsServiceName must be present and empty",
        "PackageCacheDirectory must be present and empty",
        "--provision-content-cache",
        "Signed release staging requires exactly one certificate-store selector",
        "Sparse or incomplete checkouts cannot be published",
        "traverses a reparse point and cannot be archived",
        "-SourceProvenance `$sourceGitProvenance")) {
    if ($stageText -cnotmatch [regex]::Escape($expectedStageBoundary)) {
        throw "Release staging is missing required source boundary '$expectedStageBoundary'."
    }
}
foreach ($expectedPublicationBoundary in @(
        "Assert-CleanGitWorkTree",
        "-RequireCleanGitWorkTree",
        "-ExpectedGitCommit")) {
    if ($prepareText -cnotmatch [regex]::Escape($expectedPublicationBoundary)) {
        throw "Final publication is missing required source boundary '$expectedPublicationBoundary'."
    }
}
if ($stageText -cmatch 'CodeSigningCertificate(?:Path|Password)' `
    -or $prepareText -cmatch 'CodeSigningCertificate(?:Path|Password)') {
    throw "Release staging or final publication still contains a removed file/password signing chain."
}
$stationAgentUndeployedContract =
    '-ExpectedOutputPattern "^OpenLineOps Station Agent terminated: OpenLineOps:WindowsServiceName must contain 1-80 ASCII letters, digits, periods, underscores, or hyphens\.$"'
if ($stagedAgentBundleText -cnotmatch [regex]::Escape(
        'Agent release template must expose an explicit empty WindowsServiceName') `
    -or $stagedAgentBundleText -cnotmatch [regex]::Escape(
        'Agent release template must expose an explicit empty PackageCacheDirectory') `
    -or $stagedAgentBundleText -cnotmatch [regex]::Escape('OpenLineOps__*') `
    -or $stagedAgentBundleText -cnotmatch [regex]::Escape($stationAgentUndeployedContract) `
    -or $stagedAgentBundleText -cmatch [regex]::Escape(
        'BrokerUri must include a dedicated non-guest username')) {
    throw "Staged Agent bundle verification must scrub deployment overrides and bind normal startup to the empty WindowsServiceName fail-closed contract."
}
if ($rabbitMqText -match 'WaitForExit\s*\(\s*\)') {
    throw "Staged Agent RabbitMQ verification still contains an unbounded WaitForExit call."
}
foreach ($expectedTimeoutBoundary in @(
        "TestTimeoutSeconds",
        "taskkill.exe",
        "/T /F",
        "finally")) {
    if ($rabbitMqText -cnotmatch [regex]::Escape($expectedTimeoutBoundary)) {
        throw "Staged Agent RabbitMQ verification is missing timeout boundary '$expectedTimeoutBoundary'."
    }
}
foreach ($expectedCleanupBoundary in @(
        "CleanupRunScopedWindowsAgentServicesAndAccess",
        "openlineops-agent-service-cleanup",
        "Get-ManifestPathKind",
        "ordinary non-reparse file",
        "FileAttributes]::ReparsePoint",
        "FileAttributes]::Device",
        '-not $PrepareManifest -and $manifestPathKind -ceq "Absent"',
        "changed while proving its exact service scope absent",
        "Assert-RunScopeAbsent",
        "SourceExists",
        "Assert-NoReparseAncestors",
        "SetOwner",
        "AreAccessRulesProtected",
        "serviceSid",
        "serviceSidType",
        "olo-runner-staged-agent",
        "packageCacheRoot",
        "CommonApplicationData",
        "olo-staged-agent-rmq-content-`$serviceSuffix",
        "olo-runner-staged-agent-content-`$serviceSuffix",
        "olo-studio-two-agent-`$role-content-`$serviceSuffix",
        "NT AUTHORITY\LocalService",
        "PreserveManifest")) {
    if ($agentServiceCleanupText -cnotmatch [regex]::Escape($expectedCleanupBoundary)) {
        throw "Run-scoped Agent service cleanup is missing boundary '$expectedCleanupBoundary'."
    }
}
foreach ($expectedAbortBoundary in @(
        "OPENLINEOPS_AGENT_SERVICE_EXTERNAL_ABORT_GATE",
        "OPENLINEOPS_AGENT_SERVICE_EXTERNAL_ABORT_READY_PATH",
        "Get-DescendantProcessIds",
        "/T /F",
        "Assert-RunScopeGone",
        "-PreserveManifest")) {
    if ($externalAbortText -cnotmatch [regex]::Escape($expectedAbortBoundary)) {
        throw "External-abort Agent service proof is missing boundary '$expectedAbortBoundary'."
    }
}
if ($rabbitMqText -cnotmatch '(?s)finally\s*\{.*?&\s*\$cleanupScript' `
    -or $studioText -cnotmatch '(?s)finally\s*\{.*?serviceCleanupScript.*?Invoke-StudioCompensation' `
    -or $runnerText -cnotmatch '(?s)finally\s*\{.*?serviceCleanupScript.*?Invoke-RunnerAgentCompensation') {
    throw "RabbitMQ, Studio, and Runner wrappers must run the shared service scavenger from finally before compensation."
}
if ($studioText -cmatch 'Invoke-TemporaryAccountPreflight|New-LocalUser|Remove-LocalUser') {
    throw "Studio wrapper still contains destructive account preflight outside the strict scavenger contract."
}

$stageFormatter = Get-FunctionDefinition -Ast $stageAst -Name "Format-CommandArgumentsForLog"
$stageFormatterTest = [scriptblock]::Create(
    $stageFormatter.Extent.Text + [Environment]::NewLine +
    "(Format-CommandArgumentsForLog -Arguments @('--api-token','stage-secret','--password=inline-secret','https://uri-user:stage-uri-secret@example.invalid/path','https://example.invalid/path?token=stage-query-secret','safe')) -join ' '")
$stageFormatted = (& $stageFormatterTest).ToString()
if ($stageFormatted -match 'stage-secret|inline-secret|stage-uri-secret|stage-query-secret' `
    -or $stageFormatted -notmatch '<redacted>|<redacted-uri>') {
    throw "Release staging command formatter did not redact secret values."
}

$prepareFormatter = Get-FunctionDefinition -Ast $prepareAst -Name "Format-CommandLine"
$prepareFormatterTest = [scriptblock]::Create(
    $prepareFormatter.Extent.Text + [Environment]::NewLine +
    "Format-CommandLine @('tool','--access-token','publication-secret','--password=inline-publication-secret','https://uri-user:publication-uri-secret@example.invalid/path','https://example.invalid/path?token=publication-query-secret','safe')")
$prepareFormatted = (& $prepareFormatterTest).ToString()
if ($prepareFormatted -match 'publication-secret|inline-publication-secret|publication-uri-secret|publication-query-secret' `
    -or $prepareFormatted -notmatch '<redacted>|<redacted-uri>') {
    throw "Final publication command formatter did not redact secret values."
}

$resolvedWorkRoot = Resolve-RepoPath $WorkRoot
if (Test-Path -LiteralPath $resolvedWorkRoot) {
    Remove-Item -LiteralPath $resolvedWorkRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $resolvedWorkRoot -Force | Out-Null

$serviceTokenHelperGuardDefinitions = @(
    (Get-FunctionDefinition -Ast $stageAst -Name "Get-RelativePathUnderDirectory").Extent.Text,
    (Get-FunctionDefinition -Ast $stageAst -Name "Test-PortableExecutableContainsAsciiMarker").Extent.Text,
    (Get-FunctionDefinition -Ast $stageAst -Name "Assert-NoTestOnlyServiceTokenHelper").Extent.Text
) -join [Environment]::NewLine
$serviceTokenHelperGuard = [scriptblock]::Create(
    "param(`$Root, `$ArtifactKind)" + [Environment]::NewLine +
    $serviceTokenHelperGuardDefinitions + [Environment]::NewLine +
    "Assert-NoTestOnlyServiceTokenHelper -Root `$Root -ArtifactKind `$ArtifactKind")
$serviceTokenHelperPayloadFixtures = @(
    [ordered]@{
        Name = "case-varied-directory"
        RelativePath = "WINDOWS-SERVICE-TOKEN-TEST-HELPER/renamed-host.exe"
        IncludeBinaryIdentity = $false
    },
    [ordered]@{
        Name = "case-varied-file-prefix"
        RelativePath = "openlineops.windowsservicetoken.testhelper.renamed.bin"
        IncludeBinaryIdentity = $false
    },
    [ordered]@{
        Name = "renamed-binary-identity"
        RelativePath = "support/renamed-host.bin"
        IncludeBinaryIdentity = $true
    })
foreach ($fixture in $serviceTokenHelperPayloadFixtures) {
    $fixtureRoot = Join-Path $resolvedWorkRoot ("service-token-helper-" + $fixture.Name)
    $fixturePath = Join-Path $fixtureRoot $fixture.RelativePath
    New-Item -ItemType Directory -Path (Split-Path $fixturePath -Parent) -Force | Out-Null
    if ($fixture.IncludeBinaryIdentity) {
        $portableExecutable = [System.IO.File]::ReadAllBytes(
            (Join-Path $env:SystemRoot "System32/where.exe"))
        $identityMarker = [System.Text.Encoding]::ASCII.GetBytes(
            "OpenLineOps.WindowsServiceToken.TestHelper")
        $payload = [byte[]]::new($portableExecutable.Length + $identityMarker.Length)
        [System.Buffer]::BlockCopy(
            $portableExecutable,
            0,
            $payload,
            0,
            $portableExecutable.Length)
        [System.Buffer]::BlockCopy(
            $identityMarker,
            0,
            $payload,
            $portableExecutable.Length,
            $identityMarker.Length)
        [System.IO.File]::WriteAllBytes($fixturePath, $payload)
    }
    else {
        [System.IO.File]::WriteAllText(
            $fixturePath,
            "test-only-helper-sentinel",
            [System.Text.UTF8Encoding]::new($false))
    }
    $failure = $null
    try {
        & $serviceTokenHelperGuard $fixtureRoot "agent"
    }
    catch {
        $failure = $_.Exception.Message
    }
    if ($failure -notmatch "contains the test-only Windows service-token helper") {
        throw "Release staging accepted the $($fixture.Name) test-only service-token helper fixture."
    }
}

$cleanupFixtureBundleRoot = [System.IO.Path]::GetFullPath(
    (Join-Path $resolvedWorkRoot "cleanup-manifest-agent-bundle"))
New-Item -ItemType Directory -Path $cleanupFixtureBundleRoot -Force | Out-Null
Copy-Item `
    -LiteralPath (Join-Path $env:SystemRoot "System32/where.exe") `
    -Destination (Join-Path $cleanupFixtureBundleRoot "OpenLineOps.Agent.exe")
$cleanupManifestBase = [System.IO.Path]::GetFullPath(
    (Join-Path ([System.IO.Path]::GetTempPath()) "openlineops-agent-service-cleanup"))
New-Item -ItemType Directory -Path $cleanupManifestBase -Force | Out-Null
$cleanupFixtureGitHubEnvironment = @{
    GITHUB_REPOSITORY = "openlineops/openlineops"
    GITHUB_SHA = "0000000000000000000000000000000000000000"
    GITHUB_RUN_ID = "1"
    GITHUB_SERVER_URL = "https://github.com"
}

function Invoke-CleanupManifestFixture {
    param(
        [Parameter(Mandatory = $true)][string] $Scope,
        [Parameter(Mandatory = $true)][string] $ManifestPath
    )

    return Invoke-GitHubFixturePowerShellProcess `
        -ScriptPath $AgentServiceCleanupScript `
        -Arguments @(
            "-Kind", "rabbitmq",
            "-Scope", $Scope,
            "-AgentBundleRoot", $cleanupFixtureBundleRoot,
            "-ManifestPath", $ManifestPath,
            "-Configuration", "Release",
            "-NoBuild",
            "-NoRestore") `
        -GitHubEnvironment $cleanupFixtureGitHubEnvironment
}

$cleanupManifestFixturePaths = [System.Collections.Generic.List[string]]::new()
$cleanupManifestReparseTarget = [System.IO.Path]::GetFullPath(
    (Join-Path $resolvedWorkRoot "cleanup-manifest-reparse-target"))
try {
    $absentScope = [System.Guid]::NewGuid().ToString("N")
    $absentManifestPath = [System.IO.Path]::GetFullPath(
        (Join-Path $cleanupManifestBase "rabbitmq-$absentScope.json"))
    $manifestPathKindFunction = Get-FunctionDefinition `
        -Ast $agentServiceCleanupAst `
        -Name "Get-ManifestPathKind"
    $manifestPathKindTest = [scriptblock]::Create(
        "param(`$ManifestPath)" + [Environment]::NewLine +
        $manifestPathKindFunction.Extent.Text + [Environment]::NewLine +
        "Get-ManifestPathKind -Path `$ManifestPath")
    $absentPathKind = & $manifestPathKindTest $absentManifestPath
    if ($absentPathKind -cne "Absent") {
        throw "Run-scoped Agent cleanup did not classify a truly absent manifest path as absent."
    }

    $contractScope = [System.Guid]::NewGuid().ToString("N")
    $contractManifestPath = [System.IO.Path]::GetFullPath(
        (Join-Path $cleanupManifestBase "rabbitmq-$contractScope.json"))
    $cleanupManifestFixturePaths.Add($contractManifestPath) | Out-Null
    $manifestBuilderDefinitions = @(
        "Assert-LowerHex",
        "Get-TextSha256",
        "Get-ServiceSidFromName",
        "Get-ExpectedManifest") | ForEach-Object {
        (Get-FunctionDefinition -Ast $agentServiceCleanupAst -Name $_).Extent.Text
    }
    $manifestBuilder = [scriptblock]::Create(
        "param(`$Kind, `$Scope, `$ResolvedAgentBundleRoot)" + [Environment]::NewLine +
        '$LocalServiceAccountName = "NT AUTHORITY\LocalService"' + [Environment]::NewLine +
        '$LocalServiceAccountSid = "S-1-5-19"' + [Environment]::NewLine +
        '$RestrictedServiceSidType = "Restricted"' + [Environment]::NewLine +
        ($manifestBuilderDefinitions -join [Environment]::NewLine) + [Environment]::NewLine +
        "Get-ExpectedManifest -ResolvedAgentBundleRoot `$ResolvedAgentBundleRoot")
    $expectedContract = & $manifestBuilder "rabbitmq" $contractScope $cleanupFixtureBundleRoot
    [System.IO.File]::WriteAllText(
        $contractManifestPath,
        ((ConvertTo-Json $expectedContract -Depth 8) + "`r`n"),
        [System.Text.UTF8Encoding]::new($false))
    $contract = Get-Content -LiteralPath $contractManifestPath -Raw | ConvertFrom-Json
    $contractProperties = @($contract.PSObject.Properties.Name)
    $expectedContractProperties = @("schema", "schemaVersion", "kind", "scope", "entries")
    $entry = $contract.entries[0]
    $entryProperties = @($entry.PSObject.Properties.Name)
    $expectedEntryProperties = @(
        "role",
        "serviceSuffix",
        "serviceName",
        "serviceAccountName",
        "serviceAccountSid",
        "serviceSid",
        "serviceSidType",
        "executablePath",
        "executableSha256",
        "ownedRoot",
        "packageCacheRoot")
    $commonApplicationData = [System.IO.Path]::GetFullPath(
        [System.Environment]::GetFolderPath(
            [System.Environment+SpecialFolder]::CommonApplicationData))
    $runnerScope = [System.Guid]::NewGuid().ToString("N")
    $runnerContract = (& $manifestBuilder "runner" $runnerScope $cleanupFixtureBundleRoot |
            ConvertTo-Json -Depth 8 | ConvertFrom-Json)
    $expectedRunnerPackageCacheRoot = [System.IO.Path]::GetFullPath(
        (Join-Path $commonApplicationData "olo-runner-staged-agent-content-$runnerScope/content"))
    if (@($runnerContract.entries).Count -ne 1 `
        -or $runnerContract.entries[0].role -cne "runner" `
        -or $runnerContract.entries[0].serviceSuffix -cne $runnerScope `
        -or $runnerContract.entries[0].packageCacheRoot -cne $expectedRunnerPackageCacheRoot `
        -or [System.IO.Path]::GetDirectoryName(
            [System.IO.Path]::GetDirectoryName([string]$runnerContract.entries[0].packageCacheRoot)) -cne `
            $commonApplicationData) {
        throw "Runner cleanup manifest does not bind its direct CommonApplicationData package-cache anchor."
    }
    $studioScope = [System.Guid]::NewGuid().ToString("N")
    $studioContract = (& $manifestBuilder "studio-two-agent" $studioScope $cleanupFixtureBundleRoot |
            ConvertTo-Json -Depth 8 | ConvertFrom-Json)
    $studioEntries = @($studioContract.entries)
    if ($studioEntries.Count -ne 2 `
        -or (($studioEntries.role | Sort-Object) -join '|') -cne "downstream|entry" `
        -or @($studioEntries.serviceSuffix | Sort-Object -Unique).Count -ne 2) {
        throw "Studio cleanup manifest must bind exactly one distinct entry and downstream service."
    }
    foreach ($studioEntry in $studioEntries) {
        $expectedStudioPackageCacheRoot = [System.IO.Path]::GetFullPath(
            (Join-Path $commonApplicationData (
                    "olo-studio-two-agent-$($studioEntry.role)-content-$($studioEntry.serviceSuffix)/content")))
        if ([string]$studioEntry.serviceSuffix -cnotmatch '^[0-9a-f]{32}$' `
            -or $studioEntry.packageCacheRoot -cne $expectedStudioPackageCacheRoot `
            -or [System.IO.Path]::GetDirectoryName(
                [System.IO.Path]::GetDirectoryName([string]$studioEntry.packageCacheRoot)) -cne `
                $commonApplicationData) {
            throw "Studio cleanup manifest does not bind a direct role-specific CommonApplicationData package-cache anchor."
        }
    }
    $expectedOwnedRoot = [System.IO.Path]::GetFullPath(
        (Join-Path (Join-Path $env:SystemRoot "Temp") "olo-staged-agent-rmq-$contractScope"))
    $expectedPackageCacheRoot = [System.IO.Path]::GetFullPath(
        (Join-Path $commonApplicationData "olo-staged-agent-rmq-content-$contractScope/content"))
    if (($contractProperties -join '|') -cne ($expectedContractProperties -join '|') `
        -or $contract.schema -cne "openlineops-agent-service-cleanup" `
        -or $contract.schemaVersion -ne 1 `
        -or $contract.kind -cne "rabbitmq" `
        -or $contract.scope -cne $contractScope `
        -or @($contract.entries).Count -ne 1 `
        -or ($entryProperties -join '|') -cne ($expectedEntryProperties -join '|') `
        -or $entry.role -cne "rabbitmq" `
        -or $entry.serviceSuffix -cne $contractScope `
        -or $entry.ownedRoot -cne $expectedOwnedRoot `
        -or $entry.packageCacheRoot -cne $expectedPackageCacheRoot `
        -or [System.IO.Path]::GetDirectoryName([string]$entry.packageCacheRoot) -cne `
            (Join-Path $commonApplicationData "olo-staged-agent-rmq-content-$contractScope") `
        -or [System.IO.Path]::GetDirectoryName(
            [System.IO.Path]::GetDirectoryName([string]$entry.packageCacheRoot)) -cne `
            $commonApplicationData) {
        throw "Run-scoped Agent cleanup manifest does not expose its exact Temp-owned and ProgramData package-cache contract."
    }

    $entry.packageCacheRoot = [System.IO.Path]::GetFullPath(
        (Join-Path $commonApplicationData "noncanonical-$contractScope/content"))
    [System.IO.File]::WriteAllText(
        $contractManifestPath,
        ((ConvertTo-Json $contract -Depth 8) + "`r`n"),
        [System.Text.UTF8Encoding]::new($false))
    $manifestMatcherDefinition = Get-FunctionDefinition `
        -Ast $agentServiceCleanupAst `
        -Name "Assert-ManifestMatches"
    $manifestMatcher = [scriptblock]::Create(
        "param(`$Path, `$Expected)" + [Environment]::NewLine +
        $manifestMatcherDefinition.Extent.Text + [Environment]::NewLine +
        "Assert-ManifestMatches -Path `$Path -Expected `$Expected")
    $mutatedCacheFailure = $null
    try {
        & $manifestMatcher $contractManifestPath $expectedContract
    }
    catch {
        $mutatedCacheFailure = $_.Exception.Message
    }
    if ($mutatedCacheFailure -notmatch "differs from its deterministic strict contract") {
        throw "Run-scoped Agent cleanup accepted a non-canonical packageCacheRoot mutation."
    }

    $entry.packageCacheRoot = $expectedPackageCacheRoot
    $entry | Add-Member -NotePropertyName legacyPackageCacheRoot -NotePropertyValue $expectedPackageCacheRoot
    [System.IO.File]::WriteAllText(
        $contractManifestPath,
        ((ConvertTo-Json $contract -Depth 8) + "`r`n"),
        [System.Text.UTF8Encoding]::new($false))
    $legacyCacheFailure = $null
    try {
        & $manifestMatcher $contractManifestPath $expectedContract
    }
    catch {
        $legacyCacheFailure = $_.Exception.Message
    }
    if ($legacyCacheFailure -notmatch "differs from its deterministic strict contract") {
        throw "Run-scoped Agent cleanup accepted a legacy package-cache compatibility field."
    }

    $directoryScope = [System.Guid]::NewGuid().ToString("N")
    $directoryManifestPath = [System.IO.Path]::GetFullPath(
        (Join-Path $cleanupManifestBase "rabbitmq-$directoryScope.json"))
    New-Item -ItemType Directory -Path $directoryManifestPath | Out-Null
    $cleanupManifestFixturePaths.Add($directoryManifestPath) | Out-Null
    $directorySentinelPath = Join-Path $directoryManifestPath "sentinel.txt"
    [System.IO.File]::WriteAllText(
        $directorySentinelPath,
        "directory-must-remain",
        [System.Text.UTF8Encoding]::new($false))
    $directoryResult = Invoke-CleanupManifestFixture `
        -Scope $directoryScope `
        -ManifestPath $directoryManifestPath
    if ($directoryResult.ExitCode -eq 0 `
        -or $directoryResult.Text -notmatch "ordinary non-reparse file" `
        -or -not (Test-Path -LiteralPath $directoryManifestPath -PathType Container) `
        -or (Get-Content -LiteralPath $directorySentinelPath -Raw) -cne "directory-must-remain") {
        Write-Host $directoryResult.Text
        throw "Run-scoped Agent cleanup did not fail closed without mutating a directory at the manifest path."
    }

    New-Item -ItemType Directory -Path $cleanupManifestReparseTarget | Out-Null
    $reparseSentinelPath = Join-Path $cleanupManifestReparseTarget "sentinel.txt"
    [System.IO.File]::WriteAllText(
        $reparseSentinelPath,
        "reparse-target-must-remain",
        [System.Text.UTF8Encoding]::new($false))
    $reparseScope = [System.Guid]::NewGuid().ToString("N")
    $reparseManifestPath = [System.IO.Path]::GetFullPath(
        (Join-Path $cleanupManifestBase "rabbitmq-$reparseScope.json"))
    New-Item `
        -ItemType Junction `
        -Path $reparseManifestPath `
        -Target $cleanupManifestReparseTarget | Out-Null
    $cleanupManifestFixturePaths.Add($reparseManifestPath) | Out-Null
    $reparseResult = Invoke-CleanupManifestFixture `
        -Scope $reparseScope `
        -ManifestPath $reparseManifestPath
    $reparseItem = Get-Item -LiteralPath $reparseManifestPath -Force
    if ($reparseResult.ExitCode -eq 0 `
        -or $reparseResult.Text -notmatch "ordinary non-reparse file" `
        -or (($reparseItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -eq 0) `
        -or (Get-Content -LiteralPath $reparseSentinelPath -Raw) -cne "reparse-target-must-remain") {
        Write-Host $reparseResult.Text
        throw "Run-scoped Agent cleanup did not fail closed without traversing or mutating a reparse-point manifest path."
    }
}
finally {
    foreach ($fixturePath in $cleanupManifestFixturePaths) {
        if ([System.IO.Path]::GetDirectoryName($fixturePath) -cne $cleanupManifestBase) {
            throw "Cleanup manifest mutation fixture escaped its deterministic private base."
        }
        if (Test-Path -LiteralPath $fixturePath) {
            $fixtureItem = Get-Item -LiteralPath $fixturePath -Force
            if (($fixtureItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                [System.IO.Directory]::Delete($fixturePath, $false)
            }
            elseif ($fixtureItem -is [System.IO.DirectoryInfo]) {
                Remove-Item -LiteralPath $fixturePath -Recurse -Force
            }
            else {
                Remove-Item -LiteralPath $fixturePath -Force
            }
        }
    }
    if (Test-Path -LiteralPath $cleanupManifestReparseTarget) {
        $expectedReparseParent = [System.IO.Path]::GetFullPath($resolvedWorkRoot).TrimEnd('\', '/')
        if ([System.IO.Path]::GetDirectoryName($cleanupManifestReparseTarget) -cne $expectedReparseParent) {
            throw "Cleanup manifest reparse target escaped the verification work root."
        }
        Remove-Item -LiteralPath $cleanupManifestReparseTarget -Recurse -Force
    }
}

$trackedCopyRepo = Join-Path $resolvedWorkRoot "tracked-copy-repo"
$trackedCopyDestination = Join-Path $resolvedWorkRoot "tracked-copy-output"
New-Item -ItemType Directory -Path $trackedCopyRepo -Force | Out-Null
Invoke-Git -WorkingDirectory $trackedCopyRepo -Arguments @("init", "--quiet")
$trackedPath = Join-Path $trackedCopyRepo "src/tracked.txt"
$untrackedPath = Join-Path $trackedCopyRepo "src/untracked-secret.txt"
$sensitiveTrackedPath = Join-Path $trackedCopyRepo "certs/release-signing.pfx"
New-Item -ItemType Directory -Path (Split-Path $trackedPath -Parent) -Force | Out-Null
New-Item -ItemType Directory -Path (Split-Path $sensitiveTrackedPath -Parent) -Force | Out-Null
[System.IO.File]::WriteAllText($trackedPath, "indexed-content", [System.Text.UTF8Encoding]::new($false))
[System.IO.File]::WriteAllText($sensitiveTrackedPath, "not-a-real-certificate", [System.Text.UTF8Encoding]::new($false))
Invoke-Git -WorkingDirectory $trackedCopyRepo -Arguments @("add", "src/tracked.txt", "certs/release-signing.pfx")
[System.IO.File]::WriteAllText($trackedPath, "tracked-working-tree-content", [System.Text.UTF8Encoding]::new($false))
[System.IO.File]::WriteAllText($untrackedPath, "must-never-ship", [System.Text.UTF8Encoding]::new($false))

$copyFunctions = @(
    (Get-FunctionDefinition -Ast $stageAst -Name "Test-IsSensitiveSourcePath").Extent.Text,
    (Get-FunctionDefinition -Ast $stageAst -Name "Test-IsExcludedSourcePath").Extent.Text,
    (Get-FunctionDefinition -Ast $stageAst -Name "Copy-SourceArchiveContent").Extent.Text
) -join ([Environment]::NewLine + [Environment]::NewLine)
$copyTest = [scriptblock]::Create(
    "param(`$RepoRoot, `$DestinationDirectory)" + [Environment]::NewLine +
    $copyFunctions + [Environment]::NewLine +
    "Copy-SourceArchiveContent -DestinationDirectory `$DestinationDirectory")
& $copyTest $trackedCopyRepo $trackedCopyDestination

$copiedTrackedPath = Join-Path $trackedCopyDestination "src/tracked.txt"
if (-not (Test-Path -LiteralPath $copiedTrackedPath -PathType Leaf) `
    -or (Get-Content -LiteralPath $copiedTrackedPath -Raw) -cne "tracked-working-tree-content") {
    throw "Tracked source copy did not preserve the current tracked working-tree file."
}
if (Test-Path -LiteralPath (Join-Path $trackedCopyDestination "src/untracked-secret.txt")) {
    throw "Untracked content entered the source staging tree."
}
if (Test-Path -LiteralPath (Join-Path $trackedCopyDestination "certs/release-signing.pfx")) {
    throw "Sensitive tracked content entered the source staging tree."
}

$publicationRepo = Join-Path $resolvedWorkRoot "formal-publication-repo"
$publicationEng = Join-Path $publicationRepo "eng"
New-Item -ItemType Directory -Path $publicationEng -Force | Out-Null
Copy-Item -LiteralPath $PrepareScript -Destination (Join-Path $publicationEng "prepare-final-publication.ps1")
Invoke-Git -WorkingDirectory $publicationRepo -Arguments @("init", "--quiet")
Invoke-Git -WorkingDirectory $publicationRepo -Arguments @("config", "user.name", "OpenLineOps Regression")
Invoke-Git -WorkingDirectory $publicationRepo -Arguments @("config", "user.email", "regression@openlineops.invalid")
Invoke-Git -WorkingDirectory $publicationRepo -Arguments @("add", "eng/prepare-final-publication.ps1")
Invoke-Git -WorkingDirectory $publicationRepo -Arguments @(
    "-c",
    "commit.gpgsign=false",
    "commit",
    "--quiet",
    "-m",
    "fixture")
$publicationCommit = (& git -C $publicationRepo rev-parse HEAD).Trim().ToLowerInvariant()
if ($LASTEXITCODE -ne 0 -or $publicationCommit -cnotmatch '^[0-9a-f]{40,64}$') {
    throw "Could not resolve the formal publication fixture commit."
}
$publicationIntegrationRoot = Join-Path $publicationRepo "output/production-integration-evidence"
New-Item -ItemType Directory -Path $publicationIntegrationRoot -Force | Out-Null
$publicationTrxPath = Join-Path $publicationIntegrationRoot "production-integration.trx"
[System.IO.File]::WriteAllText(
    $publicationTrxPath,
    "<TestRun><ResultSummary outcome=`"Completed`"><Counters total=`"1`" executed=`"1`" passed=`"1`" failed=`"0`" notExecuted=`"0`" /></ResultSummary></TestRun>",
    [System.Text.UTF8Encoding]::new($false))
$publicationTrxFile = Get-Item -LiteralPath $publicationTrxPath
$publicationEvidencePath = Join-Path $publicationIntegrationRoot "integration-evidence.json"
$publicationEvidence = [ordered]@{
    schemaVersion = 1
    generatedAtUtc = [System.DateTimeOffset]::UtcNow.ToString("O")
    product = "OpenLineOps"
    repository = "openlineops/openlineops"
    commitSha = $publicationCommit
    runId = "123456"
    runUrl = "https://github.com/openlineops/openlineops/actions/runs/123456"
    jobName = "production-integration"
    testName = "OpenLineOps.PostgresIntegration.Tests.PostgresRabbitMqProductionCoordinationIntegrationTests.DurableOutboxAndResultInboxSurviveCoordinatorRestartAcrossRealBroker"
    conclusion = "success"
    counters = [ordered]@{
        total = 1
        executed = 1
        passed = 1
        failed = 0
        skipped = 0
    }
    trx = [ordered]@{
        relativePath = "output/production-integration-evidence/production-integration.trx"
        sizeBytes = $publicationTrxFile.Length
        sha256 = (Get-FileHash -LiteralPath $publicationTrxPath -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}
[System.IO.File]::WriteAllText(
    $publicationEvidencePath,
    (($publicationEvidence | ConvertTo-Json -Depth 8) + [Environment]::NewLine),
    [System.Text.UTF8Encoding]::new($false))
[System.IO.File]::WriteAllText(
    (Join-Path $publicationRepo "untracked-publication-input.txt"),
    "dirty-worktree-sentinel",
    [System.Text.UTF8Encoding]::new($false))

$publicationArguments = @(
    "-Version", "0.0.0-security-regression",
    "-RepositoryUrl", "https://github.com/openlineops/openlineops",
    "-SecurityContact", "security@openlineops.invalid",
    "-ConductContact", "conduct@openlineops.invalid",
    "-ProductionIntegrationEvidencePath", $publicationEvidencePath,
    "-ConfirmMitLicense",
    "-CodeSigningCertificateThumbprint", "00112233445566778899AABBCCDDEEFF00112233")
$publicationGitHubEnvironment = @{
    GITHUB_REPOSITORY = $publicationEvidence.repository
    GITHUB_SHA = $publicationEvidence.commitSha
    GITHUB_RUN_ID = $publicationEvidence.runId
    GITHUB_SERVER_URL = "https://github.com"
}
$dirtyPublication = Invoke-GitHubFixturePowerShellProcess `
    -ScriptPath (Join-Path $publicationEng "prepare-final-publication.ps1") `
    -Arguments $publicationArguments `
    -GitHubEnvironment $publicationGitHubEnvironment
if ($dirtyPublication.ExitCode -eq 0 `
    -or $dirtyPublication.Text -notmatch "requires a clean Git worktree" `
    -or $dirtyPublication.Text -match "does not match the current GitHub Actions") {
    Write-Host $dirtyPublication.Text
    throw "Formal publication did not fail closed on an untracked worktree change."
}

$removedPasswordInvocation = Invoke-GitHubFixturePowerShellProcess `
    -ScriptPath (Join-Path $publicationEng "prepare-final-publication.ps1") `
    -Arguments ($publicationArguments + @(
        "-CodeSigningCertificatePassword",
        "publication-password-sentinel")) `
    -GitHubEnvironment $publicationGitHubEnvironment
if ($removedPasswordInvocation.ExitCode -eq 0 `
    -or $removedPasswordInvocation.Text -notmatch "CodeSigningCertificatePassword" `
    -or $removedPasswordInvocation.Text -match "publication-password-sentinel") {
    Write-Host $removedPasswordInvocation.Text
    throw "Removed signing password CLI behavior was not rejected without disclosing its value."
}

$timeoutFixturePath = Join-Path $resolvedWorkRoot "timeout-process-fixture.ps1"
$timeoutChildPidPath = Join-Path $resolvedWorkRoot "timeout-child.pid"
[System.IO.File]::WriteAllText(
    $timeoutFixturePath,
    @'
param([Parameter(Mandatory = $true)][string] $ChildPidPath)
$child = Start-Process `
    -FilePath "powershell" `
    -ArgumentList @("-NoProfile", "-Command", "Start-Sleep -Seconds 120") `
    -WindowStyle Hidden `
    -PassThru
[System.IO.File]::WriteAllText($ChildPidPath, $child.Id.ToString())
Wait-Process -Id $child.Id
'@,
    [System.Text.UTF8Encoding]::new($false))
$timeoutParent = $null
$timeoutChildId = $null
try {
    $timeoutParent = Start-Process `
        -FilePath "powershell" `
        -ArgumentList @(
            "-NoProfile",
            "-ExecutionPolicy",
            "Bypass",
            "-File",
            $timeoutFixturePath,
            "-ChildPidPath",
            $timeoutChildPidPath) `
        -WindowStyle Hidden `
        -PassThru
    $deadline = [System.DateTimeOffset]::UtcNow.AddSeconds(10)
    while (-not (Test-Path -LiteralPath $timeoutChildPidPath -PathType Leaf) `
        -and [System.DateTimeOffset]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 50
    }
    if (-not (Test-Path -LiteralPath $timeoutChildPidPath -PathType Leaf)) {
        throw "Timeout cleanup fixture did not publish its child PID."
    }
    $timeoutChildId = [int](Get-Content -LiteralPath $timeoutChildPidPath -Raw)

    $stopProcessTree = Get-FunctionDefinition -Ast $rabbitMqAst -Name "Stop-ProcessTree"
    $stopProcessTreeTest = [scriptblock]::Create(
        "param(`$TargetProcessId)" + [Environment]::NewLine +
        $stopProcessTree.Extent.Text + [Environment]::NewLine +
        "Stop-ProcessTree -ProcessId `$TargetProcessId")
    & $stopProcessTreeTest $timeoutParent.Id
    if (-not $timeoutParent.WaitForExit(10000) `
        -or $null -ne (Get-Process -Id $timeoutChildId -ErrorAction SilentlyContinue)) {
        throw "RabbitMQ timeout cleanup did not terminate the complete fixture process tree."
    }
}
finally {
    if ($null -ne $timeoutParent -and -not $timeoutParent.HasExited) {
        & (Join-Path $env:SystemRoot "System32/taskkill.exe") /PID $timeoutParent.Id /T /F |
            Out-Null
    }
    if ($null -ne $timeoutParent) {
        $timeoutParent.Dispose()
    }
    if ($null -ne $timeoutChildId `
        -and $null -ne (Get-Process -Id $timeoutChildId -ErrorAction SilentlyContinue)) {
        Stop-Process -Id $timeoutChildId -Force
    }
}

Write-Host "Release staging security verification passed."
Write-Host " - Source staging copied only Git-index tracked paths and excluded an untracked sentinel."
Write-Host " - Final publication rejected dirty Git state and bound staging to a full commit."
Write-Host " - Formal file/password signing parameters are absent and command logs redact secret-shaped arguments."
Write-Host " - Staged Agent RabbitMQ verification has a finite timeout and behavior-verified taskkill process-tree cleanup."
Write-Host " - RabbitMQ, Studio, and Runner gates invoke the strict restricted-service-SID scavenger from finally."
Write-Host " - Cleanup manifest absence, directory obstruction, and reparse-point mutations are behavior-verified."
Write-Host " - Cleanup manifests bind mutable Temp roots separately from exact ProgramData package-cache roots and reject compatibility fields."
exit 0

param(
    [string] $EvidenceRoot = "output/staged-agent-bundle-e2e",

    [switch] $RequireSanitizedRoot
)

$ErrorActionPreference = "Stop"
$RepoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$ExpectedExactTests = @(
    [pscustomobject][ordered]@{
        FullyQualifiedName = "OpenLineOps.Agent.Tests.SignedVendorProgramStationE2ETests.SignedFrozenPluginRunsThroughAgentStationRuntimeAndBundledHost"
        TrxRelativePath = "test-results/signed-frozen-plugin.trx"
    },
    [pscustomobject][ordered]@{
        FullyQualifiedName = "OpenLineOps.Agent.Tests.SignedVendorProgramStationE2ETests.SignedFrozenPythonFlowRunsThroughAgentStationRuntimeAndWorker"
        TrxRelativePath = "test-results/signed-frozen-python.trx"
    },
    [pscustomobject][ordered]@{
        FullyQualifiedName = "OpenLineOps.Agent.Tests.LeastPrivilegeLauncherContractTests.ConcurrentWorkersUseDistinctAppContainersAndKillDescendants"
        TrxRelativePath = "test-results/staged-launcher-isolation.trx"
    },
    [pscustomobject][ordered]@{
        FullyQualifiedName = "OpenLineOps.Agent.Tests.LeastPrivilegeLauncherContractTests.StaleAppContainerProfileIsRecoveredBeforeNextLaunch"
        TrxRelativePath = "test-results/staged-launcher-crash-recovery.trx"
    },
    [pscustomobject][ordered]@{
        FullyQualifiedName = "OpenLineOps.Agent.Tests.LeastPrivilegeLauncherContractTests.ProvisioningCommandGrantsRuntimeCapabilityRecursively"
        TrxRelativePath = "test-results/staged-python-runtime-provisioning.trx"
    }
)

function Resolve-PathValue {
    param([Parameter(Mandatory = $true)][string] $Path)

    if ([System.IO.Path]::IsPathRooted($Path)) {
        [System.IO.Path]::GetFullPath($Path)
    }
    else {
        [System.IO.Path]::GetFullPath((Join-Path $RepoRoot $Path))
    }
}

function Assert-Condition {
    param(
        [Parameter(Mandatory = $true)][bool] $Condition,
        [Parameter(Mandatory = $true)][string] $Message
    )

    if (-not $Condition) {
        throw $Message
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

function Assert-ExactProperties {
    param(
        [Parameter(Mandatory = $true)] $Value,
        [Parameter(Mandatory = $true)][string[]] $Expected,
        [Parameter(Mandatory = $true)][string] $Description
    )

    $actual = @($Value.PSObject.Properties.Name)
    Assert-Condition ($actual.Count -eq $Expected.Count `
            -and @($actual | Where-Object { $Expected -cnotcontains $_ }).Count -eq 0 `
            -and @($Expected | Where-Object { $actual -cnotcontains $_ }).Count -eq 0) `
        "$Description does not have the exact strict-schema properties."
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

function Test-JsonInteger {
    param([AllowNull()] $Value)

    return $Value -is [byte] `
        -or $Value -is [sbyte] `
        -or $Value -is [int16] `
        -or $Value -is [uint16] `
        -or $Value -is [int32] `
        -or $Value -is [uint32] `
        -or $Value -is [int64] `
        -or $Value -is [uint64]
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

function Assert-ExactChildElements {
    param(
        [Parameter(Mandatory = $true)] $Element,
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][string[]] $Expected,
        [Parameter(Mandatory = $true)][string] $Description
    )

    $actual = @($Element.ChildNodes | Where-Object {
            $_.NodeType -eq [System.Xml.XmlNodeType]::Element
        } | ForEach-Object { $_.LocalName })
    Assert-Condition (($actual -join "`n") -ceq ($Expected -join "`n")) `
        "$Description does not contain the exact sanitized element sequence."
}

function Assert-ExactTest {
    param(
        [Parameter(Mandatory = $true)] $Evidence,
        [Parameter(Mandatory = $true)][string] $Root,
        [Parameter(Mandatory = $true)][string] $ExpectedFullyQualifiedName,
        [Parameter(Mandatory = $true)][string] $ExpectedTrxRelativePath
    )

    Assert-ExactProperties $Evidence @(
        "fullyQualifiedName",
        "result",
        "trxRelativePath",
        "trxSha256") "Staged Agent exact-test evidence"
    Assert-Condition ($Evidence.result -ceq "passed" `
            -and $Evidence.fullyQualifiedName -ceq $ExpectedFullyQualifiedName `
            -and $Evidence.trxRelativePath -ceq $ExpectedTrxRelativePath `
            -and $Evidence.trxSha256 -cmatch '^[0-9a-f]{64}$') `
        "Staged Agent exact test evidence is invalid."
    $trxPath = [System.IO.Path]::GetFullPath(
        (Join-Path $Root ([string]$Evidence.trxRelativePath).Replace(
            '/',
            [System.IO.Path]::DirectorySeparatorChar)))
    $rootPrefix = $Root.TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    Assert-Condition ($trxPath.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase) `
            -and (Test-Path -LiteralPath $trxPath -PathType Leaf)) `
        "Staged Agent exact test TRX is missing or escaped its evidence root."
    Assert-Condition ((Get-FileHash -LiteralPath $trxPath -Algorithm SHA256).Hash.ToLowerInvariant() `
            -ceq [string]$Evidence.trxSha256) `
        "Staged Agent exact test TRX SHA-256 changed."
    $trxFile = Get-Item -LiteralPath $trxPath -Force
    Assert-Condition ($trxFile.Length -gt 0 -and $trxFile.Length -le 65536) `
        "Staged Agent exact test TRX is outside the minimal public size bound."
    $trxText = Get-Content -LiteralPath $trxPath -Raw
    Assert-NoSensitiveText -Text $trxText -Description "Staged Agent exact-test TRX"

    $trx = Read-SafeXml $trxPath
    Assert-Condition ($trx.DocumentElement.LocalName -ceq "TestRun" `
            -and $trx.DocumentElement.NamespaceURI -ceq `
                "http://microsoft.com/schemas/VisualStudio/TeamTest/2010" `
            -and $trx.TestRun.name -ceq "staged-agent-bundle-e2e" `
            -and $trx.TestRun.runUser -ceq "redacted") `
        "Staged Agent exact-test TRX identity is not sanitized."
    $rootAttributeNames = @($trx.TestRun.Attributes | ForEach-Object { $_.Name })
    Assert-Condition ($rootAttributeNames.Count -eq 3 `
            -and @($rootAttributeNames | Where-Object {
                $_ -cnotin @("xmlns", "name", "runUser")
            }).Count -eq 0) `
        "Staged Agent exact-test TRX contains non-public root attributes."
    Assert-ExactChildElements `
        $trx.TestRun `
        @("Results", "TestDefinitions", "ResultSummary") `
        "Staged Agent exact-test TRX root"
    Assert-Condition (@($trx.SelectNodes("//text()") | Where-Object {
                -not [string]::IsNullOrWhiteSpace($_.Value)
            }).Count -eq 0) `
        "Staged Agent exact-test TRX contains raw text output."

    $resultsContainer = $trx.TestRun.Results
    $definitionsContainer = $trx.TestRun.TestDefinitions
    $summary = $trx.TestRun.ResultSummary
    Assert-Condition ($resultsContainer.Attributes.Count -eq 0 `
            -and $definitionsContainer.Attributes.Count -eq 0) `
        "Staged Agent exact-test TRX containers contain non-public attributes."
    Assert-ExactChildElements $resultsContainer @("UnitTestResult") `
        "Staged Agent exact-test results"
    Assert-ExactChildElements $definitionsContainer @("UnitTest") `
        "Staged Agent exact-test definitions"
    Assert-ExactChildElements $summary @("Counters") `
        "Staged Agent exact-test summary"

    $result = $resultsContainer.UnitTestResult
    $definition = $definitionsContainer.UnitTest
    $testMethod = $definition.TestMethod
    $counters = $summary.Counters
    Assert-Condition ($result.Attributes.Count -eq 4 `
            -and $definition.Attributes.Count -eq 3 `
            -and $testMethod.Attributes.Count -eq 3 `
            -and $summary.Attributes.Count -eq 1 `
            -and $counters.Attributes.Count -eq 5) `
        "Staged Agent exact-test TRX contains non-minimal evidence attributes."
    Assert-ExactChildElements $definition @("TestMethod") `
        "Staged Agent exact-test definition"
    Assert-ExactChildElements $result @() "Staged Agent exact-test result"
    Assert-ExactChildElements $testMethod @() "Staged Agent exact-test method"
    Assert-ExactChildElements $counters @() "Staged Agent exact-test counters"

    $lastSeparator = $ExpectedFullyQualifiedName.LastIndexOf('.')
    $expectedClassName = $ExpectedFullyQualifiedName.Substring(0, $lastSeparator)
    $expectedMethodName = $ExpectedFullyQualifiedName.Substring($lastSeparator + 1)
    $parsedTestId = [System.Guid]::Empty
    Assert-Condition ([System.Guid]::TryParseExact(
            [string]$definition.id,
            "D",
            [ref]$parsedTestId) `
            -and $parsedTestId -ne [System.Guid]::Empty `
            -and $result.testId -ceq $definition.id `
            -and $result.testName -ceq $ExpectedFullyQualifiedName `
            -and $result.computerName -ceq "redacted" `
            -and $result.outcome -ceq "Passed" `
            -and $definition.name -ceq $ExpectedFullyQualifiedName `
            -and $definition.storage -ceq "OpenLineOps.Agent.Tests.dll" `
            -and $testMethod.codeBase -ceq "OpenLineOps.Agent.Tests.dll" `
            -and $testMethod.className -ceq $expectedClassName `
            -and $testMethod.name -ceq $expectedMethodName `
            -and $summary.outcome -ceq "Completed" `
            -and [int]$counters.total -eq 1 `
            -and [int]$counters.executed -eq 1 `
            -and [int]$counters.passed -eq 1 `
            -and [int]$counters.failed -eq 0 `
            -and [int]$counters.notExecuted -eq 0) `
        "Staged Agent sanitized TRX does not prove the exact Passed and zero-Skipped test."
}

function Assert-TestEvidenceBinding {
    param(
        [Parameter(Mandatory = $true)] $Bound,
        [Parameter(Mandatory = $true)] $Canonical,
        [Parameter(Mandatory = $true)][string] $Description
    )

    Assert-ExactProperties $Bound @(
        "fullyQualifiedName",
        "result",
        "trxRelativePath",
        "trxSha256") $Description
    Assert-Condition ($Bound.fullyQualifiedName -ceq $Canonical.fullyQualifiedName `
            -and $Bound.result -ceq $Canonical.result `
            -and $Bound.trxRelativePath -ceq $Canonical.trxRelativePath `
            -and $Bound.trxSha256 -ceq $Canonical.trxSha256) `
        "$Description is not bound to its canonical top-level exact-test evidence."
}

function Assert-NoSensitiveText {
    param(
        [Parameter(Mandatory = $true)][string] $Text,
        [Parameter(Mandatory = $true)][string] $Description
    )

    foreach ($pattern in @(
            '-----BEGIN (?:RSA |EC |DSA |OPENSSH )?PRIVATE KEY-----',
            '(?i)"(?:apiAccessToken|artifactUploadBearerToken|bearerToken|authorization|clientSecret|password)"\s*:',
            '(?i)\bBearer\s+[A-Za-z0-9._~+/=-]{8,}',
            '(?i)\bOPENLINEOPS_[A-Z0-9_]*(?:TOKEN|PASSWORD|SECRET)\s*[=:]',
            '(?i)(?:amqp|amqps|http|https)://[^\s/:@]+:[^\s/@]+@',
            '(?i)(?:(?<![A-Za-z0-9+.-])[A-Z]:[\\/]|\\\\[A-Za-z0-9._-]+\\|/(?:home|tmp|Users|workspace)/)',
            '(?i)<(?:Output|StdOut|StdErr)(?:\s|>)')) {
        if ($Text -match $pattern) {
            throw "$Description contains credential, private-key, local-path, or raw-log material."
        }
    }
}

function Assert-SanitizedEvidenceRoot {
    param(
        [Parameter(Mandatory = $true)][string] $Root,
        [Parameter(Mandatory = $true)][string[]] $ExpectedFiles
    )

    $rootPrefix = $Root.TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    $expected = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::Ordinal)
    foreach ($relativePath in $ExpectedFiles) {
        Assert-Condition ($relativePath -cmatch '^(?!/)(?!.*\\)(?!.*(?:^|/)\.\.(?:/|$))[A-Za-z0-9._/-]+$' `
                -and $expected.Add($relativePath)) `
            "Staged Agent public evidence path is non-canonical or duplicated: $relativePath"
    }

    $actual = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::Ordinal)
    $entries = @(Get-ChildItem -LiteralPath $Root -Force -Recurse)
    foreach ($entry in $entries) {
        Assert-Condition (($entry.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -eq 0) `
            "Staged Agent public evidence contains a reparse point: $($entry.FullName)"
        Assert-Condition ($entry.FullName.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) `
            "Staged Agent public evidence escaped its root."
        $relativePath = $entry.FullName.Substring($rootPrefix.Length).Replace('\', '/')
        if ($entry -is [System.IO.DirectoryInfo]) {
            Assert-Condition ($relativePath -cin @(
                    "test-results",
                    "rabbitmq-process",
                    "entry-point-probes")) `
                "Staged Agent public evidence contains an unknown directory: $relativePath"
            continue
        }
        Assert-Condition ($entry -is [System.IO.FileInfo] `
                -and $expected.Contains($relativePath) `
                -and $actual.Add($relativePath)) `
            "Staged Agent public evidence contains an unknown or duplicate file: $relativePath"
        Assert-NoSensitiveText `
            -Text (Get-Content -LiteralPath $entry.FullName -Raw) `
            -Description "Staged Agent public evidence '$relativePath'"
    }
    Assert-Condition ($actual.Count -eq $expected.Count) `
        "Staged Agent sanitized evidence membership is incomplete."
}

$resolvedRoot = Resolve-PathValue $EvidenceRoot
Assert-Condition (Test-Path -LiteralPath $resolvedRoot -PathType Container) `
    "Staged Agent evidence root does not exist: $resolvedRoot"
$evidencePath = Join-Path $resolvedRoot "evidence.json"
Assert-Condition (Test-Path -LiteralPath $evidencePath -PathType Leaf) `
    "Staged Agent evidence.json is missing."
try {
    $evidence = Get-Content -LiteralPath $evidencePath -Raw | ConvertFrom-Json
}
catch {
    throw "Staged Agent evidence.json is invalid JSON: $($_.Exception.Message)"
}

Assert-Condition ($evidence.schemaVersion -eq 1 `
        -and $evidence.product -ceq "OpenLineOps" `
        -and $evidence.status -ceq "passed" `
        -and -not [string]::IsNullOrWhiteSpace([string]$evidence.releaseVersion) `
        -and $evidence.agentArtifact.sha256 -cmatch '^[0-9a-f]{64}$' `
        -and $evidence.samplePluginArtifact.sha256 -cmatch '^[0-9a-f]{64}$') `
    "Staged Agent top-level evidence identity is invalid."

$expectedEntryPoints = [ordered]@{
    "station-agent-service" = [pscustomobject][ordered]@{
        executable = "OpenLineOps.Agent.exe"
        requiredExitCode = $null
        outputContract = '^OpenLineOps Station Agent terminated: OpenLineOps:WindowsServiceName must contain 1-80 ASCII letters, digits, periods, underscores, or hyphens\.$'
    }
    "station-runtime" = [pscustomobject][ordered]@{
        executable = "OpenLineOps.StationRuntime.exe"
        requiredExitCode = 64
        outputContract = "execute-operation --request-file"
    }
    "plugin-host" = [pscustomobject][ordered]@{
        executable = "OpenLineOps.PluginHost.exe"
        requiredExitCode = 2
        outputContract = "--manifest is required"
    }
    "python-script-worker" = [pscustomobject][ordered]@{
        executable = "OpenLineOps.ScriptWorker.exe"
        requiredExitCode = 2
        outputContract = "Python script worker request body is required"
    }
}
$entryPoints = @($evidence.entryPoints)
$probes = @($evidence.entryPointProbes)
Assert-Condition ($entryPoints.Count -eq 4 -and $probes.Count -eq 4) `
    "Staged Agent evidence must contain exactly four entry points and probes."
foreach ($role in $expectedEntryPoints.Keys) {
    $expected = $expectedEntryPoints[$role]
    $entryPoint = @($entryPoints | Where-Object { $_.role -ceq $role })
    $probe = @($probes | Where-Object { $_.name -ceq $role })
    Assert-Condition ($entryPoint.Count -eq 1 -and $probe.Count -eq 1) `
        "Staged Agent entry point or probe '$role' is missing or duplicated."
    $entryPoint = $entryPoint[0]
    $probe = $probe[0]
    Assert-ExactProperties $entryPoint @(
        "role",
        "relativePath",
        "sha256") "Staged Agent entry point '$role'"
    Assert-ExactProperties $probe @(
        "name",
        "executable",
        "executableSha256",
        "exitCode",
        "outputContract",
        "status") "Staged Agent entry-point probe '$role'"
    Assert-Condition ($entryPoint.relativePath -ceq $expected.executable `
            -and $entryPoint.sha256 -cmatch '^[0-9a-f]{64}$' `
            -and $probe.executable -ceq $entryPoint.relativePath `
            -and $probe.executableSha256 -ceq $entryPoint.sha256 `
            -and (Test-JsonInteger $probe.exitCode) `
            -and $probe.outputContract -ceq $expected.outputContract `
            -and $probe.status -ceq "passed") `
        "Staged Agent entry-point probe '$role' is not value- and hash-bound to its bundle executable."
    if ($role -ceq "station-agent-service") {
        Assert-Condition ($probe.exitCode -ne 0) `
            "Staged Agent service probe must fail closed with a non-zero integer exit code."
    }
    else {
        Assert-Condition ($probe.exitCode -eq $expected.requiredExitCode) `
            "Staged Agent entry-point probe '$role' has the wrong exit code."
    }
}

$entryPointProbeReference = $evidence.entryPointProbeEvidence
Assert-ExactProperties $entryPointProbeReference @(
    "evidence",
    "evidenceSha256") "Staged Agent entry-point raw evidence reference"
Assert-Condition ($entryPointProbeReference.evidence -ceq "entry-point-probes/evidence.json" `
        -and $entryPointProbeReference.evidenceSha256 -cmatch '^[0-9a-f]{64}$') `
    "Staged Agent entry-point raw evidence reference is invalid."
$entryPointProbeRawPath = [System.IO.Path]::GetFullPath(
    (Join-Path $resolvedRoot ([string]$entryPointProbeReference.evidence).Replace(
        '/',
        [System.IO.Path]::DirectorySeparatorChar)))
$entryPointProbeRootPrefix = $resolvedRoot.TrimEnd(
    [System.IO.Path]::DirectorySeparatorChar,
    [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
Assert-Condition ($entryPointProbeRawPath.StartsWith(
            $entryPointProbeRootPrefix,
            [System.StringComparison]::OrdinalIgnoreCase) `
        -and (Test-Path -LiteralPath $entryPointProbeRawPath -PathType Leaf) `
        -and (Get-FileHash -LiteralPath $entryPointProbeRawPath -Algorithm SHA256).Hash.ToLowerInvariant() `
            -ceq [string]$entryPointProbeReference.evidenceSha256) `
    "Staged Agent entry-point raw evidence is missing, escaped, or changed."
$entryPointProbeRaw = Get-Content -LiteralPath $entryPointProbeRawPath -Raw |
    ConvertFrom-Json
Assert-ExactProperties $entryPointProbeRaw @(
    "schema",
    "schemaVersion",
    "agentArtifactSha256",
    "entryPoints",
    "probes") "Staged Agent entry-point raw evidence"
$rawEntryPoints = @($entryPointProbeRaw.entryPoints)
$rawProbes = @($entryPointProbeRaw.probes)
Assert-Condition ($rawEntryPoints.Count -eq 4 -and $rawProbes.Count -eq 4) `
    "Staged Agent entry-point raw evidence must contain exactly four entry points and probes."
foreach ($role in $expectedEntryPoints.Keys) {
    $rawEntryPoint = @($rawEntryPoints | Where-Object { $_.role -ceq $role })
    $rawProbe = @($rawProbes | Where-Object { $_.name -ceq $role })
    Assert-Condition ($rawEntryPoint.Count -eq 1 -and $rawProbe.Count -eq 1) `
        "Staged Agent raw entry point or probe '$role' is missing or duplicated."
    Assert-ExactProperties $rawEntryPoint[0] @(
        "role",
        "relativePath",
        "sha256") "Staged Agent raw entry point '$role'"
    Assert-ExactProperties $rawProbe[0] @(
        "name",
        "executable",
        "executableSha256",
        "exitCode",
        "outputContract",
        "status") "Staged Agent raw entry-point probe '$role'"
}
Assert-Condition ($entryPointProbeRaw.schema -ceq `
        "openlineops.staged-agent-entry-point-probe-evidence" `
        -and $entryPointProbeRaw.schemaVersion -eq 1 `
        -and $entryPointProbeRaw.agentArtifactSha256 -ceq $evidence.agentArtifact.sha256 `
        -and (ConvertTo-Json @($entryPointProbeRaw.entryPoints) -Depth 6 -Compress) `
            -ceq (ConvertTo-Json $entryPoints -Depth 6 -Compress) `
        -and (ConvertTo-Json @($entryPointProbeRaw.probes) -Depth 6 -Compress) `
            -ceq (ConvertTo-Json $probes -Depth 6 -Compress)) `
    "Staged Agent entry-point public evidence is not value- and hash-bound to its raw probe evidence."

$exactTests = @($evidence.exactTestEvidence)
Assert-Condition ($exactTests.Count -eq 5 `
        -and @($exactTests.fullyQualifiedName | Select-Object -Unique).Count -eq 5) `
    "Staged Agent evidence must contain five unique exact tests."
for ($index = 0; $index -lt $ExpectedExactTests.Count; $index++) {
    Assert-ExactTest `
        -Evidence $exactTests[$index] `
        -Root $resolvedRoot `
        -ExpectedFullyQualifiedName $ExpectedExactTests[$index].FullyQualifiedName `
        -ExpectedTrxRelativePath $ExpectedExactTests[$index].TrxRelativePath
}
$chains = @($evidence.signedPackageProcessChains)
Assert-Condition ($chains.Count -eq 2) `
    "Staged signed package process-chain evidence is incomplete."
$pluginChain = $chains[0]
$pythonChain = $chains[1]
Assert-ExactProperties $pluginChain @(
    "name",
    "path",
    "packageFormat",
    "exactTest",
    "status") "Staged signed plugin process chain"
Assert-ExactProperties $pythonChain @(
    "name",
    "path",
    "packageFormat",
    "executionPolicy",
    "tokenIsAppContainer",
    "appContainerSid",
    "integrityRid",
    "exactTest",
    "status") "Staged signed Python process chain"
Assert-Condition ($pluginChain.name -ceq "signed-frozen-plugin" `
        -and $pluginChain.path -ceq `
            "Agent application layer -> staged StationRuntime -> staged PluginHost" `
        -and $pluginChain.packageFormat -ceq ".olopkg" `
        -and $pluginChain.status -ceq "passed" `
        -and $pythonChain.name -ceq "signed-frozen-python" `
        -and $pythonChain.path -ceq `
            "Agent application layer -> staged StationRuntime -> staged ScriptWorker" `
        -and $pythonChain.packageFormat -ceq ".olopkg" `
        -and $pythonChain.executionPolicy -ceq "Required PerExecutionAppContainer" `
        -and $pythonChain.tokenIsAppContainer -eq $true `
        -and $pythonChain.appContainerSid -cmatch '^S-1-15-2-(?:[0-9]+-){6}[0-9]+$' `
        -and $pythonChain.integrityRid -eq 4096 `
        -and $pythonChain.status -ceq "passed") `
    "Staged signed package process-chain evidence is incomplete."
Assert-TestEvidenceBinding `
    $pluginChain.exactTest `
    $exactTests[0] `
    "Staged signed plugin exact-test binding"
Assert-TestEvidenceBinding `
    $pythonChain.exactTest `
    $exactTests[1] `
    "Staged signed Python exact-test binding"
$python = $evidence.productionPythonPolicy
Assert-ExactProperties $python @(
    "templateVerified",
    "requireLeastPrivilegeExecution",
    "isolationMode",
    "identity",
    "launcher",
    "noInteractivePrompt",
    "childTokenIsAppContainer",
    "childAppContainerSid",
    "childIntegrityRid",
    "stagedIsolationTest",
    "stagedCrashRecoveryTest",
    "stagedPythonRuntimeProvisioningTest",
    "stagedExecutionVerified") "Staged Python production policy"
Assert-Condition ($python.templateVerified -eq $true `
        -and $python.requireLeastPrivilegeExecution -eq $true `
        -and $python.isolationMode -ceq "LeastPrivilegeIdentity" `
        -and $python.identity -ceq "PerExecutionAppContainer" `
        -and $python.launcher -ceq "OpenLineOps.LeastPrivilegeLauncher.exe" `
        -and $python.noInteractivePrompt -eq $true `
        -and $python.childTokenIsAppContainer -eq $true `
        -and $python.childAppContainerSid -cmatch '^S-1-15-2-(?:[0-9]+-){6}[0-9]+$' `
        -and $python.childIntegrityRid -eq 4096 `
        -and $python.stagedExecutionVerified -eq $true) `
    "Staged Python least-privilege policy evidence is incomplete."
Assert-TestEvidenceBinding `
    $python.stagedIsolationTest `
    $exactTests[2] `
    "Staged Python isolation exact-test binding"
Assert-TestEvidenceBinding `
    $python.stagedCrashRecoveryTest `
    $exactTests[3] `
    "Staged Python crash-recovery exact-test binding"
Assert-TestEvidenceBinding `
    $python.stagedPythonRuntimeProvisioningTest `
    $exactTests[4] `
    "Staged Python runtime-provisioning exact-test binding"

$rabbit = $evidence.rabbitMqTransportCoverage
$materialArrivalIpcFields = @(
    "serviceTokenConnected",
    "pipeExactAclVerified",
    "durablePublicationVerified",
    "ordinaryCiTokenExplicitAccessDenied")
Assert-ExactProperties `
    -Value $rabbit.materialArrivalIpc `
    -Expected $materialArrivalIpcFields `
    -Description "Staged Agent material-arrival IPC evidence"
foreach ($field in $materialArrivalIpcFields) {
    Assert-Condition ($rabbit.materialArrivalIpc.$field -is [bool] `
            -and $rabbit.materialArrivalIpc.$field -eq $true) `
        "Staged Agent material-arrival IPC evidence field '$field' must be the JSON boolean true."
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
    -Value $rabbit.immutableContentCache `
    -Expected $immutableContentCacheFields `
    -Description "Staged Agent immutable content-cache evidence"
foreach ($field in $immutableContentCacheFields) {
    Assert-Condition ($rabbit.immutableContentCache.$field -is [bool] `
            -and $rabbit.immutableContentCache.$field -eq $true) `
        "Staged Agent immutable content-cache evidence field '$field' must be the JSON boolean true."
}
Assert-JsonBooleanProperties $rabbit ([ordered]@{
        operatorTraceGetVerified = $true
        brokerOutageVerified = $true
        coordinatorTransportResultInboxRestartedAfterBrokerRecovery = $true
        offlineCompletionWasNotDelivered = $true
        completionDeliveredOnceAfterReconnect = $true
        duplicateRedeliveryRejected = $true
        duplicateAfterRestartRejected = $true
        windowsServiceLifecycleVerified = $true
        cleanShutdownVerified = $true
    }) "Staged Agent public RabbitMQ evidence"
Assert-Condition ($rabbit.status -ceq "passed" `
        -and $rabbit.executionStatus -ceq "Completed" `
        -and $rabbit.judgement -ceq "Passed" `
        -and $rabbit.vendorProgram -ceq "OpenLineOps.VendorTestHelper.exe" `
        -and $rabbit.centralArtifactTransport -ceq "authenticated-http-stream" `
        -and $rabbit.operatorTraceGetVerified -eq $true `
        -and $rabbit.brokerOutageVerified -eq $true `
        -and $rabbit.coordinatorTransportResultInboxRestartedAfterBrokerRecovery -eq $true `
        -and $rabbit.offlinePendingOutboxCount -ge 1 `
        -and $rabbit.offlineCompletionWasNotDelivered -eq $true `
        -and $rabbit.completionDeliveredOnceAfterReconnect -eq $true `
        -and $rabbit.duplicateRedeliveryRejected -eq $true `
        -and $rabbit.duplicateAfterRestartRejected -eq $true `
        -and $rabbit.runtimeFinishedExecutionCount -eq 1 `
        -and $rabbit.firstAgentPid -gt 0 `
        -and $rabbit.restartedAgentPid -gt 0 `
        -and $rabbit.firstAgentPid -ne $rabbit.restartedAgentPid `
        -and $rabbit.packageContentSha256 -cmatch '^[0-9a-f]{64}$' `
        -and -not [string]::IsNullOrWhiteSpace([string]$rabbit.agentId) `
        -and -not [string]::IsNullOrWhiteSpace([string]$rabbit.stationId) `
        -and [string]$rabbit.windowsServiceName -cmatch '^OpenLineOpsAgentE2E-[0-9a-f]{32}$' `
        -and $rabbit.windowsServiceLifecycleVerified -is [bool] `
        -and $rabbit.windowsServiceLifecycleVerified -eq $true `
        -and $rabbit.cleanShutdownVerified -is [bool] `
        -and $rabbit.cleanShutdownVerified -eq $true) `
    "Staged Agent RabbitMQ transport closure is incomplete or was skipped."

foreach ($identity in @($rabbit.agentHostIdentity, $rabbit.restartedAgentHostIdentity)) {
    Assert-ExactProperties $identity @(
        "nonAdministrative",
        "isPrimaryToken",
        "hasLinkedToken",
        "isRestrictedToken",
        "administratorGroupPresent",
        "administratorGroupEnabled",
        "administratorGroupDenyOnly",
        "serviceLogonSidPresent",
        "serviceLogonSidEnabled",
        "exactServiceSidPresent",
        "exactServiceSidEnabled",
        "exactServiceSidRestricted",
        "isAuthenticated",
        "isSystem",
        "identityStrategy",
        "serviceAccountName",
        "serviceAccountSid",
        "serviceSid") "Staged Agent host identity"
    Assert-JsonBooleanProperties $identity ([ordered]@{
            nonAdministrative = $true
            isPrimaryToken = $true
            hasLinkedToken = $false
            isRestrictedToken = $true
            administratorGroupPresent = $false
            administratorGroupEnabled = $false
            administratorGroupDenyOnly = $false
            serviceLogonSidPresent = $true
            serviceLogonSidEnabled = $true
            exactServiceSidPresent = $true
            exactServiceSidEnabled = $true
            exactServiceSidRestricted = $true
            isAuthenticated = $true
            isSystem = $false
        }) "Staged Agent host identity"
    Assert-Condition ($identity.nonAdministrative -eq $true `
            -and $identity.isPrimaryToken -eq $true `
            -and $identity.hasLinkedToken -eq $false `
            -and $identity.isRestrictedToken -eq $true `
            -and $identity.administratorGroupPresent -eq $false `
            -and $identity.administratorGroupEnabled -eq $false `
            -and $identity.administratorGroupDenyOnly -eq $false `
            -and $identity.serviceLogonSidPresent -eq $true `
            -and $identity.serviceLogonSidEnabled -eq $true `
            -and $identity.exactServiceSidPresent -eq $true `
            -and $identity.exactServiceSidEnabled -eq $true `
            -and $identity.exactServiceSidRestricted -eq $true `
            -and $identity.isAuthenticated -eq $true `
            -and $identity.isSystem -eq $false `
            -and $identity.identityStrategy -ceq "local-service-restricted-service-sid" `
            -and $identity.serviceAccountName -ceq "NT AUTHORITY\LocalService" `
            -and $identity.serviceAccountSid -ceq "S-1-5-19" `
            -and $identity.serviceSid -cmatch '^S-1-5-80-(?:[0-9]+-){4}[0-9]+$') `
        "Staged Agent host identity is not the required LocalService restricted service-SID token."
}
Assert-Condition ($rabbit.agentHostIdentity.identityStrategy -ceq $rabbit.restartedAgentHostIdentity.identityStrategy `
        -and $rabbit.agentHostIdentity.serviceAccountName -ceq $rabbit.restartedAgentHostIdentity.serviceAccountName `
        -and $rabbit.agentHostIdentity.serviceAccountSid -ceq $rabbit.restartedAgentHostIdentity.serviceAccountSid `
        -and $rabbit.agentHostIdentity.serviceSid -ceq $rabbit.restartedAgentHostIdentity.serviceSid) `
    "Restarted staged Agent identity differs from its initial identity."
$expectedServiceSid = Get-ServiceSidFromName $rabbit.windowsServiceName
Assert-Condition ($rabbit.agentHostIdentity.serviceSid -ceq $expectedServiceSid) `
    "Staged Agent service SID does not match its exact Windows service name."

$presence = $rabbit.presence
Assert-JsonBooleanProperties $presence ([ordered]@{
        startedAndHeartbeatPersisted = $true
        expiredOfflineDuringBrokerOutage = $true
        freshOnlineAfterReconnect = $true
    }) "Staged Agent public presence evidence"
Assert-Condition (@($presence.persistedStates) -ccontains "Started" `
        -and @($presence.persistedStates) -ccontains "Heartbeat" `
        -and $presence.startedAndHeartbeatPersisted -eq $true `
        -and $presence.expiredOfflineDuringBrokerOutage -eq $true `
        -and $presence.offlineDuringBrokerOutage.status -ceq "Offline" `
        -and $presence.offlineDuringBrokerOutage.health -ceq "Expired" `
        -and $presence.freshOnlineAfterReconnect -eq $true `
        -and $presence.onlineAfterReconnect.status -ceq "Idle" `
        -and $presence.onlineAfterReconnect.health -ceq "Online") `
    "Staged Agent persisted presence outage/reconnect evidence is incomplete."

$requiredArtifacts = [ordered]@{
    "measurements.csv" = "Csv"
    "inspection.png" = "Image"
    "report.pdf" = "Report"
    "stdout.log" = "Log"
    "stderr.log" = "Log"
}
$artifacts = @($rabbit.vendorArtifacts)
Assert-Condition ($artifacts.Count -ge 5) `
    "Staged vendor completion contains fewer than five required artifacts."
foreach ($name in $requiredArtifacts.Keys) {
    $matches = @($artifacts | Where-Object {
            $_.name -ceq $name `
                -and $_.kind -ceq $requiredArtifacts[$name] `
                -and $_.storageKey -cmatch '^station-artifacts/' `
                -and $_.receiptId -cmatch '^[0-9a-f]{64}$' `
                -and $_.sha256 -cmatch '^[0-9a-f]{64}$' `
                -and $_.sizeBytes -ge 0
        })
    Assert-Condition ($matches.Count -eq 1) `
        "Staged vendor artifact '$name/$($requiredArtifacts[$name])' is missing or invalid."
}

Assert-Condition ($rabbit.evidence -ceq "rabbitmq-process/evidence.json" `
        -and $rabbit.evidenceSha256 -cmatch '^[0-9a-f]{64}$') `
    "Staged RabbitMQ raw evidence reference is invalid."
$rawPath = [System.IO.Path]::GetFullPath(
    (Join-Path $resolvedRoot ([string]$rabbit.evidence).Replace(
        '/',
        [System.IO.Path]::DirectorySeparatorChar)))
$rootPrefix = $resolvedRoot.TrimEnd(
    [System.IO.Path]::DirectorySeparatorChar,
    [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
Assert-Condition ($rawPath.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase) `
        -and (Test-Path -LiteralPath $rawPath -PathType Leaf) `
        -and (Get-FileHash -LiteralPath $rawPath -Algorithm SHA256).Hash.ToLowerInvariant() `
            -ceq [string]$rabbit.evidenceSha256) `
    "Staged RabbitMQ raw evidence is missing, escaped, or changed."
$raw = Get-Content -LiteralPath $rawPath -Raw | ConvertFrom-Json
Assert-Condition ($raw.broker.tls -is [bool]) `
    "Staged Agent raw broker TLS marker must be a JSON boolean."
Assert-JsonBooleanProperties $raw ([ordered]@{
        operatorTraceGetVerified = $true
        brokerOutageVerified = $true
        coordinatorTransportResultInboxRestartedAfterBrokerRecovery = $true
        offlineCompletionWasNotDelivered = $true
        completionDeliveredOnceAfterReconnect = $true
        duplicateRedeliveryRejected = $true
        duplicateAfterRestartRejected = $true
        windowsServiceLifecycleVerified = $true
        cleanShutdownVerified = $true
    }) "Staged Agent raw RabbitMQ evidence"
foreach ($rawIdentity in @($raw.agentHostIdentity, $raw.restartedAgentHostIdentity)) {
    Assert-JsonBooleanProperties $rawIdentity ([ordered]@{
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
        }) "Staged Agent raw host identity"
}
Assert-JsonBooleanProperties $raw.presence ([ordered]@{
        startedAndHeartbeatPersisted = $true
        expiredOfflineDuringBrokerOutage = $true
        freshOnlineAfterReconnect = $true
    }) "Staged Agent raw presence evidence"
Assert-ExactProperties `
    -Value $raw.materialArrivalIpc `
    -Expected $materialArrivalIpcFields `
    -Description "Staged Agent raw material-arrival IPC evidence"
foreach ($field in $materialArrivalIpcFields) {
    Assert-Condition ($raw.materialArrivalIpc.$field -is [bool] `
            -and $raw.materialArrivalIpc.$field -eq $true) `
        "Staged Agent raw material-arrival IPC evidence field '$field' must be the JSON boolean true."
    Assert-Condition ($rabbit.materialArrivalIpc.$field -ceq $raw.materialArrivalIpc.$field) `
        "Staged Agent material-arrival IPC field '$field' differs from raw RabbitMQ evidence."
}
Assert-ExactProperties `
    -Value $raw.immutableContentCache `
    -Expected $immutableContentCacheFields `
    -Description "Staged Agent raw immutable content-cache evidence"
foreach ($field in $immutableContentCacheFields) {
    Assert-Condition ($raw.immutableContentCache.$field -is [bool] `
            -and $raw.immutableContentCache.$field -eq $true) `
        "Staged Agent raw immutable content-cache evidence field '$field' must be the JSON boolean true."
    Assert-Condition ($rabbit.immutableContentCache.$field -ceq $raw.immutableContentCache.$field) `
        "Staged Agent immutable content-cache field '$field' differs from raw RabbitMQ evidence."
}
foreach ($field in @(
        "executionStatus",
        "judgement",
        "vendorProgram",
        "centralArtifactTransport",
        "operatorTraceGetVerified",
        "brokerOutageVerified",
        "coordinatorTransportResultInboxRestartedAfterBrokerRecovery",
        "offlinePendingOutboxCount",
        "offlineCompletionWasNotDelivered",
        "completionDeliveredOnceAfterReconnect",
        "duplicateRedeliveryRejected",
        "duplicateAfterRestartRejected",
        "runtimeFinishedExecutionCount",
        "firstAgentPid",
        "restartedAgentPid",
        "packageContentSha256",
        "windowsServiceName",
        "windowsServiceLifecycleVerified")) {
    Assert-Condition ($rabbit.$field -ceq $raw.$field) `
        "Staged Agent summary field '$field' differs from raw RabbitMQ evidence."
}
foreach ($identityField in @(
        "nonAdministrative",
        "isPrimaryToken",
        "hasLinkedToken",
        "isRestrictedToken",
        "administratorGroupPresent",
        "administratorGroupEnabled",
        "administratorGroupDenyOnly",
        "serviceLogonSidPresent",
        "serviceLogonSidEnabled",
        "exactServiceSidPresent",
        "exactServiceSidEnabled",
        "exactServiceSidRestricted",
        "isAuthenticated",
        "isSystem",
        "identityStrategy",
        "serviceAccountName",
        "serviceAccountSid",
        "serviceSid")) {
    Assert-Condition ($rabbit.agentHostIdentity.$identityField -ceq $raw.agentHostIdentity.$identityField) `
        "Staged Agent initial identity field '$identityField' differs from raw RabbitMQ evidence."
    Assert-Condition ($rabbit.restartedAgentHostIdentity.$identityField -ceq $raw.restartedAgentHostIdentity.$identityField) `
        "Staged Agent restarted identity field '$identityField' differs from raw RabbitMQ evidence."
}
Assert-Condition ((ConvertTo-Json @($rabbit.vendorArtifacts) -Depth 10 -Compress) `
        -ceq (ConvertTo-Json @($raw.vendorArtifacts) -Depth 10 -Compress)) `
    "Staged Agent vendor artifacts differ from raw RabbitMQ evidence."

if ($RequireSanitizedRoot) {
    Assert-SanitizedEvidenceRoot `
        -Root $resolvedRoot `
        -ExpectedFiles (@(
                "evidence.json",
                [string]$entryPointProbeReference.evidence,
                [string]$rabbit.evidence) + @(
                $exactTests | ForEach-Object { [string]$_.trxRelativePath }))
}

Write-Host "Staged Agent evidence verification passed."
Write-Host " - Exact tests: $($exactTests.Count)"
Write-Host " - Required central vendor artifacts: $($requiredArtifacts.Count)"
Write-Host " - RabbitMQ and Windows SCM service lifecycle boundaries: required and passed"
Write-Host " - Sanitized public root required: $([bool]$RequireSanitizedRoot)"

param(
    [string] $BundleRoot = ".",

    [string] $WorkRoot = "output/ci-release-artifact-inspection",

    [switch] $RequirePublishable,

    [switch] $RequireSignedWindowsArtifacts
)

$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.Runtime.Serialization
Add-Type -AssemblyName System.Xml.Linq

$RepoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
. (Join-Path $PSScriptRoot "publication-evidence-case-contract.ps1")
$Failures = New-Object System.Collections.Generic.List[string]
$ExpectedArtifactKinds = @("agent", "api", "desktop", "plugin-host", "runner", "sample-plugin", "script-worker", "source")
$RequiredProductionIntegrationTest = "OpenLineOps.PostgresIntegration.Tests.PostgresRabbitMqProductionCoordinationIntegrationTests.DurableOutboxAndResultInboxSurviveCoordinatorRestartAcrossRealBroker"
$StagedWindowsAgentRecoveryBoundary = "Published Windows Agent process, signed vendor helper, broker outage, durable SQLite Inbox/Outbox, presence TTL, and transport result-inbox restart"
$DurableCoordinatorRecoveryBoundary = "PostgreSQL coordination store and RabbitMQ transport survive Coordinator transport/store cold restart exactly once"

function Resolve-RepoPath {
    param([Parameter(Mandatory = $true)][string] $Path)

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }

    return [System.IO.Path]::GetFullPath((Join-Path $RepoRoot $Path))
}

function Assert-UnderRepoRoot {
    param([Parameter(Mandatory = $true)][string] $Path)

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $normalizedRoot = $RepoRoot.TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
    $rootPrefix = $normalizedRoot + [System.IO.Path]::DirectorySeparatorChar

    if (-not $fullPath.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to write outside the repository root: $fullPath"
    }
}

function Add-Failure {
    param([Parameter(Mandatory = $true)][string] $Message)
    $Failures.Add($Message) | Out-Null
}

function Get-JsonXmlPropertyName {
    param([Parameter(Mandatory = $true)][System.Xml.Linq.XElement] $Element)

    $encodedName = $Element.Attribute([System.Xml.Linq.XName]::Get("item"))
    if ($null -ne $encodedName) {
        return $encodedName.Value
    }

    return $Element.Name.LocalName
}

function Assert-NoDuplicateJsonProperties {
    param(
        [Parameter(Mandatory = $true)][System.Xml.Linq.XElement] $Element,
        [Parameter(Mandatory = $true)][string] $JsonPath
    )

    $typeAttribute = $Element.Attribute([System.Xml.Linq.XName]::Get("type"))
    $elementType = if ($null -eq $typeAttribute) { $null } else { $typeAttribute.Value }
    if ($elementType -ceq "object") {
        $names = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
        foreach ($child in @($Element.Elements())) {
            $propertyName = Get-JsonXmlPropertyName $child
            if (-not $names.Add($propertyName)) {
                throw "Duplicate JSON property '$propertyName' at $JsonPath."
            }

            Assert-NoDuplicateJsonProperties -Element $child -JsonPath "$JsonPath.$propertyName"
        }

        return
    }

    if ($elementType -ceq "array") {
        $index = 0
        foreach ($child in @($Element.Elements())) {
            Assert-NoDuplicateJsonProperties -Element $child -JsonPath "$JsonPath[$index]"
            $index++
        }
    }
}

function Test-JsonObjectProperties {
    param(
        [Parameter(Mandatory = $true)][AllowNull()]$Value,
        [Parameter(Mandatory = $true)][string] $Description,
        [Parameter(Mandatory = $true)][string[]] $RequiredProperties,
        [string[]] $OptionalProperties = @()
    )

    if ($null -eq $Value) {
        Add-Failure "$Description must be a JSON object."
        return $false
    }

    $names = @($Value.PSObject.Properties | Where-Object { $_.MemberType -eq "NoteProperty" } | ForEach-Object { $_.Name })
    $allowed = @($RequiredProperties) + @($OptionalProperties)
    $missing = @($RequiredProperties | Where-Object { $names -cnotcontains $_ })
    $unexpected = @($names | Where-Object { $allowed -cnotcontains $_ })
    if ($missing.Count -gt 0) {
        Add-Failure "$Description is missing exact property name(s): $($missing -join ', ')."
    }

    if ($unexpected.Count -gt 0) {
        Add-Failure "$Description has unexpected or non-canonical property name(s): $($unexpected -join ', ')."
    }

    return $missing.Count -eq 0 -and $unexpected.Count -eq 0
}

function Test-JsonArrayValue {
    param(
        [AllowNull()]$Value,
        [Parameter(Mandatory = $true)][string] $Description
    )

    if ($Value -isnot [System.Array]) {
        Add-Failure "$Description must be a JSON array."
        return $false
    }

    return $true
}

function Test-RequiredJsonString {
    param(
        [AllowNull()]$Value,
        [Parameter(Mandatory = $true)][string] $Description
    )

    if ($Value -isnot [string] -or
        [string]::IsNullOrWhiteSpace($Value) -or
        $Value -cne $Value.Trim()) {
        Add-Failure "$Description must be a non-empty canonical string."
        return $false
    }

    return $true
}

function Test-ExactStringSet {
    param(
        [Parameter(Mandatory = $true)][string[]] $Actual,
        [Parameter(Mandatory = $true)][string[]] $Expected,
        [Parameter(Mandatory = $true)][string] $Description
    )

    $actualValues = @($Actual | Sort-Object)
    $expectedValues = @($Expected | Sort-Object)
    if (($actualValues -join "|") -cne ($expectedValues -join "|")) {
        Add-Failure "$Description were '$($actualValues -join ', ')', expected '$($expectedValues -join ', ')'."
    }
}

function Write-Utf8NoBom {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][string] $Content
    )

    $directory = Split-Path -Parent $Path
    if (-not [string]::IsNullOrWhiteSpace($directory) -and -not (Test-Path -LiteralPath $directory -PathType Container)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }

    [System.IO.File]::WriteAllText($Path, $Content, [System.Text.UTF8Encoding]::new($false))
}

function Test-RequiredDirectory {
    param([Parameter(Mandatory = $true)][string] $Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        Add-Failure "Missing required directory: $Path"
        return $false
    }

    return $true
}

function Test-RequiredFile {
    param([Parameter(Mandatory = $true)][string] $Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        Add-Failure "Missing required file: $Path"
        return $false
    }

    return $true
}

function Get-FileSha256 {
    param([Parameter(Mandatory = $true)][string] $Path)

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Test-JsonInteger {
    param(
        [AllowNull()] $Value,
        [long] $Minimum = 0
    )

    if ($null -eq $Value) {
        return $false
    }

    $integerTypes = @(
        [byte], [sbyte], [int16], [uint16], [int32], [uint32], [int64], [uint64])
    if ($integerTypes -notcontains $Value.GetType()) {
        return $false
    }

    return [decimal] $Value -ge [decimal] $Minimum
}

function Read-ProductionIntegrationTrx {
    param([Parameter(Mandatory = $true)][string] $Path)

    try {
        if ((Get-Item -LiteralPath $Path).Length -gt 67108864) {
            throw "TRX exceeds the 64 MiB verification limit."
        }

        $settings = [System.Xml.XmlReaderSettings]::new()
        $settings.DtdProcessing = [System.Xml.DtdProcessing]::Prohibit
        $settings.XmlResolver = $null
        $reader = [System.Xml.XmlReader]::Create($Path, $settings)
        try {
            $document = [System.Xml.XmlDocument]::new()
            $document.XmlResolver = $null
            $document.Load($reader)
        }
        finally {
            $reader.Dispose()
        }

        $counters = $document.SelectSingleNode(
            "/*[local-name()='TestRun']/*[local-name()='ResultSummary']/*[local-name()='Counters']")
        if ($null -eq $counters) {
            throw "TRX is missing ResultSummary/Counters."
        }

        $values = [ordered]@{}
        foreach ($mapping in @(
                [pscustomobject]@{ Trx = "total"; Evidence = "total" },
                [pscustomobject]@{ Trx = "executed"; Evidence = "executed" },
                [pscustomobject]@{ Trx = "passed"; Evidence = "passed" },
                [pscustomobject]@{ Trx = "failed"; Evidence = "failed" },
                [pscustomobject]@{ Trx = "notExecuted"; Evidence = "skipped" })) {
            $rawValue = $counters.GetAttribute($mapping.Trx)
            $parsed = 0
            if ([string]::IsNullOrWhiteSpace($rawValue) `
                -or $rawValue -cnotmatch '^(?:0|[1-9][0-9]*)$' `
                -or -not [int]::TryParse(
                    $rawValue,
                    [System.Globalization.NumberStyles]::None,
                    [System.Globalization.CultureInfo]::InvariantCulture,
                    [ref] $parsed)) {
                throw "TRX counter '$($mapping.Trx)' is not a canonical non-negative integer."
            }

            $values[$mapping.Evidence] = $parsed
        }

        if ($values.total -le 0 `
            -or $values.executed -ne $values.total `
            -or $values.passed -ne $values.total `
            -or $values.failed -ne 0 `
            -or $values.skipped -ne 0) {
            throw "TRX does not prove that every test passed with zero failed or skipped tests."
        }

        $results = @($document.SelectNodes(
                "/*[local-name()='TestRun']/*[local-name()='Results']/*[local-name()='UnitTestResult']"))
        if ($results.Count -ne $values.total `
            -or @($results | Where-Object { $_.GetAttribute("outcome") -cne "Passed" }).Count -ne 0) {
            throw "TRX result records do not match its all-passed counters."
        }

        $requiredResults = @($results | Where-Object {
                $_.GetAttribute("testName") -ceq $RequiredProductionIntegrationTest
            })
        if ($requiredResults.Count -ne 1) {
            throw "TRX must contain exactly one Passed result for '$RequiredProductionIntegrationTest'."
        }

        return [pscustomobject] $values
    }
    catch {
        Add-Failure "Production integration TRX is invalid: $($_.Exception.Message)"
        return $null
    }
}

function Read-JsonFile {
    param([Parameter(Mandatory = $true)][string] $Path)

    if (-not (Test-RequiredFile $Path)) {
        return $null
    }

    try {
        $rawJson = [System.IO.File]::ReadAllText($Path)
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($rawJson)
        $reader = [System.Runtime.Serialization.Json.JsonReaderWriterFactory]::CreateJsonReader(
            $bytes,
            [System.Xml.XmlDictionaryReaderQuotas]::Max)
        try {
            $document = [System.Xml.Linq.XDocument]::Load($reader)
        }
        finally {
            $reader.Dispose()
        }

        Assert-NoDuplicateJsonProperties -Element $document.Root -JsonPath '$'
        return $rawJson | ConvertFrom-Json
    }
    catch {
        Add-Failure "Invalid JSON file '$Path': $($_.Exception.Message)"
        return $null
    }
}

function Test-ArtifactKinds {
    param(
        [Parameter(Mandatory = $true)][string[]] $ActualKinds,
        [Parameter(Mandatory = $true)][string] $Description
    )

    $actual = @($ActualKinds | Sort-Object)
    $expected = @($ExpectedArtifactKinds | Sort-Object)
    if (($actual -join "|") -cne ($expected -join "|")) {
        Add-Failure "$Description artifact kinds were '$($actual -join ", ")', expected '$($expected -join ", ")'."
    }
}

function Test-GateStatus {
    param(
        [Parameter(Mandatory = $true)]$Evidence,
        [Parameter(Mandatory = $true)][string] $GateName,
        [Parameter(Mandatory = $true)][string] $ExpectedStatus
    )

    $gate = @($Evidence.gates | Where-Object { $_.name -ceq $GateName }) | Select-Object -First 1
    if ($null -eq $gate) {
        Add-Failure "Publication evidence is missing gate '$GateName'."
        return
    }

    if ($gate.status -cne $ExpectedStatus) {
        Add-Failure "Publication evidence gate '$GateName' had status '$($gate.status)', expected '$ExpectedStatus'."
    }
}

function Test-PreflightCase {
    param(
        [Parameter(Mandatory = $true)]$Preflight,
        [Parameter(Mandatory = $true)][string] $Name,
        [Parameter(Mandatory = $true)][string] $Expected
    )

    $case = @($Preflight.cases | Where-Object { $_.name -ceq $Name }) | Select-Object -First 1
    if ($null -eq $case) {
        Add-Failure "Final publication preflight is missing case '$Name'."
        return
    }

    if ($case.expected -cne $Expected) {
        Add-Failure "Final publication preflight case '$Name' expected '$($case.expected)', expected metadata '$Expected'."
    }

    if ($Expected -ceq "pass" -and $case.exitCode -ne 0) {
        Add-Failure "Final publication preflight case '$Name' should pass with exit code 0."
    }

    if ($Expected -ceq "fail" -and $case.exitCode -eq 0) {
        Add-Failure "Final publication preflight case '$Name' should fail with a non-zero exit code."
    }
}

function Test-ManifestDocument {
    param([Parameter(Mandatory = $true)]$Manifest)

    if (-not (Test-JsonObjectProperties `
        -Value $Manifest `
        -Description "Release manifest" `
        -RequiredProperties @("schemaVersion", "product", "version", "generatedAtUtc", "commit", "artifacts"))) {
        return
    }

    Test-RequiredJsonString -Value $Manifest.version -Description "Release manifest version" | Out-Null
    Test-RequiredJsonString -Value $Manifest.generatedAtUtc -Description "Release manifest generatedAtUtc" | Out-Null
    if (-not (Test-JsonArrayValue -Value $Manifest.artifacts -Description "Release manifest artifacts")) {
        return
    }

    $index = 0
    foreach ($artifact in @($Manifest.artifacts)) {
        $description = "Release manifest artifact[$index]"
        if (Test-JsonObjectProperties `
            -Value $artifact `
            -Description $description `
            -RequiredProperties @("relativePath", "fileName", "kind", "sizeBytes", "sha256")) {
            Test-RequiredJsonString -Value $artifact.relativePath -Description "$description relativePath" | Out-Null
            Test-RequiredJsonString -Value $artifact.fileName -Description "$description fileName" | Out-Null
            Test-RequiredJsonString -Value $artifact.kind -Description "$description kind" | Out-Null
            Test-RequiredJsonString -Value $artifact.sha256 -Description "$description sha256" | Out-Null
            if ($ExpectedArtifactKinds -cnotcontains $artifact.kind) {
                Add-Failure "$description kind '$($artifact.kind)' is not a canonical artifact kind."
            }
        }

        $index++
    }
}

function Test-DependencyInventoryDocument {
    param([Parameter(Mandatory = $true)]$Inventory)

    if (-not (Test-JsonObjectProperties `
        -Value $Inventory `
        -Description "Dependency inventory" `
        -RequiredProperties @(
            "schemaVersion", "product", "version", "generatedAtUtc", "packageCounts", "reviewPolicy", "packages"))) {
        return
    }

    Test-RequiredJsonString -Value $Inventory.version -Description "Dependency inventory version" | Out-Null
    Test-RequiredJsonString -Value $Inventory.generatedAtUtc -Description "Dependency inventory generatedAtUtc" | Out-Null
    Test-JsonObjectProperties `
        -Value $Inventory.packageCounts `
        -Description "Dependency inventory packageCounts" `
        -RequiredProperties @("total", "nuget", "npm", "uniqueLicenseValues") | Out-Null
    Test-JsonObjectProperties `
        -Value $Inventory.reviewPolicy `
        -Description "Dependency inventory reviewPolicy" `
        -RequiredProperties @("blockedLicensePatterns") | Out-Null
    Test-JsonArrayValue `
        -Value $Inventory.reviewPolicy.blockedLicensePatterns `
        -Description "Dependency inventory blockedLicensePatterns" | Out-Null
    if (-not (Test-JsonArrayValue -Value $Inventory.packages -Description "Dependency inventory packages")) {
        return
    }

    $identities = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    $index = 0
    foreach ($package in @($Inventory.packages)) {
        $description = "Dependency inventory package[$index]"
        if (Test-JsonObjectProperties `
            -Value $package `
            -Description $description `
            -RequiredProperties @("ecosystem", "name", "version", "license", "licenseSource")) {
            foreach ($field in @("ecosystem", "name", "version", "license", "licenseSource")) {
                Test-RequiredJsonString -Value $package.$field -Description "$description $field" | Out-Null
            }

            if (@("nuget", "npm") -cnotcontains $package.ecosystem) {
                Add-Failure "$description ecosystem '$($package.ecosystem)' must be exactly 'nuget' or 'npm'."
            }

            $identity = "$($package.ecosystem)`0$($package.name)`0$($package.version)"
            if (-not $identities.Add($identity)) {
                Add-Failure "$description duplicates package identity '$($package.ecosystem)/$($package.name)/$($package.version)'."
            }
        }

        $index++
    }
}

function Test-PublicationEvidenceDocument {
    param(
        [Parameter(Mandatory = $true)]$Evidence,
        [Parameter(Mandatory = $true)][string] $Description
    )

    if (-not (Test-JsonObjectProperties `
        -Value $Evidence `
        -Description $Description `
        -RequiredProperties @(
            "schemaVersion", "generatedAtUtc", "product", "publishable", "repoRoot", "artifactsRoot", "outputRoot",
            "license", "githubActions", "release", "e2eEvidence", "pendingExternal", "internalFailures", "gates"))) {
        return
    }

    Test-RequiredJsonString -Value $Evidence.generatedAtUtc -Description "$Description generatedAtUtc" | Out-Null
    Test-RequiredJsonString -Value $Evidence.repoRoot -Description "$Description repoRoot" | Out-Null
    Test-RequiredJsonString -Value $Evidence.artifactsRoot -Description "$Description artifactsRoot" | Out-Null
    Test-RequiredJsonString -Value $Evidence.outputRoot -Description "$Description outputRoot" | Out-Null
    Test-JsonObjectProperties `
        -Value $Evidence.license `
        -Description "$Description license" `
        -RequiredProperties @("fileLicense", "confirmedForPublication") | Out-Null
    $githubActionsShapeValid = Test-JsonObjectProperties `
        -Value $Evidence.githubActions `
        -Description "$Description githubActions" `
        -RequiredProperties @(
            "repository", "commitSha", "runId", "runUrl",
            "productionIntegrationConclusion", "proofSupplied")
    if ($githubActionsShapeValid) {
        if ($Evidence.githubActions.proofSupplied -isnot [bool]) {
            Add-Failure "$Description githubActions proofSupplied must be a JSON boolean."
        }
        elseif ($Evidence.githubActions.proofSupplied -eq $true `
            -and ($Evidence.githubActions.repository -isnot [string] `
                -or $Evidence.githubActions.repository -cnotmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$' `
                -or $Evidence.githubActions.commitSha -isnot [string] `
                -or $Evidence.githubActions.commitSha -cnotmatch '^[0-9a-f]{40}$' `
                -or $Evidence.githubActions.runId -isnot [string] `
                -or $Evidence.githubActions.runId -cnotmatch '^[1-9][0-9]*$' `
                -or $Evidence.githubActions.runUrl -isnot [string] `
                -or $Evidence.githubActions.runUrl -cne "https://github.com/$($Evidence.githubActions.repository)/actions/runs/$($Evidence.githubActions.runId)" `
                -or $Evidence.githubActions.productionIntegrationConclusion -isnot [string] `
                -or $Evidence.githubActions.productionIntegrationConclusion -cne "success")) {
            Add-Failure "$Description githubActions proof must be a canonical successful GitHub run binding."
        }
    }
    Test-JsonObjectProperties `
        -Value $Evidence.release `
        -Description "$Description release" `
        -RequiredProperties @(
            "product", "version", "manifestPath", "provenancePath", "provenanceGeneratedAtUtc", "artifactCount", "artifactKinds") | Out-Null
    Test-JsonArrayValue -Value $Evidence.release.artifactKinds -Description "$Description release artifactKinds" | Out-Null
    if (Test-JsonObjectProperties `
            -Value $Evidence.e2eEvidence `
            -Description "$Description e2eEvidence" `
            -RequiredProperties @(
                "stagedAgentBundle", "productionClosure", "studioTwoAgent",
                "studioTwoAgentManifest", "runnerStagedAgent", "runnerStagedAgentTrx",
                "productionIntegration", "productionIntegrationTrx", "recoveryComposition")) {
        foreach ($property in @(
                "stagedAgentBundle", "productionClosure", "studioTwoAgent",
                "studioTwoAgentManifest", "runnerStagedAgent", "runnerStagedAgentTrx")) {
            Test-JsonObjectProperties `
                -Value $Evidence.e2eEvidence.$property `
                -Description "$Description e2eEvidence.$property" `
                -RequiredProperties @(
                    "sourceRelativePath", "embeddedRelativePath", "sizeBytes", "sha256") | Out-Null
        }

        if ($Evidence.githubActions.proofSupplied -eq $true) {
            foreach ($property in @("productionIntegration", "productionIntegrationTrx")) {
                Test-JsonObjectProperties `
                    -Value $Evidence.e2eEvidence.$property `
                    -Description "$Description e2eEvidence.$property" `
                    -RequiredProperties @(
                        "sourceRelativePath", "embeddedRelativePath", "sizeBytes", "sha256") | Out-Null
            }
        }

        Test-JsonObjectProperties `
            -Value $Evidence.e2eEvidence.recoveryComposition `
            -Description "$Description e2eEvidence.recoveryComposition" `
            -RequiredProperties @(
                "stagedWindowsAgentBoundary",
                "durableCoordinatorRecoveryBoundary",
                "productionIntegrationWorkflowJob",
                "productionIntegrationTest",
                "proofRepository",
                "proofCommitSha",
                "proofRunId",
                "proofRunUrl",
                "productionIntegrationConclusion",
                "releaseManifestSha256",
                "proofSupplied") | Out-Null
        if ($Evidence.e2eEvidence.recoveryComposition.productionIntegrationWorkflowJob -cne "production-integration" `
            -or $Evidence.e2eEvidence.recoveryComposition.productionIntegrationTest -cne $RequiredProductionIntegrationTest `
            -or $Evidence.e2eEvidence.recoveryComposition.stagedWindowsAgentBoundary -cne $StagedWindowsAgentRecoveryBoundary `
            -or $Evidence.e2eEvidence.recoveryComposition.durableCoordinatorRecoveryBoundary -cne $DurableCoordinatorRecoveryBoundary `
            -or $Evidence.e2eEvidence.recoveryComposition.proofRepository -cne $Evidence.githubActions.repository `
            -or $Evidence.e2eEvidence.recoveryComposition.proofCommitSha -cne $Evidence.githubActions.commitSha `
            -or $Evidence.e2eEvidence.recoveryComposition.proofRunId -cne $Evidence.githubActions.runId `
            -or $Evidence.e2eEvidence.recoveryComposition.proofRunUrl -cne $Evidence.githubActions.runUrl `
            -or $Evidence.e2eEvidence.recoveryComposition.productionIntegrationConclusion -cne $Evidence.githubActions.productionIntegrationConclusion `
            -or $Evidence.e2eEvidence.recoveryComposition.proofSupplied -isnot [bool] `
            -or [bool] $Evidence.e2eEvidence.recoveryComposition.proofSupplied -ne [bool] $Evidence.githubActions.proofSupplied) {
            Add-Failure "$Description recovery composition does not identify the required PostgreSQL/RabbitMQ cold-restart gate."
        }
    }
    Test-JsonArrayValue -Value $Evidence.pendingExternal -Description "$Description pendingExternal" | Out-Null
    Test-JsonArrayValue -Value $Evidence.internalFailures -Description "$Description internalFailures" | Out-Null
    if (-not (Test-JsonArrayValue -Value $Evidence.gates -Description "$Description gates")) {
        return
    }

    if ($Evidence.product -cne "OpenLineOps") {
        Add-Failure "$Description product must be exactly 'OpenLineOps'."
    }

    if ($Evidence.license.fileLicense -cne "MIT") {
        Add-Failure "$Description license fileLicense must be exactly 'MIT'."
    }
    if ($Evidence.license.confirmedForPublication -isnot [bool]) {
        Add-Failure "$Description license confirmedForPublication must be a JSON boolean."
    }

    $expectedGateNames = @(
        "open-source metadata",
        "third-party license metadata",
        "release candidate inspection",
        "release candidate inspection behavior",
        "Windows package signing readiness",
        "publication metadata finalization behavior",
        "publication readiness with pending external allowed",
        "strict publication readiness",
        "signed release candidate inspection")
    $gateNames = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    $index = 0
    foreach ($gate in @($Evidence.gates)) {
        $gateDescription = "$Description gate[$index]"
        if (Test-JsonObjectProperties `
            -Value $gate `
            -Description $gateDescription `
            -RequiredProperties @("name", "status", "exitCode", "pendingAllowed", "command", "output")) {
            Test-RequiredJsonString -Value $gate.name -Description "$gateDescription name" | Out-Null
            Test-RequiredJsonString -Value $gate.status -Description "$gateDescription status" | Out-Null
            if (@("pass", "fail", "pending external") -cnotcontains $gate.status) {
                Add-Failure "$gateDescription status '$($gate.status)' is not canonical."
            }

            if (-not $gateNames.Add([string]$gate.name)) {
                Add-Failure "$Description contains duplicate gate name '$($gate.name)'."
            }
        }

        $index++
    }

    Test-ExactStringSet `
        -Actual @($Evidence.gates | ForEach-Object { $_.name }) `
        -Expected $expectedGateNames `
        -Description "$Description gate names"
}

function Test-PreflightDocument {
    param([Parameter(Mandatory = $true)]$Preflight)

    if (-not (Test-JsonObjectProperties `
        -Value $Preflight `
        -Description "Final publication preflight" `
        -RequiredProperties @("schemaVersion", "generatedAtUtc", "product", "workRoot", "cases"))) {
        return
    }

    Test-RequiredJsonString -Value $Preflight.generatedAtUtc -Description "Final publication preflight generatedAtUtc" | Out-Null
    Test-RequiredJsonString -Value $Preflight.workRoot -Description "Final publication preflight workRoot" | Out-Null
    if (-not (Test-JsonArrayValue -Value $Preflight.cases -Description "Final publication preflight cases")) {
        return
    }

    if ($Preflight.product -cne "OpenLineOps") {
        Add-Failure "Final publication preflight product must be exactly 'OpenLineOps'."
    }

    $caseNames = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    $index = 0
    foreach ($case in @($Preflight.cases)) {
        $description = "Final publication preflight case[$index]"
        if (Test-JsonObjectProperties `
            -Value $case `
            -Description $description `
            -RequiredProperties @("name", "exitCode", "expected", "output")) {
            Test-RequiredJsonString -Value $case.name -Description "$description name" | Out-Null
            Test-RequiredJsonString -Value $case.expected -Description "$description expected" | Out-Null
            if (@("pass", "fail") -cnotcontains $case.expected) {
                Add-Failure "$description expected '$($case.expected)' is not canonical."
            }

            if (-not $caseNames.Add([string]$case.name)) {
                Add-Failure "Final publication preflight contains duplicate case name '$($case.name)'."
            }
        }

        $index++
    }

    Test-ExactStringSet `
        -Actual @($Preflight.cases | ForEach-Object { $_.name }) `
        -Expected @("missing-license-confirmation", "missing-production-integration-evidence", "missing-signing-selector", "valid-plan") `
        -Description "Final publication preflight case names"
}

function Invoke-ReleaseCandidateInspection {
    param([Parameter(Mandatory = $true)][string] $ArtifactsRoot)

    $inspectionWorkRoot = Join-Path $ResolvedWorkRoot "work/release-candidate-inspection"
    $arguments = @(
        "-NoProfile",
        "-ExecutionPolicy",
        "Bypass",
        "-File",
        (Join-Path $PSScriptRoot "inspect-release-candidate.ps1"),
        "-ArtifactsRoot",
        $ArtifactsRoot,
        "-WorkRoot",
        $inspectionWorkRoot
    )

    if ($RequireSignedWindowsArtifacts -or $RequirePublishable) {
        $arguments += "-RequireSignedWindowsArtifacts"
    }

    $previousErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $output = & powershell @arguments 2>&1
        $exitCode = $LASTEXITCODE
        if ($null -eq $exitCode) {
            $exitCode = 0
        }
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }

    if ($exitCode -ne 0) {
        Add-Failure "Release candidate inspection failed with exit code $exitCode. Output: $(($output | Out-String).Trim())"
    }
}

function Remove-TemporaryInspectionWork {
    foreach ($relativePath in @("work", "release-candidate-inspection")) {
        $temporaryWorkRoot = Join-Path $ResolvedWorkRoot $relativePath
        if (Test-Path -LiteralPath $temporaryWorkRoot) {
            Remove-Item -LiteralPath $temporaryWorkRoot -Recurse -Force
        }
    }
}

function Write-InspectionReport {
    $status = if ($Failures.Count -eq 0) { "pass" } else { "fail" }
    $artifactKinds = @()
    $artifactCount = 0
    $releaseVersion = $null
    if ($null -ne $manifest) {
        $releaseVersion = $manifest.version
        $artifactCount = @($manifest.artifacts).Count
        $artifactKinds = @($manifest.artifacts | ForEach-Object { $_.kind } | Sort-Object -Unique)
    }

    $dependencyCounts = [ordered]@{
        total = $null
        nuget = $null
        npm = $null
        uniqueLicenseValues = $null
    }
    if ($null -ne $dependencyInventory) {
        $dependencyCounts.total = $dependencyInventory.packageCounts.total
        $dependencyCounts.nuget = $dependencyInventory.packageCounts.nuget
        $dependencyCounts.npm = $dependencyInventory.packageCounts.npm
        $dependencyCounts.uniqueLicenseValues = $dependencyInventory.packageCounts.uniqueLicenseValues
    }

    $metadataChecksumCount = 0
    if (Test-Path -LiteralPath $metadataChecksumsPath -PathType Leaf) {
        $metadataChecksumCount = @(
            Get-Content -LiteralPath $metadataChecksumsPath |
                Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
        ).Count
    }

    $preflightCases = @()
    if ($null -ne $preflight) {
        $preflightCases = @($preflight.cases | ForEach-Object {
            [ordered]@{
                name = $_.name
                expected = $_.expected
                exitCode = $_.exitCode
            }
        })
    }

    $report = [ordered]@{
        schemaVersion = 1
        generatedAtUtc = [System.DateTimeOffset]::UtcNow.ToString("O")
        product = "OpenLineOps"
        status = $status
        bundleRoot = $ResolvedBundleRoot
        workRoot = $ResolvedWorkRoot
        requirePublishable = [bool] $RequirePublishable
        requireSignedWindowsArtifacts = [bool] $RequireSignedWindowsArtifacts
        release = [ordered]@{
            version = $releaseVersion
            artifactCount = $artifactCount
            artifactKinds = $artifactKinds
            metadataChecksumCount = $metadataChecksumCount
            dependencyCounts = $dependencyCounts
        }
        publicationEvidence = [ordered]@{
            present = $null -ne $evidence
            publishable = if ($null -ne $evidence) { $evidence.publishable } else { $null }
            pendingExternalCount = if ($null -ne $evidence) { @($evidence.pendingExternal).Count } else { $null }
            internalFailureCount = if ($null -ne $evidence) { @($evidence.internalFailures).Count } else { $null }
        }
        finalPublicationPreflight = [ordered]@{
            present = $null -ne $preflight
            cases = $preflightCases
        }
        failures = @($Failures)
    }

    $jsonPath = Join-Path $ResolvedWorkRoot "ci-release-artifact-inspection.json"
    $markdownPath = Join-Path $ResolvedWorkRoot "ci-release-artifact-inspection.md"
    Write-Utf8NoBom -Path $jsonPath -Content (($report | ConvertTo-Json -Depth 10) + [Environment]::NewLine)

    $markdown = New-Object System.Collections.Generic.List[string]
    $markdown.Add("# CI Release Artifact Inspection") | Out-Null
    $markdown.Add("") | Out-Null
    $markdown.Add("- Generated at UTC: $($report.generatedAtUtc)") | Out-Null
    $markdown.Add("- Product: OpenLineOps") | Out-Null
    $markdown.Add("- Status: $status") | Out-Null
    $markdown.Add("- Bundle root: $ResolvedBundleRoot") | Out-Null
    $markdown.Add("- Release version: $releaseVersion") | Out-Null
    $markdown.Add("- Artifact kinds: $($artifactKinds -join ', ')") | Out-Null
    $markdown.Add("- Dependency packages: $($dependencyCounts.total)") | Out-Null
    $markdown.Add("- Metadata checksum entries: $metadataChecksumCount") | Out-Null
    $markdown.Add("- Publishable: $($report.publicationEvidence.publishable)") | Out-Null
    $markdown.Add("") | Out-Null
    $markdown.Add("## Failures") | Out-Null
    if ($Failures.Count -eq 0) {
        $markdown.Add("- None") | Out-Null
    }
    else {
        foreach ($failure in $Failures) {
            $markdown.Add("- $failure") | Out-Null
        }
    }

    $markdown.Add("") | Out-Null
    $markdown.Add("Detailed results are captured in ci-release-artifact-inspection.json.") | Out-Null
    Write-Utf8NoBom -Path $markdownPath -Content (($markdown -join [Environment]::NewLine) + [Environment]::NewLine)

    Write-Host "Inspection report: $jsonPath"
    Write-Host "Inspection summary: $markdownPath"
}

$ResolvedBundleRoot = Resolve-RepoPath $BundleRoot
$ResolvedWorkRoot = Resolve-RepoPath $WorkRoot
Assert-UnderRepoRoot $ResolvedWorkRoot
New-Item -ItemType Directory -Path $ResolvedWorkRoot -Force | Out-Null
Remove-TemporaryInspectionWork

if (-not (Test-Path -LiteralPath $ResolvedBundleRoot -PathType Container)) {
    throw "BundleRoot does not exist: $ResolvedBundleRoot"
}

$artifactsRoot = Join-Path $ResolvedBundleRoot "artifacts/release"
$publicationEvidenceRoot = Join-Path $ResolvedBundleRoot "output/publication-evidence"
$publicationEvidenceVerificationRoot = Join-Path $ResolvedBundleRoot "output/publication-evidence-verification"
$finalPublicationPreflightRoot = Join-Path $ResolvedBundleRoot "output/final-publication-preflight"
$stagedAgentEvidenceRoot = Join-Path $ResolvedBundleRoot "output/staged-agent-bundle-e2e"
$studioTwoAgentEvidenceRoot = Join-Path $ResolvedBundleRoot "output/studio-two-agent-production-closure"
$runnerStagedAgentEvidenceRoot = Join-Path $ResolvedBundleRoot "output/runner-staged-agent-e2e"
$productionIntegrationEvidenceRoot = Join-Path $ResolvedBundleRoot "output/production-integration-evidence"
$productionClosureEvidenceRoot = Join-Path $ResolvedBundleRoot "artifacts/production-closure-e2e"

foreach ($directory in @(
        $artifactsRoot,
        $publicationEvidenceRoot,
        $publicationEvidenceVerificationRoot,
        $finalPublicationPreflightRoot,
        $stagedAgentEvidenceRoot,
        $studioTwoAgentEvidenceRoot,
        $runnerStagedAgentEvidenceRoot,
        $productionIntegrationEvidenceRoot,
        $productionClosureEvidenceRoot)) {
    Test-RequiredDirectory $directory | Out-Null
}

$manifestPath = Join-Path $artifactsRoot "release-manifest.json"
$checksumsPath = Join-Path $artifactsRoot "checksums.sha256"
$dependencyInventoryPath = Join-Path $artifactsRoot "release-dependency-inventory.json"
$metadataChecksumsPath = Join-Path $artifactsRoot "release-metadata-checksums.sha256"
$provenancePath = Join-Path $artifactsRoot "release-provenance.json"
$releaseNotesPath = Join-Path $artifactsRoot "release-notes.md"
$evidencePath = Join-Path $publicationEvidenceRoot "publication-evidence.json"
$evidenceMarkdownPath = Join-Path $publicationEvidenceRoot "publication-evidence.md"
$preflightPath = Join-Path $finalPublicationPreflightRoot "publication-preflight.json"
$preflightMarkdownPath = Join-Path $finalPublicationPreflightRoot "publication-preflight.md"
$stagedAgentEvidencePath = Join-Path $stagedAgentEvidenceRoot "evidence.json"
$studioTwoAgentEvidencePath = Join-Path $studioTwoAgentEvidenceRoot "evidence.json"
$studioTwoAgentManifestPath = Join-Path $studioTwoAgentEvidenceRoot "evidence-manifest.json"
$runnerStagedAgentEvidencePath = Join-Path $runnerStagedAgentEvidenceRoot "evidence.json"
$runnerStagedAgentTrxPath = Join-Path $runnerStagedAgentEvidenceRoot "test-results/runner-staged-agent-e2e.trx"
$productionIntegrationEvidencePath = Join-Path $productionIntegrationEvidenceRoot "integration-evidence.json"
$productionIntegrationTrxPath = Join-Path $productionIntegrationEvidenceRoot "production-integration.trx"
$productionClosureSummaries = if (Test-Path -LiteralPath $productionClosureEvidenceRoot -PathType Container) {
    @(Get-ChildItem `
        -LiteralPath $productionClosureEvidenceRoot `
        -Filter "summary.json" `
        -File `
        -Recurse)
}
else {
    @()
}

foreach ($file in @(
        $manifestPath,
        $checksumsPath,
        $dependencyInventoryPath,
        $metadataChecksumsPath,
        $provenancePath,
        $releaseNotesPath,
        $evidenceMarkdownPath,
        $preflightMarkdownPath,
        $stagedAgentEvidencePath,
        $studioTwoAgentEvidencePath,
        $studioTwoAgentManifestPath,
        $runnerStagedAgentEvidencePath,
        $runnerStagedAgentTrxPath,
        $productionIntegrationEvidencePath,
        $productionIntegrationTrxPath)) {
    Test-RequiredFile $file | Out-Null
}
$releaseManifestSha256 = if (Test-Path -LiteralPath $manifestPath -PathType Leaf) {
    Get-FileSha256 $manifestPath
}
else {
    $null
}
if ($productionClosureSummaries.Count -ne 1) {
    Add-Failure "CI artifact bundle must contain exactly one packaged production closure summary; found $($productionClosureSummaries.Count)."
}
try {
    & (Join-Path $PSScriptRoot "verify-staged-agent-evidence.ps1") `
        -EvidenceRoot $stagedAgentEvidenceRoot `
        -RequireSanitizedRoot
}
catch {
    Add-Failure "Staged Agent evidence failed strict validation: $($_.Exception.Message)"
}
try {
    & (Join-Path $PSScriptRoot "verify-production-closure-evidence.ps1") `
        -EvidenceRoot $productionClosureEvidenceRoot `
        -RequirePassed
}
catch {
    Add-Failure "Production closure evidence failed strict validation: $($_.Exception.Message)"
}
try {
    & (Join-Path $PSScriptRoot "verify-studio-two-agent-production-evidence.ps1") `
        -EvidenceRoot $studioTwoAgentEvidenceRoot
}
catch {
    Add-Failure "Studio two-Agent evidence failed strict validation: $($_.Exception.Message)"
}
try {
    & (Join-Path $PSScriptRoot "verify-runner-staged-agent-evidence.ps1") `
        -EvidenceRoot $runnerStagedAgentEvidenceRoot `
        -RequirePassed
}
catch {
    Add-Failure "Runner staged-Agent evidence failed strict validation: $($_.Exception.Message)"
}
$stagedAgentE2e = Read-JsonFile $stagedAgentEvidencePath
if ($null -ne $stagedAgentE2e `
    -and ($stagedAgentE2e.status -cne "passed" `
        -or $stagedAgentE2e.rabbitMqTransportCoverage.status -cne "passed" `
        -or $stagedAgentE2e.rabbitMqTransportCoverage.coordinatorTransportResultInboxRestartedAfterBrokerRecovery -ne $true `
        -or $stagedAgentE2e.rabbitMqTransportCoverage.agentHostIdentity.nonAdministrative -ne $true `
        -or $stagedAgentE2e.rabbitMqTransportCoverage.agentHostIdentity.isPrimaryToken -ne $true `
        -or $stagedAgentE2e.rabbitMqTransportCoverage.agentHostIdentity.isElevated -ne $false `
        -or $stagedAgentE2e.rabbitMqTransportCoverage.agentHostIdentity.administratorGroupPresent -ne $false `
        -or $stagedAgentE2e.rabbitMqTransportCoverage.agentHostIdentity.administratorGroupEnabled -ne $false `
        -or $stagedAgentE2e.rabbitMqTransportCoverage.agentHostIdentity.administratorGroupDenyOnly -ne $false `
        -or $stagedAgentE2e.rabbitMqTransportCoverage.agentHostIdentity.principalAdministratorMembership -ne $false `
        -or $stagedAgentE2e.rabbitMqTransportCoverage.agentHostIdentity.isAuthenticated -ne $true `
        -or $stagedAgentE2e.rabbitMqTransportCoverage.agentHostIdentity.isSystem -ne $false `
        -or $stagedAgentE2e.rabbitMqTransportCoverage.agentHostIdentity.identityStrategy -cne "temporary-standard-service-account" `
        -or $stagedAgentE2e.rabbitMqTransportCoverage.restartedAgentHostIdentity.nonAdministrative -ne $true `
        -or $stagedAgentE2e.rabbitMqTransportCoverage.restartedAgentHostIdentity.isPrimaryToken -ne $true `
        -or $stagedAgentE2e.rabbitMqTransportCoverage.restartedAgentHostIdentity.isElevated -ne $false `
        -or $stagedAgentE2e.rabbitMqTransportCoverage.restartedAgentHostIdentity.administratorGroupPresent -ne $false `
        -or $stagedAgentE2e.rabbitMqTransportCoverage.restartedAgentHostIdentity.administratorGroupEnabled -ne $false `
        -or $stagedAgentE2e.rabbitMqTransportCoverage.restartedAgentHostIdentity.administratorGroupDenyOnly -ne $false `
        -or $stagedAgentE2e.rabbitMqTransportCoverage.restartedAgentHostIdentity.principalAdministratorMembership -ne $false `
        -or $stagedAgentE2e.rabbitMqTransportCoverage.restartedAgentHostIdentity.isAuthenticated -ne $true `
        -or $stagedAgentE2e.rabbitMqTransportCoverage.restartedAgentHostIdentity.isSystem -ne $false `
        -or $stagedAgentE2e.rabbitMqTransportCoverage.restartedAgentHostIdentity.identityStrategy -cne "temporary-standard-service-account" `
        -or [string]$stagedAgentE2e.rabbitMqTransportCoverage.windowsServiceName -cnotmatch '^OpenLineOpsAgentE2E-[0-9a-f]{32}$' `
        -or $stagedAgentE2e.rabbitMqTransportCoverage.windowsServiceLifecycleVerified -ne $true `
        -or $stagedAgentE2e.rabbitMqTransportCoverage.presence.startedAndHeartbeatPersisted -ne $true `
        -or $stagedAgentE2e.rabbitMqTransportCoverage.presence.expiredOfflineDuringBrokerOutage -ne $true `
        -or $stagedAgentE2e.rabbitMqTransportCoverage.presence.freshOnlineAfterReconnect -ne $true)) {
    Add-Failure "Staged Agent bundle evidence does not prove the passed RabbitMQ, Windows SCM service, and presence closure."
}
if ($productionClosureSummaries.Count -eq 1) {
    $productionClosureE2e = Read-JsonFile $productionClosureSummaries[0].FullName
    if ($null -ne $productionClosureE2e `
        -and ($productionClosureE2e.schema -cne "openlineops.production-closure-e2e" `
            -or $productionClosureE2e.status -cne "passed" `
            -or $productionClosureE2e.packagedBinaries.unchangedDuringRun -ne $true)) {
        Add-Failure "Packaged production closure evidence is not passed and immutable."
    }
}

$manifest = Read-JsonFile $manifestPath
$dependencyInventory = Read-JsonFile $dependencyInventoryPath
$provenance = Read-JsonFile $provenancePath
$productionIntegration = Read-JsonFile $productionIntegrationEvidencePath
$productionIntegrationTrx = if (Test-Path -LiteralPath $productionIntegrationTrxPath -PathType Leaf) {
    Read-ProductionIntegrationTrx -Path $productionIntegrationTrxPath
}
else {
    $null
}
$evidence = Read-JsonFile $evidencePath
$preflight = Read-JsonFile $preflightPath

if ($null -ne $manifest) {
    Test-ManifestDocument $manifest

    if ($manifest.schemaVersion -ne 1) {
        Add-Failure "Release manifest schemaVersion must be 1."
    }

    if ($manifest.product -cne "OpenLineOps") {
        Add-Failure "Release manifest product must be exactly 'OpenLineOps'."
    }

    Test-ArtifactKinds -ActualKinds @($manifest.artifacts | ForEach-Object { $_.kind }) -Description "Release manifest"
}

if ($null -ne $dependencyInventory) {
    Test-DependencyInventoryDocument $dependencyInventory

    if ($dependencyInventory.schemaVersion -ne 1) {
        Add-Failure "Dependency inventory schemaVersion must be 1."
    }

    if ($dependencyInventory.product -cne "OpenLineOps") {
        Add-Failure "Dependency inventory product must be exactly 'OpenLineOps'."
    }

    if ($null -ne $manifest -and $dependencyInventory.version -cne $manifest.version) {
        Add-Failure "Dependency inventory version '$($dependencyInventory.version)' does not match manifest version '$($manifest.version)'."
    }

    if ($dependencyInventory.packageCounts.total -ne @($dependencyInventory.packages).Count) {
        Add-Failure "Dependency inventory package count does not match package list count."
    }
}

if ($null -ne $productionIntegration) {
    $integrationShapeValid = Test-JsonObjectProperties `
        -Value $productionIntegration `
        -Description "Production integration evidence" `
        -RequiredProperties @(
            "schemaVersion", "generatedAtUtc", "product", "repository", "commitSha",
            "runId", "runUrl", "jobName", "testName", "conclusion", "counters", "trx")
    $integrationCountersValid = Test-JsonObjectProperties `
        -Value $productionIntegration.counters `
        -Description "Production integration evidence counters" `
        -RequiredProperties @("total", "executed", "passed", "failed", "skipped")
    $integrationTrxValid = Test-JsonObjectProperties `
        -Value $productionIntegration.trx `
        -Description "Production integration evidence TRX" `
        -RequiredProperties @("relativePath", "sizeBytes", "sha256")
    if ($integrationShapeValid -and $integrationCountersValid -and $integrationTrxValid) {
        if (-not (Test-JsonInteger -Value $productionIntegration.schemaVersion -Minimum 1) `
            -or $productionIntegration.schemaVersion -ne 1 `
            -or $productionIntegration.generatedAtUtc -isnot [string] `
            -or $productionIntegration.generatedAtUtc -cne $productionIntegration.generatedAtUtc.Trim() `
            -or $productionIntegration.product -isnot [string] `
            -or $productionIntegration.product -cne "OpenLineOps" `
            -or $productionIntegration.repository -isnot [string] `
            -or $productionIntegration.repository -cnotmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$' `
            -or $productionIntegration.commitSha -isnot [string] `
            -or $productionIntegration.commitSha -cnotmatch '^[0-9a-f]{40}$' `
            -or $productionIntegration.runId -isnot [string] `
            -or $productionIntegration.runId -cnotmatch '^[1-9][0-9]*$' `
            -or $productionIntegration.runUrl -isnot [string] `
            -or $productionIntegration.runUrl -cne "https://github.com/$($productionIntegration.repository)/actions/runs/$($productionIntegration.runId)" `
            -or $productionIntegration.jobName -isnot [string] `
            -or $productionIntegration.jobName -cne "production-integration" `
            -or $productionIntegration.testName -isnot [string] `
            -or $productionIntegration.testName -cne $RequiredProductionIntegrationTest `
            -or $productionIntegration.conclusion -isnot [string] `
            -or $productionIntegration.conclusion -cne "success" `
            -or -not (Test-JsonInteger -Value $productionIntegration.counters.total -Minimum 1) `
            -or -not (Test-JsonInteger -Value $productionIntegration.counters.executed) `
            -or -not (Test-JsonInteger -Value $productionIntegration.counters.passed) `
            -or -not (Test-JsonInteger -Value $productionIntegration.counters.failed) `
            -or -not (Test-JsonInteger -Value $productionIntegration.counters.skipped) `
            -or $productionIntegration.counters.executed -ne $productionIntegration.counters.total `
            -or $productionIntegration.counters.passed -ne $productionIntegration.counters.total `
            -or $productionIntegration.counters.failed -ne 0 `
            -or $productionIntegration.counters.skipped -ne 0 `
            -or $productionIntegration.trx.relativePath -isnot [string] `
            -or $productionIntegration.trx.relativePath -cne "output/production-integration-evidence/production-integration.trx" `
            -or -not (Test-JsonInteger -Value $productionIntegration.trx.sizeBytes -Minimum 1) `
            -or $productionIntegration.trx.sha256 -isnot [string] `
            -or $productionIntegration.trx.sha256 -cnotmatch '^[0-9a-f]{64}$' `
            -or -not (Test-Path -LiteralPath $productionIntegrationTrxPath -PathType Leaf) `
            -or $productionIntegration.trx.sizeBytes -ne (Get-Item -LiteralPath $productionIntegrationTrxPath).Length `
            -or $productionIntegration.trx.sha256 -cne (Get-FileSha256 $productionIntegrationTrxPath)) {
            Add-Failure "Production integration evidence is not a successful zero-skip same-run TRX proof."
        }

        if ($null -ne $productionIntegrationTrx) {
            foreach ($counterName in @("total", "executed", "passed", "failed", "skipped")) {
                if ($productionIntegrationTrx.$counterName -ne $productionIntegration.counters.$counterName) {
                    Add-Failure "Production integration TRX counter '$counterName' does not match its JSON evidence."
                }
            }
        }

        $githubContext = @(
            $env:GITHUB_REPOSITORY,
            $env:GITHUB_SHA,
            $env:GITHUB_RUN_ID,
            $env:GITHUB_SERVER_URL)
        if (@($githubContext | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }).Count -gt 0 `
            -and ($env:GITHUB_REPOSITORY -cne $productionIntegration.repository `
                -or $env:GITHUB_SHA -cne $productionIntegration.commitSha `
                -or $env:GITHUB_RUN_ID -cne $productionIntegration.runId `
                -or $env:GITHUB_SERVER_URL -cne "https://github.com")) {
            Add-Failure "Production integration evidence does not match the current GitHub Actions repository, commit, and run."
        }
    }
}

if ($null -ne $provenance) {
    $provenanceShapeValid = Test-JsonObjectProperties `
        -Value $provenance `
        -Description "Release provenance" `
        -RequiredProperties @(
            "schemaVersion", "product", "version", "generatedAtUtc", "source", "build", "tools", "release", "artifacts")
    $provenanceSourceValid = Test-JsonObjectProperties `
        -Value $provenance.source `
        -Description "Release provenance source" `
        -RequiredProperties @("available", "commit", "branch", "dirty")
    $provenanceReleaseValid = Test-JsonObjectProperties `
        -Value $provenance.release `
        -Description "Release provenance release" `
        -RequiredProperties @("manifest", "checksums", "notes", "dependencyInventory")
    $provenanceManifestValid = Test-JsonObjectProperties `
        -Value $provenance.release.manifest `
        -Description "Release provenance manifest" `
        -RequiredProperties @("path", "sha256")
    if ($provenanceShapeValid -and $provenanceSourceValid -and $provenanceReleaseValid -and $provenanceManifestValid `
        -and (-not (Test-JsonInteger -Value $provenance.schemaVersion -Minimum 1) `
            -or $provenance.schemaVersion -ne 1 `
            -or $provenance.product -isnot [string] `
            -or $provenance.product -cne "OpenLineOps" `
            -or $provenance.source.available -isnot [bool] `
            -or $provenance.source.available -ne $true `
            -or $provenance.source.dirty -isnot [bool] `
            -or $provenance.source.dirty -ne $false `
            -or $provenance.source.commit -isnot [string] `
            -or $provenance.source.commit -cnotmatch '^[0-9a-f]{40}$' `
            -or $null -eq $productionIntegration `
            -or $provenance.source.commit -cne $productionIntegration.commitSha `
            -or $provenance.release.manifest.path -cne "release-manifest.json" `
            -or $provenance.release.manifest.sha256 -isnot [string] `
            -or $provenance.release.manifest.sha256 -cne $releaseManifestSha256)) {
        Add-Failure "Release provenance must be clean and match the production integration commit and release manifest."
    }
}

Invoke-ReleaseCandidateInspection -ArtifactsRoot $artifactsRoot

if ($null -ne $evidence) {
    Test-PublicationEvidenceDocument -Evidence $evidence -Description "Publication evidence"

    if ($evidence.schemaVersion -ne 1) {
        Add-Failure "Publication evidence schemaVersion must be 1."
    }

    if ($evidence.product -cne "OpenLineOps") {
        Add-Failure "Publication evidence product must be exactly 'OpenLineOps'."
    }

    if (@($evidence.internalFailures).Count -ne 0) {
        Add-Failure "Publication evidence contains internal failures: $(@($evidence.internalFailures) -join "; ")"
    }

    if ($null -ne $manifest) {
        if ($evidence.release.version -cne $manifest.version) {
            Add-Failure "Publication evidence release version '$($evidence.release.version)' does not match manifest version '$($manifest.version)'."
        }

        if ($evidence.release.artifactCount -ne @($manifest.artifacts).Count) {
            Add-Failure "Publication evidence artifact count '$($evidence.release.artifactCount)' does not match manifest artifact count '$(@($manifest.artifacts).Count)'."
        }
    }

    Test-ArtifactKinds -ActualKinds @($evidence.release.artifactKinds) -Description "Publication evidence"
    Test-GateStatus -Evidence $evidence -GateName "release candidate inspection" -ExpectedStatus "pass"
    Test-GateStatus -Evidence $evidence -GateName "release candidate inspection behavior" -ExpectedStatus "pass"
    Test-GateStatus -Evidence $evidence -GateName "publication readiness with pending external allowed" -ExpectedStatus "pass"

    if ($evidence.githubActions.proofSupplied -ne $true `
        -or $null -eq $productionIntegration `
        -or $evidence.githubActions.repository -cne $productionIntegration.repository `
        -or $evidence.githubActions.commitSha -cne $productionIntegration.commitSha `
        -or $evidence.githubActions.runId -cne $productionIntegration.runId `
        -or $evidence.githubActions.runUrl -cne $productionIntegration.runUrl `
        -or $evidence.githubActions.productionIntegrationConclusion -cne "success" `
        -or $evidence.e2eEvidence.recoveryComposition.releaseManifestSha256 -cne $releaseManifestSha256) {
        Add-Failure "Publication evidence is not bound to the successful same-run production integration proof and release manifest."
    }

    $e2eSources = [ordered]@{
        stagedAgentBundle = [ordered]@{
            expectedSource = "output/staged-agent-bundle-e2e/evidence.json"
            sourcePath = $stagedAgentEvidencePath
            expectedEmbedded = "e2e-evidence/staged-agent-bundle.json"
        }
        productionClosure = if ($productionClosureSummaries.Count -eq 1) {
            [ordered]@{
                expectedSource = $productionClosureSummaries[0].FullName.Substring(
                    $ResolvedBundleRoot.TrimEnd('\', '/').Length + 1).Replace('\', '/')
                sourcePath = $productionClosureSummaries[0].FullName
                expectedEmbedded = "e2e-evidence/production-closure.json"
            }
        }
        else {
            $null
        }
        studioTwoAgent = [ordered]@{
            expectedSource = "output/studio-two-agent-production-closure/evidence.json"
            sourcePath = $studioTwoAgentEvidencePath
            expectedEmbedded = "e2e-evidence/studio-two-agent.json"
        }
        studioTwoAgentManifest = [ordered]@{
            expectedSource = "output/studio-two-agent-production-closure/evidence-manifest.json"
            sourcePath = $studioTwoAgentManifestPath
            expectedEmbedded = "e2e-evidence/studio-two-agent-manifest.json"
        }
        runnerStagedAgent = [ordered]@{
            expectedSource = "output/runner-staged-agent-e2e/evidence.json"
            sourcePath = $runnerStagedAgentEvidencePath
            expectedEmbedded = "e2e-evidence/runner-staged-agent.json"
        }
        runnerStagedAgentTrx = [ordered]@{
            expectedSource = "output/runner-staged-agent-e2e/test-results/runner-staged-agent-e2e.trx"
            sourcePath = $runnerStagedAgentTrxPath
            expectedEmbedded = "e2e-evidence/runner-staged-agent.trx"
        }
        productionIntegration = [ordered]@{
            expectedSource = "output/production-integration-evidence/integration-evidence.json"
            sourcePath = $productionIntegrationEvidencePath
            expectedEmbedded = "e2e-evidence/production-integration.json"
        }
        productionIntegrationTrx = [ordered]@{
            expectedSource = "output/production-integration-evidence/production-integration.trx"
            sourcePath = $productionIntegrationTrxPath
            expectedEmbedded = "e2e-evidence/production-integration.trx"
        }
    }
    foreach ($name in $e2eSources.Keys) {
        $source = $e2eSources[$name]
        $record = $evidence.e2eEvidence.$name
        if ($null -eq $source -or $null -eq $record) {
            Add-Failure "Publication evidence is missing required $name E2E evidence."
            continue
        }

        $embeddedPath = Join-Path `
            $publicationEvidenceRoot `
            $record.embeddedRelativePath.Replace('/', [System.IO.Path]::DirectorySeparatorChar)
        if ($record.sourceRelativePath -cne $source.expectedSource `
            -or $record.embeddedRelativePath -cne $source.expectedEmbedded `
            -or $record.sha256 -isnot [string] `
            -or $record.sha256 -cnotmatch '^[0-9a-f]{64}$' `
            -or -not (Test-JsonInteger -Value $record.sizeBytes -Minimum 1) `
            -or -not (Test-Path -LiteralPath $source.sourcePath -PathType Leaf) `
            -or -not (Test-Path -LiteralPath $embeddedPath -PathType Leaf) `
            -or (Get-Item -LiteralPath $source.sourcePath).Length -ne [long]$record.sizeBytes `
            -or (Get-Item -LiteralPath $embeddedPath).Length -ne [long]$record.sizeBytes `
            -or (Get-FileSha256 $source.sourcePath) -cne $record.sha256 `
            -or (Get-FileSha256 $embeddedPath) -cne $record.sha256) {
            Add-Failure "Publication $name E2E evidence source, embedded copy, size, or SHA-256 does not match."
        }
    }

    if ($RequirePublishable) {
        if ($evidence.publishable -ne $true) {
            Add-Failure "Publication evidence must be publishable when -RequirePublishable is supplied."
        }

        if (@($evidence.pendingExternal).Count -ne 0) {
            Add-Failure "Publication evidence still has pending external items: $(@($evidence.pendingExternal) -join "; ")"
        }

        if ($evidence.license.confirmedForPublication -ne $true) {
            Add-Failure "Publication evidence must record final MIT confirmation when -RequirePublishable is supplied."
        }

        if ($evidence.githubActions.proofSupplied -ne $true -or [string]::IsNullOrWhiteSpace($evidence.githubActions.runUrl)) {
            Add-Failure "Publication evidence must record GitHub-hosted CI proof when -RequirePublishable is supplied."
        }
    }
}

foreach ($case in (Get-PublicationEvidenceCaseContract)) {
    $caseRoot = Join-Path $publicationEvidenceVerificationRoot $case.RelativeDirectory
    Test-RequiredDirectory $caseRoot | Out-Null
    Test-RequiredFile (Join-Path $caseRoot "publication-evidence.json") | Out-Null
    Test-RequiredFile (Join-Path $caseRoot "publication-evidence.md") | Out-Null
}

$defaultVerificationRoot = Join-Path `
    $publicationEvidenceVerificationRoot `
    (Get-PublicationEvidenceCaseRelativeDirectory -Name "default")
$defaultVerification = Read-JsonFile (Join-Path $defaultVerificationRoot "publication-evidence.json")
if ($null -ne $defaultVerification) {
    Test-PublicationEvidenceDocument `
        -Evidence $defaultVerification `
        -Description "Default publication evidence verification"
    if (@($defaultVerification.internalFailures).Count -ne 0) {
        Add-Failure "Default publication evidence verification contains internal failures."
    }
}

$confirmedVerificationRoot = Join-Path `
    $publicationEvidenceVerificationRoot `
    (Get-PublicationEvidenceCaseRelativeDirectory -Name "confirmed-proof")
$confirmedVerification = Read-JsonFile (Join-Path $confirmedVerificationRoot "publication-evidence.json")
if ($null -ne $confirmedVerification) {
    Test-PublicationEvidenceDocument `
        -Evidence $confirmedVerification `
        -Description "Confirmed publication evidence verification"
    if ($confirmedVerification.license.confirmedForPublication -ne $true) {
        Add-Failure "Confirmed publication evidence verification must record final MIT confirmation."
    }

    if ($confirmedVerification.githubActions.proofSupplied -ne $true) {
        Add-Failure "Confirmed publication evidence verification must record GitHub Actions proof."
    }
}

$invalidProofVerificationRoot = Join-Path `
    $publicationEvidenceVerificationRoot `
    (Get-PublicationEvidenceCaseRelativeDirectory -Name "invalid-production-integration-evidence")
$invalidProofVerification = Read-JsonFile (Join-Path $invalidProofVerificationRoot "publication-evidence.json")
if ($null -ne $invalidProofVerification) {
    Test-PublicationEvidenceDocument `
        -Evidence $invalidProofVerification `
        -Description "Invalid production integration publication evidence verification"
    if (-not (@($invalidProofVerification.internalFailures) | Where-Object { $_ -cmatch "commit does not match a clean release provenance" })) {
        Add-Failure "Invalid production integration verification must record the expected commit-binding failure."
    }
}

$invalidTrxVerificationRoot = Join-Path `
    $publicationEvidenceVerificationRoot `
    (Get-PublicationEvidenceCaseRelativeDirectory -Name "invalid-production-integration-trx")
$invalidTrxVerification = Read-JsonFile (Join-Path $invalidTrxVerificationRoot "publication-evidence.json")
if ($null -ne $invalidTrxVerification) {
    Test-PublicationEvidenceDocument `
        -Evidence $invalidTrxVerification `
        -Description "Invalid production integration TRX publication evidence verification"
    if (-not (@($invalidTrxVerification.internalFailures) | Where-Object {
                $_ -cmatch "TRX result records do not match its all-passed counters"
            })) {
        Add-Failure "Invalid production integration TRX verification must record the expected semantic failure."
    }
}

$requirePublishableVerificationRoot = Join-Path `
    $publicationEvidenceVerificationRoot `
    (Get-PublicationEvidenceCaseRelativeDirectory -Name "require-publishable")
$requirePublishableVerification = Read-JsonFile (Join-Path $requirePublishableVerificationRoot "publication-evidence.json")
if ($null -ne $requirePublishableVerification) {
    Test-PublicationEvidenceDocument `
        -Evidence $requirePublishableVerification `
        -Description "Require-publishable publication evidence verification"
}

if ($null -ne $preflight) {
    Test-PreflightDocument $preflight

    if ($preflight.schemaVersion -ne 1) {
        Add-Failure "Final publication preflight schemaVersion must be 1."
    }

    if ($preflight.product -cne "OpenLineOps") {
        Add-Failure "Final publication preflight product must be exactly 'OpenLineOps'."
    }

    Test-PreflightCase -Preflight $preflight -Name "missing-license-confirmation" -Expected "fail"
    Test-PreflightCase -Preflight $preflight -Name "missing-production-integration-evidence" -Expected "fail"
    Test-PreflightCase -Preflight $preflight -Name "missing-signing-selector" -Expected "fail"
    Test-PreflightCase -Preflight $preflight -Name "valid-plan" -Expected "pass"
}

Write-InspectionReport
Remove-TemporaryInspectionWork

if ($Failures.Count -gt 0) {
    Write-Host "CI release artifact bundle inspection failed:" -ForegroundColor Red
    foreach ($failure in $Failures) {
        Write-Host " - $failure" -ForegroundColor Red
    }

    exit 1
}

Write-Host "CI release artifact bundle inspection passed."
Write-Host "Bundle: $ResolvedBundleRoot"
if ($RequirePublishable) {
    Write-Host "Publishable requirement: enforced"
}

exit 0

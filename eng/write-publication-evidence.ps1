param(
    [string] $ArtifactsRoot = "artifacts/release",

    [string] $OutputRoot = "output/publication-evidence",

    [string] $StagedAgentEvidenceRoot = "output/staged-agent-bundle-e2e",

    [string] $ProductionClosureEvidenceRoot = "artifacts/production-closure-e2e",

    [string] $StudioTwoAgentEvidenceRoot = "output/studio-two-agent-production-closure",

    [string] $RunnerStagedAgentEvidenceRoot = "output/runner-staged-agent-e2e",

    [string] $ProductionIntegrationEvidencePath,

    [switch] $ConfirmMitLicense,

    [switch] $RequirePublishable,

    [switch] $SkipClean
)

$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.Runtime.Serialization
Add-Type -AssemblyName System.Xml.Linq

$RepoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$InternalFailures = New-Object System.Collections.Generic.List[string]
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

function New-CleanDirectory {
    param([Parameter(Mandatory = $true)][string] $Path)

    Assert-UnderRepoRoot $Path
    if ((Test-Path -LiteralPath $Path) -and -not $SkipClean) {
        Remove-Item -LiteralPath $Path -Recurse -Force
    }

    New-Item -ItemType Directory -Path $Path -Force | Out-Null
}

function Write-Utf8NoBom {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][string] $Content
    )

    $directory = [System.IO.Path]::GetDirectoryName($Path)
    if (-not [string]::IsNullOrWhiteSpace($directory)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }

    [System.IO.File]::WriteAllText(
        $Path,
        $Content,
        [System.Text.UTF8Encoding]::new($false))
}

function Get-FileSha256 {
    param([Parameter(Mandatory = $true)][string] $Path)

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
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

function Read-StrictJsonFile {
    param([Parameter(Mandatory = $true)][string] $Path)

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

    if ((Get-Item -LiteralPath $Path).Length -gt 67108864) {
        throw "Production integration TRX exceeds the 64 MiB verification limit."
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
        throw "Production integration TRX is missing ResultSummary/Counters."
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
            throw "Production integration TRX counter '$($mapping.Trx)' is not a canonical non-negative integer."
        }

        $values[$mapping.Evidence] = $parsed
    }

    if ($values.total -le 0 `
        -or $values.executed -ne $values.total `
        -or $values.passed -ne $values.total `
        -or $values.failed -ne 0 `
        -or $values.skipped -ne 0) {
        throw "Production integration TRX does not prove that every test passed with zero failed or skipped tests."
    }

    $results = @($document.SelectNodes(
            "/*[local-name()='TestRun']/*[local-name()='Results']/*[local-name()='UnitTestResult']"))
    if ($results.Count -ne $values.total `
        -or @($results | Where-Object { $_.GetAttribute("outcome") -cne "Passed" }).Count -ne 0) {
        throw "Production integration TRX result records do not match its all-passed counters."
    }

    $requiredResults = @($results | Where-Object {
            $_.GetAttribute("testName") -ceq $RequiredProductionIntegrationTest
        })
    if ($requiredResults.Count -ne 1) {
        throw "Production integration TRX must contain exactly one Passed result for '$RequiredProductionIntegrationTest'."
    }

    return [pscustomobject] $values
}

function Test-ExactJsonProperties {
    param(
        [Parameter(Mandatory = $true)] $Value,
        [Parameter(Mandatory = $true)][string] $Description,
        [Parameter(Mandatory = $true)][string[]] $ExpectedProperties
    )

    if ($null -eq $Value -or $Value -isnot [pscustomobject]) {
        $InternalFailures.Add("$Description must be a JSON object.") | Out-Null
        return $false
    }

    $actual = @($Value.PSObject.Properties.Name | Sort-Object)
    $expected = @($ExpectedProperties | Sort-Object)
    $difference = @(Compare-Object -ReferenceObject $expected -DifferenceObject $actual -CaseSensitive)
    if ($difference.Count -ne 0) {
        $InternalFailures.Add("$Description has missing, unexpected, or non-canonical properties.") | Out-Null
        return $false
    }

    return $true
}

function Test-ExactTrueBooleanJsonObject {
    param(
        [Parameter(Mandatory = $true)][AllowNull()] $Value,
        [Parameter(Mandatory = $true)][string] $Description,
        [Parameter(Mandatory = $true)][string[]] $ExpectedProperties
    )

    if ($null -eq $Value) {
        $InternalFailures.Add("$Description must be a JSON object.") | Out-Null
        return $false
    }

    if (-not (Test-ExactJsonProperties `
            -Value $Value `
            -Description $Description `
            -ExpectedProperties $ExpectedProperties)) {
        return $false
    }

    $valid = $true
    foreach ($property in $ExpectedProperties) {
        if ($Value.$property -isnot [bool] -or $Value.$property -ne $true) {
            $InternalFailures.Add(
                "$Description property '$property' must be the JSON boolean true.") | Out-Null
            $valid = $false
        }
    }

    return $valid
}

function Test-ExpectedJsonBooleanProperties {
    param(
        [Parameter(Mandatory = $true)][AllowNull()] $Value,
        [Parameter(Mandatory = $true)][string] $Description,
        [Parameter(Mandatory = $true)][System.Collections.IDictionary] $Expected
    )

    if ($null -eq $Value) {
        $InternalFailures.Add("$Description must be a JSON object.") | Out-Null
        return $false
    }

    $valid = $true
    foreach ($property in $Expected.Keys) {
        if ($Value.$property -isnot [bool] `
            -or $Value.$property -ne $Expected[$property]) {
            $InternalFailures.Add(
                "$Description property '$property' must be the JSON boolean $($Expected[$property].ToString().ToLowerInvariant()).") | Out-Null
            $valid = $false
        }
    }

    return $valid
}

function Get-RepoRelativePath {
    param([Parameter(Mandatory = $true)][string] $Path)

    $resolved = [System.IO.Path]::GetFullPath($Path)
    $normalizedRoot = $RepoRoot.TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
    $prefix = $normalizedRoot + [System.IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith(
            $prefix,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "E2E evidence must remain under the repository root: $resolved"
    }

    return $resolved.Substring($prefix.Length).Replace('\', '/')
}

function Copy-E2eEvidence {
    param(
        [Parameter(Mandatory = $true)][string] $SourcePath,
        [Parameter(Mandatory = $true)][string] $EmbeddedName,
        [Parameter(Mandatory = $true)][string] $Description
    )

    if (-not (Test-Path -LiteralPath $SourcePath -PathType Leaf)) {
        $InternalFailures.Add("Missing required $Description evidence at $SourcePath.") | Out-Null
        return $null
    }

    $sourceFile = Get-Item -LiteralPath $SourcePath
    if ($sourceFile.Attributes.HasFlag([System.IO.FileAttributes]::ReparsePoint)) {
        $InternalFailures.Add("$Description evidence cannot be a reparse point.") | Out-Null
        return $null
    }

    $embeddedDirectory = Join-Path $resolvedOutputRoot "e2e-evidence"
    New-Item -ItemType Directory -Path $embeddedDirectory -Force | Out-Null
    $embeddedPath = Join-Path $embeddedDirectory $EmbeddedName
    Copy-Item -LiteralPath $SourcePath -Destination $embeddedPath -Force
    $sourceHash = Get-FileSha256 $SourcePath
    if ((Get-FileSha256 $embeddedPath) -cne $sourceHash) {
        $InternalFailures.Add("Embedded $Description evidence hash does not match its source.") | Out-Null
        return $null
    }

    return [ordered]@{
        sourceRelativePath = Get-RepoRelativePath $SourcePath
        embeddedRelativePath = "e2e-evidence/$EmbeddedName"
        sizeBytes = $sourceFile.Length
        sha256 = $sourceHash
    }
}

function ConvertTo-CommandLine {
    param([Parameter(Mandatory = $true)][string[]] $Command)

    $parts = @($Command | ForEach-Object {
        if ($_ -match "\s") {
            '"' + $_.Replace('"', '\"') + '"'
        }
        else {
            $_
        }
    })

    return ($parts -join " ")
}

function Invoke-EvidenceCommand {
    param(
        [Parameter(Mandatory = $true)][string] $Name,
        [Parameter(Mandatory = $true)][string[]] $Command,
        [switch] $Required,
        [switch] $PendingAllowed
    )

    $executable = $Command[0]
    $arguments = @()
    if ($Command.Count -gt 1) {
        $arguments = $Command[1..($Command.Count - 1)]
    }

    $output = & $executable @arguments 2>&1
    $exitCode = $LASTEXITCODE
    if ($null -eq $exitCode) {
        $exitCode = 0
    }

    $text = (($output | ForEach-Object { $_.ToString() }) -join [Environment]::NewLine).Trim()

    if ($Required -and $exitCode -ne 0) {
        $InternalFailures.Add("$Name failed with exit code $exitCode.") | Out-Null
    }

    return [pscustomobject]@{
        name = $Name
        command = ConvertTo-CommandLine $Command
        exitCode = $exitCode
        pendingAllowed = [bool] $PendingAllowed
        output = $text
    }
}

function Get-PendingExternalMessages {
    param([Parameter(Mandatory = $true)]$Gates)

    $messages = New-Object System.Collections.Generic.List[string]
    foreach ($gate in $Gates) {
        if ([string]::IsNullOrWhiteSpace($gate.output)) {
            continue
        }

        $pendingContinuation = $null
        foreach ($line in ($gate.output -split "\r?\n")) {
            if ($null -ne $pendingContinuation) {
                $trimmedContinuation = $line.Trim()
                if (-not [string]::IsNullOrWhiteSpace($trimmedContinuation) -and
                    $trimmedContinuation -notmatch "^(WARNING:|Publication readiness|Release manifest|Manifest:|Checksums:)") {
                    $messages.Add(($pendingContinuation + " " + $trimmedContinuation).Trim()) | Out-Null
                    $pendingContinuation = $null
                    continue
                }

                $messages.Add($pendingContinuation.Trim()) | Out-Null
                $pendingContinuation = $null
            }

            if ($line -match "PENDING EXTERNAL:\s*(.+)$") {
                $message = $Matches[1].Trim()
                if ($message -match "Status:\s*$") {
                    $pendingContinuation = $message
                }
                else {
                    $messages.Add($message) | Out-Null
                }
            }
        }

        if ($null -ne $pendingContinuation) {
            $messages.Add($pendingContinuation.Trim()) | Out-Null
        }
    }

    $normalized = @($messages | ForEach-Object { $_.Trim() } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    $hasSignedStatus = @($normalized | Where-Object {
        $_ -match "release executable '.+' is not signed with a valid Authenticode signature\. Status:\s*NotSigned\.?"
    }).Count -gt 0

    if ($hasSignedStatus) {
        $normalized = @($normalized | Where-Object {
            $_ -notmatch "release executable '.+' is not signed with a valid Authenticode signature\. Status:\s*$"
        })
    }

    return @($normalized | Select-Object -Unique)
}

function Get-GateStatus {
    param([Parameter(Mandatory = $true)]$Gate)

    if ($Gate.exitCode -eq 0) {
        return "pass"
    }

    if ($Gate.pendingAllowed) {
        return "pending external"
    }

    return "fail"
}

function Test-StrictReadinessFailure {
    param([Parameter(Mandatory = $true)]$Gate)

    if ($Gate.exitCode -eq 0) {
        return
    }

    $failureLines = @($Gate.output -split "\r?\n" | Where-Object { $_ -match "^\s*-\s+" })
    $nonPending = @($failureLines | Where-Object { $_ -notmatch "PENDING EXTERNAL:" })
    if ($failureLines.Count -eq 0 -or $nonPending.Count -gt 0) {
        $InternalFailures.Add("Strict publication readiness failed for a non-external reason.") | Out-Null
    }
}

function Test-SignedInspectionFailure {
    param([Parameter(Mandatory = $true)]$Gate)

    if ($Gate.exitCode -eq 0) {
        return
    }

    if ($Gate.output -notmatch "Status:\s*NotSigned") {
        $InternalFailures.Add("Signed release candidate inspection failed for a reason other than expected unsigned Windows executables.") | Out-Null
    }
}

$resolvedOutputRoot = Resolve-RepoPath $OutputRoot
New-CleanDirectory $resolvedOutputRoot
$resolvedStagedAgentEvidenceRoot = Resolve-RepoPath $StagedAgentEvidenceRoot
$resolvedProductionClosureEvidenceRoot = Resolve-RepoPath $ProductionClosureEvidenceRoot
$resolvedStudioTwoAgentEvidenceRoot = Resolve-RepoPath $StudioTwoAgentEvidenceRoot
$resolvedRunnerStagedAgentEvidenceRoot = Resolve-RepoPath $RunnerStagedAgentEvidenceRoot
$resolvedProductionIntegrationEvidencePath = if ([string]::IsNullOrWhiteSpace(
        $ProductionIntegrationEvidencePath)) {
    $null
}
else {
    Resolve-RepoPath $ProductionIntegrationEvidencePath
}
$childWorkRoot = Join-Path $resolvedOutputRoot "work"
$releaseCandidateInspectionWorkRoot = Join-Path $childWorkRoot "release-candidate-inspection"
$releaseCandidateInspectionVerificationWorkRoot = Join-Path $childWorkRoot "release-candidate-inspection-verification"
$windowsSigningReadinessWorkRoot = Join-Path $childWorkRoot "windows-signing-readiness"
$publicationMetadataFinalizationWorkRoot = Join-Path $childWorkRoot "publication-metadata-finalization"
$publicationReadinessAllowedWorkRoot = Join-Path $childWorkRoot "publication-readiness-allowed"
$publicationReadinessStrictWorkRoot = Join-Path $childWorkRoot "publication-readiness-strict"
$signedInspectionWorkRoot = Join-Path $childWorkRoot "signed-release-candidate-inspection"

$resolvedArtifactsRoot = Resolve-RepoPath $ArtifactsRoot
$manifestPath = Join-Path $resolvedArtifactsRoot "release-manifest.json"
$provenancePath = Join-Path $resolvedArtifactsRoot "release-provenance.json"

$stagedAgentEvidencePath = Join-Path $resolvedStagedAgentEvidenceRoot "evidence.json"
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
try {
    & (Join-Path $PSScriptRoot "verify-staged-agent-evidence.ps1") `
        -EvidenceRoot $resolvedStagedAgentEvidenceRoot `
        -RequireSanitizedRoot
}
catch {
    $InternalFailures.Add(
        "Staged Agent evidence failed strict validation: $($_.Exception.Message)") | Out-Null
}
$stagedAgentDocument = $null
if (Test-Path -LiteralPath $stagedAgentEvidencePath -PathType Leaf) {
    try {
        $stagedAgentDocument = Get-Content -LiteralPath $stagedAgentEvidencePath -Raw |
            ConvertFrom-Json
        $immutableContentCacheValid = Test-ExactTrueBooleanJsonObject `
            -Value $stagedAgentDocument.rabbitMqTransportCoverage.immutableContentCache `
            -Description "Staged Agent publication immutable content-cache evidence" `
            -ExpectedProperties $immutableContentCacheFields
        $stagedTransportBooleansValid = Test-ExpectedJsonBooleanProperties `
            -Value $stagedAgentDocument.rabbitMqTransportCoverage `
            -Description "Staged Agent publication transport evidence" `
            -Expected ([ordered]@{
                coordinatorTransportResultInboxRestartedAfterBrokerRecovery = $true
                windowsServiceLifecycleVerified = $true
            })
        $stagedIdentityExpectedBooleans = [ordered]@{
            nonAdministrative = $true
            isPrimaryToken = $true
            isElevated = $false
            hasRestrictions = $true
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
        }
        $stagedInitialIdentityBooleansValid = Test-ExpectedJsonBooleanProperties `
            -Value $stagedAgentDocument.rabbitMqTransportCoverage.agentHostIdentity `
            -Description "Staged Agent publication initial identity" `
            -Expected $stagedIdentityExpectedBooleans
        $stagedRestartedIdentityBooleansValid = Test-ExpectedJsonBooleanProperties `
            -Value $stagedAgentDocument.rabbitMqTransportCoverage.restartedAgentHostIdentity `
            -Description "Staged Agent publication restarted identity" `
            -Expected $stagedIdentityExpectedBooleans
        $stagedPresenceBooleansValid = Test-ExpectedJsonBooleanProperties `
            -Value $stagedAgentDocument.rabbitMqTransportCoverage.presence `
            -Description "Staged Agent publication presence" `
            -Expected ([ordered]@{
                startedAndHeartbeatPersisted = $true
                expiredOfflineDuringBrokerOutage = $true
                freshOnlineAfterReconnect = $true
            })
        if ($stagedAgentDocument.schemaVersion -ne 1 `
            -or $stagedAgentDocument.product -cne "OpenLineOps" `
            -or $stagedAgentDocument.status -cne "passed" `
            -or $stagedAgentDocument.rabbitMqTransportCoverage.status -cne "passed" `
            -or -not $immutableContentCacheValid `
            -or -not $stagedTransportBooleansValid `
            -or -not $stagedInitialIdentityBooleansValid `
            -or -not $stagedRestartedIdentityBooleansValid `
            -or -not $stagedPresenceBooleansValid `
            -or $stagedAgentDocument.rabbitMqTransportCoverage.coordinatorTransportResultInboxRestartedAfterBrokerRecovery -ne $true `
            -or $stagedAgentDocument.rabbitMqTransportCoverage.agentHostIdentity.nonAdministrative -ne $true `
            -or $stagedAgentDocument.rabbitMqTransportCoverage.agentHostIdentity.isPrimaryToken -ne $true `
            -or $stagedAgentDocument.rabbitMqTransportCoverage.agentHostIdentity.isElevated -ne $false `
            -or $stagedAgentDocument.rabbitMqTransportCoverage.agentHostIdentity.hasRestrictions -ne $true `
            -or $stagedAgentDocument.rabbitMqTransportCoverage.agentHostIdentity.administratorGroupPresent -ne $false `
            -or $stagedAgentDocument.rabbitMqTransportCoverage.agentHostIdentity.administratorGroupEnabled -ne $false `
            -or $stagedAgentDocument.rabbitMqTransportCoverage.agentHostIdentity.administratorGroupDenyOnly -ne $false `
            -or $stagedAgentDocument.rabbitMqTransportCoverage.agentHostIdentity.serviceLogonSidPresent -ne $true `
            -or $stagedAgentDocument.rabbitMqTransportCoverage.agentHostIdentity.serviceLogonSidEnabled -ne $true `
            -or $stagedAgentDocument.rabbitMqTransportCoverage.agentHostIdentity.exactServiceSidPresent -ne $true `
            -or $stagedAgentDocument.rabbitMqTransportCoverage.agentHostIdentity.exactServiceSidEnabled -ne $true `
            -or $stagedAgentDocument.rabbitMqTransportCoverage.agentHostIdentity.exactServiceSidRestricted -ne $true `
            -or $stagedAgentDocument.rabbitMqTransportCoverage.agentHostIdentity.isAuthenticated -ne $true `
            -or $stagedAgentDocument.rabbitMqTransportCoverage.agentHostIdentity.isSystem -ne $false `
            -or $stagedAgentDocument.rabbitMqTransportCoverage.agentHostIdentity.identityStrategy -cne "local-service-restricted-service-sid" `
            -or $stagedAgentDocument.rabbitMqTransportCoverage.agentHostIdentity.serviceAccountName -cne "NT AUTHORITY\LocalService" `
            -or $stagedAgentDocument.rabbitMqTransportCoverage.agentHostIdentity.serviceAccountSid -cne "S-1-5-19" `
            -or [string]$stagedAgentDocument.rabbitMqTransportCoverage.agentHostIdentity.serviceSid -cnotmatch '^S-1-5-80-(?:[0-9]+-){4}[0-9]+$' `
            -or $stagedAgentDocument.rabbitMqTransportCoverage.restartedAgentHostIdentity.nonAdministrative -ne $true `
            -or $stagedAgentDocument.rabbitMqTransportCoverage.restartedAgentHostIdentity.isPrimaryToken -ne $true `
            -or $stagedAgentDocument.rabbitMqTransportCoverage.restartedAgentHostIdentity.isElevated -ne $false `
            -or $stagedAgentDocument.rabbitMqTransportCoverage.restartedAgentHostIdentity.hasRestrictions -ne $true `
            -or $stagedAgentDocument.rabbitMqTransportCoverage.restartedAgentHostIdentity.administratorGroupPresent -ne $false `
            -or $stagedAgentDocument.rabbitMqTransportCoverage.restartedAgentHostIdentity.administratorGroupEnabled -ne $false `
            -or $stagedAgentDocument.rabbitMqTransportCoverage.restartedAgentHostIdentity.administratorGroupDenyOnly -ne $false `
            -or $stagedAgentDocument.rabbitMqTransportCoverage.restartedAgentHostIdentity.serviceLogonSidPresent -ne $true `
            -or $stagedAgentDocument.rabbitMqTransportCoverage.restartedAgentHostIdentity.serviceLogonSidEnabled -ne $true `
            -or $stagedAgentDocument.rabbitMqTransportCoverage.restartedAgentHostIdentity.exactServiceSidPresent -ne $true `
            -or $stagedAgentDocument.rabbitMqTransportCoverage.restartedAgentHostIdentity.exactServiceSidEnabled -ne $true `
            -or $stagedAgentDocument.rabbitMqTransportCoverage.restartedAgentHostIdentity.exactServiceSidRestricted -ne $true `
            -or $stagedAgentDocument.rabbitMqTransportCoverage.restartedAgentHostIdentity.isAuthenticated -ne $true `
            -or $stagedAgentDocument.rabbitMqTransportCoverage.restartedAgentHostIdentity.isSystem -ne $false `
            -or $stagedAgentDocument.rabbitMqTransportCoverage.restartedAgentHostIdentity.identityStrategy -cne "local-service-restricted-service-sid" `
            -or $stagedAgentDocument.rabbitMqTransportCoverage.restartedAgentHostIdentity.serviceAccountName -cne "NT AUTHORITY\LocalService" `
            -or $stagedAgentDocument.rabbitMqTransportCoverage.restartedAgentHostIdentity.serviceAccountSid -cne "S-1-5-19" `
            -or $stagedAgentDocument.rabbitMqTransportCoverage.restartedAgentHostIdentity.serviceSid -cne $stagedAgentDocument.rabbitMqTransportCoverage.agentHostIdentity.serviceSid `
            -or [string]$stagedAgentDocument.rabbitMqTransportCoverage.windowsServiceName -cnotmatch '^OpenLineOpsAgentE2E-[0-9a-f]{32}$' `
            -or $stagedAgentDocument.rabbitMqTransportCoverage.windowsServiceLifecycleVerified -ne $true `
            -or $stagedAgentDocument.rabbitMqTransportCoverage.presence.startedAndHeartbeatPersisted -ne $true `
            -or $stagedAgentDocument.rabbitMqTransportCoverage.presence.expiredOfflineDuringBrokerOutage -ne $true `
            -or $stagedAgentDocument.rabbitMqTransportCoverage.presence.freshOnlineAfterReconnect -ne $true) {
            $InternalFailures.Add("Staged Agent bundle E2E evidence is not a passed production transport and Windows SCM service closure.") | Out-Null
        }
    }
    catch {
        $InternalFailures.Add("Staged Agent bundle E2E evidence is invalid JSON: $($_.Exception.Message)") | Out-Null
    }
}
$stagedAgentEvidence = Copy-E2eEvidence `
    -SourcePath $stagedAgentEvidencePath `
    -EmbeddedName "staged-agent-bundle.json" `
    -Description "staged Agent bundle E2E"

$productionSummaries = if (Test-Path -LiteralPath $resolvedProductionClosureEvidenceRoot -PathType Container) {
    @(Get-ChildItem `
        -LiteralPath $resolvedProductionClosureEvidenceRoot `
        -Filter "summary.json" `
        -File `
        -Recurse)
}
else {
    @()
}
$productionClosureEvidence = $null
$productionClosureDocument = $null
try {
    & (Join-Path $PSScriptRoot "verify-production-closure-evidence.ps1") `
        -EvidenceRoot $resolvedProductionClosureEvidenceRoot `
        -RequirePassed
}
catch {
    $InternalFailures.Add(
        "Production closure evidence failed strict validation: $($_.Exception.Message)") | Out-Null
}
if ($productionSummaries.Count -ne 1) {
    $InternalFailures.Add(
        "Production closure evidence must contain exactly one summary.json; found $($productionSummaries.Count).") | Out-Null
}
else {
    try {
        $productionClosureDocument = Get-Content `
            -LiteralPath $productionSummaries[0].FullName `
            -Raw | ConvertFrom-Json
        $productionClosureBooleansValid = Test-ExpectedJsonBooleanProperties `
            -Value $productionClosureDocument.packagedBinaries `
            -Description "Packaged production closure binary evidence" `
            -Expected ([ordered]@{
                unchangedDuringRun = $true
            })
        if ($productionClosureDocument.schema -cne "openlineops.production-closure-e2e" `
            -or $productionClosureDocument.status -cne "passed" `
            -or -not $productionClosureBooleansValid `
            -or $productionClosureDocument.packagedBinaries.unchangedDuringRun -ne $true) {
            $InternalFailures.Add("Packaged production closure E2E evidence is not passed and immutable.") | Out-Null
        }
    }
    catch {
        $InternalFailures.Add("Packaged production closure E2E evidence is invalid JSON: $($_.Exception.Message)") | Out-Null
    }
    $productionClosureEvidence = Copy-E2eEvidence `
        -SourcePath $productionSummaries[0].FullName `
        -EmbeddedName "production-closure.json" `
        -Description "packaged production closure E2E"
}

$studioTwoAgentEvidencePath = Join-Path $resolvedStudioTwoAgentEvidenceRoot "evidence.json"
$studioTwoAgentManifestPath = Join-Path $resolvedStudioTwoAgentEvidenceRoot "evidence-manifest.json"
try {
    & (Join-Path $PSScriptRoot "verify-studio-two-agent-production-evidence.ps1") `
        -EvidenceRoot $resolvedStudioTwoAgentEvidenceRoot
}
catch {
    $InternalFailures.Add(
        "Studio two-Agent production evidence failed strict validation: $($_.Exception.Message)") | Out-Null
}
$studioTwoAgentEvidence = Copy-E2eEvidence `
    -SourcePath $studioTwoAgentEvidencePath `
    -EmbeddedName "studio-two-agent.json" `
    -Description "Studio two-Agent production closure"
$studioTwoAgentManifestEvidence = Copy-E2eEvidence `
    -SourcePath $studioTwoAgentManifestPath `
    -EmbeddedName "studio-two-agent-manifest.json" `
    -Description "Studio two-Agent evidence manifest"

$runnerStagedAgentEvidencePath = Join-Path $resolvedRunnerStagedAgentEvidenceRoot "evidence.json"
$runnerStagedAgentTrxPath = Join-Path `
    $resolvedRunnerStagedAgentEvidenceRoot `
    "test-results/runner-staged-agent-e2e.trx"
try {
    & (Join-Path $PSScriptRoot "verify-runner-staged-agent-evidence.ps1") `
        -EvidenceRoot $resolvedRunnerStagedAgentEvidenceRoot `
        -RequirePassed
}
catch {
    $InternalFailures.Add(
        "Runner staged-Agent production evidence failed strict validation: $($_.Exception.Message)") | Out-Null
}
$runnerStagedAgentEvidence = Copy-E2eEvidence `
    -SourcePath $runnerStagedAgentEvidencePath `
    -EmbeddedName "runner-staged-agent.json" `
    -Description "Runner staged-Agent production closure"
$runnerStagedAgentTrxEvidence = Copy-E2eEvidence `
    -SourcePath $runnerStagedAgentTrxPath `
    -EmbeddedName "runner-staged-agent.trx" `
    -Description "Runner staged-Agent exact-test TRX"

$gates = @()
$gates += Invoke-EvidenceCommand `
    -Name "open-source metadata" `
    -Command @("powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", (Resolve-RepoPath "eng/verify-open-source-metadata.ps1")) `
    -Required
$gates += Invoke-EvidenceCommand `
    -Name "third-party license metadata" `
    -Command @("powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", (Resolve-RepoPath "eng/verify-third-party-license-metadata.ps1")) `
    -Required
$gates += Invoke-EvidenceCommand `
    -Name "release candidate inspection" `
    -Command @("powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", (Resolve-RepoPath "eng/inspect-release-candidate.ps1"), "-ArtifactsRoot", $resolvedArtifactsRoot, "-WorkRoot", $releaseCandidateInspectionWorkRoot) `
    -Required
$gates += Invoke-EvidenceCommand `
    -Name "release candidate inspection behavior" `
    -Command @("powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", (Resolve-RepoPath "eng/verify-release-candidate-inspection.ps1"), "-WorkRoot", $releaseCandidateInspectionVerificationWorkRoot) `
    -Required
$gates += Invoke-EvidenceCommand `
    -Name "Windows package signing readiness" `
    -Command @("powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", (Resolve-RepoPath "eng/verify-windows-signing-readiness.ps1"), "-WorkRoot", $windowsSigningReadinessWorkRoot) `
    -Required
$gates += Invoke-EvidenceCommand `
    -Name "publication metadata finalization behavior" `
    -Command @("powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", (Resolve-RepoPath "eng/verify-publication-metadata-finalization.ps1"), "-WorkRoot", $publicationMetadataFinalizationWorkRoot) `
    -Required
$gates += Invoke-EvidenceCommand `
    -Name "publication readiness with pending external allowed" `
    -Command @("powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", (Resolve-RepoPath "eng/verify-publication-readiness.ps1"), "-ArtifactsRoot", $resolvedArtifactsRoot, "-WorkRoot", $publicationReadinessAllowedWorkRoot, "-AllowPendingExternal") `
    -Required
$strictReadiness = Invoke-EvidenceCommand `
    -Name "strict publication readiness" `
    -Command @("powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", (Resolve-RepoPath "eng/verify-publication-readiness.ps1"), "-ArtifactsRoot", $resolvedArtifactsRoot, "-WorkRoot", $publicationReadinessStrictWorkRoot) `
    -PendingAllowed
$gates += $strictReadiness
$signedInspection = Invoke-EvidenceCommand `
    -Name "signed release candidate inspection" `
    -Command @("powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", (Resolve-RepoPath "eng/inspect-release-candidate.ps1"), "-ArtifactsRoot", $resolvedArtifactsRoot, "-WorkRoot", $signedInspectionWorkRoot, "-RequireSignedWindowsArtifacts") `
    -PendingAllowed
$gates += $signedInspection

Test-StrictReadinessFailure $strictReadiness
Test-SignedInspectionFailure $signedInspection

$pendingExternal = New-Object System.Collections.Generic.List[string]
foreach ($message in (Get-PendingExternalMessages $gates)) {
    $pendingExternal.Add($message) | Out-Null
}

if (-not $ConfirmMitLicense) {
    $pendingExternal.Add("Final MIT license decision has not been explicitly confirmed for publication.") | Out-Null
}

$pendingExternal = @($pendingExternal | Select-Object -Unique)

$manifest = $null
if (Test-Path -LiteralPath $manifestPath -PathType Leaf) {
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
}
else {
    $InternalFailures.Add("Missing release manifest at $manifestPath.") | Out-Null
}

$provenance = $null
if (Test-Path -LiteralPath $provenancePath -PathType Leaf) {
    $provenance = Get-Content -LiteralPath $provenancePath -Raw | ConvertFrom-Json
}
else {
    $InternalFailures.Add("Missing release provenance at $provenancePath.") | Out-Null
}

$productionIntegrationDocument = $null
$productionIntegrationEvidence = $null
$productionIntegrationTrxEvidence = $null
$productionIntegrationProofValid = $false
if ($null -eq $resolvedProductionIntegrationEvidencePath) {
    $pendingExternal += "Bound PostgreSQL/RabbitMQ production integration evidence has not been supplied."
}
elseif (-not (Test-Path -LiteralPath $resolvedProductionIntegrationEvidencePath -PathType Leaf)) {
    $InternalFailures.Add(
        "Production integration evidence does not exist: $resolvedProductionIntegrationEvidencePath") | Out-Null
}
else {
    $proofFailureCount = $InternalFailures.Count
    try {
        $productionIntegrationDocument = Read-StrictJsonFile `
            -Path $resolvedProductionIntegrationEvidencePath
        $rootValid = Test-ExactJsonProperties `
            -Value $productionIntegrationDocument `
            -Description "Production integration evidence" `
            -ExpectedProperties @(
                "schemaVersion", "generatedAtUtc", "product", "repository", "commitSha",
                "runId", "runUrl", "jobName", "testName", "conclusion", "counters", "trx")
        $countersValid = Test-ExactJsonProperties `
            -Value $productionIntegrationDocument.counters `
            -Description "Production integration evidence counters" `
            -ExpectedProperties @("total", "executed", "passed", "failed", "skipped")
        $trxValid = Test-ExactJsonProperties `
            -Value $productionIntegrationDocument.trx `
            -Description "Production integration evidence TRX" `
            -ExpectedProperties @("relativePath", "sizeBytes", "sha256")
        if ($rootValid -and $countersValid -and $trxValid) {
            $expectedRunUrl = "https://github.com/$($productionIntegrationDocument.repository)/actions/runs/$($productionIntegrationDocument.runId)"
            if (-not (Test-JsonInteger -Value $productionIntegrationDocument.schemaVersion -Minimum 1) `
                -or $productionIntegrationDocument.schemaVersion -ne 1 `
                -or $productionIntegrationDocument.generatedAtUtc -isnot [string] `
                -or $productionIntegrationDocument.generatedAtUtc -cne $productionIntegrationDocument.generatedAtUtc.Trim() `
                -or $productionIntegrationDocument.product -isnot [string] `
                -or $productionIntegrationDocument.product -cne "OpenLineOps" `
                -or $productionIntegrationDocument.repository -isnot [string] `
                -or $productionIntegrationDocument.repository -cnotmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$' `
                -or $productionIntegrationDocument.commitSha -isnot [string] `
                -or $productionIntegrationDocument.commitSha -cnotmatch '^[0-9a-f]{40}$' `
                -or $productionIntegrationDocument.runId -isnot [string] `
                -or $productionIntegrationDocument.runId -cnotmatch '^[1-9][0-9]*$' `
                -or $productionIntegrationDocument.runUrl -isnot [string] `
                -or $productionIntegrationDocument.runUrl -cne $expectedRunUrl `
                -or $productionIntegrationDocument.jobName -isnot [string] `
                -or $productionIntegrationDocument.jobName -cne "production-integration" `
                -or $productionIntegrationDocument.testName -isnot [string] `
                -or $productionIntegrationDocument.testName -cne $RequiredProductionIntegrationTest `
                -or $productionIntegrationDocument.conclusion -isnot [string] `
                -or $productionIntegrationDocument.conclusion -cne "success" `
                -or -not (Test-JsonInteger -Value $productionIntegrationDocument.counters.total -Minimum 1) `
                -or -not (Test-JsonInteger -Value $productionIntegrationDocument.counters.executed) `
                -or -not (Test-JsonInteger -Value $productionIntegrationDocument.counters.passed) `
                -or -not (Test-JsonInteger -Value $productionIntegrationDocument.counters.failed) `
                -or -not (Test-JsonInteger -Value $productionIntegrationDocument.counters.skipped) `
                -or $productionIntegrationDocument.counters.executed -ne $productionIntegrationDocument.counters.total `
                -or $productionIntegrationDocument.counters.passed -ne $productionIntegrationDocument.counters.total `
                -or $productionIntegrationDocument.counters.failed -ne 0 `
                -or $productionIntegrationDocument.counters.skipped -ne 0 `
                -or $productionIntegrationDocument.trx.relativePath -isnot [string] `
                -or $productionIntegrationDocument.trx.relativePath -cnotmatch '^(?!/)(?!.*\\)(?!.*(?:^|/)\.\.(?:/|$)).+/production-integration\.trx$' `
                -or -not (Test-JsonInteger -Value $productionIntegrationDocument.trx.sizeBytes -Minimum 1) `
                -or $productionIntegrationDocument.trx.sha256 -isnot [string] `
                -or $productionIntegrationDocument.trx.sha256 -cnotmatch '^[0-9a-f]{64}$') {
                $InternalFailures.Add(
                    "Production integration evidence does not satisfy the strict successful same-run contract.") | Out-Null
            }

            if ($null -eq $provenance `
                -or $provenance.source.available -isnot [bool] `
                -or $provenance.source.available -ne $true `
                -or $provenance.source.dirty -isnot [bool] `
                -or $provenance.source.dirty -ne $false `
                -or $provenance.source.commit -isnot [string] `
                -or $provenance.source.commit -cne $productionIntegrationDocument.commitSha) {
                $InternalFailures.Add(
                    "Production integration evidence commit does not match a clean release provenance source.") | Out-Null
            }

            $githubContext = @(
                $env:GITHUB_REPOSITORY,
                $env:GITHUB_SHA,
                $env:GITHUB_RUN_ID,
                $env:GITHUB_SERVER_URL)
            if (@($githubContext | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }).Count -gt 0 `
                -and ($env:GITHUB_REPOSITORY -cne $productionIntegrationDocument.repository `
                    -or $env:GITHUB_SHA -cne $productionIntegrationDocument.commitSha `
                    -or $env:GITHUB_RUN_ID -cne $productionIntegrationDocument.runId `
                    -or $env:GITHUB_SERVER_URL -cne "https://github.com")) {
                $InternalFailures.Add(
                    "Production integration evidence does not match the current GitHub Actions repository, commit, and run.") | Out-Null
            }

            $resolvedTrxPath = Resolve-RepoPath $productionIntegrationDocument.trx.relativePath
            if ([System.IO.Path]::GetDirectoryName($resolvedTrxPath) -cne `
                    [System.IO.Path]::GetDirectoryName($resolvedProductionIntegrationEvidencePath) `
                -or -not (Test-Path -LiteralPath $resolvedTrxPath -PathType Leaf) `
                -or (Get-Item -LiteralPath $resolvedTrxPath).Length -ne `
                    [long] $productionIntegrationDocument.trx.sizeBytes `
                -or (Get-FileSha256 $resolvedTrxPath) -cne `
                    $productionIntegrationDocument.trx.sha256) {
                $InternalFailures.Add(
                    "Production integration TRX path, size, or SHA-256 does not match its evidence.") | Out-Null
            }
            else {
                $trxSummary = Read-ProductionIntegrationTrx -Path $resolvedTrxPath
                foreach ($counterName in @("total", "executed", "passed", "failed", "skipped")) {
                    if ($trxSummary.$counterName -ne $productionIntegrationDocument.counters.$counterName) {
                        $InternalFailures.Add(
                            "Production integration TRX counter '$counterName' does not match its JSON evidence.") | Out-Null
                    }
                }

                $productionIntegrationTrxEvidence = Copy-E2eEvidence `
                    -SourcePath $resolvedTrxPath `
                    -EmbeddedName "production-integration.trx" `
                    -Description "production integration TRX"
            }
        }
    }
    catch {
        $InternalFailures.Add(
            "Production integration evidence is invalid JSON or data: $($_.Exception.Message)") | Out-Null
    }

    $productionIntegrationEvidence = Copy-E2eEvidence `
        -SourcePath $resolvedProductionIntegrationEvidencePath `
        -EmbeddedName "production-integration.json" `
        -Description "production integration"
    $productionIntegrationProofValid = `
        $InternalFailures.Count -eq $proofFailureCount `
        -and $null -ne $productionIntegrationEvidence `
        -and $null -ne $productionIntegrationTrxEvidence
}

$pendingExternal = @($pendingExternal | Select-Object -Unique)

$artifactKinds = @()
$artifactCount = 0
if ($null -ne $manifest) {
    $artifactCount = @($manifest.artifacts).Count
    $artifactKinds = @($manifest.artifacts | ForEach-Object { $_.kind } | Select-Object -Unique | Sort-Object)
}

$publishable = ($InternalFailures.Count -eq 0 -and $pendingExternal.Count -eq 0 -and $strictReadiness.exitCode -eq 0 -and $signedInspection.exitCode -eq 0)

$gateSummaries = @($gates | ForEach-Object {
    [ordered]@{
        name = $_.name
        status = Get-GateStatus $_
        exitCode = $_.exitCode
        pendingAllowed = $_.pendingAllowed
        command = $_.command
        output = $_.output
    }
})

$evidence = [ordered]@{
    schemaVersion = 1
    generatedAtUtc = [System.DateTimeOffset]::UtcNow.ToString("O")
    product = "OpenLineOps"
    publishable = $publishable
    repoRoot = $RepoRoot
    artifactsRoot = $resolvedArtifactsRoot
    outputRoot = $resolvedOutputRoot
    license = [ordered]@{
        fileLicense = "MIT"
        confirmedForPublication = [bool] $ConfirmMitLicense
    }
    githubActions = [ordered]@{
        repository = if ($null -ne $productionIntegrationDocument) { $productionIntegrationDocument.repository } else { $null }
        commitSha = if ($null -ne $productionIntegrationDocument) { $productionIntegrationDocument.commitSha } else { $null }
        runId = if ($null -ne $productionIntegrationDocument) { $productionIntegrationDocument.runId } else { $null }
        runUrl = if ($null -ne $productionIntegrationDocument) { $productionIntegrationDocument.runUrl } else { $null }
        productionIntegrationConclusion = if ($null -ne $productionIntegrationDocument) { $productionIntegrationDocument.conclusion } else { $null }
        proofSupplied = $productionIntegrationProofValid
    }
    release = [ordered]@{
        product = if ($null -ne $manifest) { $manifest.product } else { $null }
        version = if ($null -ne $manifest) { $manifest.version } else { $null }
        manifestPath = $manifestPath
        provenancePath = $provenancePath
        provenanceGeneratedAtUtc = if ($null -ne $provenance) { $provenance.generatedAtUtc } else { $null }
        artifactCount = $artifactCount
        artifactKinds = $artifactKinds
    }
    e2eEvidence = [ordered]@{
        stagedAgentBundle = $stagedAgentEvidence
        productionClosure = $productionClosureEvidence
        studioTwoAgent = $studioTwoAgentEvidence
        studioTwoAgentManifest = $studioTwoAgentManifestEvidence
        runnerStagedAgent = $runnerStagedAgentEvidence
        runnerStagedAgentTrx = $runnerStagedAgentTrxEvidence
        productionIntegration = $productionIntegrationEvidence
        productionIntegrationTrx = $productionIntegrationTrxEvidence
        recoveryComposition = [ordered]@{
            stagedWindowsAgentBoundary = $StagedWindowsAgentRecoveryBoundary
            durableCoordinatorRecoveryBoundary = $DurableCoordinatorRecoveryBoundary
            productionIntegrationWorkflowJob = "production-integration"
            productionIntegrationTest = $RequiredProductionIntegrationTest
            proofRepository = if ($null -ne $productionIntegrationDocument) { $productionIntegrationDocument.repository } else { $null }
            proofCommitSha = if ($null -ne $productionIntegrationDocument) { $productionIntegrationDocument.commitSha } else { $null }
            proofRunId = if ($null -ne $productionIntegrationDocument) { $productionIntegrationDocument.runId } else { $null }
            proofRunUrl = if ($null -ne $productionIntegrationDocument) { $productionIntegrationDocument.runUrl } else { $null }
            productionIntegrationConclusion = if ($null -ne $productionIntegrationDocument) { $productionIntegrationDocument.conclusion } else { $null }
            releaseManifestSha256 = if (Test-Path -LiteralPath $manifestPath -PathType Leaf) { Get-FileSha256 $manifestPath } else { $null }
            proofSupplied = $productionIntegrationProofValid
        }
    }
    pendingExternal = $pendingExternal
    internalFailures = @($InternalFailures)
    gates = $gateSummaries
}

$jsonPath = Join-Path $resolvedOutputRoot "publication-evidence.json"
$markdownPath = Join-Path $resolvedOutputRoot "publication-evidence.md"
Write-Utf8NoBom -Path $jsonPath -Content (($evidence | ConvertTo-Json -Depth 12) + [Environment]::NewLine)

$markdown = New-Object System.Collections.Generic.List[string]
$markdown.Add("# Publication Evidence") | Out-Null
$markdown.Add("") | Out-Null
$markdown.Add("- Generated at UTC: $($evidence.generatedAtUtc)") | Out-Null
$markdown.Add("- Product: OpenLineOps") | Out-Null
$markdown.Add("- Publishable: $publishable") | Out-Null
$markdown.Add("- Release version: $($evidence.release.version)") | Out-Null
$markdown.Add("- Artifact kinds: $($artifactKinds -join ', ')") | Out-Null
$markdown.Add("- Staged Agent E2E SHA-256: $($stagedAgentEvidence.sha256)") | Out-Null
$markdown.Add("- Production closure SHA-256: $($productionClosureEvidence.sha256)") | Out-Null
$markdown.Add("- Studio two-Agent closure SHA-256: $($studioTwoAgentEvidence.sha256)") | Out-Null
$markdown.Add("- Runner staged-Agent closure SHA-256: $($runnerStagedAgentEvidence.sha256)") | Out-Null
$markdown.Add("- Durable Coordinator recovery CI gate: $($evidence.e2eEvidence.recoveryComposition.productionIntegrationTest)") | Out-Null
$markdown.Add("- Bound CI run: $($evidence.githubActions.runUrl)") | Out-Null
$markdown.Add("- Bound CI commit: $($evidence.githubActions.commitSha)") | Out-Null
$markdown.Add("") | Out-Null
$markdown.Add("## Pending External Items") | Out-Null
if ($pendingExternal.Count -eq 0) {
    $markdown.Add("- None") | Out-Null
}
else {
    foreach ($item in $pendingExternal) {
        $markdown.Add("- $item") | Out-Null
    }
}

$markdown.Add("") | Out-Null
$markdown.Add("## Internal Failures") | Out-Null
if ($InternalFailures.Count -eq 0) {
    $markdown.Add("- None") | Out-Null
}
else {
    foreach ($failure in $InternalFailures) {
        $markdown.Add("- $failure") | Out-Null
    }
}

$markdown.Add("") | Out-Null
$markdown.Add("## Gate Results") | Out-Null
$markdown.Add("| Gate | Status | Exit code |") | Out-Null
$markdown.Add("| --- | --- | --- |") | Out-Null
foreach ($gate in $gateSummaries) {
    $markdown.Add("| $($gate.name) | $($gate.status) | $($gate.exitCode) |") | Out-Null
}

$markdown.Add("") | Out-Null
$markdown.Add("Command output is captured in publication-evidence.json.") | Out-Null
Write-Utf8NoBom -Path $markdownPath -Content (($markdown -join [Environment]::NewLine) + [Environment]::NewLine)

Write-Host "Publication evidence written."
Write-Host "Markdown: $markdownPath"
Write-Host "JSON: $jsonPath"

if ($InternalFailures.Count -gt 0) {
    Write-Host "Publication evidence failed:" -ForegroundColor Red
    foreach ($failure in $InternalFailures) {
        Write-Host " - $failure" -ForegroundColor Red
    }

    exit 1
}

if ($pendingExternal.Count -gt 0) {
    foreach ($item in $pendingExternal) {
        Write-Warning "PENDING EXTERNAL: $item"
    }
}

if ($RequirePublishable -and -not $publishable) {
    Write-Host "Publication is not yet publishable." -ForegroundColor Red
    exit 1
}

Write-Host "Publication evidence checks passed."
exit 0

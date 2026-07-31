param(
    [Parameter(Mandatory = $true)]
    [string] $EvidenceRoot,

    [ValidateSet("Simulation", "Physical", "Pilot")]
    [string] $Level = "Simulation"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$resolvedRoot = [System.IO.Path]::GetFullPath($EvidenceRoot)
$summaryPath = Join-Path $resolvedRoot "qualification-summary.json"
$pathComparison = if ([System.IO.Path]::DirectorySeparatorChar -eq '\') {
    [System.StringComparison]::OrdinalIgnoreCase
}
else {
    [System.StringComparison]::Ordinal
}

function Require-Property {
    param(
        [Parameter(Mandatory = $true)][object] $Object,
        [Parameter(Mandatory = $true)][string] $Name,
        [Parameter(Mandatory = $true)][string] $Description
    )

    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) {
        throw "$Description is missing required property '$Name'."
    }

    return $property.Value
}

function Require-CanonicalText {
    param(
        [AllowEmptyString()][string] $Value,
        [Parameter(Mandatory = $true)][string] $Description
    )

    if ([string]::IsNullOrWhiteSpace($Value) `
        -or -not [string]::Equals(
            $Value,
            $Value.Trim(),
            [System.StringComparison]::Ordinal) `
        -or $Value.IndexOfAny([char[]]@("`r", "`n", "`0")) -ge 0) {
        throw "$Description must be non-empty canonical text."
    }

    return $Value
}

function Require-UtcTimestamp {
    param(
        [AllowEmptyString()][string] $Value,
        [Parameter(Mandatory = $true)][string] $Description
    )

    [System.DateTimeOffset] $parsed = [System.DateTimeOffset]::MinValue
    if (-not [System.DateTimeOffset]::TryParse(
            $Value,
            [System.Globalization.CultureInfo]::InvariantCulture,
            [System.Globalization.DateTimeStyles]::RoundtripKind,
            [ref] $parsed) `
        -or $parsed.Offset -ne [System.TimeSpan]::Zero) {
        throw "$Description must be a canonical UTC timestamp."
    }

    return $parsed
}

function Resolve-EvidencePath {
    param(
        [Parameter(Mandatory = $true)][string] $RelativePath,
        [Parameter(Mandatory = $true)][string] $Description
    )

    Require-CanonicalText -Value $RelativePath -Description $Description | Out-Null
    if ([System.IO.Path]::IsPathRooted($RelativePath) `
        -or $RelativePath.IndexOf(
            '\',
            [System.StringComparison]::Ordinal) -ge 0 `
        -or $RelativePath.Split('/') -contains '..') {
        throw "$Description must be a normalized relative path using forward slashes."
    }

    $fullPath = [System.IO.Path]::GetFullPath(
        (Join-Path $resolvedRoot $RelativePath))
    $rootPrefix = $resolvedRoot.TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar) +
        [System.IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith($rootPrefix, $pathComparison)) {
        throw "$Description escapes the evidence root."
    }

    return $fullPath
}

function Require-Minimum {
    param(
        [Parameter(Mandatory = $true)][object] $Metrics,
        [Parameter(Mandatory = $true)][string] $Name,
        [Parameter(Mandatory = $true)][decimal] $Minimum
    )

    $value = Require-Property -Object $Metrics -Name $Name -Description "metrics"
    try {
        $number = [decimal] $value
    }
    catch {
        throw "metrics.$Name must be numeric."
    }

    if ($number -lt $Minimum) {
        throw "metrics.$Name must be at least $Minimum for level $Level; found $number."
    }
}

if (-not (Test-Path -LiteralPath $summaryPath -PathType Leaf)) {
    throw "Industrial qualification summary is missing: $summaryPath"
}

try {
    $summary = Get-Content -LiteralPath $summaryPath -Raw -Encoding utf8 |
        ConvertFrom-Json
}
catch {
    throw "Industrial qualification summary is invalid JSON: $($_.Exception.Message)"
}

if ((Require-Property $summary "schemaVersion" "summary") -ne 1) {
    throw "summary.schemaVersion must be exactly 1."
}
if ((Require-Property $summary "product" "summary") -cne "OpenLineOps") {
    throw "summary.product must be exactly 'OpenLineOps'."
}
if ((Require-Property $summary "status" "summary") -cne "Passed") {
    throw "summary.status must be exactly 'Passed'."
}
Require-CanonicalText `
    -Value ([string](Require-Property $summary "qualificationId" "summary")) `
    -Description "summary.qualificationId" | Out-Null
$startedAt = Require-UtcTimestamp `
    -Value ([string](Require-Property $summary "startedAtUtc" "summary")) `
    -Description "summary.startedAtUtc"
$completedAt = Require-UtcTimestamp `
    -Value ([string](Require-Property $summary "completedAtUtc" "summary")) `
    -Description "summary.completedAtUtc"
if ($completedAt -lt $startedAt) {
    throw "summary.completedAtUtc cannot precede summary.startedAtUtc."
}

$metrics = Require-Property $summary "metrics" "summary"
Require-Minimum $metrics "simulatedUnits" 10000
Require-Minimum $metrics "traceabilityCompletenessPercent" 100
if ([decimal](Require-Property $metrics "traceabilityCompletenessPercent" "metrics") -ne 100) {
    throw "metrics.traceabilityCompletenessPercent must be exactly 100."
}
if ([decimal](Require-Property $metrics "nonIdempotentDuplicateActions" "metrics") -ne 0) {
    throw "metrics.nonIdempotentDuplicateActions must be exactly 0."
}

$terminalUnits = [decimal](Require-Property $metrics "terminalUnits" "metrics")
$completeTerminalUnits = [decimal](
    Require-Property $metrics "terminalUnitsWithCompleteEvidence" "metrics")
if ($terminalUnits -lt 1 -or $completeTerminalUnits -ne $terminalUnits) {
    throw "Every terminal unit must have complete evidence and terminalUnits must be positive."
}

if ($Level -in @("Physical", "Pilot")) {
    Require-Minimum $metrics "physicalUnits" 1000
}
if ($Level -eq "Pilot") {
    Require-Minimum $metrics "continuousRunHours" 168
    Require-Minimum $metrics "enterpriseNetworkOfflineHours" 24
    Require-Minimum $metrics "qualifiedStationCount" 2
}

$rollback = Require-Property $summary "rollback" "summary"
foreach ($propertyName in @(
        "oldRecordsReadable",
        "unfinishedExecutionsRecoverable",
        "mismatchedRecipeOrTestPlanRejected")) {
    if ((Require-Property $rollback $propertyName "rollback") -ne $true) {
        throw "rollback.$propertyName must be true."
    }
}

$artifacts = @(Require-Property $summary "artifacts" "summary")
if ($artifacts.Count -eq 0) {
    throw "summary.artifacts must contain evidence files."
}

$artifactPaths = [System.Collections.Generic.HashSet[string]]::new(
    [System.StringComparer]::Ordinal)
foreach ($artifact in $artifacts) {
    $relativePath = [string](Require-Property $artifact "path" "artifact")
    if (-not $artifactPaths.Add($relativePath)) {
        throw "Artifact path '$relativePath' is duplicated."
    }

    $fullPath = Resolve-EvidencePath $relativePath "artifact.path"
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        throw "Evidence artifact is missing: $relativePath"
    }

    $item = Get-Item -LiteralPath $fullPath -Force
    if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Evidence artifact cannot be a reparse point: $relativePath"
    }

    $expectedSize = [long](Require-Property $artifact "sizeBytes" "artifact")
    if ($expectedSize -lt 0 -or $item.Length -ne $expectedSize) {
        throw "Evidence artifact size mismatch for '$relativePath'."
    }

    $expectedHash = [string](Require-Property $artifact "sha256" "artifact")
    if ($expectedHash -cnotmatch '^[0-9a-f]{64}$') {
        throw "Artifact sha256 for '$relativePath' must be lowercase hexadecimal."
    }
    $actualHash = (Get-FileHash -LiteralPath $fullPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -cne $expectedHash) {
        throw "Evidence artifact hash mismatch for '$relativePath'."
    }

    Require-CanonicalText `
        -Value ([string](Require-Property $artifact "contentType" "artifact")) `
        -Description "artifact.contentType" | Out-Null
    Require-UtcTimestamp `
        -Value ([string](Require-Property $artifact "collectedAtUtc" "artifact")) `
        -Description "artifact.collectedAtUtc" | Out-Null
}

$requiredScenarios = @(
    "power-loss",
    "network-disconnect",
    "agent-crash",
    "plugin-host-crash",
    "duplicate-message",
    "out-of-order-message",
    "delayed-message",
    "stale-fencing-token",
    "disk-full",
    "database-unavailable",
    "message-broker-unavailable",
    "clock-skew",
    "certificate-rotation",
    "tampered-release-package"
)
if ($Level -in @("Physical", "Pilot")) {
    $requiredScenarios += @("emergency-stop", "safety-door")
}

$scenarios = @(Require-Property $summary "scenarios" "summary")
$scenarioIds = [System.Collections.Generic.HashSet[string]]::new(
    [System.StringComparer]::Ordinal)
foreach ($scenario in $scenarios) {
    $scenarioId = [string](Require-Property $scenario "id" "scenario")
    Require-CanonicalText $scenarioId "scenario.id" | Out-Null
    if (-not $scenarioIds.Add($scenarioId)) {
        throw "Scenario '$scenarioId' is duplicated."
    }
    if ((Require-Property $scenario "status" "scenario '$scenarioId'") -cne "Passed") {
        throw "Scenario '$scenarioId' must have status Passed."
    }

    $scenarioEvidence = @(Require-Property $scenario "evidence" "scenario '$scenarioId'")
    if ($scenarioEvidence.Count -eq 0) {
        throw "Scenario '$scenarioId' must reference at least one evidence artifact."
    }
    foreach ($evidencePath in $scenarioEvidence) {
        if (-not $artifactPaths.Contains([string]$evidencePath)) {
            throw "Scenario '$scenarioId' references undeclared artifact '$evidencePath'."
        }
    }

    if ($scenarioId -in @("emergency-stop", "safety-door")) {
        if ((Require-Property `
                $scenario `
                "executionBoundary" `
                "scenario '$scenarioId'") -cne "IndependentSafetyController") {
            throw "Scenario '$scenarioId' must execute at the independent safety-controller boundary."
        }
        if ((Require-Property `
                $scenario `
                "platformRequired" `
                "scenario '$scenarioId'") -ne $false) {
            throw "Scenario '$scenarioId' must prove that the platform is not required to act."
        }
    }
}

$missingScenarios = @(
    $requiredScenarios |
        Where-Object { -not $scenarioIds.Contains($_) }
)
if ($missingScenarios.Count -gt 0) {
    throw "Required qualification scenarios are missing: $($missingScenarios -join ', ')."
}

Write-Host (
    "Industrial qualification evidence passed at level {0}: {1} scenarios, {2} artifacts." `
        -f $Level,
        $scenarios.Count,
        $artifacts.Count)

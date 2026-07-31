$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$verifier = Join-Path $PSScriptRoot "verify-industrial-qualification-evidence.ps1"
$testRoot = Join-Path (
    [System.IO.Path]::GetTempPath()) (
    "openlineops-industrial-qualification-tests-" + [System.Guid]::NewGuid().ToString("N"))

function Write-Fixture {
    param(
        [Parameter(Mandatory = $true)][string] $Root,
        [ValidateSet("Simulation", "Physical", "Pilot")]
        [string] $Level = "Pilot"
    )

    New-Item -ItemType Directory -Path (Join-Path $Root "evidence") -Force | Out-Null
    $artifactPath = Join-Path $Root "evidence/qualification.json"
    '{"source":"independent qualification rig","passed":true}' |
        Set-Content -LiteralPath $artifactPath -Encoding utf8
    $item = Get-Item -LiteralPath $artifactPath
    $hash = (Get-FileHash -LiteralPath $artifactPath -Algorithm SHA256).Hash.ToLowerInvariant()

    $scenarioIds = @(
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
        $scenarioIds += @("emergency-stop", "safety-door")
    }

    $summary = [ordered]@{
        schemaVersion = 1
        product = "OpenLineOps"
        qualificationId = "qualification-reference-line-001"
        status = "Passed"
        startedAtUtc = "2026-07-01T00:00:00.0000000+00:00"
        completedAtUtc = "2026-07-08T00:00:00.0000000+00:00"
        metrics = [ordered]@{
            simulatedUnits = 10000
            physicalUnits = 1000
            continuousRunHours = 168
            enterpriseNetworkOfflineHours = 24
            qualifiedStationCount = 2
            nonIdempotentDuplicateActions = 0
            traceabilityCompletenessPercent = 100
            terminalUnits = 10000
            terminalUnitsWithCompleteEvidence = 10000
        }
        rollback = [ordered]@{
            oldRecordsReadable = $true
            unfinishedExecutionsRecoverable = $true
            mismatchedRecipeOrTestPlanRejected = $true
        }
        scenarios = @(
            $scenarioIds | ForEach-Object {
                $scenario = [ordered]@{
                    id = $_
                    status = "Passed"
                    evidence = @("evidence/qualification.json")
                }
                if ($_ -in @("emergency-stop", "safety-door")) {
                    $scenario.executionBoundary = "IndependentSafetyController"
                    $scenario.platformRequired = $false
                }
                $scenario
            }
        )
        artifacts = @(
            [ordered]@{
                path = "evidence/qualification.json"
                sha256 = $hash
                sizeBytes = $item.Length
                contentType = "application/json"
                collectedAtUtc = "2026-07-08T00:00:00.0000000+00:00"
            }
        )
    }

    $summary |
        ConvertTo-Json -Depth 20 |
        Set-Content -LiteralPath (Join-Path $Root "qualification-summary.json") -Encoding utf8
    return $summary
}

function Assert-Rejected {
    param(
        [Parameter(Mandatory = $true)][scriptblock] $Action,
        [Parameter(Mandatory = $true)][string] $ExpectedMessage
    )

    try {
        & $Action
        throw "Expected verifier rejection containing '$ExpectedMessage'."
    }
    catch {
        if ($_.Exception.Message.IndexOf(
                $ExpectedMessage,
                [System.StringComparison]::Ordinal) -lt 0) {
            throw "Expected rejection containing '$ExpectedMessage', got '$($_.Exception.Message)'."
        }
    }
}

try {
    $passingRoot = Join-Path $testRoot "passing"
    $summary = Write-Fixture -Root $passingRoot
    & $verifier -EvidenceRoot $passingRoot -Level Pilot

    $thresholdRoot = Join-Path $testRoot "threshold"
    $thresholdSummary = Write-Fixture -Root $thresholdRoot
    $thresholdSummary.metrics.physicalUnits = 999
    $thresholdSummary |
        ConvertTo-Json -Depth 20 |
        Set-Content -LiteralPath (Join-Path $thresholdRoot "qualification-summary.json") -Encoding utf8
    Assert-Rejected {
        & $verifier -EvidenceRoot $thresholdRoot -Level Physical
    } "metrics.physicalUnits must be at least 1000"

    $missingScenarioRoot = Join-Path $testRoot "missing-scenario"
    $missingScenarioSummary = Write-Fixture -Root $missingScenarioRoot
    $missingScenarioSummary.scenarios = @(
        $missingScenarioSummary.scenarios |
            Where-Object { $_.id -ne "stale-fencing-token" }
    )
    $missingScenarioSummary |
        ConvertTo-Json -Depth 20 |
        Set-Content -LiteralPath (
            Join-Path $missingScenarioRoot "qualification-summary.json") -Encoding utf8
    Assert-Rejected {
        & $verifier -EvidenceRoot $missingScenarioRoot -Level Pilot
    } "stale-fencing-token"

    $safetyBoundaryRoot = Join-Path $testRoot "safety-boundary"
    $safetyBoundarySummary = Write-Fixture -Root $safetyBoundaryRoot
    $emergencyStop = $safetyBoundarySummary.scenarios |
        Where-Object { $_.id -eq "emergency-stop" } |
        Select-Object -First 1
    [void] $emergencyStop.Remove("executionBoundary")
    $safetyBoundarySummary |
        ConvertTo-Json -Depth 20 |
        Set-Content -LiteralPath (
            Join-Path $safetyBoundaryRoot "qualification-summary.json") -Encoding utf8
    Assert-Rejected {
        & $verifier -EvidenceRoot $safetyBoundaryRoot -Level Pilot
    } "executionBoundary"

    $tamperedRoot = Join-Path $testRoot "tampered"
    Write-Fixture -Root $tamperedRoot | Out-Null
    '{"source":"changed after qualification"}' |
        Set-Content -LiteralPath (
            Join-Path $tamperedRoot "evidence/qualification.json") -Encoding utf8
    Assert-Rejected {
        & $verifier -EvidenceRoot $tamperedRoot -Level Pilot
    } "Evidence artifact size mismatch"

    $escapeRoot = Join-Path $testRoot "escape"
    $escapeSummary = Write-Fixture -Root $escapeRoot
    $escapeSummary.artifacts[0].path = "../outside.json"
    $escapeSummary.scenarios | ForEach-Object {
        $_.evidence = @("../outside.json")
    }
    $escapeSummary |
        ConvertTo-Json -Depth 20 |
        Set-Content -LiteralPath (
            Join-Path $escapeRoot "qualification-summary.json") -Encoding utf8
    Assert-Rejected {
        & $verifier -EvidenceRoot $escapeRoot -Level Pilot
    } "normalized relative path"

    Write-Host "Industrial qualification evidence verifier tests passed."
}
finally {
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}

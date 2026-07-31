param(
    [Parameter(Mandatory = $true)]
    [string] $TrxPath,

    [string] $OutputPath = "output/production-integration-evidence/integration-evidence.json",

    [string] $Repository = $env:GITHUB_REPOSITORY,

    [string] $CommitSha = $env:GITHUB_SHA,

    [string] $RunId = $env:GITHUB_RUN_ID,

    [string] $ServerUrl = $env:GITHUB_SERVER_URL
)

$ErrorActionPreference = "Stop"
$RepoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$RequiredJobName = "production-integration"
$RequiredTestName = "OpenLineOps.PostgresIntegration.Tests.PostgresRabbitMqProductionCoordinationIntegrationTests.DurableOutboxAndResultInboxSurviveCoordinatorRestartAcrossRealBroker"

function Resolve-RepoPath {
    param([Parameter(Mandatory = $true)][string] $Path)

    $resolved = if ([System.IO.Path]::IsPathRooted($Path)) {
        [System.IO.Path]::GetFullPath($Path)
    }
    else {
        [System.IO.Path]::GetFullPath((Join-Path $RepoRoot $Path))
    }
    $rootPrefix = $RepoRoot.TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Production integration evidence paths must remain inside the repository: $resolved"
    }

    return $resolved
}

function Assert-PathDoesNotTraverseReparsePoint {
    param([Parameter(Mandatory = $true)][string] $Path)

    $relativePath = $Path.Substring(
        $RepoRoot.TrimEnd('\', '/').Length).TrimStart('\', '/')
    $cursor = $RepoRoot
    foreach ($segment in $relativePath.Split(
            @([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar),
            [System.StringSplitOptions]::RemoveEmptyEntries)) {
        $cursor = Join-Path $cursor $segment
        if (-not (Test-Path -LiteralPath $cursor)) {
            break
        }

        if ((Get-Item -LiteralPath $cursor -Force).Attributes.HasFlag(
                [System.IO.FileAttributes]::ReparsePoint)) {
            throw "Production integration evidence path cannot traverse a reparse point: $Path"
        }
    }
}

function Read-RequiredCounter {
    param(
        [Parameter(Mandatory = $true)] $Counters,
        [Parameter(Mandatory = $true)][string] $Name
    )

    $value = $Counters.GetAttribute($Name)
    $parsed = 0
    if ([string]::IsNullOrWhiteSpace($value) `
        -or -not [int]::TryParse(
            $value,
            [System.Globalization.NumberStyles]::None,
            [System.Globalization.CultureInfo]::InvariantCulture,
            [ref] $parsed) `
        -or $parsed -lt 0) {
        throw "TRX counter '$Name' must be a canonical non-negative integer."
    }

    return $parsed
}

if ($Repository -cnotmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') {
    throw "Repository must be a canonical GitHub owner/repository identity."
}
if ($CommitSha -cnotmatch '^[0-9a-f]{40}$') {
    throw "CommitSha must be a lowercase 40-character Git commit id."
}
if ($RunId -cnotmatch '^[1-9][0-9]*$') {
    throw "RunId must be a canonical positive GitHub Actions run id."
}
if ($ServerUrl -cne "https://github.com") {
    throw "ServerUrl must be exactly https://github.com."
}

$githubBindings = @(
    [pscustomobject]@{ Name = "GITHUB_REPOSITORY"; Actual = $Repository },
    [pscustomobject]@{ Name = "GITHUB_SHA"; Actual = $CommitSha },
    [pscustomobject]@{ Name = "GITHUB_RUN_ID"; Actual = $RunId },
    [pscustomobject]@{ Name = "GITHUB_SERVER_URL"; Actual = $ServerUrl }
)
foreach ($binding in $githubBindings) {
    $expected = [Environment]::GetEnvironmentVariable($binding.Name)
    if (-not [string]::IsNullOrWhiteSpace($expected) `
        -and $binding.Actual -cne $expected) {
        throw "$($binding.Name) does not match the current GitHub Actions context."
    }
}

$resolvedTrxPath = Resolve-RepoPath $TrxPath
$resolvedOutputPath = Resolve-RepoPath $OutputPath
if ($resolvedTrxPath -ceq $resolvedOutputPath) {
    throw "TRX evidence and output evidence must be different files."
}
Assert-PathDoesNotTraverseReparsePoint $resolvedTrxPath
Assert-PathDoesNotTraverseReparsePoint $resolvedOutputPath
if (-not (Test-Path -LiteralPath $resolvedTrxPath -PathType Leaf)) {
    throw "TRX evidence does not exist: $resolvedTrxPath"
}

try {
    [xml] $trx = Get-Content -LiteralPath $resolvedTrxPath -Raw
}
catch {
    throw "TRX evidence is not valid XML: $($_.Exception.Message)"
}

$counters = $trx.SelectSingleNode("/*[local-name()='TestRun']/*[local-name()='ResultSummary']/*[local-name()='Counters']")
if ($null -eq $counters) {
    throw "TRX evidence is missing ResultSummary/Counters."
}
$total = Read-RequiredCounter -Counters $counters -Name "total"
$executed = Read-RequiredCounter -Counters $counters -Name "executed"
$passed = Read-RequiredCounter -Counters $counters -Name "passed"
$failed = Read-RequiredCounter -Counters $counters -Name "failed"
$notExecuted = Read-RequiredCounter -Counters $counters -Name "notExecuted"
if ($total -le 0 `
    -or $executed -ne $total `
    -or $passed -ne $total `
    -or $failed -ne 0 `
    -or $notExecuted -ne 0) {
    throw "TRX must prove all $total tests passed with zero failed or skipped tests."
}

$requiredResults = @($trx.SelectNodes(
        "/*[local-name()='TestRun']/*[local-name()='Results']/*[local-name()='UnitTestResult'][@testName='$RequiredTestName']"))
if ($requiredResults.Count -ne 1 `
    -or $requiredResults[0].GetAttribute("outcome") -cne "Passed") {
    throw "TRX must contain exactly one Passed result for '$RequiredTestName'."
}

$outputDirectory = [System.IO.Path]::GetDirectoryName($resolvedOutputPath)
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
$trxFile = Get-Item -LiteralPath $resolvedTrxPath
$runUrl = "$ServerUrl/$Repository/actions/runs/$RunId"
$evidence = [ordered]@{
    schemaVersion = 1
    generatedAtUtc = [System.DateTimeOffset]::UtcNow.ToString("O")
    product = "OpenLineOps"
    repository = $Repository
    commitSha = $CommitSha
    runId = $RunId
    runUrl = $runUrl
    jobName = $RequiredJobName
    testName = $RequiredTestName
    conclusion = "success"
    counters = [ordered]@{
        total = $total
        executed = $executed
        passed = $passed
        failed = $failed
        skipped = $notExecuted
    }
    trx = [ordered]@{
        relativePath = $resolvedTrxPath.Substring(
            $RepoRoot.TrimEnd('\', '/').Length + 1).Replace('\', '/')
        sizeBytes = $trxFile.Length
        sha256 = (Get-FileHash -LiteralPath $resolvedTrxPath -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}

[System.IO.File]::WriteAllText(
    $resolvedOutputPath,
    (($evidence | ConvertTo-Json -Depth 8) + [Environment]::NewLine),
    [System.Text.UTF8Encoding]::new($false))

Write-Host "Production integration evidence written: $resolvedOutputPath"

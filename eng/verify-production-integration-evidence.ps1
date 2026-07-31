param(
    [string] $WorkRoot = "output/production-integration-evidence-verification"
)

$ErrorActionPreference = "Stop"
$RepoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$Writer = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "write-production-integration-evidence.ps1"))
$ResolvedWorkRoot = [System.IO.Path]::GetFullPath((Join-Path $RepoRoot $WorkRoot))
$rootPrefix = $RepoRoot.TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
if (-not $ResolvedWorkRoot.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Verification work root must remain inside the repository."
}
if (Test-Path -LiteralPath $ResolvedWorkRoot) {
    Remove-Item -LiteralPath $ResolvedWorkRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $ResolvedWorkRoot -Force | Out-Null

$requiredTest = "OpenLineOps.PostgresIntegration.Tests.PostgresRabbitMqProductionCoordinationIntegrationTests.DurableOutboxAndResultInboxSurviveCoordinatorRestartAcrossRealBroker"
$PowerShellCommand = if ([System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
        [System.Runtime.InteropServices.OSPlatform]::Windows)) {
    "powershell"
}
else {
    "pwsh"
}

function New-Trx {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][string] $TestName,
        [Parameter(Mandatory = $true)][string] $Outcome,
        [Parameter(Mandatory = $true)][int] $Total,
        [Parameter(Mandatory = $true)][int] $Executed,
        [Parameter(Mandatory = $true)][int] $Passed,
        [Parameter(Mandatory = $true)][int] $Failed,
        [Parameter(Mandatory = $true)][int] $NotExecuted
    )

    $xml = @"
<?xml version="1.0" encoding="utf-8"?>
<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
  <Results><UnitTestResult testName="$TestName" outcome="$Outcome" /></Results>
  <ResultSummary outcome="$Outcome"><Counters total="$Total" executed="$Executed" passed="$Passed" failed="$Failed" notExecuted="$NotExecuted" /></ResultSummary>
</TestRun>
"@
    [System.IO.File]::WriteAllText(
        $Path,
        $xml,
        [System.Text.UTF8Encoding]::new($false))
}

function Invoke-Case {
    param(
        [Parameter(Mandatory = $true)][string] $Name,
        [Parameter(Mandatory = $true)][string] $TestName,
        [Parameter(Mandatory = $true)][string] $Outcome,
        [Parameter(Mandatory = $true)][int] $Total,
        [Parameter(Mandatory = $true)][int] $Executed,
        [Parameter(Mandatory = $true)][int] $Passed,
        [Parameter(Mandatory = $true)][int] $Failed,
        [Parameter(Mandatory = $true)][int] $NotExecuted,
        [Parameter(Mandatory = $true)][int] $ExpectedExitCode,
        [string] $ExpectedPattern
    )

    $caseRoot = Join-Path $ResolvedWorkRoot $Name
    New-Item -ItemType Directory -Path $caseRoot -Force | Out-Null
    $trxPath = Join-Path $caseRoot "results.trx"
    $evidencePath = Join-Path $caseRoot "evidence.json"
    New-Trx -Path $trxPath -TestName $TestName -Outcome $Outcome -Total $Total -Executed $Executed -Passed $Passed -Failed $Failed -NotExecuted $NotExecuted
    $previousErrorActionPreference = $ErrorActionPreference
    $previousGitHubRepository = $env:GITHUB_REPOSITORY
    $previousGitHubSha = $env:GITHUB_SHA
    $previousGitHubRunId = $env:GITHUB_RUN_ID
    $previousGitHubServerUrl = $env:GITHUB_SERVER_URL
    $ErrorActionPreference = "Continue"
    try {
        $env:GITHUB_REPOSITORY = "openlineops/openlineops"
        $env:GITHUB_SHA = "0123456789abcdef0123456789abcdef01234567"
        $env:GITHUB_RUN_ID = "123456789"
        $env:GITHUB_SERVER_URL = "https://github.com"
        $output = & $PowerShellCommand `
            -NoProfile `
            -ExecutionPolicy Bypass `
            -File $Writer `
            -TrxPath $trxPath `
            -OutputPath $evidencePath `
            -Repository "openlineops/openlineops" `
            -CommitSha "0123456789abcdef0123456789abcdef01234567" `
            -RunId "123456789" `
            -ServerUrl "https://github.com" 2>&1
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
        $env:GITHUB_REPOSITORY = $previousGitHubRepository
        $env:GITHUB_SHA = $previousGitHubSha
        $env:GITHUB_RUN_ID = $previousGitHubRunId
        $env:GITHUB_SERVER_URL = $previousGitHubServerUrl
    }

    $text = ($output | Out-String)
    if ($exitCode -ne $ExpectedExitCode `
        -or (-not [string]::IsNullOrWhiteSpace($ExpectedPattern) -and $text -cnotmatch $ExpectedPattern)) {
        Write-Host $text
        throw "Production integration evidence case '$Name' failed unexpectedly."
    }

    if ($ExpectedExitCode -eq 0) {
        $evidence = Get-Content -LiteralPath $evidencePath -Raw | ConvertFrom-Json
        if ($evidence.repository -cne "openlineops/openlineops" `
            -or $evidence.commitSha -cne "0123456789abcdef0123456789abcdef01234567" `
            -or $evidence.runId -cne "123456789" `
            -or $evidence.conclusion -cne "success" `
            -or $evidence.trx.sha256 -cnotmatch '^[0-9a-f]{64}$' `
            -or (Get-FileHash -LiteralPath $trxPath -Algorithm SHA256).Hash.ToLowerInvariant() -cne $evidence.trx.sha256) {
            throw "Passing production integration evidence did not bind its run, commit, and TRX hash."
        }
    }

    Write-Host "Case '$Name' passed."
}

Invoke-Case -Name "passed" -TestName $requiredTest -Outcome "Passed" -Total 27 -Executed 27 -Passed 27 -Failed 0 -NotExecuted 0 -ExpectedExitCode 0
Invoke-Case -Name "missing-required-test" -TestName "Other.Test" -Outcome "Passed" -Total 27 -Executed 27 -Passed 27 -Failed 0 -NotExecuted 0 -ExpectedExitCode 1 -ExpectedPattern "exactly one Passed result"
Invoke-Case -Name "failed" -TestName $requiredTest -Outcome "Failed" -Total 27 -Executed 27 -Passed 26 -Failed 1 -NotExecuted 0 -ExpectedExitCode 1 -ExpectedPattern "zero failed or skipped"
Invoke-Case -Name "skipped" -TestName $requiredTest -Outcome "Passed" -Total 27 -Executed 26 -Passed 26 -Failed 0 -NotExecuted 1 -ExpectedExitCode 1 -ExpectedPattern "zero failed or skipped"

$samePathRoot = Join-Path $ResolvedWorkRoot "same-input-output"
New-Item -ItemType Directory -Path $samePathRoot -Force | Out-Null
$samePath = Join-Path $samePathRoot "evidence.trx"
New-Trx -Path $samePath -TestName $requiredTest -Outcome "Passed" -Total 27 -Executed 27 -Passed 27 -Failed 0 -NotExecuted 0
$samePathEnvironment = @{
    GITHUB_REPOSITORY = $env:GITHUB_REPOSITORY
    GITHUB_SHA = $env:GITHUB_SHA
    GITHUB_RUN_ID = $env:GITHUB_RUN_ID
    GITHUB_SERVER_URL = $env:GITHUB_SERVER_URL
}
$samePathErrorActionPreference = $ErrorActionPreference
try {
    $ErrorActionPreference = "Continue"
    $env:GITHUB_REPOSITORY = "openlineops/openlineops"
    $env:GITHUB_SHA = "0123456789abcdef0123456789abcdef01234567"
    $env:GITHUB_RUN_ID = "123456789"
    $env:GITHUB_SERVER_URL = "https://github.com"
    $samePathOutput = & $PowerShellCommand `
        -NoProfile `
        -ExecutionPolicy Bypass `
        -File $Writer `
        -TrxPath $samePath `
        -OutputPath $samePath `
        -Repository "openlineops/openlineops" `
        -CommitSha "0123456789abcdef0123456789abcdef01234567" `
        -RunId "123456789" `
        -ServerUrl "https://github.com" 2>&1 | Out-String
    $samePathExitCode = $LASTEXITCODE
}
finally {
    $ErrorActionPreference = $samePathErrorActionPreference
    $env:GITHUB_REPOSITORY = $samePathEnvironment.GITHUB_REPOSITORY
    $env:GITHUB_SHA = $samePathEnvironment.GITHUB_SHA
    $env:GITHUB_RUN_ID = $samePathEnvironment.GITHUB_RUN_ID
    $env:GITHUB_SERVER_URL = $samePathEnvironment.GITHUB_SERVER_URL
}
if ($samePathExitCode -eq 0 -or $samePathOutput -cnotmatch "must be different files") {
    throw "Production integration evidence writer accepted the same TRX and output path."
}
Write-Host "Case 'same-input-output' passed."

$contextRoot = Join-Path $ResolvedWorkRoot "github-context-mismatch"
New-Item -ItemType Directory -Path $contextRoot -Force | Out-Null
$contextTrx = Join-Path $contextRoot "results.trx"
$contextEvidence = Join-Path $contextRoot "evidence.json"
New-Trx -Path $contextTrx -TestName $requiredTest -Outcome "Passed" -Total 27 -Executed 27 -Passed 27 -Failed 0 -NotExecuted 0
$previousGitHubRepository = $env:GITHUB_REPOSITORY
$contextErrorActionPreference = $ErrorActionPreference
try {
    $ErrorActionPreference = "Continue"
    $env:GITHUB_REPOSITORY = "trusted/openlineops"
    $contextOutput = & $PowerShellCommand `
        -NoProfile `
        -ExecutionPolicy Bypass `
        -File $Writer `
        -TrxPath $contextTrx `
        -OutputPath $contextEvidence `
        -Repository "attacker/openlineops" `
        -CommitSha "0123456789abcdef0123456789abcdef01234567" `
        -RunId "123456789" `
        -ServerUrl "https://github.com" 2>&1 | Out-String
    $contextExitCode = $LASTEXITCODE
}
finally {
    $ErrorActionPreference = $contextErrorActionPreference
    $env:GITHUB_REPOSITORY = $previousGitHubRepository
}
if ($contextExitCode -eq 0 -or $contextOutput -cnotmatch "GITHUB_REPOSITORY does not match") {
    throw "Production integration evidence writer accepted a repository that differed from GitHub context."
}
Write-Host "Case 'github-context-mismatch' passed."

Write-Host "Production integration evidence regression tests passed."
exit 0

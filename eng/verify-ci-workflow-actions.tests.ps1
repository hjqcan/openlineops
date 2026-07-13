param(
    [string] $WorkflowPath = ".github/workflows/build.yml",

    [string] $VerifierPath = "eng/verify-ci-workflow-actions.ps1"
)

$ErrorActionPreference = "Stop"

$RepoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$RepoRootPrefix = $RepoRoot.TrimEnd(
    [System.IO.Path]::DirectorySeparatorChar,
    [System.IO.Path]::AltDirectorySeparatorChar) +
    [System.IO.Path]::DirectorySeparatorChar

function Resolve-RepoPath {
    param([Parameter(Mandatory = $true)][string] $Path)

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }

    return [System.IO.Path]::GetFullPath((Join-Path $RepoRoot $Path))
}

function Assert-UnderRepoRoot {
    param([Parameter(Mandatory = $true)][string] $Path)

    if (-not $Path.StartsWith(
            $RepoRootPrefix,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing CI workflow verifier test output outside the repository root: $Path"
    }
}

$resolvedWorkflowPath = Resolve-RepoPath $WorkflowPath
$resolvedVerifierPath = Resolve-RepoPath $VerifierPath
if (-not (Test-Path -LiteralPath $resolvedWorkflowPath -PathType Leaf)) {
    throw "WorkflowPath does not exist: $resolvedWorkflowPath"
}
if (-not (Test-Path -LiteralPath $resolvedVerifierPath -PathType Leaf)) {
    throw "VerifierPath does not exist: $resolvedVerifierPath"
}

$workflowContent = Get-Content -LiteralPath $resolvedWorkflowPath -Raw
$testRoot = Resolve-RepoPath (
    "output/ci-workflow-verifier-tests-" + [Guid]::NewGuid().ToString("N"))
Assert-UnderRepoRoot $testRoot
New-Item -ItemType Directory -Path $testRoot -Force | Out-Null

$cases = @(
    [pscustomobject]@{
        Name = "Erlang version pin"
        Search = '$erlangVersion = "27.3.4.8"'
        Replacement = '$erlangVersion = "27.3.4.2"'
        ExpectedFailure = "pin the Windows RabbitMQ gate to Erlang 27.3.4.8"
    },
    [pscustomobject]@{
        Name = "RabbitMQ version pin"
        Search = '$rabbitMqVersion = "4.3.1"'
        Replacement = '$rabbitMqVersion = "4.3.0"'
        ExpectedFailure = "pin the Windows staged Agent gate to RabbitMQ 4.3.1"
    },
    [pscustomobject]@{
        Name = "RabbitMQ application readiness"
        Search = '& $rabbitMqCtl await_startup --timeout 120'
        Replacement = '& $rabbitMqCtl version'
        ExpectedFailure = "await application startup through rabbitmqctl"
    },
    [pscustomobject]@{
        Name = "RabbitMQ localhost readiness"
        Search = '$client.ConnectAsync("127.0.0.1", 5672)'
        Replacement = '$client.ConnectAsync("127.0.0.1", 5673)'
        ExpectedFailure = "fail unless its localhost AMQP port becomes ready"
    },
    [pscustomobject]@{
        Name = "RabbitMQ URI export"
        Search = '"OPENLINEOPS_RABBITMQ_URI=$rabbitMqUri" | Out-File'
        Replacement = '"BROKEN_RABBITMQ_URI=$rabbitMqUri" | Out-File'
        ExpectedFailure = "export OPENLINEOPS_RABBITMQ_URI"
    },
    [pscustomobject]@{
        Name = "staged Agent no-skip guard"
        Search = 'if ([string]::IsNullOrWhiteSpace($env:OPENLINEOPS_RABBITMQ_URI)) {'
        Replacement = 'if ($false) {'
        ExpectedFailure = "instead of allowing a skipped transport test"
    },
    [pscustomobject]@{
        Name = "staged Agent continue on error"
        Search = "      - name: Run staged Agent bundle E2E"
        Replacement = "      - name: Run staged Agent bundle E2E`n        continue-on-error: true"
        ExpectedFailure = "must not continue on error"
    }
)

try {
    foreach ($case in $cases) {
        if (-not $workflowContent.Contains($case.Search)) {
            throw "The verifier regression fixture token is missing for '$($case.Name)'."
        }

        $fixtureContent = $workflowContent.Replace($case.Search, $case.Replacement)
        $fixturePath = Join-Path $testRoot (
            $case.Name.Replace(" ", "-").ToLowerInvariant() + ".yml")
        [System.IO.File]::WriteAllText(
            $fixturePath,
            $fixtureContent,
            [System.Text.UTF8Encoding]::new($false))

        $output = & powershell.exe `
            -NoProfile `
            -NonInteractive `
            -ExecutionPolicy Bypass `
            -File $resolvedVerifierPath `
            -WorkflowPath $fixturePath 2>&1 | Out-String
        $exitCode = $LASTEXITCODE
        if ($exitCode -eq 0) {
            throw "CI workflow verifier accepted the invalid '$($case.Name)' fixture."
        }
        if ($output -notmatch [Regex]::Escape($case.ExpectedFailure)) {
            throw "CI workflow verifier rejected '$($case.Name)' for an unexpected reason:`n$output"
        }

        Write-Host "Verified rejection: $($case.Name)"
    }
}
finally {
    if (Test-Path -LiteralPath $testRoot) {
        Assert-UnderRepoRoot $testRoot
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}

Write-Host "CI workflow verifier regression tests passed."
exit 0

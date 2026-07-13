param(
    [string] $AgentBundleRoot = $env:OPENLINEOPS_STAGED_AGENT_BUNDLE_ROOT,

    [string] $SamplePluginRoot = $env:OPENLINEOPS_STAGED_SAMPLE_PLUGIN_ROOT,

    [string] $BrokerUri = $env:OPENLINEOPS_RABBITMQ_URI,

    [string] $WorkRoot = "output/staged-agent-rabbitmq-e2e",

    [string] $Configuration = "Release",

    [switch] $NoBuild,

    [switch] $NoRestore
)

$ErrorActionPreference = "Stop"
$RepoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))

function Resolve-CanonicalDirectory {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][string] $Name
    )

    if ([string]::IsNullOrWhiteSpace($Path) `
        -or [char]::IsWhiteSpace($Path[0]) `
        -or [char]::IsWhiteSpace($Path[$Path.Length - 1]) `
        -or -not [System.IO.Path]::IsPathRooted($Path)) {
        throw "$Name must be a canonical absolute directory path."
    }

    $resolved = [System.IO.Path]::GetFullPath($Path)
    if (-not (Test-Path -LiteralPath $resolved -PathType Container)) {
        throw "$Name does not exist: $resolved"
    }

    return $resolved
}

function Resolve-RepoPath {
    param([Parameter(Mandatory = $true)][string] $Path)

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }

    return [System.IO.Path]::GetFullPath((Join-Path $RepoRoot $Path))
}

function Assert-UnderRepoRoot {
    param([Parameter(Mandatory = $true)][string] $Path)

    $normalizedRoot = $RepoRoot.TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
    $rootPrefix = $normalizedRoot + [System.IO.Path]::DirectorySeparatorChar
    if (-not $Path.StartsWith(
            $rootPrefix,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing staged Agent RabbitMQ E2E work outside the repository root: $Path"
    }
}

function Assert-DirectFile {
    param(
        [Parameter(Mandatory = $true)][string] $Root,
        [Parameter(Mandatory = $true)][string] $FileName
    )

    if ([System.IO.Path]::GetFileName($FileName) -cne $FileName) {
        throw "Required staged dependency must be a direct-child file: $FileName"
    }

    $path = [System.IO.Path]::GetFullPath((Join-Path $Root $FileName))
    if ([System.IO.Path]::GetDirectoryName($path) -cne $Root) {
        throw "Required staged dependency resolves outside its root: $FileName"
    }
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required staged dependency does not exist: $path"
    }
}

if (-not [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
        [System.Runtime.InteropServices.OSPlatform]::Windows)) {
    throw "The staged Agent RabbitMQ process E2E gate requires Windows."
}

if ([string]::IsNullOrWhiteSpace($BrokerUri) `
    -or [char]::IsWhiteSpace($BrokerUri[0]) `
    -or [char]::IsWhiteSpace($BrokerUri[$BrokerUri.Length - 1])) {
    throw "BrokerUri is required; this gate cannot silently skip its real RabbitMQ boundary."
}

$parsedBrokerUri = $null
if (-not [System.Uri]::TryCreate(
        $BrokerUri,
        [System.UriKind]::Absolute,
        [ref]$parsedBrokerUri) `
    -or ($parsedBrokerUri.Scheme -cne "amqp" `
        -and $parsedBrokerUri.Scheme -cne "amqps")) {
    throw "BrokerUri must be an absolute amqp or amqps URI."
}

$resolvedAgentBundleRoot = Resolve-CanonicalDirectory `
    -Path $AgentBundleRoot `
    -Name "AgentBundleRoot"
$resolvedSamplePluginRoot = Resolve-CanonicalDirectory `
    -Path $SamplePluginRoot `
    -Name "SamplePluginRoot"
foreach ($fileName in @(
        "OpenLineOps.Agent.exe",
        "OpenLineOps.StationRuntime.exe",
        "OpenLineOps.PluginHost.exe",
        "OpenLineOps.ScriptWorker.exe",
        "OpenLineOps.LeastPrivilegeLauncher.exe")) {
    Assert-DirectFile -Root $resolvedAgentBundleRoot -FileName $fileName
}
Assert-DirectFile -Root $resolvedSamplePluginRoot -FileName "manifest.json"
Assert-DirectFile `
    -Root $resolvedSamplePluginRoot `
    -FileName "OpenLineOps.SamplePlugins.LoopbackDevice.dll"

$resolvedWorkRoot = Resolve-RepoPath $WorkRoot
Assert-UnderRepoRoot $resolvedWorkRoot
if (Test-Path -LiteralPath $resolvedWorkRoot) {
    Remove-Item -LiteralPath $resolvedWorkRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $resolvedWorkRoot -Force | Out-Null
$evidencePath = Join-Path $resolvedWorkRoot "evidence.json"

$testProject = Join-Path $RepoRoot "tests/OpenLineOps.Agent.Tests/OpenLineOps.Agent.Tests.csproj"
$testFilter = "FullyQualifiedName=OpenLineOps.Agent.Tests.StagedAgentRabbitMqProcessE2ETests.StagedAgentRoundTripsSignedPluginAndDeduplicatesAcrossRestart"
$testArguments = @(
    "test",
    $testProject,
    "--configuration",
    $Configuration,
    "--filter",
    $testFilter,
    "--logger",
    "console;verbosity=minimal"
)
if ($NoBuild) {
    $testArguments += "--no-build"
}
if ($NoRestore) {
    $testArguments += "--no-restore"
}

$previousAgentBundleRoot = $env:OPENLINEOPS_STAGED_AGENT_BUNDLE_ROOT
$previousSamplePluginRoot = $env:OPENLINEOPS_STAGED_SAMPLE_PLUGIN_ROOT
$previousBrokerUri = $env:OPENLINEOPS_RABBITMQ_URI
$previousEvidencePath = $env:OPENLINEOPS_STAGED_AGENT_RABBITMQ_EVIDENCE_PATH
try {
    $env:OPENLINEOPS_STAGED_AGENT_BUNDLE_ROOT = $resolvedAgentBundleRoot
    $env:OPENLINEOPS_STAGED_SAMPLE_PLUGIN_ROOT = $resolvedSamplePluginRoot
    $env:OPENLINEOPS_RABBITMQ_URI = $parsedBrokerUri.AbsoluteUri
    $env:OPENLINEOPS_STAGED_AGENT_RABBITMQ_EVIDENCE_PATH = $evidencePath
    $previousErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $testOutput = & dotnet @testArguments 2>&1
        $testExitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }

    $testOutput | ForEach-Object { Write-Host $_ }
    if ($testExitCode -ne 0) {
        throw "Staged Agent RabbitMQ process E2E failed with exit code $testExitCode."
    }
}
finally {
    $env:OPENLINEOPS_STAGED_AGENT_BUNDLE_ROOT = $previousAgentBundleRoot
    $env:OPENLINEOPS_STAGED_SAMPLE_PLUGIN_ROOT = $previousSamplePluginRoot
    $env:OPENLINEOPS_RABBITMQ_URI = $previousBrokerUri
    $env:OPENLINEOPS_STAGED_AGENT_RABBITMQ_EVIDENCE_PATH = $previousEvidencePath
}

if (-not (Test-Path -LiteralPath $evidencePath -PathType Leaf)) {
    throw "Staged Agent RabbitMQ process E2E did not produce its required evidence: $evidencePath"
}

$evidence = Get-Content -LiteralPath $evidencePath -Raw | ConvertFrom-Json
if ($evidence.schema -cne "openlineops.staged-agent-rabbitmq-e2e-evidence" `
    -or $evidence.schemaVersion -ne 1 `
    -or $evidence.executionStatus -cne "Completed" `
    -or $evidence.duplicateRedeliveryRejected -ne $true `
    -or $evidence.duplicateAfterRestartRejected -ne $true `
    -or $evidence.cleanShutdownVerified -ne $true) {
    throw "Staged Agent RabbitMQ process E2E evidence is incomplete or invalid."
}

Write-Host "Staged Agent RabbitMQ process E2E passed."
Write-Host " - Broker: $($parsedBrokerUri.Host):$($parsedBrokerUri.Port) (TLS: $($parsedBrokerUri.Scheme -ceq 'amqps'))"
Write-Host " - True staged Agent process completed a signed plugin Station Job."
Write-Host " - Redelivery and post-restart duplicate execution were rejected by durable SQLite state."
Write-Host " - Evidence: $evidencePath"

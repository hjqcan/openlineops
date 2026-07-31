param(
    [string] $ArtifactsRoot = "artifacts/release",

    [string] $EvidenceRoot = "output/runner-staged-agent-e2e",

    [string] $PostgreSqlConnectionString = $env:OPENLINEOPS_POSTGRES_CONNECTION_STRING,

    [string] $RabbitMqUri = $env:OPENLINEOPS_RABBITMQ_URI,

    [string] $Configuration = "Release",

    [string] $DotNetPath = "dotnet",

    [switch] $NoBuild,

    [switch] $NoRestore,

    [ValidateRange(60, 900)]
    [int] $TestTimeoutSeconds = 240,

    [ValidateRange(30, 180)]
    [int] $CleanupTimeoutSeconds = 90
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.IO.Compression.FileSystem
$RepoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$ExactTest = "OpenLineOps.Runner.Tests.RunnerPublishedProjectProcessE2ETests.PublishedProjectRunsThroughPostgreSqlRabbitMqAndStagedAgent"
$CleanupExactTest = "OpenLineOps.Runner.Tests.RunnerStagedAgentCleanupTests.CleanupIsolatedPostgreSqlSchemaAndRabbitMqQueues"

function Resolve-RepoPath {
    param([Parameter(Mandatory = $true)][string] $Path)

    $resolved = if ([System.IO.Path]::IsPathRooted($Path)) {
        [System.IO.Path]::GetFullPath($Path)
    }
    else {
        [System.IO.Path]::GetFullPath((Join-Path $RepoRoot $Path))
    }
    $prefix = $RepoRoot.TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Runner staged-Agent gate paths must remain under the repository root."
    }
    return $resolved
}

function Assert-NoReparseAncestors {
    param([Parameter(Mandatory = $true)][string] $Path)

    $current = [System.IO.DirectoryInfo]::new([System.IO.Path]::GetFullPath($Path))
    while ($null -ne $current) {
        if ($current.Exists `
            -and (($current.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0)) {
            throw "Runner staged-Agent gate refuses reparse points in path ancestors."
        }
        $current = $current.Parent
    }
}

function Assert-NoReparseTree {
    param([Parameter(Mandatory = $true)][string] $Root)

    if (-not (Test-Path -LiteralPath $Root)) {
        return
    }
    $rootItem = Get-Item -LiteralPath $Root -Force
    if (($rootItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Runner staged-Agent gate refuses a reparse-point root."
    }
    foreach ($entry in @(Get-ChildItem -LiteralPath $Root -Force -Recurse)) {
        if (($entry.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Runner staged-Agent gate refuses reparse points."
        }
    }
}

function Reset-OwnedDirectory {
    param([Parameter(Mandatory = $true)][string] $Path)

    Assert-NoReparseAncestors $Path
    if (Test-Path -LiteralPath $Path) {
        Assert-NoReparseTree $Path
        Remove-Item -LiteralPath $Path -Recurse -Force
    }
    New-Item -ItemType Directory -Path $Path | Out-Null
}

function Get-Sha256 {
    param([Parameter(Mandatory = $true)][string] $Path)
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-TextSha256 {
    param([Parameter(Mandatory = $true)][string] $Text)
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($Text)
    $algorithm = [System.Security.Cryptography.SHA256]::Create()
    try {
        $hash = $algorithm.ComputeHash($bytes)
        ([System.BitConverter]::ToString($hash)).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $algorithm.Dispose()
    }
}

function Get-SingleReleaseArtifact {
    param(
        [Parameter(Mandatory = $true)] $Manifest,
        [Parameter(Mandatory = $true)][string] $Kind,
        [Parameter(Mandatory = $true)][string] $ArtifactsRoot
    )

    $entries = @($Manifest.artifacts | Where-Object { $_.kind -ceq $Kind })
    if ($entries.Count -ne 1) {
        throw "Release manifest must contain exactly one '$Kind' artifact."
    }
    $relativePath = [string]$entries[0].relativePath
    if ($relativePath -cnotmatch '^[A-Za-z0-9._/-]+$' `
        -or $relativePath.Contains('..') `
        -or $relativePath.Contains('\')) {
        throw "Release '$Kind' artifact path is not canonical."
    }
    $path = [System.IO.Path]::GetFullPath(
        (Join-Path $ArtifactsRoot $relativePath.Replace('/', '\')))
    $prefix = $ArtifactsRoot.TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
    if (-not $path.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase) `
        -or -not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Release '$Kind' artifact is absent or escaped its root."
    }
    $file = Get-Item -LiteralPath $path
    if ($file.Length -ne [long]$entries[0].sizeBytes `
        -or (Get-Sha256 $path) -cne [string]$entries[0].sha256) {
        throw "Release '$Kind' archive does not match release-manifest.json."
    }
    [pscustomobject]@{ Entry = $entries[0]; Path = $path }
}

function Expand-SafeArchive {
    param(
        [Parameter(Mandatory = $true)][string] $ArchivePath,
        [Parameter(Mandatory = $true)][string] $Destination
    )

    Reset-OwnedDirectory $Destination
    $archive = [System.IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        if ($archive.Entries.Count -lt 1 -or $archive.Entries.Count -gt 10000) {
            throw "Release archive entry count is outside the safe bound."
        }
        $names = [System.Collections.Generic.HashSet[string]]::new(
            [System.StringComparer]::OrdinalIgnoreCase)
        [long]$totalExpandedBytes = 0
        foreach ($entry in $archive.Entries) {
            $name = $entry.FullName
            $segments = @($name.Split('/'))
            $totalExpandedBytes = [long]($totalExpandedBytes + [long]$entry.Length)
            if ([string]::IsNullOrWhiteSpace($name) `
                -or $name.Contains('\') `
                -or $name.StartsWith('/') `
                -or $name.Contains(':') `
                -or @($segments | Where-Object { $_ -ceq '' -or $_ -ceq '.' -or $_ -ceq '..' }).Count -gt 0 `
                -or -not $names.Add($name) `
                -or (($entry.ExternalAttributes -band 0xF0000000) -eq 0xA0000000) `
                -or $entry.Length -gt 536870912 `
                -or ($entry.Length -gt 0 -and $entry.CompressedLength -eq 0) `
                -or ($entry.CompressedLength -gt 0 `
                    -and ([double]$entry.Length / [double]$entry.CompressedLength) -gt 100.0)) {
                throw "Release archive contains an unsafe or duplicate entry."
            }
            $target = [System.IO.Path]::GetFullPath(
                (Join-Path $Destination $name.Replace('/', '\')))
            $destinationPrefix = $Destination.TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
            if (-not $target.StartsWith(
                    $destinationPrefix,
                    [System.StringComparison]::OrdinalIgnoreCase)) {
                throw "Release archive entry escapes the extraction root."
            }
        }
        if ($totalExpandedBytes -gt 1073741824) {
            throw "Release archive expanded size exceeds the safe bound."
        }
    }
    finally {
        $archive.Dispose()
    }
    [System.IO.Compression.ZipFile]::ExtractToDirectory($ArchivePath, $Destination)
    Assert-NoReparseTree $Destination
    foreach ($file in @(Get-ChildItem -LiteralPath $Destination -File -Recurse)) {
        $file.Attributes = $file.Attributes -bor [System.IO.FileAttributes]::ReadOnly
    }
}

function Assert-ExtractedBundle {
    param(
        [Parameter(Mandatory = $true)][string] $Root,
        [Parameter(Mandatory = $true)][string] $Kind,
        [Parameter(Mandatory = $true)][string] $Role,
        [Parameter(Mandatory = $true)][string] $ExecutableFileName
    )

    $manifestPath = Join-Path $Root 'bundle-manifest.json'
    $checksumsPath = Join-Path $Root 'bundle-checksums.sha256'
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($manifest.schemaVersion -ne 1 `
        -or $manifest.product -cne 'OpenLineOps' `
        -or $manifest.artifactKind -cne $Kind `
        -or $manifest.runtimeIdentifier -cne 'win-x64' `
        -or $manifest.selfContained -ne $true `
        -or @($manifest.entryPoints | Where-Object {
                $_.role -ceq $Role -and $_.relativePath -ceq $ExecutableFileName
            }).Count -ne 1) {
        throw "Extracted $Kind bundle identity is invalid."
    }
    $expected = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::Ordinal)
    $expectedCaseFold = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::OrdinalIgnoreCase)
    $expectedLines = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::Ordinal)
    foreach ($file in @($manifest.files)) {
        $relative = [string]$file.relativePath
        if ($relative -cnotmatch '^[A-Za-z0-9._/-]+$' `
            -or $relative.Contains('..') `
            -or -not $expected.Add($relative) `
            -or -not $expectedCaseFold.Add($relative) `
            -or [string]$file.sha256 -cnotmatch '^[0-9a-f]{64}$' `
            -or [long]$file.sizeBytes -lt 0) {
            throw "Extracted $Kind bundle manifest has an unsafe file identity."
        }
        $path = [System.IO.Path]::GetFullPath(
            (Join-Path $Root $relative.Replace('/', '\')))
        $prefix = $Root.TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
        if (-not $path.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase) `
            -or -not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Extracted $Kind bundle manifest file is absent or escaped its root."
        }
        $actual = Get-Item -LiteralPath $path
        if ($actual.Length -ne [long]$file.sizeBytes `
            -or (Get-Sha256 $path) -cne [string]$file.sha256) {
            throw "Extracted $Kind bundle payload differs from its manifest."
        }
        [void]$expectedLines.Add("$($file.sha256)  $relative")
    }
    $actualPaths = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::Ordinal)
    foreach ($file in @(Get-ChildItem -LiteralPath $Root -File -Recurse)) {
        $relative = $file.FullName.Substring(
            $Root.TrimEnd('\', '/').Length + 1).Replace('\', '/')
        if ($relative -cin @('bundle-manifest.json', 'bundle-checksums.sha256')) {
            continue
        }
        [void]$actualPaths.Add($relative)
    }
    if (-not $actualPaths.SetEquals($expected)) {
        throw "Extracted $Kind bundle membership differs from its manifest."
    }
    $actualLines = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::Ordinal)
    foreach ($line in @(Get-Content -LiteralPath $checksumsPath)) {
        if (-not $actualLines.Add($line)) {
            throw "Extracted $Kind bundle checksums contain duplicates."
        }
    }
    if (-not $actualLines.SetEquals($expectedLines)) {
        throw "Extracted $Kind bundle checksums differ from its manifest."
    }
}

function Stop-ProcessTree {
    param([Parameter(Mandatory = $true)][int] $ProcessId)

    if ($null -eq (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue)) {
        return
    }
    $taskKill = Join-Path $env:SystemRoot "System32/taskkill.exe"
    $killer = Start-Process `
        -FilePath $taskKill `
        -ArgumentList @('/PID', "$ProcessId", '/T', '/F') `
        -WindowStyle Hidden `
        -PassThru
    try {
        [void]$killer.Handle
        if (-not $killer.WaitForExit(15000)) {
            $killer.Kill()
            throw "Timed-out taskkill could not enforce the process-tree cleanup bound."
        }
        $killer.WaitForExit()
        $killer.Refresh()
        $killerExitCode = [int]$killer.ExitCode
        if ($killerExitCode -ne 0 `
        -and $null -ne (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue)) {
            throw "Timed-out Runner staged-Agent test process tree could not be terminated."
        }
    }
    finally {
        $killer.Dispose()
    }
}

function Get-SanitizedProcessDiagnostic {
    param(
        [Parameter(Mandatory = $true)][string[]] $Paths,
        [Parameter(Mandatory = $true)][string[]] $SensitiveValues,
        [Parameter(Mandatory = $true)][string[]] $PrivatePaths
    )

    $parts = [System.Collections.Generic.List[string]]::new()
    foreach ($path in $Paths) {
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            $text = ((Get-Content -LiteralPath $path -Tail 120) -join "`n")
            foreach ($value in $SensitiveValues) {
                if (-not [string]::IsNullOrEmpty($value)) {
                    $text = $text.Replace($value, '[redacted]')
                }
            }
            foreach ($privatePath in $PrivatePaths) {
                if (-not [string]::IsNullOrEmpty($privatePath)) {
                    $text = $text.Replace($privatePath, '[private-path]')
                }
            }
            if ($text.Length -gt 12000) {
                $text = $text.Substring($text.Length - 12000)
            }
            $parts.Add($text)
        }
    }
    $parts -join "`n"
}

function Invoke-RunnerAgentCompensation {
    param(
        [Parameter(Mandatory = $true)][string] $DotNetPath,
        [Parameter(Mandatory = $true)][string] $Configuration,
        [Parameter(Mandatory = $true)][string] $PrivateRoot,
        [Parameter(Mandatory = $true)][string] $ConnectionString,
        [Parameter(Mandatory = $true)][System.Uri] $Broker,
        [Parameter(Mandatory = $true)][string] $ScopeId,
        [Parameter(Mandatory = $true)][string] $ExactTest,
        [switch] $NoBuild,
        [switch] $NoRestore
    )

    $cleanupRoot = Join-Path $PrivateRoot 'compensation'
    Reset-OwnedDirectory $cleanupRoot
    $stdout = Join-Path $cleanupRoot 'cleanup.stdout.log'
    $stderr = Join-Path $cleanupRoot 'cleanup.stderr.log'
    $trxName = 'cleanup.trx'
    $trxPath = Join-Path $cleanupRoot $trxName
    $variables = @(
        'OPENLINEOPS_RUNNER_AGENT_CLEANUP_ENABLED',
        'OPENLINEOPS_RUNNER_AGENT_CLEANUP_POSTGRES_CONNECTION_STRING',
        'OPENLINEOPS_RUNNER_AGENT_CLEANUP_RABBITMQ_URI',
        'OPENLINEOPS_RUNNER_AGENT_CLEANUP_SCOPE_ID')
    $oldValues = @{}
    foreach ($name in $variables) {
        $oldValues[$name] = [System.Environment]::GetEnvironmentVariable($name)
    }
    $process = $null
    try {
        $env:OPENLINEOPS_RUNNER_AGENT_CLEANUP_ENABLED = '1'
        $env:OPENLINEOPS_RUNNER_AGENT_CLEANUP_POSTGRES_CONNECTION_STRING = $ConnectionString
        $env:OPENLINEOPS_RUNNER_AGENT_CLEANUP_RABBITMQ_URI = $Broker.AbsoluteUri
        $env:OPENLINEOPS_RUNNER_AGENT_CLEANUP_SCOPE_ID = $ScopeId
        $arguments = @(
            'test',
            (Join-Path $RepoRoot 'tests/OpenLineOps.Runner.Tests/OpenLineOps.Runner.Tests.csproj'),
            '--configuration', $Configuration,
            '--filter', "FullyQualifiedName=$ExactTest",
            '--results-directory', $cleanupRoot,
            '--logger', "trx;LogFileName=$trxName",
            '--logger', 'console;verbosity=minimal')
        if ($NoBuild) { $arguments += '--no-build' }
        if ($NoRestore) { $arguments += '--no-restore' }
        $process = Start-Process `
            -FilePath $DotNetPath `
            -ArgumentList $arguments `
            -RedirectStandardOutput $stdout `
            -RedirectStandardError $stderr `
            -WindowStyle Hidden `
            -PassThru
        [void]$process.Handle
        if (-not $process.WaitForExit(60000)) {
            Stop-ProcessTree $process.Id
            throw "Runner staged-Agent compensation exceeded its 60-second process bound."
        }
        $process.WaitForExit()
        $process.Refresh()
        $exitCode = [int]$process.ExitCode
        if ($exitCode -ne 0) {
            $diagnostic = Get-SanitizedProcessDiagnostic `
                -Paths @($stdout, $stderr) `
                -SensitiveValues @($ConnectionString, $Broker.AbsoluteUri) `
                -PrivatePaths @($RepoRoot, $PrivateRoot)
            throw "Runner staged-Agent compensation exact test failed with exit code $exitCode. $diagnostic"
        }
        if (-not (Test-Path -LiteralPath $trxPath -PathType Leaf)) {
            throw "Runner staged-Agent compensation did not produce its private TRX."
        }
        [xml] $trx = Get-Content -LiteralPath $trxPath -Raw
        $definitions = @($trx.TestRun.TestDefinitions.UnitTest)
        $results = @($trx.TestRun.Results.UnitTestResult)
        $counters = $trx.TestRun.ResultSummary.Counters
        if ($definitions.Count -ne 1 `
            -or $results.Count -ne 1 `
            -or $results[0].outcome -cne 'Passed' `
            -or "$($definitions[0].TestMethod.className).$($definitions[0].TestMethod.name)" -cne $ExactTest `
            -or [int]$counters.total -ne 1 `
            -or [int]$counters.executed -ne 1 `
            -or [int]$counters.passed -ne 1 `
            -or [int]$counters.failed -ne 0 `
            -or [int]$counters.notExecuted -ne 0) {
            throw "Runner staged-Agent compensation TRX is not exactly one Passed and zero Skipped."
        }
    }
    finally {
        if ($null -ne $process) {
            if (-not $process.HasExited) {
                Stop-ProcessTree $process.Id
                [void]$process.WaitForExit(15000)
            }
            $process.Dispose()
        }
        foreach ($name in $variables) {
            [System.Environment]::SetEnvironmentVariable($name, $oldValues[$name])
        }
    }
}

function Sanitize-Trx {
    param([Parameter(Mandatory = $true)][string] $Path)

    [xml] $trx = Get-Content -LiteralPath $Path -Raw
    $trx.TestRun.name = "runner-staged-agent-e2e"
    $trx.TestRun.runUser = "redacted"
    foreach ($definition in @($trx.TestRun.TestDefinitions.UnitTest)) {
        $definition.storage = "OpenLineOps.Runner.Tests.dll"
        $definition.TestMethod.codeBase = "OpenLineOps.Runner.Tests.dll"
    }
    foreach ($result in @($trx.TestRun.Results.UnitTestResult)) {
        $result.computerName = "redacted"
        foreach ($output in @($result.Output)) {
            if ($null -ne $output) {
                [void]$result.RemoveChild($output)
            }
        }
    }
    foreach ($output in @($trx.TestRun.ResultSummary.Output)) {
        if ($null -ne $output) {
            [void]$trx.TestRun.ResultSummary.RemoveChild($output)
        }
    }
    $settings = [System.Xml.XmlWriterSettings]::new()
    $settings.Encoding = [System.Text.UTF8Encoding]::new($false)
    $settings.Indent = $true
    $settings.NewLineChars = "`n"
    $writer = [System.Xml.XmlWriter]::Create($Path, $settings)
    try { $trx.Save($writer) } finally { $writer.Dispose() }
}

if (-not [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
        [System.Runtime.InteropServices.OSPlatform]::Windows)) {
    throw "The Runner staged-Agent process gate requires Windows."
}
if ([string]::IsNullOrWhiteSpace($PostgreSqlConnectionString)) {
    throw "PostgreSqlConnectionString is required; the real PostgreSQL boundary cannot be skipped."
}
$broker = $null
if (-not [System.Uri]::TryCreate($RabbitMqUri, [System.UriKind]::Absolute, [ref]$broker) `
    -or $broker.Scheme -cnotin @('amqp', 'amqps')) {
    throw "RabbitMqUri must be an absolute amqp or amqps URI."
}

$resolvedArtifactsRoot = Resolve-RepoPath $ArtifactsRoot
$resolvedEvidenceRoot = Resolve-RepoPath $EvidenceRoot
$fixedEvidenceRoot = [System.IO.Path]::GetFullPath(
    (Join-Path $RepoRoot 'output/runner-staged-agent-e2e'))
if ($resolvedEvidenceRoot -cne $fixedEvidenceRoot) {
    throw "Runner staged-Agent public evidence root is fixed at output/runner-staged-agent-e2e."
}
$scopeId = [System.Guid]::NewGuid().ToString('N')
$requestedServiceScope = $env:OPENLINEOPS_RUNNER_STAGED_AGENT_SERVICE_SCOPE
$serviceScope = if ([string]::IsNullOrWhiteSpace($requestedServiceScope)) {
    $scopeId
}
else {
    $requestedServiceScope
}
if ($serviceScope -cnotmatch '^[0-9a-f]{32}$') {
    throw "OPENLINEOPS_RUNNER_STAGED_AGENT_SERVICE_SCOPE must contain exactly 32 lowercase hexadecimal characters."
}
$requestedCleanupManifestPath = $env:OPENLINEOPS_AGENT_SERVICE_CLEANUP_MANIFEST_PATH
$cleanupManifestPath = if ([string]::IsNullOrWhiteSpace($requestedCleanupManifestPath)) {
    [System.IO.Path]::GetFullPath((Join-Path `
            ([System.IO.Path]::GetTempPath()) `
            "openlineops-agent-service-cleanup/runner-$serviceScope.json"))
}
else {
    [System.IO.Path]::GetFullPath($requestedCleanupManifestPath)
}
$serviceCleanupScript = Join-Path $PSScriptRoot 'invoke-run-scoped-agent-service-cleanup.ps1'
$tempBase = [System.IO.Path]::GetFullPath(
    (Join-Path ([System.IO.Path]::GetTempPath()) 'openlineops-runner-staged-agent-gates'))
Assert-NoReparseAncestors $tempBase
$resolvedPrivateRoot = Join-Path $tempBase $scopeId
Assert-NoReparseAncestors $resolvedArtifactsRoot
Assert-NoReparseAncestors $resolvedEvidenceRoot
Assert-NoReparseAncestors $resolvedPrivateRoot
if (-not (Test-Path -LiteralPath $resolvedArtifactsRoot -PathType Container)) {
    throw "Release artifacts root does not exist."
}
Assert-NoReparseTree $resolvedArtifactsRoot
Reset-OwnedDirectory $resolvedEvidenceRoot
if (Test-Path -LiteralPath $resolvedPrivateRoot) {
    throw "Fresh Runner staged-Agent private scope already exists."
}
New-Item -ItemType Directory -Path $resolvedPrivateRoot | Out-Null
$succeeded = $false
$runtimeMayHaveMutated = $false
$primaryFailure = $null
$cleanupFailures = [System.Collections.Generic.List[System.Exception]]::new()
$cleanupManifestPrepared = $false
$previous = @{}
$gateVariables = @(
    'OPENLINEOPS_RUNNER_AGENT_GATE_RUNNER_BUNDLE_ROOT',
    'OPENLINEOPS_RUNNER_AGENT_GATE_AGENT_BUNDLE_ROOT',
    'OPENLINEOPS_RUNNER_AGENT_GATE_POSTGRES_CONNECTION_STRING',
    'OPENLINEOPS_RUNNER_AGENT_GATE_RABBITMQ_URI',
    'OPENLINEOPS_RUNNER_AGENT_GATE_EVIDENCE_PATH',
    'OPENLINEOPS_RUNNER_AGENT_GATE_ENABLED',
    'OPENLINEOPS_RUNNER_AGENT_GATE_SCOPE_ID',
    'OPENLINEOPS_RUNNER_STAGED_AGENT_SERVICE_SCOPE',
    'OPENLINEOPS_AGENT_SERVICE_CLEANUP_GATE',
    'OPENLINEOPS_AGENT_SERVICE_CLEANUP_MANIFEST_PATH',
    'DOTNET_CLI_UI_LANGUAGE',
    'VSLANG')
foreach ($name in $gateVariables) {
    $previous[$name] = [System.Environment]::GetEnvironmentVariable($name)
}

try {
    $manifestPath = Join-Path $resolvedArtifactsRoot 'release-manifest.json'
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($manifest.schemaVersion -ne 1 -or $manifest.product -cne 'OpenLineOps') {
        throw "Release manifest is not the strict OpenLineOps schema."
    }
    $runnerArtifact = Get-SingleReleaseArtifact $manifest 'runner' $resolvedArtifactsRoot
    $agentArtifact = Get-SingleReleaseArtifact $manifest 'agent' $resolvedArtifactsRoot
    $runnerBundleRoot = Join-Path $resolvedPrivateRoot 'runner-bundle'
    $agentBundleRoot = Join-Path $resolvedPrivateRoot 'agent-bundle'
    Expand-SafeArchive $runnerArtifact.Path $runnerBundleRoot
    Expand-SafeArchive $agentArtifact.Path $agentBundleRoot
    Assert-ExtractedBundle $runnerBundleRoot 'runner' 'headless-runner' 'OpenLineOps.Runner.exe'
    Assert-ExtractedBundle $agentBundleRoot 'agent' 'station-agent-service' 'OpenLineOps.Agent.exe'

    & $serviceCleanupScript `
        -Kind runner `
        -Scope $serviceScope `
        -AgentBundleRoot $agentBundleRoot `
        -ManifestPath $cleanupManifestPath `
        -Configuration $Configuration `
        -DotNetPath $DotNetPath `
        -PrepareManifest `
        -NoBuild:$NoBuild `
        -NoRestore:$NoRestore `
        -CleanupTimeoutSeconds $CleanupTimeoutSeconds
    $cleanupManifestPrepared = $true

    $resultsRoot = Join-Path $resolvedEvidenceRoot 'test-results'
    New-Item -ItemType Directory -Path $resultsRoot | Out-Null
    $evidencePath = Join-Path $resolvedEvidenceRoot 'evidence.json'
    $trxName = 'runner-staged-agent-e2e.trx'
    $trxPath = Join-Path $resultsRoot $trxName
    $privateStdout = Join-Path $resolvedPrivateRoot 'dotnet-test.stdout.log'
    $privateStderr = Join-Path $resolvedPrivateRoot 'dotnet-test.stderr.log'

    $env:OPENLINEOPS_RUNNER_AGENT_GATE_RUNNER_BUNDLE_ROOT = $runnerBundleRoot
    $env:OPENLINEOPS_RUNNER_AGENT_GATE_AGENT_BUNDLE_ROOT = $agentBundleRoot
    $env:OPENLINEOPS_RUNNER_AGENT_GATE_POSTGRES_CONNECTION_STRING = $PostgreSqlConnectionString
    $env:OPENLINEOPS_RUNNER_AGENT_GATE_RABBITMQ_URI = $broker.AbsoluteUri
    $env:OPENLINEOPS_RUNNER_AGENT_GATE_EVIDENCE_PATH = $evidencePath
    $env:OPENLINEOPS_RUNNER_AGENT_GATE_ENABLED = '1'
    $env:OPENLINEOPS_RUNNER_AGENT_GATE_SCOPE_ID = $scopeId
    $env:OPENLINEOPS_RUNNER_STAGED_AGENT_SERVICE_SCOPE = $serviceScope
    $env:OPENLINEOPS_AGENT_SERVICE_CLEANUP_GATE = $null
    $env:OPENLINEOPS_AGENT_SERVICE_CLEANUP_MANIFEST_PATH = $cleanupManifestPath
    $env:DOTNET_CLI_UI_LANGUAGE = 'en-US'
    $env:VSLANG = '1033'

    $arguments = @(
        'test',
        (Join-Path $RepoRoot 'tests/OpenLineOps.Runner.Tests/OpenLineOps.Runner.Tests.csproj'),
        '--configuration', $Configuration,
        '--filter', "FullyQualifiedName=$ExactTest",
        '--results-directory', $resultsRoot,
        '--logger', "trx;LogFileName=$trxName",
        '--logger', 'console;verbosity=minimal')
    if ($NoBuild) { $arguments += '--no-build' }
    if ($NoRestore) { $arguments += '--no-restore' }

    $testProcess = Start-Process `
        -FilePath $DotNetPath `
        -ArgumentList $arguments `
        -RedirectStandardOutput $privateStdout `
        -RedirectStandardError $privateStderr `
        -WindowStyle Hidden `
        -PassThru
    $runtimeMayHaveMutated = $true
    try {
        [void]$testProcess.Handle
        if (-not $testProcess.WaitForExit([int]($TestTimeoutSeconds * 1000))) {
            Stop-ProcessTree $testProcess.Id
            throw "Runner staged-Agent exact test timed out; its complete process tree was killed."
        }
        $testProcess.WaitForExit()
        $testProcess.Refresh()
        $testExitCode = [int]$testProcess.ExitCode
        if ($testExitCode -ne 0) {
            $diagnostic = Get-SanitizedProcessDiagnostic `
                -Paths @($privateStdout, $privateStderr) `
                -SensitiveValues @($PostgreSqlConnectionString, $broker.AbsoluteUri) `
                -PrivatePaths @($RepoRoot, $resolvedPrivateRoot)
            throw "Runner staged-Agent exact test failed with exit code $testExitCode. $diagnostic"
        }
    }
    finally {
        if (-not $testProcess.HasExited) {
            Stop-ProcessTree $testProcess.Id
            [void]$testProcess.WaitForExit(15000)
        }
        $testProcess.Dispose()
    }

    if (-not (Test-Path -LiteralPath $evidencePath -PathType Leaf) `
        -or -not (Test-Path -LiteralPath $trxPath -PathType Leaf)) {
        throw "Runner staged-Agent test did not emit both JSON and TRX evidence."
    }
    Sanitize-Trx $trxPath
    [xml] $trx = Get-Content -LiteralPath $trxPath -Raw
    $definitions = @($trx.TestRun.TestDefinitions.UnitTest)
    $results = @($trx.TestRun.Results.UnitTestResult)
    $counters = $trx.TestRun.ResultSummary.Counters
    if ($definitions.Count -ne 1 `
        -or $results.Count -ne 1 `
        -or $results[0].outcome -cne 'Passed' `
        -or "$($definitions[0].TestMethod.className).$($definitions[0].TestMethod.name)" -cne $ExactTest `
        -or [int]$counters.total -ne 1 `
        -or [int]$counters.executed -ne 1 `
        -or [int]$counters.passed -ne 1 `
        -or [int]$counters.failed -ne 0 `
        -or [int]$counters.notExecuted -ne 0) {
        throw "TRX does not prove exactly one Passed and zero Skipped tests."
    }

    $evidence = Get-Content -LiteralPath $evidencePath -Raw | ConvertFrom-Json
    $evidence | Add-Member -NotePropertyName testEvidence -NotePropertyValue ([ordered]@{
        fullyQualifiedName = $ExactTest
        outcome = 'Passed'
        total = 1
        executed = 1
        passed = 1
        failed = 0
        skipped = 0
        trxRelativePath = "test-results/$trxName"
        trxSha256 = Get-Sha256 $trxPath
    })
    $releaseManifestSha256 = Get-Sha256 $manifestPath
    $runnerBundleManifestSha256 = Get-Sha256 (Join-Path $runnerBundleRoot 'bundle-manifest.json')
    $agentBundleManifestSha256 = Get-Sha256 (Join-Path $agentBundleRoot 'bundle-manifest.json')
    $runnerExecutableSha256 = Get-Sha256 (Join-Path $runnerBundleRoot 'OpenLineOps.Runner.exe')
    $agentExecutableSha256 = Get-Sha256 (Join-Path $agentBundleRoot 'OpenLineOps.Agent.exe')
    if ($evidence.execution.runner.executableSha256 -cne $runnerExecutableSha256 `
        -or $evidence.execution.runner.runningImageSha256 -cne $runnerExecutableSha256 `
        -or $evidence.execution.runner.bundleManifestSha256 -cne $runnerBundleManifestSha256 `
        -or $evidence.execution.agent.executableSha256 -cne $agentExecutableSha256 `
        -or $evidence.execution.agent.runningImageSha256 -cne $agentExecutableSha256 `
        -or $evidence.execution.agent.bundleManifestSha256 -cne $agentBundleManifestSha256) {
        throw "Running image evidence is not bound to the extracted release artifacts."
    }
    $releaseCanonical = @(
        "releaseManifestSha256=$releaseManifestSha256",
        "runnerArchiveRelativePath=$($runnerArtifact.Entry.relativePath)",
        "runnerArchiveSizeBytes=$($runnerArtifact.Entry.sizeBytes)",
        "runnerArchiveSha256=$($runnerArtifact.Entry.sha256)",
        "runnerBundleManifestSha256=$runnerBundleManifestSha256",
        "runnerExecutableSha256=$runnerExecutableSha256",
        "agentArchiveRelativePath=$($agentArtifact.Entry.relativePath)",
        "agentArchiveSizeBytes=$($agentArtifact.Entry.sizeBytes)",
        "agentArchiveSha256=$($agentArtifact.Entry.sha256)",
        "agentBundleManifestSha256=$agentBundleManifestSha256",
        "agentExecutableSha256=$agentExecutableSha256"
    ) -join "`n"
    $evidence | Add-Member -NotePropertyName releaseArtifacts -NotePropertyValue ([ordered]@{
        releaseManifestSha256 = $releaseManifestSha256
        runner = [ordered]@{
            archiveRelativePath = [string]$runnerArtifact.Entry.relativePath
            archiveSizeBytes = [long]$runnerArtifact.Entry.sizeBytes
            archiveSha256 = [string]$runnerArtifact.Entry.sha256
            bundleManifestSha256 = $runnerBundleManifestSha256
            executableSha256 = $runnerExecutableSha256
        }
        agent = [ordered]@{
            archiveRelativePath = [string]$agentArtifact.Entry.relativePath
            archiveSizeBytes = [long]$agentArtifact.Entry.sizeBytes
            archiveSha256 = [string]$agentArtifact.Entry.sha256
            bundleManifestSha256 = $agentBundleManifestSha256
            executableSha256 = $agentExecutableSha256
        }
        attestationSha256 = Get-TextSha256 $releaseCanonical
    })
    $temporaryEvidencePath = "$evidencePath.tmp"
    [System.IO.File]::WriteAllText(
        $temporaryEvidencePath,
        (($evidence | ConvertTo-Json -Depth 20) + "`n"),
        [System.Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $temporaryEvidencePath -Destination $evidencePath -Force

    & (Join-Path $PSScriptRoot 'verify-runner-staged-agent-evidence.ps1') `
        -EvidenceRoot $resolvedEvidenceRoot `
        -RequirePassed
    $succeeded = $true
}
catch {
    $primaryFailure = $_.Exception
}
finally {
    foreach ($name in $gateVariables) {
        try {
            [System.Environment]::SetEnvironmentVariable($name, $previous[$name])
        }
        catch {
            $cleanupFailures.Add($_.Exception)
        }
    }
    if ($cleanupManifestPrepared) {
        try {
            & $serviceCleanupScript `
                -Kind runner `
                -Scope $serviceScope `
                -AgentBundleRoot $agentBundleRoot `
                -ManifestPath $cleanupManifestPath `
                -Configuration $Configuration `
                -DotNetPath $DotNetPath `
                -NoBuild:$NoBuild `
                -NoRestore:$NoRestore `
                -CleanupTimeoutSeconds $CleanupTimeoutSeconds
        }
        catch {
            $cleanupFailures.Add($_.Exception)
        }
    }
    if (-not $succeeded -and $runtimeMayHaveMutated) {
        try {
            Invoke-RunnerAgentCompensation `
                -DotNetPath $DotNetPath `
                -Configuration $Configuration `
                -PrivateRoot $resolvedPrivateRoot `
                -ConnectionString $PostgreSqlConnectionString `
                -Broker $broker `
                -ScopeId $scopeId `
                -ExactTest $CleanupExactTest `
                -NoBuild:$NoBuild `
                -NoRestore:$NoRestore
        }
        catch {
            $cleanupFailures.Add($_.Exception)
        }
    }
    if (Test-Path -LiteralPath $resolvedPrivateRoot) {
        try {
            Assert-NoReparseAncestors $resolvedPrivateRoot
            Assert-NoReparseTree $resolvedPrivateRoot
            Remove-Item -LiteralPath $resolvedPrivateRoot -Recurse -Force
        }
        catch {
            $cleanupFailures.Add($_.Exception)
        }
    }
    if ((-not $succeeded -or $cleanupFailures.Count -gt 0) `
        -and (Test-Path -LiteralPath $resolvedEvidenceRoot)) {
        try {
            Assert-NoReparseAncestors $resolvedEvidenceRoot
            Assert-NoReparseTree $resolvedEvidenceRoot
            Remove-Item -LiteralPath $resolvedEvidenceRoot -Recurse -Force
        }
        catch {
            $cleanupFailures.Add($_.Exception)
        }
    }
}

if ($null -ne $primaryFailure) {
    if ($cleanupFailures.Count -eq 0) {
        throw $primaryFailure
    }
    $allFailures = [System.Collections.Generic.List[System.Exception]]::new()
    $allFailures.Add($primaryFailure)
    foreach ($failure in $cleanupFailures) { $allFailures.Add($failure) }
    $failureSummary = (($allFailures | ForEach-Object { $_.Message }) -join ' || ')
    foreach ($secret in @($PostgreSqlConnectionString, $broker.AbsoluteUri)) {
        if (-not [string]::IsNullOrEmpty($secret)) {
            $failureSummary = $failureSummary.Replace($secret, '[redacted]')
        }
    }
    foreach ($privatePath in @($RepoRoot, $resolvedPrivateRoot)) {
        if (-not [string]::IsNullOrEmpty($privatePath)) {
            $failureSummary = $failureSummary.Replace($privatePath, '[private-path]')
        }
    }
    throw [System.AggregateException]::new(
        "Runner staged-Agent gate failed and bounded compensation was not complete: $failureSummary",
        $allFailures.ToArray())
}
if ($cleanupFailures.Count -gt 0) {
    $failureSummary = (($cleanupFailures | ForEach-Object { $_.Message }) -join ' || ')
    throw [System.AggregateException]::new(
        "Runner staged-Agent gate cleanup was not complete: $failureSummary",
        $cleanupFailures.ToArray())
}

Write-Host "Runner staged-Agent production gate passed."
Write-Host " - Exact test: $ExactTest"
Write-Host " - Evidence: $(Join-Path $resolvedEvidenceRoot 'evidence.json')"

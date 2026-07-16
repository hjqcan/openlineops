param(
    [string] $StagedWorkRoot = "artifacts/release-work",

    [string] $ReleaseManifestPath = "artifacts/release/release-manifest.json",

    [string] $ProductionEvidenceRoot = "artifacts/production-closure-e2e",

    [string] $EvidenceRoot = "output/studio-two-agent-production-closure",

    [string] $PostgreSqlConnectionString = $env:OPENLINEOPS_POSTGRES_CONNECTION_STRING,

    [string] $RabbitMqUri = $env:OPENLINEOPS_RABBITMQ_URI,

    [ValidateSet("Debug", "Release")]
    [string] $Configuration = "Release",

    [string] $DotNetPath = "dotnet",

    [switch] $NoBuild,

    [switch] $NoRestore,

    [ValidateRange(300, 1800)]
    [int] $PackagedTimeoutSeconds = 1200,

    [ValidateRange(120, 900)]
    [int] $TestTimeoutSeconds = 600,

    [ValidateRange(30, 180)]
    [int] $CleanupTimeoutSeconds = 90
)

$ErrorActionPreference = "Stop"
$RepoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$ExactTest = "OpenLineOps.Agent.Tests.StagedAgentRabbitMqProcessE2ETests.StudioProjectRunsThroughStagedCoordinatorAndTwoStagedAgents"
$CleanupExactTest = "OpenLineOps.Agent.Tests.StagedAgentRabbitMqProcessE2ETests.CleanupStudioTwoAgentPostgreSqlRabbitMqAndPrivateHandoff"
$TestProject = Join-Path $RepoRoot "tests/OpenLineOps.Agent.Tests/OpenLineOps.Agent.Tests.csproj"
$ExpectedProductionRoot = Join-Path $RepoRoot "artifacts/production-closure-e2e"
$ExpectedEvidenceRoot = Join-Path $RepoRoot "output/studio-two-agent-production-closure"

function Resolve-RepoPath {
    param([Parameter(Mandatory = $true)][string] $Path)

    $resolved = if ([System.IO.Path]::IsPathRooted($Path)) {
        [System.IO.Path]::GetFullPath($Path)
    }
    else {
        [System.IO.Path]::GetFullPath((Join-Path $RepoRoot $Path))
    }
    $repoPrefix = $RepoRoot.TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($repoPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Studio two-Agent gate repository paths must remain below the repository root."
    }
    return $resolved
}

function Assert-NoReparseAncestors {
    param([Parameter(Mandatory = $true)][string] $Path)

    $current = [System.IO.DirectoryInfo]::new([System.IO.Path]::GetFullPath($Path))
    while ($null -ne $current) {
        if ($current.Exists `
            -and (($current.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0)) {
            throw "Studio two-Agent gate refuses reparse points in path ancestors."
        }
        $current = $current.Parent
    }
}

function Assert-NoReparseTree {
    param([Parameter(Mandatory = $true)][string] $Root)

    if (-not (Test-Path -LiteralPath $Root)) { return }
    $rootItem = Get-Item -LiteralPath $Root -Force
    if (($rootItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Studio two-Agent gate refuses a reparse-point root."
    }
    foreach ($item in @(Get-ChildItem -LiteralPath $Root -Force -Recurse)) {
        if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Studio two-Agent gate refuses reparse points in an owned tree."
        }
    }
}

function Assert-DirectChildPath {
    param(
        [Parameter(Mandatory = $true)][string] $Base,
        [Parameter(Mandatory = $true)][string] $Path
    )

    $baseFull = [System.IO.Path]::GetFullPath($Base).TrimEnd('\', '/')
    $pathFull = [System.IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
    if (-not [string]::Equals(
            [System.IO.Path]::GetDirectoryName($pathFull),
            $baseFull,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Studio two-Agent cleanup target is not a direct child of its owned base."
    }
}

function Remove-OwnedDirectChild {
    param(
        [Parameter(Mandatory = $true)][string] $Base,
        [Parameter(Mandatory = $true)][string] $Path
    )

    Assert-DirectChildPath $Base $Path
    Assert-NoReparseAncestors $Base
    if (Test-Path -LiteralPath $Path) {
        Assert-NoReparseTree $Path
        Remove-Item -LiteralPath $Path -Recurse -Force
    }
}

function Reset-RepoDirectory {
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

function Get-PrivateLogDiagnostic {
    param([Parameter(Mandatory = $true)][string[]] $Paths)

    $parts = @()
    foreach ($path in $Paths) {
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            $file = Get-Item -LiteralPath $path
            $parts += "sha256=$(Get-Sha256 $path),sizeBytes=$($file.Length)"
        }
        else {
            $parts += "absent"
        }
    }
    return ($parts -join ';')
}

function ConvertTo-NativeArgument {
    param([Parameter(Mandatory = $true)][AllowEmptyString()][string] $Value)

    if ($Value.Length -gt 0 -and $Value -cnotmatch '[\s"]') { return $Value }
    return '"' + $Value.Replace('"', '\"') + '"'
}

function Stop-ProcessTree {
    param([Parameter(Mandatory = $true)][int] $ProcessId)

    if ($null -eq (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue)) { return }
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
            throw "Studio two-Agent taskkill exceeded its bounded wait."
        }
        $killer.WaitForExit()
        $killer.Refresh()
        if ([int]$killer.ExitCode -ne 0 `
            -and $null -ne (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue)) {
            throw "Studio two-Agent process tree could not be terminated."
        }
    }
    finally {
        $killer.Dispose()
    }
}

function Invoke-BoundedPrivateProcess {
    param(
        [Parameter(Mandatory = $true)][string] $FilePath,
        [Parameter(Mandatory = $true)][string[]] $Arguments,
        [Parameter(Mandatory = $true)][string] $WorkingDirectory,
        [Parameter(Mandatory = $true)][string] $StdoutPath,
        [Parameter(Mandatory = $true)][string] $StderrPath,
        [Parameter(Mandatory = $true)][int] $TimeoutSeconds,
        [Parameter(Mandatory = $true)][string] $FailureCode
    )

    $argumentText = (($Arguments | ForEach-Object { ConvertTo-NativeArgument ([string]$_) }) -join ' ')
    $process = $null
    try {
        try {
            $process = Start-Process `
                -FilePath $FilePath `
                -ArgumentList $argumentText `
                -WorkingDirectory $WorkingDirectory `
                -RedirectStandardOutput $StdoutPath `
                -RedirectStandardError $StderrPath `
                -WindowStyle Hidden `
                -PassThru
        }
        catch {
            throw "$FailureCode-START"
        }
        [void]$process.Handle
        if (-not $process.WaitForExit([int]($TimeoutSeconds * 1000))) {
            Stop-ProcessTree $process.Id
            $diagnostic = Get-PrivateLogDiagnostic @($StdoutPath, $StderrPath)
            throw "$FailureCode-TIMEOUT privateLogs=$diagnostic"
        }
        $process.WaitForExit()
        $process.Refresh()
        $exitCode = [int]$process.ExitCode
        if ($exitCode -ne 0) {
            $diagnostic = Get-PrivateLogDiagnostic @($StdoutPath, $StderrPath)
            throw "$FailureCode-EXIT-$exitCode privateLogs=$diagnostic"
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
    }
}

function Assert-ExactPassedTrx {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][string] $FullyQualifiedName
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Studio two-Agent exact test did not produce its private TRX."
    }
    [xml] $trx = Get-Content -LiteralPath $Path -Raw
    $definitions = @($trx.TestRun.TestDefinitions.UnitTest)
    $results = @($trx.TestRun.Results.UnitTestResult)
    $counters = $trx.TestRun.ResultSummary.Counters
    if ($definitions.Count -ne 1 `
        -or $results.Count -ne 1 `
        -or $results[0].outcome -cne 'Passed' `
        -or "$($definitions[0].TestMethod.className).$($definitions[0].TestMethod.name)" -cne $FullyQualifiedName `
        -or [int]$counters.total -ne 1 `
        -or [int]$counters.executed -ne 1 `
        -or [int]$counters.passed -ne 1 `
        -or [int]$counters.failed -ne 0 `
        -or [int]$counters.notExecuted -ne 0) {
        throw "Studio two-Agent private TRX does not prove exactly one Passed and zero skipped tests."
    }
}

function Test-IsAdministrator {
    $identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
    try {
        $principal = [System.Security.Principal.WindowsPrincipal]::new($identity)
        return $principal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)
    }
    finally {
        $identity.Dispose()
    }
}

function Test-LocalAccountExists {
    param([Parameter(Mandatory = $true)][string] $Name)
    return $null -ne (Get-LocalUser -Name $Name -ErrorAction SilentlyContinue)
}

function Remove-ExactLocalAccount {
    param([Parameter(Mandatory = $true)][string] $Name)

    if (Test-LocalAccountExists $Name) {
        Remove-LocalUser -Name $Name -ErrorAction Stop
    }
    if (Test-LocalAccountExists $Name) {
        throw "Studio two-Agent temporary account cleanup was not complete."
    }
}

function Test-AdministratorMemberSid {
    param([Parameter(Mandatory = $true)][string] $Sid)

    $members = @(Get-LocalGroupMember -SID 'S-1-5-32-544' -ErrorAction Stop)
    return @($members | Where-Object { $_.SID.Value -ceq $Sid }).Count -gt 0
}

function Invoke-TemporaryAccountPreflight {
    param([Parameter(Mandatory = $true)][string[]] $Names)

    $created = [System.Collections.Generic.List[string]]::new()
    try {
        foreach ($name in $Names) {
            if (Test-LocalAccountExists $name) {
                throw "Studio two-Agent preflight refuses an existing temporary account name."
            }
            $password = ConvertTo-SecureString `
                "Aa1!$([System.Guid]::NewGuid().ToString('N'))" `
                -AsPlainText `
                -Force
            New-LocalUser `
                -Name $name `
                -Password $password `
                -AccountNeverExpires `
                -PasswordNeverExpires `
                -Description 'Temporary OpenLineOps formal E2E preflight identity' | Out-Null
            $created.Add($name)
            $user = Get-LocalUser -Name $name -ErrorAction Stop
            if (Test-AdministratorMemberSid $user.SID.Value) {
                throw "Studio two-Agent preflight account unexpectedly belongs to Administrators."
            }
        }
        $sids = @($Names | ForEach-Object { (Get-LocalUser -Name $_ -ErrorAction Stop).SID.Value })
        if ($sids.Count -ne 2 -or $sids[0] -ceq $sids[1]) {
            throw "Studio two-Agent preflight did not create two distinct account SIDs."
        }
    }
    finally {
        $failures = [System.Collections.Generic.List[System.Exception]]::new()
        foreach ($name in @($created)) {
            try { Remove-ExactLocalAccount $name } catch { $failures.Add($_.Exception) }
        }
        if ($failures.Count -gt 0) {
            throw [System.AggregateException]::new(
                "Studio two-Agent account preflight cleanup was incomplete.",
                $failures.ToArray())
        }
    }
}

function Invoke-StudioCompensation {
    param(
        [Parameter(Mandatory = $true)][string] $PrivateRoot,
        [Parameter(Mandatory = $true)][string] $HandoffPath,
        [Parameter(Mandatory = $true)][string] $Suffix
    )

    if (-not (Test-Path -LiteralPath $HandoffPath -PathType Leaf)) { return }
    $cleanupRoot = Join-Path $PrivateRoot 'compensation'
    New-Item -ItemType Directory -Path $cleanupRoot -Force | Out-Null
    $stdout = Join-Path $cleanupRoot 'cleanup.stdout.log'
    $stderr = Join-Path $cleanupRoot 'cleanup.stderr.log'
    $trxName = 'cleanup.trx'
    $trxPath = Join-Path $cleanupRoot $trxName
    $env:OPENLINEOPS_STUDIO_TWO_AGENT_CLEANUP_GATE = 'true'
    $arguments = @(
        'test', $TestProject,
        '--configuration', $Configuration,
        '--filter', "FullyQualifiedName=$CleanupExactTest",
        '--results-directory', $cleanupRoot,
        '--logger', "trx;LogFileName=$trxName",
        '--logger', 'console;verbosity=minimal')
    if ($NoBuild) { $arguments += '--no-build' }
    if ($NoRestore) { $arguments += '--no-restore' }
    Invoke-BoundedPrivateProcess `
        -FilePath $DotNetPath `
        -Arguments $arguments `
        -WorkingDirectory $RepoRoot `
        -StdoutPath $stdout `
        -StderrPath $stderr `
        -TimeoutSeconds $CleanupTimeoutSeconds `
        -FailureCode 'STUDIO-TWO-AGENT-COMPENSATION'
    Assert-ExactPassedTrx $trxPath $CleanupExactTest
}

if (-not [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
        [System.Runtime.InteropServices.OSPlatform]::Windows)) {
    throw "Studio two-Agent formal production gate requires Windows."
}
if (-not (Test-IsAdministrator)) {
    throw "Studio two-Agent formal production gate requires an elevated Windows administrator token."
}
if ([string]::IsNullOrWhiteSpace($PostgreSqlConnectionString)) {
    throw "OPENLINEOPS_POSTGRES_CONNECTION_STRING is required."
}
$broker = $null
if (-not [System.Uri]::TryCreate($RabbitMqUri, [System.UriKind]::Absolute, [ref]$broker) `
    -or $broker.Scheme -cnotin @('amqp', 'amqps') `
    -or ($broker.Scheme -ceq 'amqp' -and -not $broker.IsLoopback)) {
    throw "OPENLINEOPS_RABBITMQ_URI must be loopback amqp or amqps."
}

$resolvedStagedWorkRoot = Resolve-RepoPath $StagedWorkRoot
$resolvedReleaseManifest = Resolve-RepoPath $ReleaseManifestPath
$resolvedProductionRoot = Resolve-RepoPath $ProductionEvidenceRoot
$resolvedEvidenceRoot = Resolve-RepoPath $EvidenceRoot
if (-not [string]::Equals(
        $resolvedProductionRoot,
        $ExpectedProductionRoot,
        [System.StringComparison]::OrdinalIgnoreCase) `
    -or -not [string]::Equals(
        $resolvedEvidenceRoot,
        $ExpectedEvidenceRoot,
        [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Studio two-Agent public evidence roots are fixed at their canonical repository paths."
}
if (-not (Test-Path -LiteralPath $resolvedReleaseManifest -PathType Leaf) `
    -or [System.IO.Path]::GetFileName($resolvedReleaseManifest) -cne 'release-manifest.json') {
    throw "Studio two-Agent release-manifest.json is absent."
}

$stagedAgentRoot = Join-Path $resolvedStagedWorkRoot 'agent'
$stagedApiRoot = Join-Path $resolvedStagedWorkRoot 'api'
$stagedPluginRoot = Join-Path $resolvedStagedWorkRoot 'sample-plugin'
foreach ($required in @(
        (Join-Path $stagedAgentRoot 'OpenLineOps.Agent.exe'),
        (Join-Path $stagedApiRoot 'OpenLineOps.Api.exe'),
        (Join-Path $stagedPluginRoot 'OpenLineOps.SamplePlugins.LoopbackDevice.dll'))) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Studio two-Agent staged release prerequisite is absent."
    }
}
foreach ($root in @($resolvedStagedWorkRoot, $resolvedProductionRoot, $resolvedEvidenceRoot)) {
    Assert-NoReparseAncestors $root
}
Assert-NoReparseTree $resolvedStagedWorkRoot

$suffix = [System.Guid]::NewGuid().ToString('N')
$entryAccount = "oloe2eentry$($suffix.Substring(0, 5))"
$downstreamAccount = "oloe2edown$($suffix.Substring(0, 6))"
$tempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd('\', '/')
$privateGateBase = Join-Path $tempRoot 'openlineops-studio-two-agent-gates'
$privateRoot = Join-Path $privateGateBase $suffix
$handoffBase = Join-Path $tempRoot 'openlineops-production-closure-handoffs'
$handoffScope = Join-Path $handoffBase $suffix
$handoffPath = Join-Path $handoffScope 'production-closure-handoff.json'
$privateExecutionBase = Join-Path $tempRoot 'openlineops-production-closure-e2e'
$harnessBase = Join-Path $env:SystemRoot 'Temp'
$harnessRoot = Join-Path $harnessBase "olo-studio-two-agent-$suffix"
foreach ($base in @($privateGateBase, $handoffBase, $privateExecutionBase, $harnessBase)) {
    Assert-NoReparseAncestors $base
}
if (Test-Path -LiteralPath $privateRoot `
    -or Test-Path -LiteralPath $handoffScope `
    -or Test-Path -LiteralPath $harnessRoot) {
    throw "Studio two-Agent gate refuses a non-fresh private identity scope."
}
New-Item -ItemType Directory -Path $privateRoot -Force | Out-Null
New-Item -ItemType Directory -Path $handoffScope -Force | Out-Null
$existingPrivateRuns = @{}
if (Test-Path -LiteralPath $privateExecutionBase -PathType Container) {
    foreach ($child in @(Get-ChildItem -LiteralPath $privateExecutionBase -Directory -Force)) {
        $existingPrivateRuns[$child.Name] = $true
    }
}

$gateVariables = @(
    'OPENLINEOPS_PRODUCTION_CLOSURE_HANDOFF_PATH',
    'OPENLINEOPS_STUDIO_TWO_AGENT_FORMAL_GATE',
    'OPENLINEOPS_STUDIO_TWO_AGENT_CLEANUP_GATE',
    'OPENLINEOPS_STUDIO_TWO_AGENT_ACCOUNT_SUFFIX',
    'OPENLINEOPS_STUDIO_TWO_AGENT_EVIDENCE_PATH',
    'OPENLINEOPS_STUDIO_TWO_AGENT_RELEASE_MANIFEST_PATH',
    'OPENLINEOPS_STAGED_AGENT_BUNDLE_ROOT',
    'OPENLINEOPS_STAGED_SAMPLE_PLUGIN_ROOT',
    'OPENLINEOPS_STAGED_API_BUNDLE_ROOT',
    'OPENLINEOPS_RABBITMQ_URI',
    'OPENLINEOPS_POSTGRES_CONNECTION_STRING',
    'DOTNET_CLI_UI_LANGUAGE',
    'VSLANG')
$previous = @{}
foreach ($name in $gateVariables) {
    $previous[$name] = [System.Environment]::GetEnvironmentVariable($name)
}

$primaryFailure = $null
$cleanupFailures = [System.Collections.Generic.List[System.Exception]]::new()
$succeeded = $false
$runtimeMayHaveMutated = $false
try {
    Invoke-TemporaryAccountPreflight @($entryAccount, $downstreamAccount)
    Reset-RepoDirectory $resolvedEvidenceRoot
    $env:OPENLINEOPS_PRODUCTION_CLOSURE_HANDOFF_PATH = $handoffPath
    $npm = (Get-Command npm.cmd -ErrorAction Stop).Source
    $packagedStdout = Join-Path $privateRoot 'packaged.stdout.log'
    $packagedStderr = Join-Path $privateRoot 'packaged.stderr.log'
    Invoke-BoundedPrivateProcess `
        -FilePath $npm `
        -Arguments @('run', 'e2e:production-closure:packaged') `
        -WorkingDirectory (Join-Path $RepoRoot 'apps/desktop') `
        -StdoutPath $packagedStdout `
        -StderrPath $packagedStderr `
        -TimeoutSeconds $PackagedTimeoutSeconds `
        -FailureCode 'PACKAGED-PRODUCTION-CLOSURE'
    if (-not (Test-Path -LiteralPath $handoffPath -PathType Leaf)) {
        throw "Packaged production closure did not emit its one-shot private handoff."
    }
    & (Join-Path $PSScriptRoot 'verify-production-closure-evidence.ps1') `
        -EvidenceRoot $resolvedProductionRoot `
        -RequirePassed

    $evidencePath = Join-Path $resolvedEvidenceRoot 'evidence-manifest.json'
    $env:OPENLINEOPS_STUDIO_TWO_AGENT_FORMAL_GATE = 'true'
    $env:OPENLINEOPS_STUDIO_TWO_AGENT_CLEANUP_GATE = $null
    $env:OPENLINEOPS_STUDIO_TWO_AGENT_ACCOUNT_SUFFIX = $suffix
    $env:OPENLINEOPS_STUDIO_TWO_AGENT_EVIDENCE_PATH = $evidencePath
    $env:OPENLINEOPS_STUDIO_TWO_AGENT_RELEASE_MANIFEST_PATH = $resolvedReleaseManifest
    $env:OPENLINEOPS_STAGED_AGENT_BUNDLE_ROOT = $stagedAgentRoot
    $env:OPENLINEOPS_STAGED_SAMPLE_PLUGIN_ROOT = $stagedPluginRoot
    $env:OPENLINEOPS_STAGED_API_BUNDLE_ROOT = $stagedApiRoot
    $env:OPENLINEOPS_RABBITMQ_URI = $broker.AbsoluteUri
    $env:OPENLINEOPS_POSTGRES_CONNECTION_STRING = $PostgreSqlConnectionString
    $env:DOTNET_CLI_UI_LANGUAGE = 'en-US'
    $env:VSLANG = '1033'

    $testRoot = Join-Path $privateRoot 'formal-test'
    New-Item -ItemType Directory -Path $testRoot | Out-Null
    $testStdout = Join-Path $testRoot 'test.stdout.log'
    $testStderr = Join-Path $testRoot 'test.stderr.log'
    $trxName = 'studio-two-agent.trx'
    $trxPath = Join-Path $testRoot $trxName
    $arguments = @(
        'test', $TestProject,
        '--configuration', $Configuration,
        '--filter', "FullyQualifiedName=$ExactTest",
        '--results-directory', $testRoot,
        '--logger', "trx;LogFileName=$trxName",
        '--logger', 'console;verbosity=minimal')
    if ($NoBuild) { $arguments += '--no-build' }
    if ($NoRestore) { $arguments += '--no-restore' }
    $runtimeMayHaveMutated = $true
    Invoke-BoundedPrivateProcess `
        -FilePath $DotNetPath `
        -Arguments $arguments `
        -WorkingDirectory $RepoRoot `
        -StdoutPath $testStdout `
        -StderrPath $testStderr `
        -TimeoutSeconds $TestTimeoutSeconds `
        -FailureCode 'STUDIO-TWO-AGENT-FORMAL-TEST'
    Assert-ExactPassedTrx $trxPath $ExactTest
    & (Join-Path $PSScriptRoot 'verify-studio-two-agent-production-evidence.ps1') `
        -EvidenceRoot $resolvedEvidenceRoot
    $succeeded = $true
}
catch {
    $primaryFailure = $_.Exception
}
finally {
    if (-not $succeeded -and $runtimeMayHaveMutated) {
        try {
            Invoke-StudioCompensation `
                -PrivateRoot $privateRoot `
                -HandoffPath $handoffPath `
                -Suffix $suffix
        }
        catch { $cleanupFailures.Add($_.Exception) }
    }

    foreach ($target in @(
            [pscustomobject]@{ Base = $harnessBase; Path = $harnessRoot },
            [pscustomobject]@{ Base = $handoffBase; Path = $handoffScope })) {
        try { Remove-OwnedDirectChild $target.Base $target.Path } catch { $cleanupFailures.Add($_.Exception) }
    }
    if (Test-Path -LiteralPath $privateExecutionBase -PathType Container) {
        foreach ($child in @(Get-ChildItem -LiteralPath $privateExecutionBase -Directory -Force)) {
            if (-not $existingPrivateRuns.ContainsKey($child.Name)) {
                try { Remove-OwnedDirectChild $privateExecutionBase $child.FullName } catch { $cleanupFailures.Add($_.Exception) }
            }
        }
    }
    foreach ($account in @($entryAccount, $downstreamAccount)) {
        try { Remove-ExactLocalAccount $account } catch { $cleanupFailures.Add($_.Exception) }
    }
    foreach ($name in $gateVariables) {
        try { [System.Environment]::SetEnvironmentVariable($name, $previous[$name]) } catch { $cleanupFailures.Add($_.Exception) }
    }
    try { Remove-OwnedDirectChild $privateGateBase $privateRoot } catch { $cleanupFailures.Add($_.Exception) }

    if (-not $succeeded) {
        foreach ($publicRoot in @($resolvedEvidenceRoot, $resolvedProductionRoot)) {
            try {
                if (Test-Path -LiteralPath $publicRoot) {
                    Assert-NoReparseAncestors $publicRoot
                    Assert-NoReparseTree $publicRoot
                    Remove-Item -LiteralPath $publicRoot -Recurse -Force
                }
            }
            catch { $cleanupFailures.Add($_.Exception) }
        }
    }
}

if ($null -ne $primaryFailure) {
    if ($cleanupFailures.Count -eq 0) { throw $primaryFailure }
    $failures = [System.Collections.Generic.List[System.Exception]]::new()
    $failures.Add($primaryFailure)
    foreach ($failure in $cleanupFailures) { $failures.Add($failure) }
    throw [System.AggregateException]::new(
        "Studio two-Agent formal production gate failed and bounded cleanup was incomplete.",
        $failures.ToArray())
}
if ($cleanupFailures.Count -gt 0) {
    throw [System.AggregateException]::new(
        "Studio two-Agent formal production gate cleanup was incomplete.",
        $cleanupFailures.ToArray())
}

Write-Host "Studio packaged-to-two-Agent production closure passed."
Write-Host " - Packaged evidence: artifacts/production-closure-e2e"
Write-Host " - Two-Agent evidence: output/studio-two-agent-production-closure"

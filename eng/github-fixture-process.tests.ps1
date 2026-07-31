param(
    [string] $WorkRoot = "output/github-fixture-process-tests"
)

$ErrorActionPreference = "Stop"
$RepoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
. (Join-Path $PSScriptRoot "github-fixture-process.ps1")

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
        throw "GitHub fixture process test output must remain inside the repository."
    }

    return $resolved
}

function Assert-RejectedEnvironment {
    param([Parameter(Mandatory = $true)][hashtable] $Environment)

    try {
        Invoke-GitHubFixturePowerShellProcess `
            -ScriptPath $probePath `
            -Arguments @("-OutputPath", $probeOutputPath) `
            -GitHubEnvironment $Environment | Out-Null
    }
    catch {
        if ($_.Exception.Message -cmatch "requires exactly the repository, commit, run, and server") {
            return
        }

        throw
    }

    throw "GitHub fixture process accepted a missing or unexpected context variable."
}

function Assert-AmbientEnvironmentRestored {
    param([Parameter(Mandatory = $true)][hashtable] $Expected)

    foreach ($entry in $Expected.GetEnumerator()) {
        if ([Environment]::GetEnvironmentVariable(
                $entry.Key,
                [EnvironmentVariableTarget]::Process) -cne $entry.Value) {
            throw "GitHub fixture process did not restore ambient variable '$($entry.Key)'."
        }
    }
}

$resolvedWorkRoot = Resolve-RepoPath $WorkRoot
if (Test-Path -LiteralPath $resolvedWorkRoot) {
    Remove-Item -LiteralPath $resolvedWorkRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $resolvedWorkRoot -Force | Out-Null
$probePath = Join-Path $resolvedWorkRoot "probe.ps1"
$probeOutputPath = Join-Path $resolvedWorkRoot "probe.json"
$exitBehaviorProbePath = Join-Path $resolvedWorkRoot "exit-behavior-probe.ps1"
$probe = @'
param([Parameter(Mandatory = $true)][string] $OutputPath)
$ErrorActionPreference = "Stop"
$acl = Get-Acl -LiteralPath $PSCommandPath
if ([string]::IsNullOrWhiteSpace($acl.Owner)) {
    throw "The Windows PowerShell security module did not return an ACL owner."
}
$document = [ordered]@{
    executablePath = [System.Diagnostics.Process]::GetCurrentProcess().MainModule.FileName
    systemRoot = $env:SystemRoot
    windir = $env:WINDIR
    modulePath = $env:PSModulePath
    repository = $env:GITHUB_REPOSITORY
    commitSha = $env:GITHUB_SHA
    runId = $env:GITHUB_RUN_ID
    serverUrl = $env:GITHUB_SERVER_URL
    unexpectedGitHubEnvironment = $env:GITHUB_ENV
    fixtureModuleTransport = $env:OPENLINEOPS_GITHUB_FIXTURE_MODULE_PATH
    fixtureScriptTransport = $env:OPENLINEOPS_GITHUB_FIXTURE_SCRIPT_PATH
    fixtureArgumentsTransport = $env:OPENLINEOPS_GITHUB_FIXTURE_ARGUMENTS
    aclOwner = $acl.Owner
}
[System.IO.File]::WriteAllText(
    $OutputPath,
    (($document | ConvertTo-Json -Depth 4) + [Environment]::NewLine),
    [System.Text.UTF8Encoding]::new($false))
'@
[System.IO.File]::WriteAllText(
    $probePath,
    $probe,
    [System.Text.UTF8Encoding]::new($false))
$exitBehaviorProbe = @'
param([Parameter(Mandatory = $true)][string] $Mode)
$ErrorActionPreference = "Stop"
switch ($Mode) {
    "handled-native-failure" {
        & (Join-Path ([Environment]::SystemDirectory) "cmd.exe") /c exit 19
        if ($LASTEXITCODE -ne 19) {
            throw "The native failure probe did not produce its expected code."
        }
        return
    }
    "explicit-exit" {
        exit 7
    }
    "uncaught-throw" {
        throw "fixture-uncaught-throw"
    }
    default {
        throw "Unknown exit behavior probe mode."
    }
}
'@
[System.IO.File]::WriteAllText(
    $exitBehaviorProbePath,
    $exitBehaviorProbe,
    [System.Text.UTF8Encoding]::new($false))

$requiredNames = @(
    "GITHUB_REPOSITORY",
    "GITHUB_SHA",
    "GITHUB_RUN_ID",
    "GITHUB_SERVER_URL",
    "GITHUB_ENV",
    "OPENLINEOPS_GITHUB_FIXTURE_MODULE_PATH",
    "OPENLINEOPS_GITHUB_FIXTURE_SCRIPT_PATH",
    "OPENLINEOPS_GITHUB_FIXTURE_ARGUMENTS",
    "SystemRoot",
    "WINDIR",
    "PSModulePath")
$originalNames = @(
    @([Environment]::GetEnvironmentVariables(
            [EnvironmentVariableTarget]::Process).Keys | Where-Object {
            $_ -like "GITHUB_*"
        }) +
    $requiredNames |
        Sort-Object -Unique)
$originalEnvironment = @{}
foreach ($name in $originalNames) {
    $originalEnvironment[$name] = [Environment]::GetEnvironmentVariable(
        $name,
        [EnvironmentVariableTarget]::Process)
}

$fixtureEnvironment = @{
    GITHUB_REPOSITORY = "fixture/openlineops"
    GITHUB_SHA = "0123456789abcdef0123456789abcdef01234567"
    GITHUB_RUN_ID = "123456789"
    GITHUB_SERVER_URL = "https://github.com"
}
$trustedHost = Get-GitHubFixtureWindowsPowerShellHost
$pollutedEnvironment = @{
    GITHUB_REPOSITORY = "ambient/openlineops"
    GITHUB_SHA = "ffffffffffffffffffffffffffffffffffffffff"
    GITHUB_RUN_ID = "987654321"
    GITHUB_SERVER_URL = "https://ambient.invalid"
    GITHUB_ENV = "C:\untrusted\github-env"
    OPENLINEOPS_GITHUB_FIXTURE_MODULE_PATH = "C:\ambient\module-transport"
    OPENLINEOPS_GITHUB_FIXTURE_SCRIPT_PATH = "C:\ambient\script-transport"
    OPENLINEOPS_GITHUB_FIXTURE_ARGUMENTS = "ambient-arguments-transport"
    SystemRoot = $trustedHost.SystemRoot
    WINDIR = $trustedHost.SystemRoot
    PSModulePath = "C:\untrusted-modules"
}

try {
    foreach ($entry in $pollutedEnvironment.GetEnumerator()) {
        [Environment]::SetEnvironmentVariable(
            $entry.Key,
            $entry.Value,
            [EnvironmentVariableTarget]::Process)
    }

    $result = Invoke-GitHubFixturePowerShellProcess `
        -ScriptPath $probePath `
        -Arguments @("-OutputPath", $probeOutputPath) `
        -GitHubEnvironment $fixtureEnvironment
    if ($result.ExitCode -ne 0) {
        throw "GitHub fixture child failed: $($result.Text)"
    }

    $probeDocument = Get-Content -LiteralPath $probeOutputPath -Raw | ConvertFrom-Json
    if (-not $probeDocument.executablePath.Equals(
            $trustedHost.PowerShellPath,
            [System.StringComparison]::OrdinalIgnoreCase) `
        -or -not $probeDocument.systemRoot.Equals(
            $trustedHost.SystemRoot,
            [System.StringComparison]::OrdinalIgnoreCase) `
        -or -not $probeDocument.windir.Equals(
            $trustedHost.SystemRoot,
            [System.StringComparison]::OrdinalIgnoreCase) `
        -or -not $probeDocument.modulePath.Equals(
            $trustedHost.SystemModulePath,
            [System.StringComparison]::OrdinalIgnoreCase) `
        -or $probeDocument.repository -cne $fixtureEnvironment.GITHUB_REPOSITORY `
        -or $probeDocument.commitSha -cne $fixtureEnvironment.GITHUB_SHA `
        -or $probeDocument.runId -cne $fixtureEnvironment.GITHUB_RUN_ID `
        -or $probeDocument.serverUrl -cne $fixtureEnvironment.GITHUB_SERVER_URL `
        -or $null -ne $probeDocument.unexpectedGitHubEnvironment `
        -or $null -ne $probeDocument.fixtureModuleTransport `
        -or $null -ne $probeDocument.fixtureScriptTransport `
        -or $null -ne $probeDocument.fixtureArgumentsTransport `
        -or [string]::IsNullOrWhiteSpace($probeDocument.aclOwner)) {
        throw "GitHub fixture child did not use the exact trusted host and isolated environment."
    }

    Assert-AmbientEnvironmentRestored -Expected $pollutedEnvironment

    $handledFailure = Invoke-GitHubFixturePowerShellProcess `
        -ScriptPath $exitBehaviorProbePath `
        -Arguments @("-Mode", "handled-native-failure") `
        -GitHubEnvironment $fixtureEnvironment
    if ($handledFailure.ExitCode -ne 0) {
        throw "A target that handled a native failure did not return exit code 0."
    }
    Assert-AmbientEnvironmentRestored -Expected $pollutedEnvironment

    $explicitExit = Invoke-GitHubFixturePowerShellProcess `
        -ScriptPath $exitBehaviorProbePath `
        -Arguments @("-Mode", "explicit-exit") `
        -GitHubEnvironment $fixtureEnvironment
    if ($explicitExit.ExitCode -ne 7) {
        throw "An explicit target exit did not propagate exit code 7."
    }
    Assert-AmbientEnvironmentRestored -Expected $pollutedEnvironment

    $uncaughtThrow = Invoke-GitHubFixturePowerShellProcess `
        -ScriptPath $exitBehaviorProbePath `
        -Arguments @("-Mode", "uncaught-throw") `
        -GitHubEnvironment $fixtureEnvironment
    if ($uncaughtThrow.ExitCode -eq 0 `
        -or $uncaughtThrow.Text -cnotmatch "fixture-uncaught-throw") {
        throw "An uncaught target exception did not produce a diagnostic nonzero exit."
    }
    Assert-AmbientEnvironmentRestored -Expected $pollutedEnvironment

    $oversizedFailureObserved = $false
    try {
        Invoke-GitHubFixturePowerShellProcess `
            -ScriptPath $probePath `
            -Arguments @("-OutputPath", ("x" * 40000)) `
            -GitHubEnvironment $fixtureEnvironment | Out-Null
    }
    catch {
        $oversizedFailureObserved = $true
    }
    if (-not $oversizedFailureObserved) {
        throw "The oversized fixture environment regression did not reach its expected failure path."
    }
    Assert-AmbientEnvironmentRestored -Expected $pollutedEnvironment

    Assert-RejectedEnvironment -Environment @{
        GITHUB_REPOSITORY = "fixture/openlineops"
        GITHUB_SHA = "0123456789abcdef0123456789abcdef01234567"
        GITHUB_RUN_ID = "123456789"
    }
    Assert-RejectedEnvironment -Environment @{
        GITHUB_REPOSITORY = "fixture/openlineops"
        GITHUB_SHA = "0123456789abcdef0123456789abcdef01234567"
        GITHUB_RUN_ID = "123456789"
        GITHUB_SERVER_URL = "https://github.com"
        GITHUB_ENV = "C:\untrusted\github-env"
    }
}
finally {
    $currentGitHubNames = @([Environment]::GetEnvironmentVariables(
            [EnvironmentVariableTarget]::Process).Keys | Where-Object {
            $_ -like "GITHUB_*"
        })
    foreach ($name in @($currentGitHubNames + $originalNames | Sort-Object -Unique)) {
        [Environment]::SetEnvironmentVariable(
            $name,
            $null,
            [EnvironmentVariableTarget]::Process)
    }
    foreach ($name in $originalNames) {
        [Environment]::SetEnvironmentVariable(
            $name,
            $originalEnvironment[$name],
            [EnvironmentVariableTarget]::Process)
    }
}

Write-Host "GitHub fixture PowerShell process verification passed."
exit 0

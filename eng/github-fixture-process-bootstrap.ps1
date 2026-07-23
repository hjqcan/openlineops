$ErrorActionPreference = "Stop"

$modulePath = [Environment]::GetEnvironmentVariable(
    "OPENLINEOPS_GITHUB_FIXTURE_MODULE_PATH",
    [EnvironmentVariableTarget]::Process)
$scriptPath = [Environment]::GetEnvironmentVariable(
    "OPENLINEOPS_GITHUB_FIXTURE_SCRIPT_PATH",
    [EnvironmentVariableTarget]::Process)
$argumentsPayload = [Environment]::GetEnvironmentVariable(
    "OPENLINEOPS_GITHUB_FIXTURE_ARGUMENTS",
    [EnvironmentVariableTarget]::Process)
if ([string]::IsNullOrWhiteSpace($modulePath) `
    -or [string]::IsNullOrWhiteSpace($scriptPath) `
    -or [string]::IsNullOrWhiteSpace($argumentsPayload)) {
    throw "GitHub fixture PowerShell bootstrap environment is incomplete."
}

$env:PSModulePath = $modulePath
$payloadBytes = [Convert]::FromBase64String($argumentsPayload)
$stream = [System.IO.MemoryStream]::new($payloadBytes, $false)
$reader = [System.IO.BinaryReader]::new(
    $stream,
    [System.Text.UTF8Encoding]::new($false, $true),
    $false)
try {
    $argumentCount = $reader.ReadInt32()
    if ($argumentCount -lt 0 -or $argumentCount -gt 1024) {
        throw "GitHub fixture PowerShell argument count is invalid."
    }

    $targetParameters = @{}
    for ($index = 0; $index -lt $argumentCount; $index++) {
        $name = $reader.ReadString()
        $hasValue = $reader.ReadBoolean()
        if ($targetParameters.ContainsKey($name)) {
            throw "GitHub fixture PowerShell argument payload contains a duplicate parameter."
        }
        $targetParameters[$name] = if ($hasValue) {
            $reader.ReadString()
        }
        else {
            $true
        }
    }
    if ($stream.Position -ne $stream.Length) {
        throw "GitHub fixture PowerShell argument payload has trailing data."
    }
}
finally {
    $reader.Dispose()
    $stream.Dispose()
}

[Environment]::SetEnvironmentVariable(
    "OPENLINEOPS_GITHUB_FIXTURE_MODULE_PATH",
    $null,
    [EnvironmentVariableTarget]::Process)
[Environment]::SetEnvironmentVariable(
    "OPENLINEOPS_GITHUB_FIXTURE_SCRIPT_PATH",
    $null,
    [EnvironmentVariableTarget]::Process)
[Environment]::SetEnvironmentVariable(
    "OPENLINEOPS_GITHUB_FIXTURE_ARGUMENTS",
    $null,
    [EnvironmentVariableTarget]::Process)

& $scriptPath @targetParameters
$targetSucceeded = $?
$targetExitCode = $LASTEXITCODE
if (-not $targetSucceeded -and $null -ne $targetExitCode) {
    exit $targetExitCode
}
exit 0

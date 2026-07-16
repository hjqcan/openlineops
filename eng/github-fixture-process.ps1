function Get-GitHubFixtureWindowsPowerShellHost {
    $systemRoot = [System.IO.Path]::GetFullPath(
        [Environment]::GetFolderPath([Environment+SpecialFolder]::Windows))
    $powerShellRelativeSegments = @("System32", "WindowsPowerShell", "v1.0", "powershell.exe")
    $powerShellPath = [System.IO.Path]::Combine(
        [string[]] (@($systemRoot) + $powerShellRelativeSegments))
    $systemModulePath = Join-Path `
        ([System.IO.Path]::GetDirectoryName($powerShellPath)) `
        "Modules"
    if (-not [System.IO.Path]::IsPathRooted($systemRoot) `
        -or -not (Test-Path -LiteralPath $powerShellPath -PathType Leaf) `
        -or -not (Test-Path -LiteralPath $systemModulePath -PathType Container)) {
        throw "The trusted Windows PowerShell system host could not be resolved."
    }

    return [pscustomobject]@{
        SystemRoot = $systemRoot
        PowerShellPath = $powerShellPath
        SystemModulePath = $systemModulePath
    }
}

function Invoke-GitHubFixturePowerShellProcess {
    param(
        [Parameter(Mandatory = $true)][string] $ScriptPath,
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][string[]] $Arguments,
        [Parameter(Mandatory = $true)][hashtable] $GitHubEnvironment
    )

    $requiredGitHubVariables = @(
        "GITHUB_REPOSITORY",
        "GITHUB_SHA",
        "GITHUB_RUN_ID",
        "GITHUB_SERVER_URL")
    $unexpectedVariables = @($GitHubEnvironment.Keys | Where-Object {
            $requiredGitHubVariables -cnotcontains $_
        })
    $missingVariables = @($requiredGitHubVariables | Where-Object {
            -not $GitHubEnvironment.ContainsKey($_) `
                -or [string]::IsNullOrWhiteSpace([string] $GitHubEnvironment[$_])
        })
    if ($unexpectedVariables.Count -ne 0 -or $missingVariables.Count -ne 0) {
        throw "Synthetic PowerShell child requires exactly the repository, commit, run, and server GitHub environment tuple."
    }

    $powerShellHost = Get-GitHubFixtureWindowsPowerShellHost
    $systemRoot = $powerShellHost.SystemRoot
    $powerShellPath = $powerShellHost.PowerShellPath
    $systemModulePath = $powerShellHost.SystemModulePath
    $bootstrapPath = Join-Path $PSScriptRoot "github-fixture-process-bootstrap.ps1"
    $resolvedScriptPath = [System.IO.Path]::GetFullPath($ScriptPath)
    if (-not (Test-Path -LiteralPath $bootstrapPath -PathType Leaf) `
        -or -not (Test-Path -LiteralPath $resolvedScriptPath -PathType Leaf)) {
        throw "The GitHub fixture bootstrap or target script does not exist."
    }

    $tokens = $null
    $parseErrors = $null
    $scriptAst = [System.Management.Automation.Language.Parser]::ParseFile(
        $resolvedScriptPath,
        [ref] $tokens,
        [ref] $parseErrors)
    if ($parseErrors.Count -ne 0) {
        throw "The GitHub fixture target script could not be parsed: $($parseErrors[0].Message)"
    }
    $declaredParameters = @{}
    if ($null -ne $scriptAst.ParamBlock) {
        foreach ($parameter in $scriptAst.ParamBlock.Parameters) {
            $name = $parameter.Name.VariablePath.UserPath
            $declaredParameters[$name] = [pscustomobject]@{
                Name = $name
                IsSwitch = $parameter.StaticType -eq `
                    [System.Management.Automation.SwitchParameter]
            }
        }
    }

    $argumentRecords = New-Object System.Collections.Generic.List[object]
    $argumentIndex = 0
    $seenParameters = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::OrdinalIgnoreCase)
    while ($argumentIndex -lt $Arguments.Count) {
        $argument = [string] $Arguments[$argumentIndex]
        if ($argument -cnotmatch '^-(?<name>[A-Za-z_][A-Za-z0-9_]*)$') {
            throw "GitHub fixture target arguments must use explicit named parameters."
        }

        $requestedName = $Matches.name
        if (-not $seenParameters.Add($requestedName)) {
            throw "GitHub fixture target parameter '$requestedName' was supplied more than once."
        }

        $declared = $declaredParameters[$requestedName]
        $hasValue = if ($null -ne $declared) {
            -not $declared.IsSwitch
        }
        else {
            $argumentIndex + 1 -lt $Arguments.Count `
                -and [string] $Arguments[$argumentIndex + 1] -cnotmatch `
                    '^-[A-Za-z_][A-Za-z0-9_]*$'
        }
        $value = $null
        if ($hasValue) {
            $argumentIndex++
            if ($argumentIndex -ge $Arguments.Count) {
                throw "GitHub fixture target parameter '$requestedName' is missing its value."
            }
            $value = [string] $Arguments[$argumentIndex]
        }

        $argumentRecords.Add([pscustomobject]@{
                Name = if ($null -ne $declared) { $declared.Name } else { $requestedName }
                HasValue = $hasValue
                Value = $value
            }) | Out-Null
        $argumentIndex++
    }

    $argumentStream = [System.IO.MemoryStream]::new()
    $argumentWriter = [System.IO.BinaryWriter]::new(
        $argumentStream,
        [System.Text.UTF8Encoding]::new($false, $true),
        $true)
    try {
        $argumentWriter.Write([int] $argumentRecords.Count)
        foreach ($record in $argumentRecords) {
            $argumentWriter.Write([string] $record.Name)
            $argumentWriter.Write([bool] $record.HasValue)
            if ($record.HasValue) {
                $argumentWriter.Write([string] $record.Value)
            }
        }
        $argumentWriter.Flush()
        $argumentsPayload = [Convert]::ToBase64String($argumentStream.ToArray())
    }
    finally {
        $argumentWriter.Dispose()
        $argumentStream.Dispose()
    }

    $githubVariableNames = @(
        @([Environment]::GetEnvironmentVariables(
                [EnvironmentVariableTarget]::Process).Keys | Where-Object {
                $_ -like "GITHUB_*"
            }) +
        $requiredGitHubVariables |
            Sort-Object -Unique)
    $previousGitHubEnvironment = @{}
    foreach ($name in $githubVariableNames) {
        $previousGitHubEnvironment[$name] = [Environment]::GetEnvironmentVariable(
            $name,
            [EnvironmentVariableTarget]::Process)
    }

    $hostEnvironment = @{
        SystemRoot = [Environment]::GetEnvironmentVariable(
            "SystemRoot",
            [EnvironmentVariableTarget]::Process)
        WINDIR = [Environment]::GetEnvironmentVariable(
            "WINDIR",
            [EnvironmentVariableTarget]::Process)
        PSModulePath = [Environment]::GetEnvironmentVariable(
            "PSModulePath",
            [EnvironmentVariableTarget]::Process)
        OPENLINEOPS_GITHUB_FIXTURE_MODULE_PATH = [Environment]::GetEnvironmentVariable(
            "OPENLINEOPS_GITHUB_FIXTURE_MODULE_PATH",
            [EnvironmentVariableTarget]::Process)
        OPENLINEOPS_GITHUB_FIXTURE_SCRIPT_PATH = [Environment]::GetEnvironmentVariable(
            "OPENLINEOPS_GITHUB_FIXTURE_SCRIPT_PATH",
            [EnvironmentVariableTarget]::Process)
        OPENLINEOPS_GITHUB_FIXTURE_ARGUMENTS = [Environment]::GetEnvironmentVariable(
            "OPENLINEOPS_GITHUB_FIXTURE_ARGUMENTS",
            [EnvironmentVariableTarget]::Process)
    }

    $previousErrorActionPreference = $ErrorActionPreference
    $output = @()
    $exitCode = 1
    try {
        foreach ($name in $githubVariableNames) {
            [Environment]::SetEnvironmentVariable(
                $name,
                $null,
                [EnvironmentVariableTarget]::Process)
        }
        foreach ($name in $requiredGitHubVariables) {
            [Environment]::SetEnvironmentVariable(
                $name,
                [string] $GitHubEnvironment[$name],
                [EnvironmentVariableTarget]::Process)
        }
        [Environment]::SetEnvironmentVariable(
            "SystemRoot",
            $systemRoot,
            [EnvironmentVariableTarget]::Process)
        [Environment]::SetEnvironmentVariable(
            "WINDIR",
            $systemRoot,
            [EnvironmentVariableTarget]::Process)
        [Environment]::SetEnvironmentVariable(
            "PSModulePath",
            $systemModulePath,
            [EnvironmentVariableTarget]::Process)
        [Environment]::SetEnvironmentVariable(
            "OPENLINEOPS_GITHUB_FIXTURE_MODULE_PATH",
            $systemModulePath,
            [EnvironmentVariableTarget]::Process)
        [Environment]::SetEnvironmentVariable(
            "OPENLINEOPS_GITHUB_FIXTURE_SCRIPT_PATH",
            $resolvedScriptPath,
            [EnvironmentVariableTarget]::Process)
        [Environment]::SetEnvironmentVariable(
            "OPENLINEOPS_GITHUB_FIXTURE_ARGUMENTS",
            $argumentsPayload,
            [EnvironmentVariableTarget]::Process)

        $ErrorActionPreference = "Continue"
        $output = @(& $powerShellPath `
                -NoProfile `
                -NonInteractive `
                -ExecutionPolicy Bypass `
                -File $bootstrapPath 2>&1)
        $exitCode = $LASTEXITCODE
        if ($null -eq $exitCode) {
            $exitCode = 0
        }
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
        foreach ($name in $hostEnvironment.Keys) {
            [Environment]::SetEnvironmentVariable(
                $name,
                $hostEnvironment[$name],
                [EnvironmentVariableTarget]::Process)
        }
        foreach ($name in $githubVariableNames) {
            [Environment]::SetEnvironmentVariable(
                $name,
                $null,
                [EnvironmentVariableTarget]::Process)
        }
        foreach ($name in $githubVariableNames) {
            [Environment]::SetEnvironmentVariable(
                $name,
                $previousGitHubEnvironment[$name],
                [EnvironmentVariableTarget]::Process)
        }
    }

    return [pscustomobject]@{
        ExitCode = $exitCode
        Text = (($output | ForEach-Object { $_.ToString() }) -join [Environment]::NewLine)
    }
}

param()

$ErrorActionPreference = "Stop"

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$failures = [System.Collections.Generic.List[string]]::new()

function Read-RequiredFile {
    param([Parameter(Mandatory = $true)][string] $RelativePath)

    $path = Join-Path $repoRoot $RelativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        $failures.Add("Missing required Coordinator trial security file: $RelativePath") | Out-Null
        return ""
    }

    return Get-Content -LiteralPath $path -Raw
}

$settingsContent = Read-RequiredFile "src/OpenLineOps.Api/appsettings.json"
if ($settingsContent.Length -gt 0) {
    try {
        $settings = $settingsContent | ConvertFrom-Json
        $trialSettings = $settings.OpenLineOps.Devices.ExternalProgramTrials
        $trialPropertyNames = @($trialSettings.PSObject.Properties.Name)
        $invalidTrialSettings = $null -eq $trialSettings `
            -or $trialPropertyNames.Count -ne 1 `
            -or $trialPropertyNames -cnotcontains "ApplicationExecutablePolicy" `
            -or $trialSettings.ApplicationExecutablePolicy -cne "Disabled"
        if ($invalidTrialSettings) {
            $failures.Add(
                "Coordinator appsettings must expose only the exact Disabled application-executable trial policy.") |
                Out-Null
        }
    }
    catch {
        $failures.Add("Coordinator appsettings is not valid JSON: $($_.Exception.Message)") | Out-Null
    }
}

$optionsContent = Read-RequiredFile `
    "modules/OpenLineOps.Devices.Api/ExternalPrograms/ExternalProgramProtocolTrialOptions.cs"
foreach ($requiredText in @(
    "ApplicationExecutableProtocolTrialPolicies.Disabled",
    "OperatingSystem.IsWindows()",
    "hostOptions.RequireRestrictedHostIdentity",
    "hostOptions.RequireImmutableContentProtection",
    "hostOptions.RequireAppContainerIsolation"
)) {
    if ($optionsContent.IndexOf($requiredText, [System.StringComparison]::Ordinal) -lt 0) {
        $failures.Add(
            "Executable trial policy is missing required fail-closed check '$requiredText'.") |
            Out-Null
    }
}

$dependencyInjectionContent = Read-RequiredFile `
    "modules/OpenLineOps.Devices.Api/DependencyInjection/DevicesModuleServiceCollectionExtensions.cs"
if ($dependencyInjectionContent.IndexOf(
        "externalProgramProtocolTrialOptions.Validate(externalProgramHostOptions);",
        [System.StringComparison]::Ordinal) -lt 0) {
    $failures.Add(
        "Devices DI must validate executable trial policy against the effective external-program host.") |
        Out-Null
}
foreach ($requiredRegistration in @(
    "services.Replace(ServiceDescriptor.Singleton<IExternalProgramHost, ExternalProgramHost>())",
    "IExternalProgramTrialExecutor,",
    "ExternalProgramResourceTrialExecutor>())"
)) {
    if ($dependencyInjectionContent.IndexOf(
            $requiredRegistration,
            [System.StringComparison]::Ordinal) -lt 0) {
        $failures.Add(
            "Devices DI must replace overrideable executable trial boundary registration '$requiredRegistration'.") |
            Out-Null
    }
}

$executorContent = Read-RequiredFile `
    "modules/OpenLineOps.Devices.Api/ExternalPrograms/ExternalProgramResourceTrialExecutor.cs"
$guardIndex = $executorContent.IndexOf(
    "&& !_options.AllowsApplicationExecutable",
    [System.StringComparison]::Ordinal)
$hostCallIndex = $executorContent.IndexOf(
    "await _host.ExecuteAsync(",
    [System.StringComparison]::Ordinal)
if ($guardIndex -lt 0 -or $hostCallIndex -lt 0 -or $guardIndex -gt $hostCallIndex) {
    $failures.Add(
        "Executable trial denial must execute before content access and external-program host invocation.") |
        Out-Null
}
if ($executorContent.IndexOf(
        "Projects.ApplicationExecutableProtocolTrialDisabled",
        [System.StringComparison]::Ordinal) -lt 0) {
    $failures.Add("Executable trial denial must use the stable conflict contract.") | Out-Null
}

$controllerContent = Read-RequiredFile `
    "modules/OpenLineOps.Projects.Api/Controllers/ExternalProgramResourcesController.cs"
$conflictResponseCount = [regex]::Matches(
    $controllerContent,
    '\[ProducesResponseType<ProblemDetails>\(StatusCodes\.Status409Conflict\)\]').Count
if ($conflictResponseCount -lt 2) {
    $failures.Add(
        "Both saved-resource and unsaved-definition trial endpoints must declare HTTP 409.") |
        Out-Null
}

if ($failures.Count -gt 0) {
    $failures | Sort-Object -Unique | ForEach-Object { Write-Error $_ }
    throw "Coordinator external-program trial security verification failed."
}

Write-Host "Coordinator executable protocol trials fail closed."

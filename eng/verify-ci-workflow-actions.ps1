param(
    [string] $WorkflowPath = ".github/workflows/build.yml"
)

$ErrorActionPreference = "Stop"

$RepoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$Failures = New-Object System.Collections.Generic.List[string]

function Resolve-RepoPath {
    param([Parameter(Mandatory = $true)][string] $Path)

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }

    return [System.IO.Path]::GetFullPath((Join-Path $RepoRoot $Path))
}

function Add-Failure {
    param([Parameter(Mandatory = $true)][string] $Message)
    $Failures.Add($Message) | Out-Null
}

function Test-ContentContains {
    param(
        [Parameter(Mandatory = $true)][string] $Content,
        [Parameter(Mandatory = $true)][string] $Pattern,
        [Parameter(Mandatory = $true)][string] $Message
    )

    if ($Content -notmatch $Pattern) {
        Add-Failure $Message
    }
}

function Test-StepCannotContinueOnError {
    param(
        [Parameter(Mandatory = $true)][string] $Content,
        [Parameter(Mandatory = $true)][string] $StepName
    )

    $stepPattern = "(?ms)^\s*-\s+name:\s*" +
        [Regex]::Escape($StepName) +
        "\s*\r?\n(?<body>.*?)(?=^\s*-\s+name:|\z)"
    $stepMatch = [Regex]::Match($Content, $stepPattern)
    if (-not $stepMatch.Success) {
        Add-Failure "Workflow is missing required step '$StepName'."
        return
    }

    if ($stepMatch.Groups["body"].Value -match "(?m)^\s*continue-on-error:\s*true\s*$") {
        Add-Failure "Critical workflow step '$StepName' must not continue on error."
    }
}

$resolvedWorkflowPath = Resolve-RepoPath $WorkflowPath
if (-not (Test-Path -LiteralPath $resolvedWorkflowPath -PathType Leaf)) {
    throw "WorkflowPath does not exist: $resolvedWorkflowPath"
}

$workflowContent = Get-Content -LiteralPath $resolvedWorkflowPath -Raw
$workflowLines = Get-Content -LiteralPath $resolvedWorkflowPath

$allowedActionRefs = [ordered]@{
    "actions/checkout@v4" = "checkout"
    "actions/setup-dotnet@v4" = "dotnet setup"
    "actions/setup-node@v4" = "node setup"
    "actions/setup-python@v6" = "python setup"
    "actions/upload-artifact@v7" = "release artifact upload"
}

$foundActionRefs = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)

for ($index = 0; $index -lt $workflowLines.Count; $index++) {
    $line = $workflowLines[$index]
    if ($line -notmatch "^\s*uses:\s*(?<ref>[^\s#]+)\s*(?:#.*)?$") {
        continue
    }

    $actionRef = $Matches["ref"].Trim("'").Trim('"')
    $lineNumber = $index + 1

    if (-not $actionRef.Contains("@")) {
        Add-Failure "Action reference must be pinned to a version or tag: $actionRef (line $lineNumber)."
        continue
    }

    if ($actionRef -match "^actions/upload-artifact@" -and $actionRef -ne "actions/upload-artifact@v7") {
        Add-Failure "Release artifact upload must use actions/upload-artifact@v7, found $actionRef (line $lineNumber)."
    }

    if (-not $allowedActionRefs.Contains($actionRef)) {
        Add-Failure "Unexpected GitHub Action reference: $actionRef (line $lineNumber)."
        continue
    }

    $foundActionRefs.Add($actionRef) | Out-Null
}

foreach ($requiredActionRef in $allowedActionRefs.Keys) {
    if (-not $foundActionRefs.Contains($requiredActionRef)) {
        Add-Failure "Missing required GitHub Action reference: $requiredActionRef."
    }
}

Test-ContentContains `
    -Content $workflowContent `
    -Pattern "actions/upload-artifact@v7" `
    -Message "Workflow must upload release artifacts with actions/upload-artifact@v7."
Test-ContentContains `
    -Content $workflowContent `
    -Pattern "actions/setup-python@v6" `
    -Message "Workflow must install Python with actions/setup-python@v6 for pythonnet validation and runtime tests."
Test-ContentContains `
    -Content $workflowContent `
    -Pattern "(?m)^\s*python-version:\s*""3\.12""\s*$" `
    -Message "Workflow must pin the CI Python minor version to 3.12."
Test-ContentContains `
    -Content $workflowContent `
    -Pattern "PYTHONNET_PYDLL" `
    -Message "Workflow must configure PYTHONNET_PYDLL for GitHub-hosted Windows runners."
Test-ContentContains `
    -Content $workflowContent `
    -Pattern "verify-ci-workflow-actions\.tests\.ps1" `
    -Message "Workflow must run the CI workflow verifier's negative regression tests."
Test-ContentContains `
    -Content $workflowContent `
    -Pattern "verify-no-version-suffix-implementations\.ps1" `
    -Message "Workflow must reject internal version tokens and version-suffixed implementations."
Test-ContentContains `
    -Content $workflowContent `
    -Pattern "verify-no-technical-debt-markers\.ps1" `
    -Message "Workflow must reject technical-debt markers and unimplemented code paths."
Test-ContentContains `
    -Content $workflowContent `
    -Pattern "verify-no-legacy-production-contracts\.ps1" `
    -Message "Workflow must reject legacy production contracts and compatibility aliases."
Test-ContentContains `
    -Content $workflowContent `
    -Pattern "verify-solution-project-coverage\.ps1" `
    -Message "Workflow must prove every formal project, including Agent and Runner hosts, is covered by the solution."
Test-ContentContains `
    -Content $workflowContent `
    -Pattern "dotnet restore src/OpenLineOps\.Agent/OpenLineOps\.Agent\.csproj --runtime win-x64" `
    -Message "Workflow must restore the self-contained win-x64 Station Agent host."
Test-ContentContains `
    -Content $workflowContent `
    -Pattern "dotnet restore src/OpenLineOps\.StationRuntime/OpenLineOps\.StationRuntime\.csproj --runtime win-x64" `
    -Message "Workflow must restore the self-contained win-x64 Station Runtime host."
Test-ContentContains `
    -Content $workflowContent `
    -Pattern "dotnet restore src/OpenLineOps\.PluginHost/OpenLineOps\.PluginHost\.csproj --runtime win-x64" `
    -Message "Workflow must restore the co-packaged self-contained win-x64 Plugin Host."
Test-ContentContains `
    -Content $workflowContent `
    -Pattern "dotnet restore src/OpenLineOps\.ScriptWorker/OpenLineOps\.ScriptWorker\.csproj --runtime win-x64" `
    -Message "Workflow must restore the self-contained win-x64 Python Script Worker host."
Test-ContentContains `
    -Content $workflowContent `
    -Pattern "dotnet restore src/OpenLineOps\.LeastPrivilegeLauncher/OpenLineOps\.LeastPrivilegeLauncher\.csproj --runtime win-x64" `
    -Message "Workflow must restore the self-contained win-x64 Least Privilege Launcher host."
Test-ContentContains `
    -Content $workflowContent `
    -Pattern "dotnet restore src/OpenLineOps\.Runner/OpenLineOps\.Runner\.csproj --runtime win-x64" `
    -Message "Workflow must restore the self-contained win-x64 headless Runner host."
Test-ContentContains `
    -Content $workflowContent `
    -Pattern "--require-kind agent --require-kind runner" `
    -Message "Workflow must require formal Agent and Runner release artifacts."
Test-ContentContains `
    -Content $workflowContent `
    -Pattern "(?ms)name:\s*Stage release artifacts.*?name:\s*Setup RabbitMQ for staged Agent E2E.*?name:\s*Run staged Agent bundle E2E\s*\r?\n\s*shell:\s*powershell\s*\r?\n\s*run:\s*\|.*?verify-staged-agent-bundle-e2e\.ps1[^\r\n]*-NoBuild[^\r\n]*-NoRestore" `
    -Message "Workflow must execute eng/verify-staged-agent-bundle-e2e.ps1 after release staging and the RabbitMQ readiness step."
Test-ContentContains `
    -Content $workflowContent `
    -Pattern '(?ms)name:\s*Setup RabbitMQ for staged Agent E2E.*?choco install erlang --version=\$erlangVersion[^\r\n]*--allow-downgrade.*?choco install rabbitmq --version=\$rabbitMqVersion[^\r\n]*--allow-downgrade' `
    -Message "Workflow must install the pinned Erlang and RabbitMQ packages for the Windows staged Agent transport gate."
Test-ContentContains `
    -Content $workflowContent `
    -Pattern '(?m)^\s*\$erlangVersion = "27\.3\.4\.8"\s*$' `
    -Message "Workflow must pin the Windows RabbitMQ gate to Erlang 27.3.4.8."
Test-ContentContains `
    -Content $workflowContent `
    -Pattern '(?m)^\s*\$rabbitMqVersion = "4\.3\.1"\s*$' `
    -Message "Workflow must pin the Windows staged Agent gate to RabbitMQ 4.3.1."
Test-ContentContains `
    -Content $workflowContent `
    -Pattern '(?ms)name:\s*Setup RabbitMQ for staged Agent E2E.*?Get-Service -Name RabbitMQ -ErrorAction Stop.*?rabbitmqctl\.bat.*?await_startup --timeout 120.*?ConnectAsync\("127\.0\.0\.1", 5672\).*?if \(-not \$ready\) \{.*?throw "RabbitMQ \$rabbitMqVersion did not become ready' `
    -Message "Workflow must start the RabbitMQ Windows service, await application startup through rabbitmqctl, and fail unless its localhost AMQP port becomes ready."
Test-ContentContains `
    -Content $workflowContent `
    -Pattern '(?m)^\s*\$rabbitMqUri = "amqp://guest:guest@127\.0\.0\.1:5672/%2f"\s*$' `
    -Message "Workflow must restrict the ephemeral guest broker credentials to the Windows runner localhost endpoint."
Test-ContentContains `
    -Content $workflowContent `
    -Pattern '(?m)^\s*"OPENLINEOPS_RABBITMQ_URI=\$rabbitMqUri" \| Out-File -FilePath \$env:GITHUB_ENV -Encoding utf8 -Append\s*$' `
    -Message "Workflow must export OPENLINEOPS_RABBITMQ_URI to subsequent GitHub Actions steps."
Test-ContentContains `
    -Content $workflowContent `
    -Pattern '(?ms)name:\s*Run staged Agent bundle E2E.*?if \(\[string\]::IsNullOrWhiteSpace\(\$env:OPENLINEOPS_RABBITMQ_URI\)\) \{\s*throw "OPENLINEOPS_RABBITMQ_URI was not provided.*?verify-staged-agent-bundle-e2e\.ps1' `
    -Message "Workflow must fail the staged Agent gate when its real RabbitMQ URI is unavailable instead of allowing a skipped transport test."
Test-StepCannotContinueOnError `
    -Content $workflowContent `
    -StepName "Setup RabbitMQ for staged Agent E2E"
Test-StepCannotContinueOnError `
    -Content $workflowContent `
    -StepName "Run staged Agent bundle E2E"
Test-ContentContains `
    -Content $workflowContent `
    -Pattern "verify-windows-signing-readiness\.ps1" `
    -Message "Workflow must verify the shared Windows package signing path."
Test-ContentContains `
    -Content $workflowContent `
    -Pattern "npm run test:line-projection" `
    -Message "Workflow must verify the persisted production-line projection independently from renderer memory."
Test-ContentContains `
    -Content $workflowContent `
    -Pattern "npm run test:editor-workspace" `
    -Message "Workflow must verify multi-editor dirty, Save All, Problems, and conflict state."
Test-ContentContains `
    -Content $workflowContent `
    -Pattern "npm run e2e:production-closure:packaged" `
    -Message "Workflow must run the packaged multi-Station production closure E2E gate."
Test-ContentContains `
    -Content $workflowContent `
    -Pattern "(?m)^\s*path:\s*artifacts/production-closure-e2e\s*$" `
    -Message "Workflow must upload packaged production closure screenshots and machine-readable evidence."
Test-ContentContains `
    -Content $workflowContent `
    -Pattern "(?ms)name:\s*Upload packaged production closure evidence\s*\r?\n\s*if:\s*\$\{\{\s*always\(\)\s*&&\s*hashFiles\('artifacts/production-closure-e2e/\*\*'\)\s*!=\s*''\s*\}\}\s*\r?\n\s*uses:\s*actions/upload-artifact@v7" `
    -Message "Workflow must upload packaged production closure failure evidence when the E2E produced evidence, without adding a second failure when the gate never ran."
Test-ContentContains `
    -Content $workflowContent `
    -Pattern "dotnet build OpenLineOps\.sln[^\r\n]*--configuration Release[^\r\n]*TreatWarningsAsErrors=true" `
    -Message "Workflow must build the .NET solution in Release and treat every warning as an error."
Test-ContentContains `
    -Content $workflowContent `
    -Pattern "dotnet test OpenLineOps\.sln[^\r\n]*--configuration Release[^\r\n]*--no-build" `
    -Message "Workflow must test the exact Release solution build."
Test-ContentContains `
    -Content $workflowContent `
    -Pattern 'OPENLINEOPS_RUN_POSTGRES_INTEGRATION:\s*"1"' `
    -Message "Workflow must run the real PostgreSQL production integration gate."
Test-ContentContains `
    -Content $workflowContent `
    -Pattern 'OPENLINEOPS_RUN_RABBITMQ_INTEGRATION:\s*"1"' `
    -Message "Workflow must run the real RabbitMQ production integration gate."
Test-ContentContains `
    -Content $workflowContent `
    -Pattern '\$expectedPythonDllName' `
    -Message "Workflow must prefer the version-specific Python runtime DLL for pythonnet."
Test-ContentContains `
    -Content $workflowContent `
    -Pattern '\^python\\d\+\\\.dll\$' `
    -Message "Workflow must reject non-version-specific python DLL stubs when discovering PYTHONNET_PYDLL."
Test-ContentContains `
    -Content $workflowContent `
    -Pattern "name:\s*openlineops-release-\$\{\{\s*github\.run_number\s*\}\}" `
    -Message "Workflow artifact name must include the GitHub run number."
Test-ContentContains `
    -Content $workflowContent `
    -Pattern "(?ms)name:\s*Upload release artifacts\s*\r?\n\s*if:\s*\$\{\{\s*always\(\)\s*&&\s*hashFiles\('artifacts/release/\*\*'\)\s*!=\s*''\s*\}\}\s*\r?\n\s*uses:\s*actions/upload-artifact@v7" `
    -Message "Workflow must upload release artifacts only after release staging produced files."
Test-ContentContains `
    -Content $workflowContent `
    -Pattern "(?m)^\s*artifacts/release\s*$" `
    -Message "Workflow artifact upload must include artifacts/release."
Test-ContentContains `
    -Content $workflowContent `
    -Pattern "(?m)^\s*output/publication-evidence\s*$" `
    -Message "Workflow artifact upload must include publication evidence output."
Test-ContentContains `
    -Content $workflowContent `
    -Pattern "(?m)^\s*output/publication-evidence-verification\s*$" `
    -Message "Workflow artifact upload must include publication evidence verification output."
Test-ContentContains `
    -Content $workflowContent `
    -Pattern "(?m)^\s*output/final-publication-preflight\s*$" `
    -Message "Workflow artifact upload must include final publication preflight output."
Test-ContentContains `
    -Content $workflowContent `
    -Pattern "(?m)^\s*output/ci-release-artifact-inspection\s*$" `
    -Message "Workflow artifact upload must include CI release artifact inspection output."
Test-ContentContains `
    -Content $workflowContent `
    -Pattern "(?m)^\s*output/staged-agent-bundle-e2e\s*$" `
    -Message "Workflow artifact upload must include staged Agent bundle E2E evidence."
Test-ContentContains `
    -Content $workflowContent `
    -Pattern "(?m)^\s*if-no-files-found:\s*error\s*$" `
    -Message "Workflow artifact upload must fail when expected files are missing."
Test-ContentContains `
    -Content $workflowContent `
    -Pattern "(?m)^\s*retention-days:\s*14\s*$" `
    -Message "Workflow artifact upload must keep release diagnostics for 14 days."
Test-ContentContains `
    -Content $workflowContent `
    -Pattern "(?m)^\s*compression-level:\s*0\s*$" `
    -Message "Workflow artifact upload must disable redundant compression for staged zip payloads."

if ($Failures.Count -gt 0) {
    Write-Host "CI workflow action verification failed:" -ForegroundColor Red
    foreach ($failure in $Failures) {
        Write-Host " - $failure" -ForegroundColor Red
    }

    exit 1
}

Write-Host "CI workflow action verification passed."
foreach ($actionRef in $foundActionRefs) {
    Write-Host " - $actionRef"
}

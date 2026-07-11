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
    -Pattern "(?ms)name:\s*Upload packaged production closure evidence\s*\r?\n\s*if:\s*always\(\)\s*\r?\n\s*uses:\s*actions/upload-artifact@v7" `
    -Message "Workflow must upload packaged production closure failure evidence even when the E2E gate fails."
Test-ContentContains `
    -Content $workflowContent `
    -Pattern "dotnet build OpenLineOps\.sln[^\r\n]*TreatWarningsAsErrors=true" `
    -Message "Workflow must treat every .NET solution build warning as an error."
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

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
    "actions/download-artifact@v8" = "release artifact download"
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
    if ($actionRef -match "^actions/download-artifact@" -and $actionRef -ne "actions/download-artifact@v8") {
        Add-Failure "Release artifact download must use actions/download-artifact@v8, found $actionRef (line $lineNumber)."
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

$dotnetSdkPins = [Regex]::Matches(
    $workflowContent,
    "(?m)^\s*dotnet-version:\s*10\.0\.301\s*$")
if ($dotnetSdkPins.Count -ne 3) {
    Add-Failure "All three .NET CI jobs must pin the exact global.json .NET SDK 10.0.301."
}

Test-ContentContains `
    -Content $workflowContent `
    -Pattern "actions/upload-artifact@v7" `
    -Message "Workflow must upload release artifacts with actions/upload-artifact@v7."
Test-ContentContains `
    -Content $workflowContent `
    -Pattern "actions/download-artifact@v8" `
    -Message "Workflow must download same-run release inputs with actions/download-artifact@v8."
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
    -Pattern "verify-evidence-validation\.tests\.ps1" `
    -Message "Workflow must run publication evidence validator mutation tests."
Test-ContentContains `
    -Content $workflowContent `
    -Pattern '(?ms)^\s{6}- name:\s*Test GitHub fixture PowerShell host\s*\r?\n\s*shell:\s*powershell\s*\r?\n\s*run:\s*\./eng/github-fixture-process\.tests\.ps1\s*$' `
    -Message "Workflow must run the trusted GitHub fixture PowerShell host regression."
Test-StepCannotContinueOnError `
    -Content $workflowContent `
    -StepName "Test GitHub fixture PowerShell host"
Test-ContentContains `
    -Content $workflowContent `
    -Pattern "verify-studio-two-agent-production-evidence\.tests\.ps1" `
    -Message "Workflow must run Studio two-Agent evidence validator mutation tests."
Test-ContentContains `
    -Content $workflowContent `
    -Pattern "verify-runner-staged-agent-evidence\.tests\.ps1" `
    -Message "Workflow must run Runner staged-Agent evidence validator mutation tests."
Test-ContentContains `
    -Content $workflowContent `
    -Pattern "verify-no-version-suffix-implementations\.ps1" `
    -Message "Workflow must reject internal version tokens and version-suffixed implementations."
Test-ContentContains `
    -Content $workflowContent `
    -Pattern "verify-no-version-suffix-implementations\.tests\.ps1" `
    -Message "Workflow must run version-suffix verifier mutation tests."
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
    -Pattern "dotnet restore src/OpenLineOps\.Api/OpenLineOps\.Api\.csproj --runtime win-x64" `
    -Message "Workflow must restore the self-contained win-x64 Coordinator API host used by staged Agent E2E."
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
    -Pattern '(?ms)^\s{2}verify:\s*.*?runs-on:\s*windows-2025\s*$' `
    -Message "The Windows release and production closure job must use the explicit Windows 2025 image."
Test-ContentContains `
    -Content $workflowContent `
    -Pattern '(?ms)name:\s*Setup PostgreSQL for Windows production gates\s*.*?timeout-minutes:\s*5\s*.*?\$serviceName = "postgresql-x64-17".*?Set-Service -Name \$serviceName -StartupType Manual.*?Start-Service -Name \$serviceName.*?ConnectAsync\("127\.0\.0\.1", 5432\).*?--command "SELECT 1;".*?OPENLINEOPS_POSTGRES_CONNECTION_STRING=\$connectionString' `
    -Message "Workflow must start and SQL-probe the GitHub Windows PostgreSQL service and export the loopback connection string."
Test-ContentContains `
    -Content $workflowContent `
    -Pattern "(?ms)name:\s*Stage release artifacts.*?name:\s*Setup PostgreSQL for Windows production gates.*?name:\s*Setup RabbitMQ for staged Agent E2E.*?name:\s*Run staged Agent bundle E2E\s*\r?\n\s*shell:\s*powershell\s*\r?\n\s*timeout-minutes:\s*25\s*\r?\n\s*run:\s*\|.*?verify-staged-agent-bundle-e2e\.ps1[^\r\n]*-NoBuild[^\r\n]*-NoRestore" `
    -Message "Workflow must execute the staged Agent gate after release staging and real PostgreSQL/RabbitMQ readiness."
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
    -StepName "Setup PostgreSQL for Windows production gates"
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
    -Pattern '(?ms)^\s{6}- name:\s*Test resource draft transition guard\s*\r?\n\s*working-directory:\s*apps/desktop\s*\r?\n\s*run:\s*npm run test:draft-transition-guard\s*$' `
    -Message "Workflow must verify Save, Discard, and Cancel guards for dirty Process, Production, and External Program resource transitions."
Test-ContentContains `
    -Content $workflowContent `
    -Pattern "npm run test:process-problem-location" `
    -Message "Workflow must verify that every real Flow validation issue preserves and focuses its exact Graph, Node, or Transition target."
Test-ContentContains `
    -Content $workflowContent `
    -Pattern "npm run test:topology-draft-workspace" `
    -Message "Workflow must verify Topology sub-draft ordering, hidden-tab Save All, discard, and runtime transition guards."
Test-ContentContains `
    -Content $workflowContent `
    -Pattern '(?ms)^\s{6}- name:\s*Test configuration draft workspace\s*\r?\n\s*working-directory:\s*apps/desktop\s*\r?\n\s*run:\s*npm run test:configuration-draft-workspace\s*$' `
    -Message "Workflow must verify Engineering and Devices Configuration dirty state, hidden-tab Save All, discard, and runtime transition guards."
Test-ContentContains `
    -Content $workflowContent `
    -Pattern '(?ms)^\s{6}- name:\s*Test runtime monitoring fail-closed projection\s*\r?\n\s*working-directory:\s*apps/desktop\s*\r?\n\s*run:\s*npm run test:runtime-monitoring-fail-closed\s*$' `
    -Message "Workflow must verify strict runtime monitoring envelopes and atomic last-known projection preservation."
Test-ContentContains `
    -Content $workflowContent `
    -Pattern "npm run test:api-credential-security" `
    -Message "Workflow must reject external API credential creation and verify private ACLs before credential reads."
Test-ContentContains `
    -Content $workflowContent `
    -Pattern "npm run test:extension-import-security" `
    -Message "Workflow must verify that Application extension imports accept only a trusted main-process ZIP selection."
Test-ContentContains `
    -Content $workflowContent `
    -Pattern "npm run test:trace-artifact-save" `
    -Message "Workflow must verify Trace artifact hashing, session binding, and destination confinement."
Test-ContentContains `
    -Content $workflowContent `
    -Pattern '(?ms)^\s{6}- name:\s*Test packaged runtime data binding\s*\r?\n\s*working-directory:\s*apps/desktop\s*\r?\n\s*run:\s*npm run test:runtime-data-binding\s*$' `
    -Message "Workflow must verify fail-closed packaged runtime data binding and destructive incompatible-state reset."
Test-ContentContains `
    -Content $workflowContent `
    -Pattern '(?ms)^\s{6}- name:\s*Smoke test staged packaged desktop\s*\r?\n\s*working-directory:\s*apps/desktop\s*\r?\n\s*timeout-minutes:\s*15\s*\r?\n\s*run:\s*npm run smoke:e2e:packaged-existing\s*$' `
    -Message "Workflow must run the staged packaged desktop restart, persistence, and single-instance E2E without rebuilding it."
Test-ContentContains `
    -Content $workflowContent `
    -Pattern "npm run test:production-route-validation" `
    -Message "Workflow must verify production route graph validation."
Test-ContentContains `
    -Content $workflowContent `
    -Pattern '(?ms)^\s{6}- name:\s*Test production route runtime projection\s*\r?\n\s*working-directory:\s*apps/desktop\s*\r?\n\s*run:\s*npm run test:production-route-runtime\s*$' `
    -Message "Workflow must verify the shared Operations, 2D, and 3D production route runtime projection."
Test-ContentContains `
    -Content $workflowContent `
    -Pattern "npm run test:production-route-layout" `
    -Message "Workflow must verify that route graph coordinates survive save, conflict handling, and reopen."
Test-ContentContains `
    -Content $workflowContent `
    -Pattern "npm run test:production-command-policy" `
    -Message "Workflow must verify that operator commands are enabled only in domain-valid Production Run states."
Test-ContentContains `
    -Content $workflowContent `
    -Pattern '(?ms)name:\s*Run packaged Studio two-Agent production closure\s*\r?\n\s*shell:\s*powershell\s*\r?\n\s*timeout-minutes:\s*35\s*\r?\n\s*run:\s*\|.*?OPENLINEOPS_POSTGRES_CONNECTION_STRING.*?OPENLINEOPS_RABBITMQ_URI.*?verify-studio-two-agent-production-closure\.ps1[^\r\n]*-NoBuild[^\r\n]*-NoRestore' `
    -Message "Workflow must run the bounded packaged-to-two-staged-Agent production closure against real PostgreSQL and RabbitMQ."
Test-ContentContains `
    -Content $workflowContent `
    -Pattern "(?ms)name:\s*Run packaged Studio two-Agent production closure.*?name:\s*Verify sanitized production closure evidence\s*\r?\n\s*id:\s*production_closure_evidence\s*\r?\n\s*if:.*?verify-production-closure-evidence\.ps1[^\r\n]*-EvidenceRoot\s+artifacts/production-closure-e2e[^\r\n]*-RequirePassed.*?name:\s*Verify sanitized Studio two-Agent evidence\s*\r?\n\s*id:\s*studio_two_agent_evidence\s*\r?\n\s*if:.*?verify-studio-two-agent-production-evidence\.ps1" `
    -Message "Workflow must scan the exact public production closure evidence before any upload or publication input."
Test-StepCannotContinueOnError `
    -Content $workflowContent `
    -StepName "Verify sanitized production closure evidence"
Test-StepCannotContinueOnError `
    -Content $workflowContent `
    -StepName "Run packaged Studio two-Agent production closure"
Test-StepCannotContinueOnError `
    -Content $workflowContent `
    -StepName "Smoke test staged packaged desktop"
Test-StepCannotContinueOnError `
    -Content $workflowContent `
    -StepName "Verify sanitized Studio two-Agent evidence"
Test-ContentContains `
    -Content $workflowContent `
    -Pattern '(?ms)name:\s*Run staged Runner production closure\s*\r?\n\s*shell:\s*powershell\s*\r?\n\s*timeout-minutes:\s*15\s*\r?\n\s*run:\s*\|.*?OPENLINEOPS_POSTGRES_CONNECTION_STRING.*?OPENLINEOPS_RABBITMQ_URI.*?verify-runner-staged-agent-e2e\.ps1[^\r\n]*-NoBuild[^\r\n]*-NoRestore.*?name:\s*Verify sanitized Runner staged-Agent evidence\s*\r?\n\s*id:\s*runner_staged_agent_evidence\s*\r?\n\s*if:.*?verify-runner-staged-agent-evidence\.ps1 -RequirePassed' `
    -Message "Workflow must run and independently scan the staged Runner production closure against the same real infrastructure."
Test-StepCannotContinueOnError `
    -Content $workflowContent `
    -StepName "Run staged Runner production closure"
Test-StepCannotContinueOnError `
    -Content $workflowContent `
    -StepName "Verify sanitized Runner staged-Agent evidence"
Test-ContentContains `
    -Content $workflowContent `
    -Pattern "(?ms)name:\s*Run staged Agent bundle E2E.*?name:\s*Run packaged Studio two-Agent production closure.*?name:\s*Run staged Runner production closure.*?name:\s*Verify CI release artifact inspection.*?name:\s*Write publication evidence.*?name:\s*Inspect CI release artifact bundle" `
    -Message "Workflow must complete staged Agent, packaged two-Agent, Runner, and CI artifact-inspector gates before publication evidence and bundle inspection."
Test-ContentContains `
    -Content $workflowContent `
    -Pattern "(?ms)name:\s*Install desktop dependencies.*?name:\s*Check desktop package vulnerabilities.*?name:\s*Build desktop.*?name:\s*Smoke test desktop.*?name:\s*Stage release artifacts.*?name:\s*Write publication evidence" `
    -Message "Workflow must complete desktop dependency audit, build, and smoke before staging and publication evidence."
Test-ContentContains `
    -Content $workflowContent `
    -Pattern "(?m)^\s*path:\s*artifacts/production-closure-e2e\s*$" `
    -Message "Workflow must upload packaged production closure screenshots and machine-readable evidence."
Test-ContentContains `
    -Content $workflowContent `
    -Pattern "(?ms)name:\s*Upload packaged production closure evidence\s*\r?\n\s*if:\s*\$\{\{\s*always\(\)\s*&&\s*steps\.production_closure_evidence\.outcome\s*==\s*'success'\s*&&\s*hashFiles\('artifacts/production-closure-e2e/\*\*'\)\s*!=\s*''\s*\}\}\s*\r?\n\s*uses:\s*actions/upload-artifact@v7" `
    -Message "Workflow must upload production closure evidence only after the independent public-evidence scanner succeeds."
Test-ContentContains `
    -Content $workflowContent `
    -Pattern "(?ms)name:\s*Upload Studio two-Agent production evidence\s*\r?\n\s*if:\s*\$\{\{\s*always\(\)\s*&&\s*steps\.studio_two_agent_evidence\.outcome\s*==\s*'success'.*?uses:\s*actions/upload-artifact@v7.*?path:\s*output/studio-two-agent-production-closure" `
    -Message "Workflow must upload Studio two-Agent evidence only after its independent public-evidence scanner succeeds."
Test-ContentContains `
    -Content $workflowContent `
    -Pattern "(?ms)name:\s*Upload Runner staged-Agent production evidence\s*\r?\n\s*if:\s*\$\{\{\s*always\(\)\s*&&\s*steps\.runner_staged_agent_evidence\.outcome\s*==\s*'success'.*?uses:\s*actions/upload-artifact@v7.*?path:\s*output/runner-staged-agent-e2e" `
    -Message "Workflow must upload Runner evidence only after its independent public-evidence scanner succeeds."
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
    -Pattern "eng/verify-dotnet-package-vulnerabilities\.ps1" `
    -Message "Workflow must run the fail-closed .NET package vulnerability audit."
Test-ContentContains `
    -Content $workflowContent `
    -Pattern "eng/verify-dotnet-package-vulnerabilities\.tests\.ps1" `
    -Message "Workflow must run regression tests proving vulnerable package JSON fails the build."
Test-ContentContains `
    -Content $workflowContent `
    -Pattern "eng/verify-third-party-license-metadata\.tests\.ps1" `
    -Message "Workflow must prove third-party dependency inventory is invariant to SDK auto-referenced publish tools."
Test-ContentContains `
    -Content $workflowContent `
    -Pattern "eng/verify-release-staging-security\.ps1" `
    -Message "Workflow must regression-test tracked-only release staging, clean provenance, credential redaction, and finite process-tree cleanup."
Test-ContentContains `
    -Content $workflowContent `
    -Pattern "(?ms)name:\s*Run staged Agent bundle E2E\s*\r?\n\s*shell:\s*powershell\s*\r?\n\s*timeout-minutes:\s*25" `
    -Message "Workflow must impose a finite timeout on the staged Agent process boundary."
Test-ContentContains `
    -Content $workflowContent `
    -Pattern "eng/verify-ci-release-artifact-inspection\.ps1" `
    -Message "Workflow must execute the CI release artifact inspector regression gate."
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
    -Pattern 'dotnet test tests/OpenLineOps\.PostgresIntegration\.Tests/OpenLineOps\.PostgresIntegration\.Tests\.csproj[^\r\n]*--configuration Release[^\r\n]*TreatWarningsAsErrors=true' `
    -Message "Workflow must execute the complete PostgreSQL/RabbitMQ project containing the durable Coordinator recovery composition gate."
Test-ContentContains `
    -Content $workflowContent `
    -Pattern 'dotnet test tests/OpenLineOps\.PostgresIntegration\.Tests/OpenLineOps\.PostgresIntegration\.Tests\.csproj[^\r\n]*--logger "trx;LogFileName=production-integration\.trx"[^\r\n]*--results-directory output/production-integration-evidence' `
    -Message "Workflow must emit a deterministic TRX for the production integration proof."
Test-ContentContains `
    -Content $workflowContent `
    -Pattern 'write-production-integration-evidence\.ps1 -TrxPath output/production-integration-evidence/production-integration\.trx' `
    -Message "Workflow must bind the real integration TRX to repository, commit, and run evidence."
Test-ContentContains `
    -Content $workflowContent `
    -Pattern 'verify-production-integration-evidence\.ps1' `
    -Message "Workflow must regression-test production integration evidence parsing."
Test-ContentContains `
    -Content $workflowContent `
    -Pattern '(?ms)^\s{2}publication-evidence:\s*\r?\n\s*name:.*?\r?\n\s*needs:\s*\r?\n\s*- verify\s*\r?\n\s*- production-integration' `
    -Message "Final publication evidence job must depend on both candidate verification and production integration."
Test-ContentContains `
    -Content $workflowContent `
    -Pattern '(?ms)name:\s*openlineops-release-input-\$\{\{\s*github\.run_number\s*\}\}.*?name:\s*openlineops-production-integration-\$\{\{\s*github\.run_number\s*\}\}' `
    -Message "Workflow must exchange same-run candidate and integration evidence through distinct artifacts."
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
    -Pattern "(?ms)name:\s*Upload release artifacts\s*\r?\n\s*uses:\s*actions/upload-artifact@v7" `
    -Message "Final publication job must upload the fully inspected release bundle."
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
    -Pattern "(?m)^\s*output/production-integration-evidence\s*$" `
    -Message "Workflow artifact upload must include the bound production integration TRX evidence."
Test-ContentContains `
    -Content $workflowContent `
    -Pattern "(?m)^\s*artifacts/production-closure-e2e\s*$" `
    -Message "Workflow release artifact upload must include packaged production closure evidence."
Test-ContentContains `
    -Content $workflowContent `
    -Pattern "(?m)^\s*output/studio-two-agent-production-closure\s*$" `
    -Message "Workflow release artifact upload must include the Studio two-Agent production closure evidence."
Test-ContentContains `
    -Content $workflowContent `
    -Pattern "(?m)^\s*output/runner-staged-agent-e2e\s*$" `
    -Message "Workflow release artifact upload must include the staged Runner production closure evidence."
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

param(
    [string] $WorkflowPath = ".github/workflows/build.yml",

    [string] $VerifierPath = "eng/verify-ci-workflow-actions.ps1"
)

$ErrorActionPreference = "Stop"

$RepoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$RepoRootPrefix = $RepoRoot.TrimEnd(
    [System.IO.Path]::DirectorySeparatorChar,
    [System.IO.Path]::AltDirectorySeparatorChar) +
    [System.IO.Path]::DirectorySeparatorChar

function Resolve-RepoPath {
    param([Parameter(Mandatory = $true)][string] $Path)

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }

    return [System.IO.Path]::GetFullPath((Join-Path $RepoRoot $Path))
}

function Assert-UnderRepoRoot {
    param([Parameter(Mandatory = $true)][string] $Path)

    if (-not $Path.StartsWith(
            $RepoRootPrefix,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing CI workflow verifier test output outside the repository root: $Path"
    }
}

$resolvedWorkflowPath = Resolve-RepoPath $WorkflowPath
$resolvedVerifierPath = Resolve-RepoPath $VerifierPath
if (-not (Test-Path -LiteralPath $resolvedWorkflowPath -PathType Leaf)) {
    throw "WorkflowPath does not exist: $resolvedWorkflowPath"
}
if (-not (Test-Path -LiteralPath $resolvedVerifierPath -PathType Leaf)) {
    throw "VerifierPath does not exist: $resolvedVerifierPath"
}

$workflowContent = Get-Content -LiteralPath $resolvedWorkflowPath -Raw
$testRoot = Resolve-RepoPath (
    "output/ci-workflow-verifier-tests-" + [Guid]::NewGuid().ToString("N"))
Assert-UnderRepoRoot $testRoot
New-Item -ItemType Directory -Path $testRoot -Force | Out-Null

$cases = @(
    [pscustomobject]@{
        Name = ".NET SDK pin"
        Search = "dotnet-version: 10.0.301"
        Replacement = "dotnet-version: 10.0.x"
        ExpectedFailure = "exact global.json .NET SDK 10.0.301"
    },
    [pscustomobject]@{
        Name = ".NET vulnerability audit"
        Search = "./eng/verify-dotnet-package-vulnerabilities.ps1"
        Replacement = "Write-Host 'package audit disabled'"
        ExpectedFailure = "fail-closed .NET package vulnerability audit"
    },
    [pscustomobject]@{
        Name = ".NET vulnerability regression"
        Search = "./eng/verify-dotnet-package-vulnerabilities.tests.ps1"
        Replacement = "Write-Host 'package audit regression disabled'"
        ExpectedFailure = "vulnerable package JSON fails the build"
    },
    [pscustomobject]@{
        Name = "third-party inventory determinism regression"
        Search = "./eng/verify-third-party-license-metadata.tests.ps1"
        Replacement = "Write-Host 'third-party inventory determinism regression disabled'"
        ExpectedFailure = "dependency inventory is invariant to SDK auto-referenced publish tools"
    },
    [pscustomobject]@{
        Name = "release staging security regression"
        Search = "./eng/verify-release-staging-security.ps1"
        Replacement = "Write-Host 'release staging security disabled'"
        ExpectedFailure = "tracked-only release staging"
    },
    [pscustomobject]@{
        Name = "Coordinator API runtime restore"
        Search = "dotnet restore src/OpenLineOps.Api/OpenLineOps.Api.csproj --runtime win-x64"
        Replacement = "dotnet restore src/OpenLineOps.Api/OpenLineOps.Api.csproj"
        ExpectedFailure = "self-contained win-x64 Coordinator API host"
    },
    [pscustomobject]@{
        Name = "evidence validator mutation regression"
        Search = "./eng/verify-evidence-validation.tests.ps1"
        Replacement = "Write-Host 'evidence validator mutation regression disabled'"
        ExpectedFailure = "publication evidence validator mutation tests"
    },
    [pscustomobject]@{
        Name = "GitHub fixture host regression deletion"
        Search = "      - name: Test GitHub fixture PowerShell host`n        shell: powershell`n        run: ./eng/github-fixture-process.tests.ps1`n"
        Replacement = ""
        ExpectedFailure = "trusted GitHub fixture PowerShell host regression"
    },
    [pscustomobject]@{
        Name = "GitHub fixture host regression rename"
        Search = "./eng/github-fixture-process.tests.ps1"
        Replacement = "./eng/github-fixture-process-smoke.ps1"
        ExpectedFailure = "trusted GitHub fixture PowerShell host regression"
    },
    [pscustomobject]@{
        Name = "Studio evidence validator mutation regression"
        Search = "./eng/verify-studio-two-agent-production-evidence.tests.ps1"
        Replacement = "Write-Host 'Studio evidence mutation regression disabled'"
        ExpectedFailure = "Studio two-Agent evidence validator mutation tests"
    },
    [pscustomobject]@{
        Name = "Runner evidence validator mutation regression"
        Search = "./eng/verify-runner-staged-agent-evidence.tests.ps1"
        Replacement = "Write-Host 'Runner evidence mutation regression disabled'"
        ExpectedFailure = "Runner staged-Agent evidence validator mutation tests"
    },
    [pscustomobject]@{
        Name = "version-suffix verifier mutation regression"
        Search = "./eng/verify-no-version-suffix-implementations.tests.ps1"
        Replacement = "Write-Host 'version-suffix mutation regression disabled'"
        ExpectedFailure = "version-suffix verifier mutation tests"
    },
    [pscustomobject]@{
        Name = "Windows PostgreSQL service"
        Search = '$serviceName = "postgresql-x64-17"'
        Replacement = '$serviceName = "postgresql-x64-16"'
        ExpectedFailure = "start and SQL-probe the GitHub Windows PostgreSQL service"
    },
    [pscustomobject]@{
        Name = "Windows PostgreSQL SQL probe"
        Search = '--command "SELECT 1;"'
        Replacement = '--command "SELECT 2;"'
        ExpectedFailure = "start and SQL-probe the GitHub Windows PostgreSQL service"
    },
    [pscustomobject]@{
        Name = "Windows PostgreSQL export"
        Search = '"OPENLINEOPS_POSTGRES_CONNECTION_STRING=$connectionString" | Out-File'
        Replacement = '"BROKEN_POSTGRES_CONNECTION_STRING=$connectionString" | Out-File'
        ExpectedFailure = "export the loopback connection string"
    },
    [pscustomobject]@{
        Name = "Erlang version pin"
        Search = '$erlangVersion = "27.3.4.8"'
        Replacement = '$erlangVersion = "27.3.4.2"'
        ExpectedFailure = "pin the Windows RabbitMQ gate to Erlang 27.3.4.8"
    },
    [pscustomobject]@{
        Name = "RabbitMQ version pin"
        Search = '$rabbitMqVersion = "4.3.1"'
        Replacement = '$rabbitMqVersion = "4.3.0"'
        ExpectedFailure = "pin the Windows staged Agent gate to RabbitMQ 4.3.1"
    },
    [pscustomobject]@{
        Name = "RabbitMQ application readiness"
        Search = '& $rabbitMqCtl await_startup --timeout 120'
        Replacement = '& $rabbitMqCtl version'
        ExpectedFailure = "await application startup through rabbitmqctl"
    },
    [pscustomobject]@{
        Name = "RabbitMQ localhost readiness"
        Search = '$client.ConnectAsync("127.0.0.1", 5672)'
        Replacement = '$client.ConnectAsync("127.0.0.1", 5673)'
        ExpectedFailure = "fail unless its localhost AMQP port becomes ready"
    },
    [pscustomobject]@{
        Name = "RabbitMQ URI export"
        Search = '"OPENLINEOPS_RABBITMQ_URI=$rabbitMqUri" | Out-File'
        Replacement = '"BROKEN_RABBITMQ_URI=$rabbitMqUri" | Out-File'
        ExpectedFailure = "export OPENLINEOPS_RABBITMQ_URI"
    },
    [pscustomobject]@{
        Name = "staged Agent no-skip guard"
        Search = 'if ([string]::IsNullOrWhiteSpace($env:OPENLINEOPS_RABBITMQ_URI)) {'
        Replacement = 'if ($false) {'
        ExpectedFailure = "instead of allowing a skipped transport test"
    },
    [pscustomobject]@{
        Name = "staged Agent continue on error"
        Search = "      - name: Run staged Agent bundle E2E"
        Replacement = "      - name: Run staged Agent bundle E2E`n        continue-on-error: true"
        ExpectedFailure = "must not continue on error"
    },
    [pscustomobject]@{
        Name = "staged Agent finite timeout"
        Search = "      - name: Run staged Agent bundle E2E`n        shell: powershell`n        timeout-minutes: 25"
        Replacement = "      - name: Run staged Agent bundle E2E`n        shell: powershell"
        ExpectedFailure = "finite timeout on the staged Agent process boundary"
    },
    [pscustomobject]@{
        Name = "production evidence scanner"
        Search = "./eng/verify-production-closure-evidence.ps1"
        Replacement = "Write-Host 'production evidence scan disabled'"
        ExpectedFailure = "scan the exact public production closure evidence"
    },
    [pscustomobject]@{
        Name = "production evidence passed-state guard"
        Search = "./eng/verify-production-closure-evidence.ps1 -EvidenceRoot artifacts/production-closure-e2e -RequirePassed"
        Replacement = "./eng/verify-production-closure-evidence.ps1 -EvidenceRoot artifacts/production-closure-e2e"
        ExpectedFailure = "scan the exact public production closure evidence"
    },
    [pscustomobject]@{
        Name = "Studio two-Agent wrapper"
        Search = "./eng/verify-studio-two-agent-production-closure.ps1 -Configuration Release -NoBuild -NoRestore"
        Replacement = "Write-Host 'Studio two-Agent closure disabled'"
        ExpectedFailure = "packaged-to-two-staged-Agent production closure"
    },
    [pscustomobject]@{
        Name = "Studio two-Agent finite timeout"
        Search = "      - name: Run packaged Studio two-Agent production closure`n        shell: powershell`n        timeout-minutes: 35"
        Replacement = "      - name: Run packaged Studio two-Agent production closure`n        shell: powershell"
        ExpectedFailure = "bounded packaged-to-two-staged-Agent"
    },
    [pscustomobject]@{
        Name = "Studio two-Agent evidence scanner"
        Search = "./eng/verify-studio-two-agent-production-evidence.ps1"
        Replacement = "Write-Host 'Studio two-Agent evidence scan disabled'"
        ExpectedFailure = "scan the exact public production closure evidence"
    },
    [pscustomobject]@{
        Name = "Studio two-Agent upload binding"
        Search = "steps.studio_two_agent_evidence.outcome == 'success'"
        Replacement = "hashFiles('output/studio-two-agent-production-closure/**') != ''"
        ExpectedFailure = "upload Studio two-Agent evidence only after"
    },
    [pscustomobject]@{
        Name = "Runner staged-Agent wrapper"
        Search = "./eng/verify-runner-staged-agent-e2e.ps1 -Configuration Release -NoBuild -NoRestore"
        Replacement = "Write-Host 'Runner staged-Agent closure disabled'"
        ExpectedFailure = "run and independently scan the staged Runner"
    },
    [pscustomobject]@{
        Name = "Runner staged-Agent finite timeout"
        Search = "      - name: Run staged Runner production closure`n        shell: powershell`n        timeout-minutes: 15"
        Replacement = "      - name: Run staged Runner production closure`n        shell: powershell"
        ExpectedFailure = "run and independently scan the staged Runner"
    },
    [pscustomobject]@{
        Name = "Runner staged-Agent evidence scanner"
        Search = "./eng/verify-runner-staged-agent-evidence.ps1 -RequirePassed"
        Replacement = "Write-Host 'Runner staged-Agent evidence scan disabled'"
        ExpectedFailure = "run and independently scan the staged Runner"
    },
    [pscustomobject]@{
        Name = "Runner staged-Agent upload binding"
        Search = "steps.runner_staged_agent_evidence.outcome == 'success'"
        Replacement = "hashFiles('output/runner-staged-agent-e2e/**') != ''"
        ExpectedFailure = "upload Runner evidence only after"
    },
    [pscustomobject]@{
        Name = "production evidence upload binding"
        Search = "steps.production_closure_evidence.outcome == 'success'"
        Replacement = "hashFiles('artifacts/production-closure-e2e/**') != ''"
        ExpectedFailure = "only after the independent public-evidence scanner succeeds"
    },
    [pscustomobject]@{
        Name = "CI artifact inspector regression"
        Search = "./eng/verify-ci-release-artifact-inspection.ps1"
        Replacement = "Write-Host 'CI artifact inspector regression disabled'"
        ExpectedFailure = "CI release artifact inspector regression gate"
    },
    [pscustomobject]@{
        Name = "production integration TRX"
        Search = '--logger "trx;LogFileName=production-integration.trx" --results-directory output/production-integration-evidence'
        Replacement = '--verbosity normal'
        ExpectedFailure = "deterministic TRX for the production integration proof"
    },
    [pscustomobject]@{
        Name = "production integration proof writer"
        Search = './eng/write-production-integration-evidence.ps1 -TrxPath output/production-integration-evidence/production-integration.trx'
        Replacement = "Write-Host 'integration proof disabled'"
        ExpectedFailure = "bind the real integration TRX"
    },
    [pscustomobject]@{
        Name = "final evidence dependency"
        Search = "      - production-integration"
        Replacement = "      - verify"
        ExpectedFailure = "depend on both candidate verification and production integration"
    },
    [pscustomobject]@{
        Name = "artifact download pin"
        Search = "actions/download-artifact@v8"
        Replacement = "actions/download-artifact@v7"
        ExpectedFailure = "actions/download-artifact@v8"
    },
    [pscustomobject]@{
        Name = "Application extension import gate"
        Search = "npm run test:extension-import-security"
        Replacement = "npm run test:trace-artifact-save"
        ExpectedFailure = "trusted main-process ZIP selection"
    },
    [pscustomobject]@{
        Name = "Flow problem location gate"
        Search = "npm run test:process-problem-location"
        Replacement = "npm run test:editor-workspace"
        ExpectedFailure = "every real Flow validation issue preserves and focuses its exact Graph, Node, or Transition target"
    },
    [pscustomobject]@{
        Name = "Resource draft transition guard deletion"
        Search = "      - name: Test resource draft transition guard`n        working-directory: apps/desktop`n        run: npm run test:draft-transition-guard`n"
        Replacement = ""
        ExpectedFailure = "Save, Discard, and Cancel guards for dirty Process, Production, and External Program resource transitions"
    },
    [pscustomobject]@{
        Name = "Resource draft transition guard rename"
        Search = "npm run test:draft-transition-guard"
        Replacement = "npm run test:draft-transition"
        ExpectedFailure = "Save, Discard, and Cancel guards for dirty Process, Production, and External Program resource transitions"
    },
    [pscustomobject]@{
        Name = "Topology draft workspace gate"
        Search = "npm run test:topology-draft-workspace"
        Replacement = "npm run test:editor-workspace"
        ExpectedFailure = "Topology sub-draft ordering, hidden-tab Save All, discard, and runtime transition guards"
    },
    [pscustomobject]@{
        Name = "Configuration draft workspace gate deletion"
        Search = "      - name: Test configuration draft workspace`n        working-directory: apps/desktop`n        run: npm run test:configuration-draft-workspace`n"
        Replacement = ""
        ExpectedFailure = "Engineering and Devices Configuration dirty state, hidden-tab Save All, discard, and runtime transition guards"
    },
    [pscustomobject]@{
        Name = "Configuration draft workspace gate rename"
        Search = "npm run test:configuration-draft-workspace"
        Replacement = "npm run test:configuration-workspace"
        ExpectedFailure = "Engineering and Devices Configuration dirty state, hidden-tab Save All, discard, and runtime transition guards"
    },
    [pscustomobject]@{
        Name = "Runtime monitoring fail-closed gate deletion"
        Search = "      - name: Test runtime monitoring fail-closed projection`n        working-directory: apps/desktop`n        run: npm run test:runtime-monitoring-fail-closed`n"
        Replacement = ""
        ExpectedFailure = "strict runtime monitoring envelopes and atomic last-known projection preservation"
    },
    [pscustomobject]@{
        Name = "Runtime monitoring fail-closed gate rename"
        Search = "npm run test:runtime-monitoring-fail-closed"
        Replacement = "npm run test:runtime-monitoring"
        ExpectedFailure = "strict runtime monitoring envelopes and atomic last-known projection preservation"
    },
    [pscustomobject]@{
        Name = "Packaged runtime data binding gate deletion"
        Search = "      - name: Test packaged runtime data binding`n        working-directory: apps/desktop`n        run: npm run test:runtime-data-binding`n"
        Replacement = ""
        ExpectedFailure = "fail-closed packaged runtime data binding and destructive incompatible-state reset"
    },
    [pscustomobject]@{
        Name = "Packaged runtime data binding gate rename"
        Search = "npm run test:runtime-data-binding"
        Replacement = "npm run test:runtime-binding"
        ExpectedFailure = "fail-closed packaged runtime data binding and destructive incompatible-state reset"
    },
    [pscustomobject]@{
        Name = "Staged packaged desktop smoke deletion"
        Search = "      - name: Smoke test staged packaged desktop`n        working-directory: apps/desktop`n        timeout-minutes: 15`n        run: npm run smoke:e2e:packaged-existing`n"
        Replacement = ""
        ExpectedFailure = "staged packaged desktop restart, persistence, and single-instance E2E"
    },
    [pscustomobject]@{
        Name = "Staged packaged desktop smoke rebuild substitution"
        Search = "npm run smoke:e2e:packaged-existing"
        Replacement = "npm run smoke:e2e:packaged"
        ExpectedFailure = "staged packaged desktop restart, persistence, and single-instance E2E"
    },
    [pscustomobject]@{
        Name = "Production route runtime projection gate deletion"
        Search = "      - name: Test production route runtime projection`n        working-directory: apps/desktop`n        run: npm run test:production-route-runtime`n"
        Replacement = ""
        ExpectedFailure = "shared Operations, 2D, and 3D production route runtime projection"
    },
    [pscustomobject]@{
        Name = "Production route runtime projection gate rename"
        Search = "npm run test:production-route-runtime"
        Replacement = "npm run test:production-route-projection"
        ExpectedFailure = "shared Operations, 2D, and 3D production route runtime projection"
    },
    [pscustomobject]@{
        Name = "production route layout gate"
        Search = "npm run test:production-route-layout"
        Replacement = "npm run test:production-route-validation"
        ExpectedFailure = "route graph coordinates survive save"
    },
    [pscustomobject]@{
        Name = "production operator command policy gate"
        Search = "npm run test:production-command-policy"
        Replacement = "npm run test:production-route-validation"
        ExpectedFailure = "operator commands are enabled only in domain-valid Production Run states"
    }
)

try {
    foreach ($case in $cases) {
        if (-not $workflowContent.Contains($case.Search)) {
            throw "The verifier regression fixture token is missing for '$($case.Name)'."
        }

        $fixtureContent = $workflowContent.Replace($case.Search, $case.Replacement)
        $fixturePath = Join-Path $testRoot (
            $case.Name.Replace(" ", "-").ToLowerInvariant() + ".yml")
        [System.IO.File]::WriteAllText(
            $fixturePath,
            $fixtureContent,
            [System.Text.UTF8Encoding]::new($false))

        $output = & powershell.exe `
            -NoProfile `
            -NonInteractive `
            -ExecutionPolicy Bypass `
            -File $resolvedVerifierPath `
            -WorkflowPath $fixturePath 2>&1 | Out-String
        $exitCode = $LASTEXITCODE
        if ($exitCode -eq 0) {
            throw "CI workflow verifier accepted the invalid '$($case.Name)' fixture."
        }
        if ($output -notmatch [Regex]::Escape($case.ExpectedFailure)) {
            throw "CI workflow verifier rejected '$($case.Name)' for an unexpected reason:`n$output"
        }

        Write-Host "Verified rejection: $($case.Name)"
    }
}
finally {
    if (Test-Path -LiteralPath $testRoot) {
        Assert-UnderRepoRoot $testRoot
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}

Write-Host "CI workflow verifier regression tests passed."
exit 0

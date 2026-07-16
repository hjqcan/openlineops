param(
    [string] $WorkRoot = "output/release-staging-security"
)

$ErrorActionPreference = "Stop"
$RepoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$StageScript = Join-Path $PSScriptRoot "stage-release-artifacts.ps1"
$PrepareScript = Join-Path $PSScriptRoot "prepare-final-publication.ps1"
$RabbitMqScript = Join-Path $PSScriptRoot "verify-staged-agent-rabbitmq-e2e.ps1"

function Resolve-RepoPath {
    param([Parameter(Mandatory = $true)][string] $Path)

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }

    return [System.IO.Path]::GetFullPath((Join-Path $RepoRoot $Path))
}

function Get-ScriptAst {
    param([Parameter(Mandatory = $true)][string] $Path)

    $tokens = $null
    $parseErrors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile(
        $Path,
        [ref]$tokens,
        [ref]$parseErrors)
    if ($parseErrors.Count -ne 0) {
        throw "PowerShell parsing failed for $Path`: $($parseErrors[0].Message)"
    }

    return $ast
}

function Get-FunctionDefinition {
    param(
        [Parameter(Mandatory = $true)]$Ast,
        [Parameter(Mandatory = $true)][string] $Name
    )

    $definition = $Ast.Find({
            param($node)
            $node -is [System.Management.Automation.Language.FunctionDefinitionAst] `
                -and $node.Name -ceq $Name
        }, $true)
    if ($null -eq $definition) {
        throw "Required function '$Name' was not found."
    }

    return $definition
}

function Assert-ParameterAbsent {
    param(
        [Parameter(Mandatory = $true)]$Ast,
        [Parameter(Mandatory = $true)][string] $Name,
        [Parameter(Mandatory = $true)][string] $Description
    )

    $parameterNames = @($Ast.ParamBlock.Parameters | ForEach-Object {
            $_.Name.VariablePath.UserPath
        })
    if ($parameterNames -ccontains $Name) {
        throw "$Description still exposes removed parameter '$Name'."
    }
}

function Invoke-Git {
    param(
        [Parameter(Mandatory = $true)][string] $WorkingDirectory,
        [Parameter(Mandatory = $true)][string[]] $Arguments
    )

    $previousErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $output = @(& git -C $WorkingDirectory @Arguments 2>&1)
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }
    if ($exitCode -ne 0) {
        throw "Git command failed in release staging regression: $($output -join [Environment]::NewLine)"
    }
}

function Invoke-PowerShellProcess {
    param(
        [Parameter(Mandatory = $true)][string] $ScriptPath,
        [Parameter(Mandatory = $true)][string[]] $Arguments
    )

    $previousErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $output = @(& powershell -NoProfile -ExecutionPolicy Bypass -File $ScriptPath @Arguments 2>&1)
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }

    return [pscustomobject]@{
        ExitCode = $exitCode
        Text = (($output | ForEach-Object { $_.ToString() }) -join [Environment]::NewLine)
    }
}

$stageAst = Get-ScriptAst $StageScript
$prepareAst = Get-ScriptAst $PrepareScript
$rabbitMqAst = Get-ScriptAst $RabbitMqScript

Assert-ParameterAbsent `
    -Ast $stageAst `
    -Name "CodeSigningCertificatePassword" `
    -Description "Release staging"
Assert-ParameterAbsent `
    -Ast $stageAst `
    -Name "CodeSigningCertificatePath" `
    -Description "Release staging"
Assert-ParameterAbsent `
    -Ast $prepareAst `
    -Name "CodeSigningCertificatePassword" `
    -Description "Final publication"
Assert-ParameterAbsent `
    -Ast $prepareAst `
    -Name "CodeSigningCertificatePath" `
    -Description "Final publication"

$stageText = Get-Content -LiteralPath $StageScript -Raw
$prepareText = Get-Content -LiteralPath $PrepareScript -Raw
$rabbitMqText = Get-Content -LiteralPath $RabbitMqScript -Raw
foreach ($expectedStageBoundary in @(
        "git -c core.quotePath=false ls-files --cached --full-name",
        "RequireCleanGitWorkTree",
        "ExpectedGitCommit",
        "CoordinatorBaseUri",
        "ArtifactUploadBearerToken",
        "Signed release staging requires exactly one certificate-store selector",
        "Sparse or incomplete checkouts cannot be published",
        "traverses a reparse point and cannot be archived",
        "-SourceProvenance `$sourceGitProvenance")) {
    if ($stageText -cnotmatch [regex]::Escape($expectedStageBoundary)) {
        throw "Release staging is missing required source boundary '$expectedStageBoundary'."
    }
}
foreach ($expectedPublicationBoundary in @(
        "Assert-CleanGitWorkTree",
        "-RequireCleanGitWorkTree",
        "-ExpectedGitCommit")) {
    if ($prepareText -cnotmatch [regex]::Escape($expectedPublicationBoundary)) {
        throw "Final publication is missing required source boundary '$expectedPublicationBoundary'."
    }
}
if ($stageText -cmatch 'CodeSigningCertificate(?:Path|Password)' `
    -or $prepareText -cmatch 'CodeSigningCertificate(?:Path|Password)') {
    throw "Release staging or final publication still contains a removed file/password signing chain."
}
if ($rabbitMqText -match 'WaitForExit\s*\(\s*\)') {
    throw "Staged Agent RabbitMQ verification still contains an unbounded WaitForExit call."
}
foreach ($expectedTimeoutBoundary in @(
        "TestTimeoutSeconds",
        "taskkill.exe",
        "/T /F",
        "finally")) {
    if ($rabbitMqText -cnotmatch [regex]::Escape($expectedTimeoutBoundary)) {
        throw "Staged Agent RabbitMQ verification is missing timeout boundary '$expectedTimeoutBoundary'."
    }
}

$stageFormatter = Get-FunctionDefinition -Ast $stageAst -Name "Format-CommandArgumentsForLog"
$stageFormatterTest = [scriptblock]::Create(
    $stageFormatter.Extent.Text + [Environment]::NewLine +
    "(Format-CommandArgumentsForLog -Arguments @('--api-token','stage-secret','--password=inline-secret','https://uri-user:stage-uri-secret@example.invalid/path','https://example.invalid/path?token=stage-query-secret','safe')) -join ' '")
$stageFormatted = (& $stageFormatterTest).ToString()
if ($stageFormatted -match 'stage-secret|inline-secret|stage-uri-secret|stage-query-secret' `
    -or $stageFormatted -notmatch '<redacted>|<redacted-uri>') {
    throw "Release staging command formatter did not redact secret values."
}

$prepareFormatter = Get-FunctionDefinition -Ast $prepareAst -Name "Format-CommandLine"
$prepareFormatterTest = [scriptblock]::Create(
    $prepareFormatter.Extent.Text + [Environment]::NewLine +
    "Format-CommandLine @('tool','--access-token','publication-secret','--password=inline-publication-secret','https://uri-user:publication-uri-secret@example.invalid/path','https://example.invalid/path?token=publication-query-secret','safe')")
$prepareFormatted = (& $prepareFormatterTest).ToString()
if ($prepareFormatted -match 'publication-secret|inline-publication-secret|publication-uri-secret|publication-query-secret' `
    -or $prepareFormatted -notmatch '<redacted>|<redacted-uri>') {
    throw "Final publication command formatter did not redact secret values."
}

$resolvedWorkRoot = Resolve-RepoPath $WorkRoot
if (Test-Path -LiteralPath $resolvedWorkRoot) {
    Remove-Item -LiteralPath $resolvedWorkRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $resolvedWorkRoot -Force | Out-Null

$trackedCopyRepo = Join-Path $resolvedWorkRoot "tracked-copy-repo"
$trackedCopyDestination = Join-Path $resolvedWorkRoot "tracked-copy-output"
New-Item -ItemType Directory -Path $trackedCopyRepo -Force | Out-Null
Invoke-Git -WorkingDirectory $trackedCopyRepo -Arguments @("init", "--quiet")
$trackedPath = Join-Path $trackedCopyRepo "src/tracked.txt"
$untrackedPath = Join-Path $trackedCopyRepo "src/untracked-secret.txt"
$sensitiveTrackedPath = Join-Path $trackedCopyRepo "certs/release-signing.pfx"
New-Item -ItemType Directory -Path (Split-Path $trackedPath -Parent) -Force | Out-Null
New-Item -ItemType Directory -Path (Split-Path $sensitiveTrackedPath -Parent) -Force | Out-Null
[System.IO.File]::WriteAllText($trackedPath, "indexed-content", [System.Text.UTF8Encoding]::new($false))
[System.IO.File]::WriteAllText($sensitiveTrackedPath, "not-a-real-certificate", [System.Text.UTF8Encoding]::new($false))
Invoke-Git -WorkingDirectory $trackedCopyRepo -Arguments @("add", "src/tracked.txt", "certs/release-signing.pfx")
[System.IO.File]::WriteAllText($trackedPath, "tracked-working-tree-content", [System.Text.UTF8Encoding]::new($false))
[System.IO.File]::WriteAllText($untrackedPath, "must-never-ship", [System.Text.UTF8Encoding]::new($false))

$copyFunctions = @(
    (Get-FunctionDefinition -Ast $stageAst -Name "Test-IsSensitiveSourcePath").Extent.Text,
    (Get-FunctionDefinition -Ast $stageAst -Name "Test-IsExcludedSourcePath").Extent.Text,
    (Get-FunctionDefinition -Ast $stageAst -Name "Copy-SourceArchiveContent").Extent.Text
) -join ([Environment]::NewLine + [Environment]::NewLine)
$copyTest = [scriptblock]::Create(
    "param(`$RepoRoot, `$DestinationDirectory)" + [Environment]::NewLine +
    $copyFunctions + [Environment]::NewLine +
    "Copy-SourceArchiveContent -DestinationDirectory `$DestinationDirectory")
& $copyTest $trackedCopyRepo $trackedCopyDestination

$copiedTrackedPath = Join-Path $trackedCopyDestination "src/tracked.txt"
if (-not (Test-Path -LiteralPath $copiedTrackedPath -PathType Leaf) `
    -or (Get-Content -LiteralPath $copiedTrackedPath -Raw) -cne "tracked-working-tree-content") {
    throw "Tracked source copy did not preserve the current tracked working-tree file."
}
if (Test-Path -LiteralPath (Join-Path $trackedCopyDestination "src/untracked-secret.txt")) {
    throw "Untracked content entered the source staging tree."
}
if (Test-Path -LiteralPath (Join-Path $trackedCopyDestination "certs/release-signing.pfx")) {
    throw "Sensitive tracked content entered the source staging tree."
}

$publicationRepo = Join-Path $resolvedWorkRoot "formal-publication-repo"
$publicationEng = Join-Path $publicationRepo "eng"
New-Item -ItemType Directory -Path $publicationEng -Force | Out-Null
Copy-Item -LiteralPath $PrepareScript -Destination (Join-Path $publicationEng "prepare-final-publication.ps1")
Invoke-Git -WorkingDirectory $publicationRepo -Arguments @("init", "--quiet")
Invoke-Git -WorkingDirectory $publicationRepo -Arguments @("config", "user.name", "OpenLineOps Regression")
Invoke-Git -WorkingDirectory $publicationRepo -Arguments @("config", "user.email", "regression@openlineops.invalid")
Invoke-Git -WorkingDirectory $publicationRepo -Arguments @("add", "eng/prepare-final-publication.ps1")
Invoke-Git -WorkingDirectory $publicationRepo -Arguments @(
    "-c",
    "commit.gpgsign=false",
    "commit",
    "--quiet",
    "-m",
    "fixture")
$publicationCommit = (& git -C $publicationRepo rev-parse HEAD).Trim().ToLowerInvariant()
if ($LASTEXITCODE -ne 0 -or $publicationCommit -cnotmatch '^[0-9a-f]{40,64}$') {
    throw "Could not resolve the formal publication fixture commit."
}
$publicationIntegrationRoot = Join-Path $publicationRepo "output/production-integration-evidence"
New-Item -ItemType Directory -Path $publicationIntegrationRoot -Force | Out-Null
$publicationTrxPath = Join-Path $publicationIntegrationRoot "production-integration.trx"
[System.IO.File]::WriteAllText(
    $publicationTrxPath,
    "<TestRun><ResultSummary outcome=`"Completed`"><Counters total=`"1`" executed=`"1`" passed=`"1`" failed=`"0`" notExecuted=`"0`" /></ResultSummary></TestRun>",
    [System.Text.UTF8Encoding]::new($false))
$publicationTrxFile = Get-Item -LiteralPath $publicationTrxPath
$publicationEvidencePath = Join-Path $publicationIntegrationRoot "integration-evidence.json"
$publicationEvidence = [ordered]@{
    schemaVersion = 1
    generatedAtUtc = [System.DateTimeOffset]::UtcNow.ToString("O")
    product = "OpenLineOps"
    repository = "openlineops/openlineops"
    commitSha = $publicationCommit
    runId = "123456"
    runUrl = "https://github.com/openlineops/openlineops/actions/runs/123456"
    jobName = "production-integration"
    testName = "OpenLineOps.PostgresIntegration.Tests.PostgresRabbitMqProductionCoordinationIntegrationTests.DurableOutboxAndResultInboxSurviveCoordinatorRestartAcrossRealBroker"
    conclusion = "success"
    counters = [ordered]@{
        total = 1
        executed = 1
        passed = 1
        failed = 0
        skipped = 0
    }
    trx = [ordered]@{
        relativePath = "output/production-integration-evidence/production-integration.trx"
        sizeBytes = $publicationTrxFile.Length
        sha256 = (Get-FileHash -LiteralPath $publicationTrxPath -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}
[System.IO.File]::WriteAllText(
    $publicationEvidencePath,
    (($publicationEvidence | ConvertTo-Json -Depth 8) + [Environment]::NewLine),
    [System.Text.UTF8Encoding]::new($false))
[System.IO.File]::WriteAllText(
    (Join-Path $publicationRepo "untracked-publication-input.txt"),
    "dirty-worktree-sentinel",
    [System.Text.UTF8Encoding]::new($false))

$publicationArguments = @(
    "-Version", "0.0.0-security-regression",
    "-RepositoryUrl", "https://github.com/openlineops/openlineops",
    "-SecurityContact", "security@openlineops.invalid",
    "-ConductContact", "conduct@openlineops.invalid",
    "-ProductionIntegrationEvidencePath", $publicationEvidencePath,
    "-ConfirmMitLicense",
    "-CodeSigningCertificateThumbprint", "00112233445566778899AABBCCDDEEFF00112233")
$dirtyPublication = Invoke-PowerShellProcess `
    -ScriptPath (Join-Path $publicationEng "prepare-final-publication.ps1") `
    -Arguments $publicationArguments
if ($dirtyPublication.ExitCode -eq 0 `
    -or $dirtyPublication.Text -notmatch "requires a clean Git worktree") {
    Write-Host $dirtyPublication.Text
    throw "Formal publication did not fail closed on an untracked worktree change."
}

$removedPasswordInvocation = Invoke-PowerShellProcess `
    -ScriptPath (Join-Path $publicationEng "prepare-final-publication.ps1") `
    -Arguments ($publicationArguments + @(
        "-CodeSigningCertificatePassword",
        "publication-password-sentinel"))
if ($removedPasswordInvocation.ExitCode -eq 0 `
    -or $removedPasswordInvocation.Text -notmatch "CodeSigningCertificatePassword" `
    -or $removedPasswordInvocation.Text -match "publication-password-sentinel") {
    Write-Host $removedPasswordInvocation.Text
    throw "Removed signing password CLI behavior was not rejected without disclosing its value."
}

$timeoutFixturePath = Join-Path $resolvedWorkRoot "timeout-process-fixture.ps1"
$timeoutChildPidPath = Join-Path $resolvedWorkRoot "timeout-child.pid"
[System.IO.File]::WriteAllText(
    $timeoutFixturePath,
    @'
param([Parameter(Mandatory = $true)][string] $ChildPidPath)
$child = Start-Process `
    -FilePath "powershell" `
    -ArgumentList @("-NoProfile", "-Command", "Start-Sleep -Seconds 120") `
    -WindowStyle Hidden `
    -PassThru
[System.IO.File]::WriteAllText($ChildPidPath, $child.Id.ToString())
Wait-Process -Id $child.Id
'@,
    [System.Text.UTF8Encoding]::new($false))
$timeoutParent = $null
$timeoutChildId = $null
try {
    $timeoutParent = Start-Process `
        -FilePath "powershell" `
        -ArgumentList @(
            "-NoProfile",
            "-ExecutionPolicy",
            "Bypass",
            "-File",
            $timeoutFixturePath,
            "-ChildPidPath",
            $timeoutChildPidPath) `
        -WindowStyle Hidden `
        -PassThru
    $deadline = [System.DateTimeOffset]::UtcNow.AddSeconds(10)
    while (-not (Test-Path -LiteralPath $timeoutChildPidPath -PathType Leaf) `
        -and [System.DateTimeOffset]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 50
    }
    if (-not (Test-Path -LiteralPath $timeoutChildPidPath -PathType Leaf)) {
        throw "Timeout cleanup fixture did not publish its child PID."
    }
    $timeoutChildId = [int](Get-Content -LiteralPath $timeoutChildPidPath -Raw)

    $stopProcessTree = Get-FunctionDefinition -Ast $rabbitMqAst -Name "Stop-ProcessTree"
    $stopProcessTreeTest = [scriptblock]::Create(
        "param(`$TargetProcessId)" + [Environment]::NewLine +
        $stopProcessTree.Extent.Text + [Environment]::NewLine +
        "Stop-ProcessTree -ProcessId `$TargetProcessId")
    & $stopProcessTreeTest $timeoutParent.Id
    if (-not $timeoutParent.WaitForExit(10000) `
        -or $null -ne (Get-Process -Id $timeoutChildId -ErrorAction SilentlyContinue)) {
        throw "RabbitMQ timeout cleanup did not terminate the complete fixture process tree."
    }
}
finally {
    if ($null -ne $timeoutParent -and -not $timeoutParent.HasExited) {
        & (Join-Path $env:SystemRoot "System32/taskkill.exe") /PID $timeoutParent.Id /T /F |
            Out-Null
    }
    if ($null -ne $timeoutParent) {
        $timeoutParent.Dispose()
    }
    if ($null -ne $timeoutChildId `
        -and $null -ne (Get-Process -Id $timeoutChildId -ErrorAction SilentlyContinue)) {
        Stop-Process -Id $timeoutChildId -Force
    }
}

Write-Host "Release staging security verification passed."
Write-Host " - Source staging copied only Git-index tracked paths and excluded an untracked sentinel."
Write-Host " - Final publication rejected dirty Git state and bound staging to a full commit."
Write-Host " - Formal file/password signing parameters are absent and command logs redact secret-shaped arguments."
Write-Host " - Staged Agent RabbitMQ verification has a finite timeout and behavior-verified taskkill process-tree cleanup."

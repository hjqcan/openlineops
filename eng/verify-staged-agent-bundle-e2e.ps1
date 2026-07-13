param(
    [string] $ArtifactsRoot = "artifacts/release",

    [string] $WorkRoot = "output/staged-agent-bundle-e2e",

    [string] $Configuration = "Release",

    [switch] $NoBuild,

    [switch] $NoRestore
)

$ErrorActionPreference = "Stop"

$RepoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$ExpectedEntryPoints = @(
    [pscustomobject][ordered]@{
        role = "station-agent-service"
        relativePath = "OpenLineOps.Agent.exe"
    },
    [pscustomobject][ordered]@{
        role = "station-runtime"
        relativePath = "OpenLineOps.StationRuntime.exe"
    },
    [pscustomobject][ordered]@{
        role = "plugin-host"
        relativePath = "OpenLineOps.PluginHost.exe"
    },
    [pscustomobject][ordered]@{
        role = "python-script-worker"
        relativePath = "OpenLineOps.ScriptWorker.exe"
    }
)

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Resolve-RepoPath {
    param([Parameter(Mandatory = $true)][string] $Path)

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }

    return [System.IO.Path]::GetFullPath((Join-Path $RepoRoot $Path))
}

function Assert-UnderRepoRoot {
    param([Parameter(Mandatory = $true)][string] $Path)

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $normalizedRoot = $RepoRoot.TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
    $rootPrefix = $normalizedRoot + [System.IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith(
            $rootPrefix,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing staged Agent E2E work outside the repository root: $fullPath"
    }
}

function New-CleanDirectory {
    param([Parameter(Mandatory = $true)][string] $Path)

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    Assert-UnderRepoRoot $fullPath
    if (Test-Path -LiteralPath $fullPath) {
        Remove-Item -LiteralPath $fullPath -Recurse -Force
    }

    New-Item -ItemType Directory -Path $fullPath -Force | Out-Null
    return $fullPath
}

function Get-FileSha256 {
    param([Parameter(Mandatory = $true)][string] $Path)

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-RelativePathUnderDirectory {
    param(
        [Parameter(Mandatory = $true)][string] $Root,
        [Parameter(Mandatory = $true)][string] $Path
    )

    $resolvedRoot = [System.IO.Path]::GetFullPath($Root).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
    $rootPrefix = $resolvedRoot + [System.IO.Path]::DirectorySeparatorChar
    $resolvedPath = [System.IO.Path]::GetFullPath($Path)
    if (-not $resolvedPath.StartsWith(
            $rootPrefix,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Path '$resolvedPath' is not under '$resolvedRoot'."
    }

    return $resolvedPath.Substring($rootPrefix.Length)
}

function Resolve-CanonicalRelativePath {
    param(
        [Parameter(Mandatory = $true)][string] $Root,
        [Parameter(Mandatory = $true)][string] $RelativePath
    )

    if ([string]::IsNullOrWhiteSpace($RelativePath) `
        -or $RelativePath.Contains("\") `
        -or [System.IO.Path]::IsPathRooted($RelativePath)) {
        throw "Release artifact path is not canonical: '$RelativePath'."
    }

    $segments = $RelativePath.Split('/')
    if ($segments.Count -eq 0 `
        -or @($segments | Where-Object {
            $_ -eq "" -or $_ -eq "." -or $_ -eq ".."
        }).Count -ne 0) {
        throw "Release artifact path contains an unsafe segment: '$RelativePath'."
    }

    $fullRoot = [System.IO.Path]::GetFullPath($Root).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
    $fullPath = [System.IO.Path]::GetFullPath((Join-Path $fullRoot (
        $RelativePath.Replace('/', [System.IO.Path]::DirectorySeparatorChar))))
    $rootPrefix = $fullRoot + [System.IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith(
            $rootPrefix,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Release artifact path resolves outside its root: '$RelativePath'."
    }

    return $fullPath
}

function Get-SingleReleaseArtifact {
    param(
        [Parameter(Mandatory = $true)] $Manifest,
        [Parameter(Mandatory = $true)][string] $Kind,
        [Parameter(Mandatory = $true)][string] $ArtifactsRoot
    )

    $entries = @($Manifest.artifacts | Where-Object { $_.kind -ceq $Kind })
    if ($entries.Count -ne 1) {
        throw "Release manifest must contain exactly one '$Kind' artifact; found $($entries.Count)."
    }

    $entry = $entries[0]
    $path = Resolve-CanonicalRelativePath -Root $ArtifactsRoot -RelativePath $entry.relativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Release artifact does not exist: $path"
    }
    if ((Get-Item -LiteralPath $path).Attributes.HasFlag(
            [System.IO.FileAttributes]::ReparsePoint)) {
        throw "Release artifact cannot be a symbolic link or reparse point: $path"
    }
    if ((Get-Item -LiteralPath $path).Length -ne [long]$entry.sizeBytes) {
        throw "Release artifact size does not match the release manifest: $($entry.relativePath)"
    }
    if ((Get-FileSha256 $path) -cne [string]$entry.sha256) {
        throw "Release artifact SHA-256 does not match the release manifest: $($entry.relativePath)"
    }

    return [pscustomobject][ordered]@{
        Entry = $entry
        Path = $path
    }
}

function Expand-CanonicalArchive {
    param(
        [Parameter(Mandatory = $true)][string] $ArchivePath,
        [Parameter(Mandatory = $true)][string] $DestinationPath
    )

    $destination = New-CleanDirectory $DestinationPath
    $archive = [System.IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        $entryNames = [System.Collections.Generic.HashSet[string]]::new(
            [System.StringComparer]::Ordinal)
        foreach ($entry in $archive.Entries) {
            if (-not $entryNames.Add($entry.FullName)) {
                throw "Release archive contains duplicate entry '$($entry.FullName)'."
            }
            if ($entry.FullName.Contains("\") `
                -or [System.IO.Path]::IsPathRooted($entry.FullName) `
                -or @($entry.FullName.Split('/') | Where-Object {
                    $_ -eq "." -or $_ -eq ".."
                }).Count -ne 0) {
                throw "Release archive contains unsafe entry '$($entry.FullName)'."
            }
        }
    }
    finally {
        $archive.Dispose()
    }

    [System.IO.Compression.ZipFile]::ExtractToDirectory($ArchivePath, $destination)
    return $destination
}

function Assert-AgentBundleManifest {
    param([Parameter(Mandatory = $true)][string] $BundleRoot)

    $manifestPath = Join-Path $BundleRoot "bundle-manifest.json"
    $checksumsPath = Join-Path $BundleRoot "bundle-checksums.sha256"
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf) `
        -or -not (Test-Path -LiteralPath $checksumsPath -PathType Leaf)) {
        throw "Extracted Agent bundle is missing its manifest or checksums file."
    }

    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($manifest.schemaVersion -ne 1 `
        -or $manifest.product -cne "OpenLineOps" `
        -or $manifest.artifactKind -cne "agent" `
        -or $manifest.runtimeIdentifier -cne "win-x64" `
        -or $manifest.selfContained -ne $true) {
        throw "Extracted Agent bundle manifest does not describe the strict self-contained win-x64 Agent contract."
    }

    $entryPoints = @($manifest.entryPoints)
    if ($entryPoints.Count -ne $ExpectedEntryPoints.Count) {
        throw "Agent bundle must declare exactly four ordered entry points."
    }
    for ($index = 0; $index -lt $ExpectedEntryPoints.Count; $index++) {
        if ($entryPoints[$index].role -cne $ExpectedEntryPoints[$index].role `
            -or $entryPoints[$index].relativePath -cne $ExpectedEntryPoints[$index].relativePath) {
            throw "Agent bundle entry point $index does not match the formal role contract."
        }
    }

    $expectedPayloadPaths = [string[]]@($manifest.files | ForEach-Object {
        [string]$_.relativePath
    })
    [System.Array]::Sort($expectedPayloadPaths, [System.StringComparer]::Ordinal)
    $actualPayloadPaths = [string[]]@(Get-ChildItem -LiteralPath $BundleRoot -Recurse -File | ForEach-Object {
        (Get-RelativePathUnderDirectory -Root $BundleRoot -Path $_.FullName).Replace('\', '/')
    } | Where-Object {
        $_ -cne "bundle-manifest.json" -and $_ -cne "bundle-checksums.sha256"
    })
    [System.Array]::Sort($actualPayloadPaths, [System.StringComparer]::Ordinal)
    if (($expectedPayloadPaths -join "`n") -cne ($actualPayloadPaths -join "`n")) {
        throw "Extracted Agent payload membership does not exactly match bundle-manifest.json."
    }

    $expectedChecksumLines = [System.Collections.Generic.List[string]]::new()
    foreach ($file in @($manifest.files)) {
        $path = Resolve-CanonicalRelativePath -Root $BundleRoot -RelativePath $file.relativePath
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Agent bundle manifest file is missing: $($file.relativePath)"
        }
        if ((Get-Item -LiteralPath $path).Length -ne [long]$file.sizeBytes `
            -or (Get-FileSha256 $path) -cne [string]$file.sha256) {
            throw "Agent bundle payload does not match its manifest: $($file.relativePath)"
        }
        $expectedChecksumLines.Add("$($file.sha256)  $($file.relativePath)")
    }

    $actualChecksumLines = @(Get-Content -LiteralPath $checksumsPath | Where-Object {
        -not [string]::IsNullOrWhiteSpace($_)
    })
    if (($expectedChecksumLines -join "`n") -cne ($actualChecksumLines -join "`n")) {
        throw "Agent bundle checksums do not exactly match bundle-manifest.json."
    }

    return $manifest
}

function Assert-AgentReleaseSecurityTemplate {
    param([Parameter(Mandatory = $true)][string] $BundleRoot)

    $configuration = Get-Content -LiteralPath (Join-Path $BundleRoot "appsettings.json") -Raw |
        ConvertFrom-Json
    $agent = $configuration.OpenLineOps.Agent
    if ($agent.RuntimeExecutablePath -cne "OpenLineOps.StationRuntime.exe" `
        -or $agent.PluginHostExecutablePath -cne "OpenLineOps.PluginHost.exe" `
        -or $agent.PythonScript.WorkerExecutablePath -cne "OpenLineOps.ScriptWorker.exe") {
        throw "Agent release template does not pin every executable to its co-packaged entry point."
    }

    $agentProperties = @($agent.PSObject.Properties.Name)
    if (-not ($agentProperties -ccontains "SafetyExecutablePath") `
        -or $agent.SafetyExecutablePath -isnot [string] `
        -or $agent.SafetyExecutablePath -cne "") {
        throw "Agent release template must leave the machine-specific SafetyExecutablePath empty."
    }

    $sandbox = $agent.PythonScript.Sandbox
    if ($sandbox.RequireLeastPrivilegeExecution -ne $true `
        -or $sandbox.IsolationMode -cne "LeastPrivilegeIdentity" `
        -or $sandbox.LeastPrivilegeIdentity -cne "RestrictedCurrentLowIntegrity" `
        -or $sandbox.LeastPrivilegeLauncherExecutable -cne "OpenLineOps.LeastPrivilegeLauncher.exe" `
        -or $sandbox.LeastPrivilegeNoInteractivePrompt -ne $true `
        -or -not [string]::IsNullOrWhiteSpace($sandbox.LeastPrivilegeArgumentsTemplate)) {
        throw "Agent release template must require the fixed bundled non-interactive RestrictedCurrentLowIntegrity launcher."
    }
    if (-not (Test-Path `
            -LiteralPath (Join-Path $BundleRoot "OpenLineOps.LeastPrivilegeLauncher.exe") `
            -PathType Leaf)) {
        throw "Agent release bundle is missing its fixed Least Privilege Launcher."
    }
    $sandboxProperties = @($sandbox.PSObject.Properties.Name)
    if (@($sandboxProperties | Where-Object {
            $_ -clike "Container*" -or $_ -ceq "AdditionalContainerRunArguments"
        }).Count -ne 0) {
        throw "Agent release template exposes a removed Python Container setting."
    }
}

function Invoke-EntryPointProbe {
    param(
        [Parameter(Mandatory = $true)][string] $Name,
        [Parameter(Mandatory = $true)][string] $ExecutablePath,
        [Parameter(Mandatory = $true)][string] $WorkingDirectory,
        [int] $ExpectedExitCode = [int]::MinValue,
        [switch] $RequireNonZeroExit,
        [Parameter(Mandatory = $true)][string] $ExpectedOutputPattern
    )

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $ExecutablePath
    $startInfo.WorkingDirectory = $WorkingDirectory
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardInput = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($key in @($startInfo.EnvironmentVariables.Keys)) {
        if ($key -like "OpenLineOps__Agent__*" `
            -or $key -ceq "DOTNET_ENVIRONMENT" `
            -or $key -ceq "ASPNETCORE_ENVIRONMENT") {
            $startInfo.EnvironmentVariables.Remove($key)
        }
    }

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    try {
        if (-not $process.Start()) {
            throw "Staged entry point '$Name' did not start."
        }
        $process.StandardInput.Close()
        $standardOutput = $process.StandardOutput.ReadToEndAsync()
        $standardError = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(15000)) {
            $process.Kill()
            $process.WaitForExit()
            throw "Staged entry point '$Name' did not stop within 15 seconds."
        }

        $output = ($standardOutput.Result + "`n" + $standardError.Result).Trim()
        if ($ExpectedExitCode -ne [int]::MinValue `
            -and $process.ExitCode -ne $ExpectedExitCode) {
            throw "Staged entry point '$Name' exited $($process.ExitCode); expected $ExpectedExitCode. Output: $output"
        }
        if ($RequireNonZeroExit -and $process.ExitCode -eq 0) {
            throw "Staged entry point '$Name' must fail closed with the undeployed release template."
        }
        if ($output -notmatch $ExpectedOutputPattern) {
            throw "Staged entry point '$Name' did not emit its expected role contract. Output: $output"
        }

        return [pscustomobject][ordered]@{
            name = $Name
            executable = [System.IO.Path]::GetFileName($ExecutablePath)
            exitCode = $process.ExitCode
            outputContract = $ExpectedOutputPattern
            status = "passed"
        }
    }
    finally {
        $process.Dispose()
    }
}

if (-not [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
        [System.Runtime.InteropServices.OSPlatform]::Windows)) {
    throw "The staged Agent bundle E2E gate requires Windows."
}

$resolvedArtifactsRoot = Resolve-RepoPath $ArtifactsRoot
$resolvedWorkRoot = Resolve-RepoPath $WorkRoot
Assert-UnderRepoRoot $resolvedWorkRoot
if (-not (Test-Path -LiteralPath $resolvedArtifactsRoot -PathType Container)) {
    throw "ArtifactsRoot does not exist: $resolvedArtifactsRoot"
}

$manifestPath = Join-Path $resolvedArtifactsRoot "release-manifest.json"
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw "Staged release manifest does not exist: $manifestPath"
}
$releaseManifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($releaseManifest.schemaVersion -ne 1 -or $releaseManifest.product -cne "OpenLineOps") {
    throw "The staged release manifest is not the strict OpenLineOps release contract."
}

$resolvedWorkRoot = New-CleanDirectory $resolvedWorkRoot
$agentArtifact = Get-SingleReleaseArtifact `
    -Manifest $releaseManifest `
    -Kind "agent" `
    -ArtifactsRoot $resolvedArtifactsRoot
$samplePluginArtifact = Get-SingleReleaseArtifact `
    -Manifest $releaseManifest `
    -Kind "sample-plugin" `
    -ArtifactsRoot $resolvedArtifactsRoot
$agentBundleRoot = Expand-CanonicalArchive `
    -ArchivePath $agentArtifact.Path `
    -DestinationPath (Join-Path $resolvedWorkRoot "agent-bundle")
$samplePluginRoot = Expand-CanonicalArchive `
    -ArchivePath $samplePluginArtifact.Path `
    -DestinationPath (Join-Path $resolvedWorkRoot "sample-plugin")

$bundleManifest = Assert-AgentBundleManifest -BundleRoot $agentBundleRoot
Assert-AgentReleaseSecurityTemplate -BundleRoot $agentBundleRoot

$probes = @()
$probes += Invoke-EntryPointProbe `
    -Name "station-agent-service" `
    -ExecutablePath (Join-Path $agentBundleRoot "OpenLineOps.Agent.exe") `
    -WorkingDirectory $agentBundleRoot `
    -RequireNonZeroExit `
    -ExpectedOutputPattern "TrustedPackagePublicKeyFiles"
$probes += Invoke-EntryPointProbe `
    -Name "station-runtime" `
    -ExecutablePath (Join-Path $agentBundleRoot "OpenLineOps.StationRuntime.exe") `
    -WorkingDirectory $agentBundleRoot `
    -ExpectedExitCode 64 `
    -ExpectedOutputPattern "execute-operation --request-file"
$probes += Invoke-EntryPointProbe `
    -Name "plugin-host" `
    -ExecutablePath (Join-Path $agentBundleRoot "OpenLineOps.PluginHost.exe") `
    -WorkingDirectory $agentBundleRoot `
    -ExpectedExitCode 2 `
    -ExpectedOutputPattern "--manifest is required"
$probes += Invoke-EntryPointProbe `
    -Name "python-script-worker" `
    -ExecutablePath (Join-Path $agentBundleRoot "OpenLineOps.ScriptWorker.exe") `
    -WorkingDirectory $agentBundleRoot `
    -ExpectedExitCode 2 `
    -ExpectedOutputPattern "Python script worker request JSON is invalid"

$testProject = Resolve-RepoPath "tests/OpenLineOps.Agent.Tests/OpenLineOps.Agent.Tests.csproj"
$testFilter = "FullyQualifiedName=OpenLineOps.Agent.Tests.SignedVendorProgramStationE2ETests.SignedFrozenPluginRunsThroughAgentStationRuntimeAndBundledHost|FullyQualifiedName=OpenLineOps.Agent.Tests.SignedVendorProgramStationE2ETests.SignedFrozenPythonFlowRunsThroughAgentStationRuntimeAndWorker"
$testArguments = @(
    "test",
    $testProject,
    "--configuration",
    $Configuration,
    "--filter",
    $testFilter,
    "--logger",
    "console;verbosity=minimal"
)
if ($NoBuild) {
    $testArguments += "--no-build"
}
if ($NoRestore) {
    $testArguments += "--no-restore"
}

$previousAgentBundleRoot = $env:OPENLINEOPS_STAGED_AGENT_BUNDLE_ROOT
$previousSamplePluginRoot = $env:OPENLINEOPS_STAGED_SAMPLE_PLUGIN_ROOT
$previousPythonTokenEvidencePath = $env:OPENLINEOPS_STAGED_PYTHON_TOKEN_EVIDENCE_PATH
$pythonTokenEvidencePath = Join-Path $resolvedWorkRoot "python-child-token-evidence.json"
try {
    $env:OPENLINEOPS_STAGED_AGENT_BUNDLE_ROOT = $agentBundleRoot
    $env:OPENLINEOPS_STAGED_SAMPLE_PLUGIN_ROOT = $samplePluginRoot
    $env:OPENLINEOPS_STAGED_PYTHON_TOKEN_EVIDENCE_PATH = $pythonTokenEvidencePath
    $previousErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $testOutput = & dotnet @testArguments 2>&1
        $testExitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }
    $testOutput | ForEach-Object { Write-Host $_ }
    if ($testExitCode -ne 0) {
        throw "Signed staged Agent process-chain tests failed with exit code $testExitCode."
    }
}
finally {
    $env:OPENLINEOPS_STAGED_AGENT_BUNDLE_ROOT = $previousAgentBundleRoot
    $env:OPENLINEOPS_STAGED_SAMPLE_PLUGIN_ROOT = $previousSamplePluginRoot
    $env:OPENLINEOPS_STAGED_PYTHON_TOKEN_EVIDENCE_PATH =
        $previousPythonTokenEvidencePath
}

if (-not (Test-Path -LiteralPath $pythonTokenEvidencePath -PathType Leaf)) {
    throw "The staged Python child did not emit token evidence."
}
$pythonTokenEvidence = Get-Content -LiteralPath $pythonTokenEvidencePath -Raw |
    ConvertFrom-Json
$pythonTokenEvidenceProperties = @($pythonTokenEvidence.PSObject.Properties.Name)
if ($pythonTokenEvidenceProperties.Count -ne 4 `
    -or -not ($pythonTokenEvidenceProperties -ccontains "schemaVersion") `
    -or -not ($pythonTokenEvidenceProperties -ccontains "isolationMode") `
    -or -not ($pythonTokenEvidenceProperties -ccontains "tokenRestricted") `
    -or -not ($pythonTokenEvidenceProperties -ccontains "integrityRid") `
    -or $pythonTokenEvidence.schemaVersion -ne 1 `
    -or $pythonTokenEvidence.isolationMode -cne "LeastPrivilegeIdentity" `
    -or $pythonTokenEvidence.tokenRestricted -ne $true `
    -or $pythonTokenEvidence.integrityRid -ne 4096) {
    throw "The staged Python child token evidence is not restricted Low Integrity execution."
}

$evidence = [ordered]@{
    schemaVersion = 1
    product = "OpenLineOps"
    generatedAtUtc = [System.DateTimeOffset]::UtcNow.ToString("O")
    releaseVersion = $releaseManifest.version
    agentArtifact = [ordered]@{
        relativePath = $agentArtifact.Entry.relativePath
        sha256 = $agentArtifact.Entry.sha256
    }
    samplePluginArtifact = [ordered]@{
        relativePath = $samplePluginArtifact.Entry.relativePath
        sha256 = $samplePluginArtifact.Entry.sha256
    }
    entryPoints = @($bundleManifest.entryPoints)
    entryPointProbes = $probes
    signedPackageProcessChains = @(
        [ordered]@{
            name = "signed-frozen-plugin"
            path = "Agent application layer -> staged StationRuntime -> staged PluginHost"
            packageFormat = ".olopkg"
            status = "passed"
        },
        [ordered]@{
            name = "signed-frozen-python"
            path = "Agent application layer -> staged StationRuntime -> staged ScriptWorker"
            packageFormat = ".olopkg"
            executionPolicy = "Required RestrictedCurrentLowIntegrity"
            tokenRestricted = [bool]$pythonTokenEvidence.tokenRestricted
            integrityRid = [int]$pythonTokenEvidence.integrityRid
            status = "passed"
        }
    )
    productionPythonPolicy = [ordered]@{
        templateVerified = $true
        requireLeastPrivilegeExecution = $true
        isolationMode = "LeastPrivilegeIdentity"
        identity = "RestrictedCurrentLowIntegrity"
        launcher = "OpenLineOps.LeastPrivilegeLauncher.exe"
        noInteractivePrompt = $true
        childTokenRestricted = [bool]$pythonTokenEvidence.tokenRestricted
        childIntegrityRid = [int]$pythonTokenEvidence.integrityRid
        stagedExecutionVerified = $true
    }
    rabbitMqTransportCoverage = [ordered]@{
        status = "not-requested"
        requirement = "Set OPENLINEOPS_RABBITMQ_URI to run the real staged Agent process boundary."
    }
    status = "passed"
}
$evidencePath = Join-Path $resolvedWorkRoot "evidence.json"
[System.IO.File]::WriteAllText(
    $evidencePath,
    (($evidence | ConvertTo-Json -Depth 10) + "`r`n"),
    [System.Text.UTF8Encoding]::new($false))

if (-not [string]::IsNullOrWhiteSpace($env:OPENLINEOPS_RABBITMQ_URI)) {
    $rabbitMqWorkRoot = Join-Path $resolvedWorkRoot "rabbitmq-process"
    & (Join-Path $PSScriptRoot "verify-staged-agent-rabbitmq-e2e.ps1") `
        -AgentBundleRoot $agentBundleRoot `
        -SamplePluginRoot $samplePluginRoot `
        -BrokerUri $env:OPENLINEOPS_RABBITMQ_URI `
        -WorkRoot $rabbitMqWorkRoot `
        -Configuration $Configuration `
        -NoBuild:$NoBuild `
        -NoRestore:$NoRestore
    $rabbitMqEvidencePath = Join-Path $rabbitMqWorkRoot "evidence.json"
    if (-not (Test-Path -LiteralPath $rabbitMqEvidencePath -PathType Leaf)) {
        throw "The staged Agent RabbitMQ child gate did not produce required evidence."
    }

    $rabbitMqEvidence = Get-Content `
        -LiteralPath $rabbitMqEvidencePath `
        -Raw | ConvertFrom-Json
    $evidence["rabbitMqTransportCoverage"] = [ordered]@{
        status = "passed"
        executionStatus = $rabbitMqEvidence.executionStatus
        judgement = $rabbitMqEvidence.judgement
        duplicateRedeliveryRejected = $rabbitMqEvidence.duplicateRedeliveryRejected
        duplicateAfterRestartRejected = $rabbitMqEvidence.duplicateAfterRestartRejected
        cleanShutdownVerified = $rabbitMqEvidence.cleanShutdownVerified
        evidence = "rabbitmq-process/evidence.json"
    }
    [System.IO.File]::WriteAllText(
        $evidencePath,
        (($evidence | ConvertTo-Json -Depth 10) + "`r`n"),
        [System.Text.UTF8Encoding]::new($false))
}

Write-Host "Staged Agent bundle E2E passed."
Write-Host " - Agent archive: $($agentArtifact.Entry.relativePath)"
Write-Host " - Four entry-point role probes passed."
Write-Host " - Signed plugin and Python .olopkg process chains used only extracted release executables."
Write-Host " - Evidence: $evidencePath"

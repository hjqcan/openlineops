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

function Remove-PrivateStagedExecutionFiles {
    param([Parameter(Mandatory = $true)][string] $Root)

    foreach ($entry in Get-ChildItem -LiteralPath $Root -Force -Recurse) {
        if (($entry.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Refusing to sanitize a staged Agent evidence tree containing a reparse point: $($entry.FullName)"
        }
    }

    foreach ($entry in Get-ChildItem -LiteralPath $Root -Force) {
        if ($entry.Name -cin @(
                "evidence.json",
                "test-results",
                "rabbitmq-process",
                "entry-point-probes")) {
            continue
        }
        Remove-Item -LiteralPath $entry.FullName -Recurse -Force
    }
    foreach ($publicEvidenceDirectory in @("rabbitmq-process", "entry-point-probes")) {
        $publicEvidenceRoot = Join-Path $Root $publicEvidenceDirectory
        foreach ($entry in Get-ChildItem -LiteralPath $publicEvidenceRoot -Force) {
            if ($entry.Name -ceq "evidence.json") {
                continue
            }
            Remove-Item -LiteralPath $entry.FullName -Recurse -Force
        }
    }
}

function Get-FileSha256 {
    param([Parameter(Mandatory = $true)][string] $Path)

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Read-SafeXml {
    param([Parameter(Mandatory = $true)][string] $Path)

    $settings = [System.Xml.XmlReaderSettings]::new()
    $settings.DtdProcessing = [System.Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver = $null
    $reader = [System.Xml.XmlReader]::Create($Path, $settings)
    try {
        $document = [System.Xml.XmlDocument]::new()
        $document.XmlResolver = $null
        $document.Load($reader)
        return $document
    }
    finally {
        $reader.Dispose()
    }
}

function Write-SanitizedExactTestTrx {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][string] $FullyQualifiedName,
        [Parameter(Mandatory = $true)][string] $TestId
    )

    $lastSeparator = $FullyQualifiedName.LastIndexOf('.')
    if ($lastSeparator -le 0 -or $lastSeparator -eq $FullyQualifiedName.Length - 1) {
        throw "Exact staged test name is not canonical: '$FullyQualifiedName'."
    }
    $className = $FullyQualifiedName.Substring(0, $lastSeparator)
    $methodName = $FullyQualifiedName.Substring($lastSeparator + 1)
    $settings = [System.Xml.XmlWriterSettings]::new()
    $settings.Encoding = [System.Text.UTF8Encoding]::new($false)
    $settings.Indent = $true
    $settings.NewLineChars = "`n"
    $settings.NewLineHandling = [System.Xml.NewLineHandling]::Replace
    $writer = [System.Xml.XmlWriter]::Create($Path, $settings)
    try {
        $writer.WriteStartDocument()
        $writer.WriteStartElement(
            "TestRun",
            "http://microsoft.com/schemas/VisualStudio/TeamTest/2010")
        $writer.WriteAttributeString("name", "staged-agent-bundle-e2e")
        $writer.WriteAttributeString("runUser", "redacted")

        $writer.WriteStartElement("Results")
        $writer.WriteStartElement("UnitTestResult")
        $writer.WriteAttributeString("testId", $TestId)
        $writer.WriteAttributeString("testName", $FullyQualifiedName)
        $writer.WriteAttributeString("computerName", "redacted")
        $writer.WriteAttributeString("outcome", "Passed")
        $writer.WriteEndElement()
        $writer.WriteEndElement()

        $writer.WriteStartElement("TestDefinitions")
        $writer.WriteStartElement("UnitTest")
        $writer.WriteAttributeString("name", $FullyQualifiedName)
        $writer.WriteAttributeString("storage", "OpenLineOps.Agent.Tests.dll")
        $writer.WriteAttributeString("id", $TestId)
        $writer.WriteStartElement("TestMethod")
        $writer.WriteAttributeString("codeBase", "OpenLineOps.Agent.Tests.dll")
        $writer.WriteAttributeString("className", $className)
        $writer.WriteAttributeString("name", $methodName)
        $writer.WriteEndElement()
        $writer.WriteEndElement()
        $writer.WriteEndElement()

        $writer.WriteStartElement("ResultSummary")
        $writer.WriteAttributeString("outcome", "Completed")
        $writer.WriteStartElement("Counters")
        $writer.WriteAttributeString("total", "1")
        $writer.WriteAttributeString("executed", "1")
        $writer.WriteAttributeString("passed", "1")
        $writer.WriteAttributeString("failed", "0")
        $writer.WriteAttributeString("notExecuted", "0")
        $writer.WriteEndElement()
        $writer.WriteEndElement()

        $writer.WriteEndElement()
        $writer.WriteEndDocument()
    }
    finally {
        $writer.Dispose()
    }
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
    $openLineOps = $configuration.OpenLineOps
    $openLineOpsProperties = @($openLineOps.PSObject.Properties.Name)
    if (-not ($openLineOpsProperties -ccontains "WindowsServiceName") `
        -or $openLineOps.WindowsServiceName -isnot [string] `
        -or $openLineOps.WindowsServiceName -cne "") {
        throw "Agent release template must expose an explicit empty WindowsServiceName for deployment-time binding."
    }
    $agent = $openLineOps.Agent
    $agentProperties = @($agent.PSObject.Properties.Name)
    if (-not ($agentProperties -ccontains "StationSystemId") `
        -or $agent.StationSystemId -isnot [string] `
        -or $agent.StationSystemId -cne "" `
        -or -not ($agentProperties -ccontains "HeartbeatInterval") `
        -or $agent.HeartbeatInterval -cne "00:00:05") {
        throw "Agent release template must expose an empty deployment StationSystemId and a 00:00:05 HeartbeatInterval."
    }
    if (-not ($agentProperties -ccontains "PackageCacheDirectory") `
        -or $agent.PackageCacheDirectory -isnot [string] `
        -or $agent.PackageCacheDirectory -cne "") {
        throw "Agent release template must expose an explicit empty PackageCacheDirectory for administrator provisioning."
    }
    $brokerUri = $null
    if ($agent.RequireBrokerTls -ne $true `
        -or $agent.BrokerUri -isnot [string] `
        -or -not [System.Uri]::TryCreate(
            $agent.BrokerUri,
            [System.UriKind]::Absolute,
            [ref]$brokerUri) `
        -or $brokerUri.Scheme -cne "amqps" `
        -or -not [string]::IsNullOrEmpty($brokerUri.UserInfo)) {
        throw "Agent release template must require amqps without embedded placeholder broker credentials."
    }
    $coordinatorUri = $null
    if (-not ($agentProperties -ccontains "CoordinatorBaseUri") `
        -or $agent.CoordinatorBaseUri -isnot [string] `
        -or -not [System.Uri]::TryCreate(
            $agent.CoordinatorBaseUri,
            [System.UriKind]::Absolute,
            [ref]$coordinatorUri) `
        -or $coordinatorUri.Scheme -cne "https" `
        -or -not [string]::IsNullOrEmpty($coordinatorUri.UserInfo) `
        -or -not [string]::IsNullOrEmpty($coordinatorUri.Query) `
        -or -not [string]::IsNullOrEmpty($coordinatorUri.Fragment) `
        -or $agent.ArtifactUploadTimeout -cne "00:05:00") {
        throw "Agent release template must use a credential-free HTTPS CoordinatorBaseUri and a 00:05:00 artifact upload timeout."
    }
    if ($agentProperties -ccontains "ArtifactUploadBearerToken") {
        throw "Agent release template must not embed ArtifactUploadBearerToken."
    }
    if ($agent.RuntimeExecutablePath -cne "OpenLineOps.StationRuntime.exe" `
        -or $agent.PluginHostExecutablePath -cne "OpenLineOps.PluginHost.exe" `
        -or $agent.PythonScript.WorkerExecutablePath -cne "OpenLineOps.ScriptWorker.exe") {
        throw "Agent release template does not pin every executable to its co-packaged entry point."
    }

    if (-not ($agentProperties -ccontains "SafetyExecutablePath") `
        -or $agent.SafetyExecutablePath -isnot [string] `
        -or $agent.SafetyExecutablePath -cne "") {
        throw "Agent release template must leave the machine-specific SafetyExecutablePath empty."
    }

    $sandbox = $agent.PythonScript.Sandbox
    if ($sandbox.RequireLeastPrivilegeExecution -ne $true `
        -or $sandbox.IsolationMode -cne "LeastPrivilegeIdentity" `
        -or $sandbox.LeastPrivilegeIdentity -cne "PerExecutionAppContainer" `
        -or $sandbox.LeastPrivilegeLauncherExecutable -cne "OpenLineOps.LeastPrivilegeLauncher.exe" `
        -or $sandbox.LeastPrivilegeNoInteractivePrompt -ne $true `
        -or -not [string]::IsNullOrWhiteSpace($sandbox.LeastPrivilegeArgumentsTemplate)) {
        throw "Agent release template must require the fixed bundled non-interactive PerExecutionAppContainer launcher."
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
        [Parameter(Mandatory = $true)][string] $ExpectedOutputPattern,
        [hashtable] $EnvironmentOverrides = @{}
    )

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $ExecutablePath
    $startInfo.WorkingDirectory = $WorkingDirectory
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardInput = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $utf8WithoutBom = [System.Text.UTF8Encoding]::new($false)
    $startInfo.StandardOutputEncoding = $utf8WithoutBom
    $startInfo.StandardErrorEncoding = $utf8WithoutBom
    foreach ($key in @($startInfo.EnvironmentVariables.Keys)) {
        if ($key -like "OpenLineOps__*" `
            -or $key -ceq "DOTNET_ENVIRONMENT" `
            -or $key -ceq "ASPNETCORE_ENVIRONMENT") {
            $startInfo.EnvironmentVariables.Remove($key)
        }
    }
    foreach ($entry in $EnvironmentOverrides.GetEnumerator()) {
        if ($entry.Key -isnot [string] `
            -or [string]::IsNullOrWhiteSpace([string]$entry.Key) `
            -or $entry.Value -isnot [string] `
            -or [string]::IsNullOrWhiteSpace([string]$entry.Value)) {
            throw "Staged entry point '$Name' has an invalid probe environment override."
        }
        $startInfo.EnvironmentVariables[[string]$entry.Key] = [string]$entry.Value
    }

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    $previousInputEncoding = [Console]::InputEncoding
    try {
        [Console]::InputEncoding = $utf8WithoutBom
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
            executableSha256 = Get-FileSha256 $ExecutablePath
            exitCode = $process.ExitCode
            outputContract = $ExpectedOutputPattern
            status = "passed"
        }
    }
    finally {
        [Console]::InputEncoding = $previousInputEncoding
        $process.Dispose()
    }
}

function Invoke-ExactDotNetTest {
    param(
        [Parameter(Mandatory = $true)][string] $TestProject,
        [Parameter(Mandatory = $true)][string] $Configuration,
        [Parameter(Mandatory = $true)][string] $FullyQualifiedName,
        [Parameter(Mandatory = $true)][string] $ResultName,
        [Parameter(Mandatory = $true)][string] $ResultsDirectory,
        [switch] $NoBuild,
        [switch] $NoRestore
    )

    [System.IO.Directory]::CreateDirectory($ResultsDirectory) | Out-Null
    $trxName = "$ResultName.trx"
    $trxPath = Join-Path $ResultsDirectory $trxName
    Remove-Item -LiteralPath $trxPath -Force -ErrorAction SilentlyContinue
    $arguments = @(
        "test",
        $TestProject,
        "--configuration",
        $Configuration,
        "--filter",
        "FullyQualifiedName=$FullyQualifiedName",
        "--results-directory",
        $ResultsDirectory,
        "--logger",
        "trx;LogFileName=$trxName"
    )
    if ($NoBuild) {
        $arguments += "--no-build"
    }
    if ($NoRestore) {
        $arguments += "--no-restore"
    }

    $previousErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $testOutput = & dotnet @arguments 2>&1
        $testExitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }
    $testOutput | ForEach-Object { Write-Host $_ }
    if ($testExitCode -ne 0) {
        throw "Exact staged test '$FullyQualifiedName' failed with exit code $testExitCode."
    }
    if (-not (Test-Path -LiteralPath $trxPath -PathType Leaf)) {
        throw "Exact staged test '$FullyQualifiedName' did not produce its TRX evidence."
    }

    $trx = Read-SafeXml $trxPath
    $definitions = @($trx.TestRun.TestDefinitions.UnitTest)
    $results = @($trx.TestRun.Results.UnitTestResult)
    $counters = $trx.TestRun.ResultSummary.Counters
    if ($definitions.Count -ne 1 `
        -or $results.Count -ne 1 `
        -or $null -eq $counters) {
        throw "Exact staged test '$FullyQualifiedName' must discover and execute exactly one test."
    }
    $definition = $definitions[0]
    $result = $results[0]
    $discoveredName = "$($definition.TestMethod.className).$($definition.TestMethod.name)"
    if ($discoveredName -cne $FullyQualifiedName `
        -or $definition.name -cne $FullyQualifiedName `
        -or $result.testName -cne $FullyQualifiedName `
        -or $result.testId -cne $definition.id `
        -or $result.outcome -cne "Passed" `
        -or [int]$counters.total -ne 1 `
        -or [int]$counters.executed -ne 1 `
        -or [int]$counters.passed -ne 1 `
        -or [int]$counters.failed -ne 0 `
        -or [int]$counters.notExecuted -ne 0) {
        throw "Exact staged test evidence does not prove '$FullyQualifiedName' passed."
    }

    Write-SanitizedExactTestTrx `
        -Path $trxPath `
        -FullyQualifiedName $FullyQualifiedName `
        -TestId ([string]$definition.id)

    return [pscustomobject][ordered]@{
        fullyQualifiedName = $FullyQualifiedName
        result = "passed"
        trxRelativePath = (Get-RelativePathUnderDirectory `
            -Root $resolvedWorkRoot `
            -Path $trxPath).Replace('\', '/')
        trxSha256 = Get-FileSha256 $trxPath
    }
}

if (-not [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
        [System.Runtime.InteropServices.OSPlatform]::Windows)) {
    throw "The staged Agent bundle E2E gate requires Windows."
}
if ([string]::IsNullOrWhiteSpace($env:OPENLINEOPS_RABBITMQ_URI)) {
    throw "OPENLINEOPS_RABBITMQ_URI is required; the staged Agent bundle E2E cannot skip its real RabbitMQ process boundary."
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
$apiArtifact = Get-SingleReleaseArtifact `
    -Manifest $releaseManifest `
    -Kind "api" `
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
$apiBundleRoot = Expand-CanonicalArchive `
    -ArchivePath $apiArtifact.Path `
    -DestinationPath (Join-Path $resolvedWorkRoot "api-bundle")
if (-not (Test-Path -LiteralPath (Join-Path $apiBundleRoot "OpenLineOps.Api.exe") -PathType Leaf)) {
    throw "The staged API bundle is missing OpenLineOps.Api.exe."
}

$bundleManifest = Assert-AgentBundleManifest -BundleRoot $agentBundleRoot
Assert-AgentReleaseSecurityTemplate -BundleRoot $agentBundleRoot

$probes = @()
$probes += Invoke-EntryPointProbe `
    -Name "station-agent-service" `
    -ExecutablePath (Join-Path $agentBundleRoot "OpenLineOps.Agent.exe") `
    -WorkingDirectory $agentBundleRoot `
    -RequireNonZeroExit `
    -ExpectedOutputPattern "^OpenLineOps Station Agent terminated: OpenLineOps:WindowsServiceName must contain 1-80 ASCII letters, digits, periods, underscores, or hyphens\.$"
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
    -ExpectedOutputPattern "Python script worker request body is required"

$testProject = Resolve-RepoPath "tests/OpenLineOps.Agent.Tests/OpenLineOps.Agent.Tests.csproj"
$previousAgentBundleRoot = $env:OPENLINEOPS_STAGED_AGENT_BUNDLE_ROOT
$previousSamplePluginRoot = $env:OPENLINEOPS_STAGED_SAMPLE_PLUGIN_ROOT
$previousPythonTokenEvidencePath = $env:OPENLINEOPS_STAGED_PYTHON_TOKEN_EVIDENCE_PATH
$previousRequireRealAppContainer = $env:OPENLINEOPS_REQUIRE_REAL_APPCONTAINER
$pythonTokenEvidencePath = Join-Path $resolvedWorkRoot "python-child-token-evidence.json"
$testResultsDirectory = Join-Path $resolvedWorkRoot "test-results"
$testEvidence = @()
try {
    $env:OPENLINEOPS_STAGED_AGENT_BUNDLE_ROOT = $agentBundleRoot
    $env:OPENLINEOPS_STAGED_SAMPLE_PLUGIN_ROOT = $samplePluginRoot
    $env:OPENLINEOPS_STAGED_PYTHON_TOKEN_EVIDENCE_PATH = $pythonTokenEvidencePath
    $env:OPENLINEOPS_REQUIRE_REAL_APPCONTAINER = "1"
    $testEvidence += Invoke-ExactDotNetTest `
        -TestProject $testProject `
        -Configuration $Configuration `
        -FullyQualifiedName "OpenLineOps.Agent.Tests.SignedVendorProgramStationE2ETests.SignedFrozenPluginRunsThroughAgentStationRuntimeAndBundledHost" `
        -ResultName "signed-frozen-plugin" `
        -ResultsDirectory $testResultsDirectory `
        -NoBuild:$NoBuild `
        -NoRestore:$NoRestore
    $testEvidence += Invoke-ExactDotNetTest `
        -TestProject $testProject `
        -Configuration $Configuration `
        -FullyQualifiedName "OpenLineOps.Agent.Tests.SignedVendorProgramStationE2ETests.SignedFrozenPythonFlowRunsThroughAgentStationRuntimeAndWorker" `
        -ResultName "signed-frozen-python" `
        -ResultsDirectory $testResultsDirectory `
        -NoBuild:$NoBuild `
        -NoRestore:$NoRestore
    $testEvidence += Invoke-ExactDotNetTest `
        -TestProject $testProject `
        -Configuration $Configuration `
        -FullyQualifiedName "OpenLineOps.Agent.Tests.LeastPrivilegeLauncherContractTests.ConcurrentWorkersUseDistinctAppContainersAndKillDescendants" `
        -ResultName "staged-launcher-isolation" `
        -ResultsDirectory $testResultsDirectory `
        -NoBuild:$NoBuild `
        -NoRestore:$NoRestore
    $testEvidence += Invoke-ExactDotNetTest `
        -TestProject $testProject `
        -Configuration $Configuration `
        -FullyQualifiedName "OpenLineOps.Agent.Tests.LeastPrivilegeLauncherContractTests.StaleAppContainerProfileIsRecoveredBeforeNextLaunch" `
        -ResultName "staged-launcher-crash-recovery" `
        -ResultsDirectory $testResultsDirectory `
        -NoBuild:$NoBuild `
        -NoRestore:$NoRestore
    $testEvidence += Invoke-ExactDotNetTest `
        -TestProject $testProject `
        -Configuration $Configuration `
        -FullyQualifiedName "OpenLineOps.Agent.Tests.LeastPrivilegeLauncherContractTests.ProvisioningCommandGrantsRuntimeCapabilityRecursively" `
        -ResultName "staged-python-runtime-provisioning" `
        -ResultsDirectory $testResultsDirectory `
        -NoBuild:$NoBuild `
        -NoRestore:$NoRestore
}
finally {
    $env:OPENLINEOPS_STAGED_AGENT_BUNDLE_ROOT = $previousAgentBundleRoot
    $env:OPENLINEOPS_STAGED_SAMPLE_PLUGIN_ROOT = $previousSamplePluginRoot
    $env:OPENLINEOPS_STAGED_PYTHON_TOKEN_EVIDENCE_PATH =
        $previousPythonTokenEvidencePath
    $env:OPENLINEOPS_REQUIRE_REAL_APPCONTAINER = $previousRequireRealAppContainer
}

if (-not (Test-Path -LiteralPath $pythonTokenEvidencePath -PathType Leaf)) {
    throw "The staged Python child did not emit token evidence."
}
$pythonTokenEvidence = Get-Content -LiteralPath $pythonTokenEvidencePath -Raw |
    ConvertFrom-Json
$pythonTokenEvidenceProperties = @($pythonTokenEvidence.PSObject.Properties.Name)
if ($pythonTokenEvidenceProperties.Count -ne 5 `
    -or -not ($pythonTokenEvidenceProperties -ccontains "schemaVersion") `
    -or -not ($pythonTokenEvidenceProperties -ccontains "isolationMode") `
    -or -not ($pythonTokenEvidenceProperties -ccontains "tokenIsAppContainer") `
    -or -not ($pythonTokenEvidenceProperties -ccontains "appContainerSid") `
    -or -not ($pythonTokenEvidenceProperties -ccontains "integrityRid") `
    -or $pythonTokenEvidence.schemaVersion -ne 1 `
    -or $pythonTokenEvidence.isolationMode -cne "LeastPrivilegeIdentity" `
    -or $pythonTokenEvidence.tokenIsAppContainer -ne $true `
    -or $pythonTokenEvidence.appContainerSid -cnotmatch '^S-1-15-2-(?:[0-9]+-){6}[0-9]+$' `
    -or $pythonTokenEvidence.integrityRid -ne 4096) {
    throw "The staged Python child evidence is not a unique Low Integrity AppContainer execution."
}

$publicEntryPoints = @($bundleManifest.entryPoints | ForEach-Object {
        $entryPoint = $_
        $file = @($bundleManifest.files | Where-Object {
                $_.relativePath -ceq $entryPoint.relativePath
            })
        if ($file.Count -ne 1 `
            -or [string]$file[0].sha256 -cnotmatch '^[0-9a-f]{64}$') {
            throw "Agent entry point '$($entryPoint.role)' is not bound to exactly one hashed bundle payload."
        }

        [pscustomobject][ordered]@{
            role = [string]$entryPoint.role
            relativePath = [string]$entryPoint.relativePath
            sha256 = [string]$file[0].sha256
        }
    })
foreach ($entryPoint in $publicEntryPoints) {
    $probe = @($probes | Where-Object {
            $_.name -ceq $entryPoint.role
        })
    if ($probe.Count -ne 1 `
        -or $probe[0].executable -cne $entryPoint.relativePath `
        -or $probe[0].executableSha256 -cne $entryPoint.sha256) {
        throw "Agent entry point '$($entryPoint.role)' probe is not hash-bound to its exact bundle payload."
    }
}

$entryPointProbeEvidenceRelativePath = "entry-point-probes/evidence.json"
$entryPointProbeEvidencePath = Join-Path `
    $resolvedWorkRoot `
    $entryPointProbeEvidenceRelativePath
[System.IO.Directory]::CreateDirectory(
    [System.IO.Path]::GetDirectoryName($entryPointProbeEvidencePath)) | Out-Null
$entryPointProbeEvidence = [ordered]@{
    schema = "openlineops.staged-agent-entry-point-probe-evidence"
    schemaVersion = 1
    agentArtifactSha256 = [string]$agentArtifact.Entry.sha256
    entryPoints = $publicEntryPoints
    probes = $probes
}
[System.IO.File]::WriteAllText(
    $entryPointProbeEvidencePath,
    (($entryPointProbeEvidence | ConvertTo-Json -Depth 6) + "`r`n"),
    [System.Text.UTF8Encoding]::new($false))

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
    entryPoints = $publicEntryPoints
    entryPointProbes = $probes
    entryPointProbeEvidence = [ordered]@{
        evidence = $entryPointProbeEvidenceRelativePath
        evidenceSha256 = Get-FileSha256 $entryPointProbeEvidencePath
    }
    exactTestEvidence = $testEvidence
    signedPackageProcessChains = @(
        [ordered]@{
            name = "signed-frozen-plugin"
            path = "Agent application layer -> staged StationRuntime -> staged PluginHost"
            packageFormat = ".olopkg"
            exactTest = $testEvidence[0]
            status = "passed"
        },
        [ordered]@{
            name = "signed-frozen-python"
            path = "Agent application layer -> staged StationRuntime -> staged ScriptWorker"
            packageFormat = ".olopkg"
            executionPolicy = "Required PerExecutionAppContainer"
            tokenIsAppContainer = [bool]$pythonTokenEvidence.tokenIsAppContainer
            appContainerSid = [string]$pythonTokenEvidence.appContainerSid
            integrityRid = [int]$pythonTokenEvidence.integrityRid
            exactTest = $testEvidence[1]
            status = "passed"
        }
    )
    productionPythonPolicy = [ordered]@{
        templateVerified = $true
        requireLeastPrivilegeExecution = $true
        isolationMode = "LeastPrivilegeIdentity"
        identity = "PerExecutionAppContainer"
        launcher = "OpenLineOps.LeastPrivilegeLauncher.exe"
        noInteractivePrompt = $true
        childTokenIsAppContainer = [bool]$pythonTokenEvidence.tokenIsAppContainer
        childAppContainerSid = [string]$pythonTokenEvidence.appContainerSid
        childIntegrityRid = [int]$pythonTokenEvidence.integrityRid
        stagedIsolationTest = $testEvidence[2]
        stagedCrashRecoveryTest = $testEvidence[3]
        stagedPythonRuntimeProvisioningTest = $testEvidence[4]
        stagedExecutionVerified = $true
    }
    rabbitMqTransportCoverage = $null
    status = "passed"
}
$evidencePath = Join-Path $resolvedWorkRoot "evidence.json"
$rabbitMqWorkRoot = Join-Path $resolvedWorkRoot "rabbitmq-process"
& (Join-Path $PSScriptRoot "verify-staged-agent-rabbitmq-e2e.ps1") `
    -AgentBundleRoot $agentBundleRoot `
    -SamplePluginRoot $samplePluginRoot `
    -ApiBundleRoot $apiBundleRoot `
    -BrokerUri $env:OPENLINEOPS_RABBITMQ_URI `
    -WorkRoot $rabbitMqWorkRoot `
    -Configuration $Configuration `
    -NoBuild:$NoBuild `
    -NoRestore:$NoRestore
$rabbitMqEvidencePath = Join-Path $rabbitMqWorkRoot "evidence.json"
if (-not (Test-Path -LiteralPath $rabbitMqEvidencePath -PathType Leaf)) {
    throw "The staged Agent RabbitMQ child gate did not produce required evidence."
}
Assert-AgentBundleManifest -BundleRoot $agentBundleRoot | Out-Null

$privateRabbitMqEvidence = Get-Content `
    -LiteralPath $rabbitMqEvidencePath `
    -Raw | ConvertFrom-Json
$publicAgentIdentity = [ordered]@{
    nonAdministrative = [bool]$privateRabbitMqEvidence.agentHostIdentity.NonAdministrative
    isPrimaryToken = [bool]$privateRabbitMqEvidence.agentHostIdentity.IsPrimaryToken
    isElevated = [bool]$privateRabbitMqEvidence.agentHostIdentity.IsElevated
    isRestrictedToken = [bool]$privateRabbitMqEvidence.agentHostIdentity.IsRestrictedToken
    administratorGroupPresent = [bool]$privateRabbitMqEvidence.agentHostIdentity.AdministratorGroupPresent
    administratorGroupEnabled = [bool]$privateRabbitMqEvidence.agentHostIdentity.AdministratorGroupEnabled
    administratorGroupDenyOnly = [bool]$privateRabbitMqEvidence.agentHostIdentity.AdministratorGroupDenyOnly
    serviceLogonSidPresent = [bool]$privateRabbitMqEvidence.agentHostIdentity.ServiceLogonSidPresent
    serviceLogonSidEnabled = [bool]$privateRabbitMqEvidence.agentHostIdentity.ServiceLogonSidEnabled
    exactServiceSidPresent = [bool]$privateRabbitMqEvidence.agentHostIdentity.ExactServiceSidPresent
    exactServiceSidEnabled = [bool]$privateRabbitMqEvidence.agentHostIdentity.ExactServiceSidEnabled
    exactServiceSidRestricted = [bool]$privateRabbitMqEvidence.agentHostIdentity.ExactServiceSidRestricted
    isAuthenticated = [bool]$privateRabbitMqEvidence.agentHostIdentity.IsAuthenticated
    isSystem = [bool]$privateRabbitMqEvidence.agentHostIdentity.IsSystem
    identityStrategy = [string]$privateRabbitMqEvidence.agentHostIdentity.identityStrategy
    serviceAccountName = [string]$privateRabbitMqEvidence.agentHostIdentity.serviceAccountName
    serviceAccountSid = [string]$privateRabbitMqEvidence.agentHostIdentity.serviceAccountSid
    serviceSid = [string]$privateRabbitMqEvidence.agentHostIdentity.serviceSid
}
$publicRestartedAgentIdentity = [ordered]@{
    nonAdministrative = [bool]$privateRabbitMqEvidence.restartedAgentHostIdentity.NonAdministrative
    isPrimaryToken = [bool]$privateRabbitMqEvidence.restartedAgentHostIdentity.IsPrimaryToken
    isElevated = [bool]$privateRabbitMqEvidence.restartedAgentHostIdentity.IsElevated
    isRestrictedToken = [bool]$privateRabbitMqEvidence.restartedAgentHostIdentity.IsRestrictedToken
    administratorGroupPresent = [bool]$privateRabbitMqEvidence.restartedAgentHostIdentity.AdministratorGroupPresent
    administratorGroupEnabled = [bool]$privateRabbitMqEvidence.restartedAgentHostIdentity.AdministratorGroupEnabled
    administratorGroupDenyOnly = [bool]$privateRabbitMqEvidence.restartedAgentHostIdentity.AdministratorGroupDenyOnly
    serviceLogonSidPresent = [bool]$privateRabbitMqEvidence.restartedAgentHostIdentity.ServiceLogonSidPresent
    serviceLogonSidEnabled = [bool]$privateRabbitMqEvidence.restartedAgentHostIdentity.ServiceLogonSidEnabled
    exactServiceSidPresent = [bool]$privateRabbitMqEvidence.restartedAgentHostIdentity.ExactServiceSidPresent
    exactServiceSidEnabled = [bool]$privateRabbitMqEvidence.restartedAgentHostIdentity.ExactServiceSidEnabled
    exactServiceSidRestricted = [bool]$privateRabbitMqEvidence.restartedAgentHostIdentity.ExactServiceSidRestricted
    isAuthenticated = [bool]$privateRabbitMqEvidence.restartedAgentHostIdentity.IsAuthenticated
    isSystem = [bool]$privateRabbitMqEvidence.restartedAgentHostIdentity.IsSystem
    identityStrategy = [string]$privateRabbitMqEvidence.restartedAgentHostIdentity.identityStrategy
    serviceAccountName = [string]$privateRabbitMqEvidence.restartedAgentHostIdentity.serviceAccountName
    serviceAccountSid = [string]$privateRabbitMqEvidence.restartedAgentHostIdentity.serviceAccountSid
    serviceSid = [string]$privateRabbitMqEvidence.restartedAgentHostIdentity.serviceSid
}
$publicMaterialArrivalIpc = [ordered]@{
    serviceTokenConnected = [bool]$privateRabbitMqEvidence.materialArrivalIpc.serviceTokenConnected
    pipeExactAclVerified = [bool]$privateRabbitMqEvidence.materialArrivalIpc.pipeExactAclVerified
    durablePublicationVerified = [bool]$privateRabbitMqEvidence.materialArrivalIpc.durablePublicationVerified
    ordinaryCiTokenExplicitAccessDenied = `
        [bool]$privateRabbitMqEvidence.materialArrivalIpc.ordinaryCiTokenExplicitAccessDenied
}
$immutableContentCacheFields = @(
    "packagedProvisionCommandVerified",
    "runningServiceAdministrationRejected",
    "serviceTokenReadExecuteVerified",
    "sealedMutationAccessDenied",
    "deepAncestorMutationAccessDenied",
    "preSealRecoveryVerified",
    "cleanupCrashResumeVerified",
    "committedAdminRemovalVerified",
    "packagedRemovalCommandVerified",
    "cacheNamespaceRemoved")
$actualImmutableContentCacheFields = @(
    $privateRabbitMqEvidence.immutableContentCache.PSObject.Properties.Name)
if ($actualImmutableContentCacheFields.Count -ne $immutableContentCacheFields.Count `
    -or @($immutableContentCacheFields | Where-Object {
            $actualImmutableContentCacheFields -cnotcontains $_
        }).Count -ne 0) {
    throw "Private staged Agent immutable content-cache evidence must use exact strict-schema properties."
}
$publicImmutableContentCache = [ordered]@{}
foreach ($field in $immutableContentCacheFields) {
    $value = $privateRabbitMqEvidence.immutableContentCache.$field
    if ($value -isnot [bool] -or $value -ne $true) {
        throw "Private staged Agent immutable content-cache field '$field' must be the JSON boolean true."
    }
    $publicImmutableContentCache[$field] = $value
}
$publicPresence = [ordered]@{
    persistedStates = @($privateRabbitMqEvidence.presence.persistedStates)
    startedAndHeartbeatPersisted = [bool]$privateRabbitMqEvidence.presence.startedAndHeartbeatPersisted
    expiredOfflineDuringBrokerOutage = [bool]$privateRabbitMqEvidence.presence.expiredOfflineDuringBrokerOutage
    offlineDuringBrokerOutage = [ordered]@{
        status = [string]$privateRabbitMqEvidence.presence.offlineDuringBrokerOutage.status
        health = [string]$privateRabbitMqEvidence.presence.offlineDuringBrokerOutage.health
    }
    freshOnlineAfterReconnect = [bool]$privateRabbitMqEvidence.presence.freshOnlineAfterReconnect
    onlineAfterReconnect = [ordered]@{
        status = [string]$privateRabbitMqEvidence.presence.onlineAfterReconnect.status
        health = [string]$privateRabbitMqEvidence.presence.onlineAfterReconnect.health
    }
}
$publicVendorArtifacts = @($privateRabbitMqEvidence.vendorArtifacts | ForEach-Object {
        [ordered]@{
            Name = [string]$_.Name
            Kind = [string]$_.Kind
            StorageKey = [string]$_.StorageKey
            ReceiptId = [string]$_.ReceiptId
            SizeBytes = [long]$_.SizeBytes
            Sha256 = [string]$_.Sha256
        }
    })
$rabbitMqEvidence = [ordered]@{
    schema = "openlineops.staged-agent-rabbitmq-e2e-evidence"
    schemaVersion = 1
    executionStatus = [string]$privateRabbitMqEvidence.executionStatus
    judgement = [string]$privateRabbitMqEvidence.judgement
    vendorProgram = [string]$privateRabbitMqEvidence.vendorProgram
    centralArtifactTransport = [string]$privateRabbitMqEvidence.centralArtifactTransport
    operatorTraceGetVerified = [bool]$privateRabbitMqEvidence.operatorTraceGetVerified
    brokerOutageVerified = [bool]$privateRabbitMqEvidence.brokerOutageVerified
    coordinatorTransportResultInboxRestartedAfterBrokerRecovery = `
        [bool]$privateRabbitMqEvidence.coordinatorTransportResultInboxRestartedAfterBrokerRecovery
    offlinePendingOutboxCount = [int]$privateRabbitMqEvidence.offlinePendingOutboxCount
    offlineCompletionWasNotDelivered = [bool]$privateRabbitMqEvidence.offlineCompletionWasNotDelivered
    completionDeliveredOnceAfterReconnect = [bool]$privateRabbitMqEvidence.completionDeliveredOnceAfterReconnect
    duplicateRedeliveryRejected = [bool]$privateRabbitMqEvidence.duplicateRedeliveryRejected
    duplicateAfterRestartRejected = [bool]$privateRabbitMqEvidence.duplicateAfterRestartRejected
    runtimeFinishedExecutionCount = [int]$privateRabbitMqEvidence.runtimeFinishedExecutionCount
    firstAgentPid = [int]$privateRabbitMqEvidence.firstAgentPid
    restartedAgentPid = [int]$privateRabbitMqEvidence.restartedAgentPid
    packageContentSha256 = [string]$privateRabbitMqEvidence.packageContentSha256
    AgentId = [string]$privateRabbitMqEvidence.AgentId
    StationId = [string]$privateRabbitMqEvidence.StationId
    vendorArtifacts = $publicVendorArtifacts
    agentHostIdentity = $publicAgentIdentity
    restartedAgentHostIdentity = $publicRestartedAgentIdentity
    materialArrivalIpc = $publicMaterialArrivalIpc
    immutableContentCache = $publicImmutableContentCache
    eventKinds = @($privateRabbitMqEvidence.eventKinds)
    progressPhases = @($privateRabbitMqEvidence.progressPhases)
    outageControlMode = [string]$privateRabbitMqEvidence.outageControlMode
    windowsServiceName = [string]$privateRabbitMqEvidence.windowsServiceName
    windowsServiceLifecycleVerified = [bool]$privateRabbitMqEvidence.windowsServiceLifecycleVerified
    presence = $publicPresence
    cleanShutdownVerified = [bool]$privateRabbitMqEvidence.cleanShutdownVerified
}
[System.IO.File]::WriteAllText(
    $rabbitMqEvidencePath,
    (($rabbitMqEvidence | ConvertTo-Json -Depth 10) + "`r`n"),
    [System.Text.UTF8Encoding]::new($false))
$evidence["rabbitMqTransportCoverage"] = [ordered]@{
    status = "passed"
    executionStatus = $rabbitMqEvidence.executionStatus
    judgement = $rabbitMqEvidence.judgement
    vendorProgram = $rabbitMqEvidence.vendorProgram
    centralArtifactTransport = $rabbitMqEvidence.centralArtifactTransport
    operatorTraceGetVerified = $rabbitMqEvidence.operatorTraceGetVerified
    vendorArtifacts = @($rabbitMqEvidence.vendorArtifacts)
    brokerOutageVerified = $rabbitMqEvidence.brokerOutageVerified
    coordinatorTransportResultInboxRestartedAfterBrokerRecovery = `
        $rabbitMqEvidence.coordinatorTransportResultInboxRestartedAfterBrokerRecovery
    agentHostIdentity = $rabbitMqEvidence.agentHostIdentity
    restartedAgentHostIdentity = $rabbitMqEvidence.restartedAgentHostIdentity
    materialArrivalIpc = $rabbitMqEvidence.materialArrivalIpc
    immutableContentCache = $rabbitMqEvidence.immutableContentCache
    agentId = $rabbitMqEvidence.AgentId
    stationId = $rabbitMqEvidence.StationId
    packageContentSha256 = $rabbitMqEvidence.packageContentSha256
    firstAgentPid = $rabbitMqEvidence.firstAgentPid
    restartedAgentPid = $rabbitMqEvidence.restartedAgentPid
    runtimeFinishedExecutionCount = $rabbitMqEvidence.runtimeFinishedExecutionCount
    eventKinds = @($rabbitMqEvidence.eventKinds)
    progressPhases = @($rabbitMqEvidence.progressPhases)
    offlinePendingOutboxCount = $rabbitMqEvidence.offlinePendingOutboxCount
    offlineCompletionWasNotDelivered = $rabbitMqEvidence.offlineCompletionWasNotDelivered
    completionDeliveredOnceAfterReconnect = `
        $rabbitMqEvidence.completionDeliveredOnceAfterReconnect
    duplicateRedeliveryRejected = $rabbitMqEvidence.duplicateRedeliveryRejected
    duplicateAfterRestartRejected = $rabbitMqEvidence.duplicateAfterRestartRejected
    outageControlMode = $rabbitMqEvidence.outageControlMode
    windowsServiceName = $rabbitMqEvidence.windowsServiceName
    windowsServiceLifecycleVerified = $rabbitMqEvidence.windowsServiceLifecycleVerified
    presence = $rabbitMqEvidence.presence
    cleanShutdownVerified = $rabbitMqEvidence.cleanShutdownVerified
    evidence = "rabbitmq-process/evidence.json"
    evidenceSha256 = Get-FileSha256 $rabbitMqEvidencePath
}
[System.IO.File]::WriteAllText(
    $evidencePath,
    (($evidence | ConvertTo-Json -Depth 10) + "`r`n"),
    [System.Text.UTF8Encoding]::new($false))

& (Join-Path $PSScriptRoot "verify-staged-agent-evidence.ps1") `
    -EvidenceRoot $resolvedWorkRoot
Remove-PrivateStagedExecutionFiles -Root $resolvedWorkRoot
& (Join-Path $PSScriptRoot "verify-staged-agent-evidence.ps1") `
    -EvidenceRoot $resolvedWorkRoot `
    -RequireSanitizedRoot

Write-Host "Staged Agent bundle E2E passed."
Write-Host " - Agent archive: $($agentArtifact.Entry.relativePath)"
Write-Host " - Four entry-point role probes passed."
Write-Host " - Signed plugin and Python .olopkg process chains used only extracted release executables."
Write-Host " - The extracted Agent passed its RabbitMQ lifecycle through a temporary Windows SCM service."
Write-Host " - Evidence: $evidencePath"

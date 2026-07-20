param(
    [ValidateSet("Debug", "Release")]
    [string] $Configuration = "Release",

    [string] $Version = "0.0.0-local",

    [string] $ArtifactsRoot = "artifacts/release",

    [string] $WorkRoot = "artifacts/release-work",

    [switch] $NoRestore,

    [switch] $SkipDesktopBuild,

    [switch] $SignWindowsPackages,

    [string] $CodeSigningSignToolPath,

    [string] $CodeSigningCertificateThumbprint,

    [switch] $CodeSigningAutoSelectCertificate,

    [switch] $CodeSigningStoreMachine,

    [string] $CodeSigningTimestampUrl = "http://timestamp.digicert.com",

    [switch] $RequireCleanGitWorkTree,

    [string] $ExpectedGitCommit
)

$ErrorActionPreference = "Stop"

$RepoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$RequiredKinds = @("source", "api", "agent", "runner", "desktop", "plugin-host", "script-worker", "sample-plugin")

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

    if (-not $fullPath.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to write outside the repository root: $fullPath"
    }
}

function Get-RepoRelativePath {
    param([Parameter(Mandatory = $true)][string] $Path)

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $normalizedRoot = $RepoRoot.TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
    $rootPrefix = $normalizedRoot + [System.IO.Path]::DirectorySeparatorChar

    if (-not $fullPath.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Path is not under the repository root: $fullPath"
    }

    return $fullPath.Substring($rootPrefix.Length)
}

function New-CleanDirectory {
    param([Parameter(Mandatory = $true)][string] $Path)

    Assert-UnderRepoRoot $Path
    if (Test-Path -LiteralPath $Path) {
        Remove-Item -LiteralPath $Path -Recurse -Force
    }

    New-Item -ItemType Directory -Path $Path -Force | Out-Null
}

function Format-CommandArgumentsForLog {
    param([Parameter(Mandatory = $true)][string[]] $Arguments)

    $formatted = @()
    $maskNext = $false
    foreach ($argument in $Arguments) {
        if ($maskNext) {
            $formatted += "<redacted>"
            $maskNext = $false
            continue
        }

        $absoluteUri = $null
        if ([System.Uri]::TryCreate($argument, [System.UriKind]::Absolute, [ref]$absoluteUri) `
            -and (-not [string]::IsNullOrEmpty($absoluteUri.UserInfo) `
                -or $absoluteUri.Query -match '(?i)(password|passphrase|token|secret|credential|api[-_]?key)=')) {
            $formatted += "<redacted-uri>"
            continue
        }

        if ($argument -match '^(?<name>[^=:\s]+)(?<separator>=|:)(?<value>.*)$' `
            -and $Matches.name -match '(?i)(password|passphrase|token|secret|credential|api[-_]?key)') {
            $formatted += "$($Matches.name)$($Matches.separator)<redacted>"
            continue
        }

        $formatted += $argument
        if ($argument -match '(?i)(password|passphrase|token|secret|credential|api[-_]?key)' `
            -or $argument -ceq "/p") {
            $maskNext = $true
        }
    }

    return $formatted
}

function Invoke-CheckedCommand {
    param(
        [Parameter(Mandatory = $true)][string] $FilePath,
        [Parameter(Mandatory = $true)][string[]] $Arguments,
        [string] $WorkingDirectory = $RepoRoot
    )

    $resolvedFilePath = $FilePath
    $cmdCommand = Get-Command "$FilePath.cmd" -ErrorAction SilentlyContinue
    if ($cmdCommand -ne $null) {
        $resolvedFilePath = $cmdCommand.Source
    }
    else {
        $command = Get-Command $FilePath -ErrorAction SilentlyContinue
        if ($command -ne $null) {
            $resolvedFilePath = $command.Source
        }
    }

    Write-Host "> $FilePath $((Format-CommandArgumentsForLog -Arguments $Arguments) -join ' ')"
    Push-Location $WorkingDirectory
    try {
        & $resolvedFilePath @Arguments | Out-Host
        $exitCode = $LASTEXITCODE
    }
    finally {
        Pop-Location
    }

    if ($exitCode -ne 0) {
        $safeArguments = Format-CommandArgumentsForLog -Arguments $Arguments
        throw "Command failed with exit code ${exitCode}: $FilePath $($safeArguments -join ' ')"
    }
}

function Invoke-ExpectedExitCode {
    param(
        [Parameter(Mandatory = $true)][string] $FilePath,
        [string[]] $Arguments = @(),
        [Parameter(Mandatory = $true)][int] $ExpectedExitCode,
        [string] $WorkingDirectory = $RepoRoot
    )

    $formattedArguments = if ($Arguments.Count -eq 0) {
        ""
    }
    else {
        (Format-CommandArgumentsForLog -Arguments $Arguments) -join ' '
    }
    Write-Host "> $FilePath $formattedArguments (expected exit $ExpectedExitCode)"
    Push-Location $WorkingDirectory
    try {
        & $FilePath @Arguments | Out-Host
        $exitCode = $LASTEXITCODE
    }
    finally {
        Pop-Location
    }
    if ($exitCode -ne $ExpectedExitCode) {
        throw "Command exited ${exitCode}, expected ${ExpectedExitCode}: $FilePath"
    }
}

function Publish-DotNetProject {
    param(
        [Parameter(Mandatory = $true)][string] $ProjectPath,
        [Parameter(Mandatory = $true)][string] $OutputDirectory
    )

    $arguments = @(
        "publish",
        (Resolve-RepoPath $ProjectPath),
        "-c",
        $Configuration,
        "-o",
        $OutputDirectory,
        "/p:UseAppHost=false",
        "/p:TreatWarningsAsErrors=true"
    )

    if ($NoRestore) {
        $arguments += "--no-restore"
    }

    Invoke-CheckedCommand -FilePath "dotnet" -Arguments $arguments
}

function Publish-WindowsSelfContainedProject {
    param(
        [Parameter(Mandatory = $true)][string] $ProjectPath,
        [Parameter(Mandatory = $true)][string] $OutputDirectory,
        [switch] $SingleFile
    )

    $arguments = @(
        "publish",
        (Resolve-RepoPath $ProjectPath),
        "-c",
        $Configuration,
        "-r",
        "win-x64",
        "--self-contained",
        "true",
        "-o",
        $OutputDirectory,
        "/p:UseAppHost=true",
        "/p:TreatWarningsAsErrors=true"
    )

    if ($SingleFile) {
        $arguments += @(
            "/p:PublishSingleFile=true",
            "/p:IncludeNativeLibrariesForSelfExtract=true",
            "/p:EnableCompressionInSingleFile=true"
        )
    }

    if ($NoRestore) {
        $arguments += "--no-restore"
    }

    Invoke-CheckedCommand -FilePath "dotnet" -Arguments $arguments
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
    if (-not $resolvedPath.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Path '$resolvedPath' is not under '$resolvedRoot'."
    }

    return $resolvedPath.Substring($rootPrefix.Length)
}

function Merge-DirectoryContents {
    param(
        [Parameter(Mandatory = $true)][string] $SourceDirectory,
        [Parameter(Mandatory = $true)][string] $DestinationDirectory
    )

    if (-not (Test-Path -LiteralPath $SourceDirectory -PathType Container)) {
        throw "Required directory does not exist: $SourceDirectory"
    }

    New-Item -ItemType Directory -Path $DestinationDirectory -Force | Out-Null
    foreach ($sourceFile in Get-ChildItem -LiteralPath $SourceDirectory -Recurse -File) {
        $relativePath = Get-RelativePathUnderDirectory `
            -Root $SourceDirectory `
            -Path $sourceFile.FullName
        $destinationPath = Join-Path $DestinationDirectory $relativePath
        $destinationParent = Split-Path $destinationPath -Parent
        New-Item -ItemType Directory -Path $destinationParent -Force | Out-Null
        if (Test-Path -LiteralPath $destinationPath -PathType Leaf) {
            if ((Get-FileSha256 $sourceFile.FullName) -cne (Get-FileSha256 $destinationPath)) {
                throw "Cannot merge different published files at '$relativePath'."
            }

            continue
        }

        Copy-Item -LiteralPath $sourceFile.FullName -Destination $destinationPath -Force
    }
}

function Write-WindowsBundleMetadata {
    param(
        [Parameter(Mandatory = $true)][string] $Root,
        [Parameter(Mandatory = $true)][string] $ArtifactKind,
        [Parameter(Mandatory = $true)][object[]] $EntryPoints
    )

    $manifestName = "bundle-manifest.json"
    $checksumsName = "bundle-checksums.sha256"
    $relativePaths = [string[]]@(
        Get-ChildItem -LiteralPath $Root -Recurse -File |
            ForEach-Object {
                (Get-RelativePathUnderDirectory -Root $Root -Path $_.FullName).Replace(
                    [System.IO.Path]::DirectorySeparatorChar,
                    '/')
            } |
            Where-Object { $_ -cne $manifestName -and $_ -cne $checksumsName }
    )
    [System.Array]::Sort($relativePaths, [System.StringComparer]::Ordinal)
    if ($relativePaths.Count -eq 0) {
        throw "Cannot write metadata for an empty $ArtifactKind bundle."
    }

    $files = @($relativePaths | ForEach-Object {
        $fullPath = Join-Path $Root $_.Replace('/', [System.IO.Path]::DirectorySeparatorChar)
        $file = Get-Item -LiteralPath $fullPath
        [ordered]@{
            relativePath = $_
            sizeBytes = $file.Length
            sha256 = Get-FileSha256 $fullPath
        }
    })

    foreach ($entryPoint in $EntryPoints) {
        if ($entryPoint.role -isnot [string] -or [string]::IsNullOrWhiteSpace($entryPoint.role) `
            -or $entryPoint.relativePath -isnot [string] `
            -or $relativePaths -cnotcontains $entryPoint.relativePath) {
            throw "The $ArtifactKind bundle entry point is invalid or missing: $($entryPoint.relativePath)"
        }
    }

    $manifest = [ordered]@{
        schemaVersion = 1
        product = "OpenLineOps"
        artifactKind = $ArtifactKind
        runtimeIdentifier = "win-x64"
        selfContained = $true
        entryPoints = $EntryPoints
        files = $files
    }
    [System.IO.File]::WriteAllText(
        (Join-Path $Root $manifestName),
        (($manifest | ConvertTo-Json -Depth 8) + "`r`n"),
        [System.Text.UTF8Encoding]::new($false))

    $checksumLines = @($files | ForEach-Object { "$($_.sha256)  $($_.relativePath)" })
    [System.IO.File]::WriteAllText(
        (Join-Path $Root $checksumsName),
        (($checksumLines -join "`r`n") + "`r`n"),
        [System.Text.UTF8Encoding]::new($false))
}

function Assert-AgentBundleConfiguration {
    param([Parameter(Mandatory = $true)][string] $Root)

    $configurationPath = Join-Path $Root "appsettings.json"
    if (-not (Test-Path -LiteralPath $configurationPath -PathType Leaf)) {
        throw "The Station Agent bundle is missing appsettings.json."
    }

    $configuration = Get-Content -LiteralPath $configurationPath -Raw | ConvertFrom-Json
    $openLineOpsConfiguration = $configuration.OpenLineOps
    $openLineOpsConfigurationProperties = @(
        $openLineOpsConfiguration.PSObject.Properties.Name)
    if (-not ($openLineOpsConfigurationProperties -ccontains "WindowsServiceName") `
        -or $openLineOpsConfiguration.WindowsServiceName -isnot [string] `
        -or $openLineOpsConfiguration.WindowsServiceName -cne "") {
        throw "OpenLineOps:WindowsServiceName must be present and empty in the release template for deployment-time binding."
    }
    $agentConfiguration = $configuration.OpenLineOps.Agent
    $agentConfigurationProperties = @($agentConfiguration.PSObject.Properties.Name)
    if (-not ($agentConfigurationProperties -ccontains "StationSystemId") `
        -or $agentConfiguration.StationSystemId -isnot [string] `
        -or $agentConfiguration.StationSystemId -cne "") {
        throw "OpenLineOps:Agent:StationSystemId must be present and empty in the release template for deployment-time binding."
    }
    if (-not ($agentConfigurationProperties -ccontains "HeartbeatInterval") `
        -or $agentConfiguration.HeartbeatInterval -cne "00:00:05") {
        throw "OpenLineOps:Agent:HeartbeatInterval must be exactly '00:00:05' in the release template."
    }
    if (-not ($agentConfigurationProperties -ccontains "PackageCacheDirectory") `
        -or $agentConfiguration.PackageCacheDirectory -isnot [string] `
        -or $agentConfiguration.PackageCacheDirectory -cne "") {
        throw "OpenLineOps:Agent:PackageCacheDirectory must be present and empty in the release template for explicit administrator provisioning."
    }
    if ($agentConfiguration.RequireBrokerTls -ne $true `
        -or $agentConfiguration.BrokerUri -isnot [string]) {
        throw "The Station Agent release template must require an explicit TLS RabbitMQ broker URI."
    }
    $brokerUri = $null
    if (-not [System.Uri]::TryCreate(
            $agentConfiguration.BrokerUri,
            [System.UriKind]::Absolute,
            [ref]$brokerUri) `
        -or $brokerUri.Scheme -cne "amqps" `
        -or -not [string]::IsNullOrEmpty($brokerUri.UserInfo)) {
        throw "The Station Agent release template BrokerUri must be an amqps URI without embedded placeholder credentials."
    }
    $coordinatorUri = $null
    if (-not ($agentConfigurationProperties -ccontains "CoordinatorBaseUri") `
        -or $agentConfiguration.CoordinatorBaseUri -isnot [string] `
        -or -not [System.Uri]::TryCreate(
            $agentConfiguration.CoordinatorBaseUri,
            [System.UriKind]::Absolute,
            [ref]$coordinatorUri) `
        -or $coordinatorUri.Scheme -cne "https" `
        -or -not [string]::IsNullOrEmpty($coordinatorUri.UserInfo) `
        -or -not [string]::IsNullOrEmpty($coordinatorUri.Query) `
        -or -not [string]::IsNullOrEmpty($coordinatorUri.Fragment) `
        -or $agentConfiguration.ArtifactUploadTimeout -cne "00:05:00") {
        throw "The Station Agent release template must use a credential-free HTTPS CoordinatorBaseUri and a 00:05:00 artifact upload timeout."
    }
    if ($agentConfigurationProperties -ccontains "ArtifactUploadBearerToken") {
        throw "The Station Agent release template must not embed ArtifactUploadBearerToken."
    }
    $runtimeExecutablePath = $configuration.OpenLineOps.Agent.RuntimeExecutablePath
    if ($runtimeExecutablePath -cne "OpenLineOps.StationRuntime.exe") {
        throw "OpenLineOps:Agent:RuntimeExecutablePath must be exactly 'OpenLineOps.StationRuntime.exe' in the release bundle."
    }

    if (-not (Test-Path -LiteralPath (Join-Path $Root $runtimeExecutablePath) -PathType Leaf)) {
        throw "The configured Station Runtime executable is missing from the Agent bundle."
    }

    $pluginHostExecutablePath = $configuration.OpenLineOps.Agent.PluginHostExecutablePath
    if ($pluginHostExecutablePath -cne "OpenLineOps.PluginHost.exe") {
        throw "OpenLineOps:Agent:PluginHostExecutablePath must be exactly 'OpenLineOps.PluginHost.exe' in the release bundle."
    }

    if (-not (Test-Path -LiteralPath (Join-Path $Root $pluginHostExecutablePath) -PathType Leaf)) {
        throw "The configured Plugin Host executable is missing from the Agent bundle."
    }

    $pythonScript = $configuration.OpenLineOps.Agent.PythonScript
    if ($null -eq $pythonScript) {
        throw "The Station Agent bundle is missing its typed PythonScript configuration."
    }

    $pythonScriptProperties = @($pythonScript.PSObject.Properties.Name)
    if (-not ($pythonScriptProperties -ccontains "HostPythonRuntimeDllPath") `
        -or $pythonScriptProperties -ccontains "PythonRuntimeDllPath") {
        throw "The Station Agent bundle must declare HostPythonRuntimeDllPath and must not contain the removed PythonRuntimeDllPath setting."
    }

    $scriptWorkerExecutablePath = $pythonScript.WorkerExecutablePath
    if ($scriptWorkerExecutablePath -cne "OpenLineOps.ScriptWorker.exe") {
        throw "OpenLineOps:Agent:PythonScript:WorkerExecutablePath must be exactly 'OpenLineOps.ScriptWorker.exe' in the release bundle."
    }

    if (-not (Test-Path -LiteralPath (Join-Path $Root $scriptWorkerExecutablePath) -PathType Leaf)) {
        throw "The configured Python Script Worker executable is missing from the Agent bundle."
    }

    if ($pythonScript.Sandbox.LeastPrivilegeIdentity -cne "PerExecutionAppContainer") {
        throw "The Station Agent release bundle must use the fixed PerExecutionAppContainer Python identity."
    }
    $leastPrivilegeLauncherPath = $pythonScript.Sandbox.LeastPrivilegeLauncherExecutable
    if ($leastPrivilegeLauncherPath -cne "OpenLineOps.LeastPrivilegeLauncher.exe") {
        throw "OpenLineOps:Agent:PythonScript:Sandbox:LeastPrivilegeLauncherExecutable must be exactly 'OpenLineOps.LeastPrivilegeLauncher.exe' in the release bundle."
    }
    if (-not (Test-Path -LiteralPath (Join-Path $Root $leastPrivilegeLauncherPath) -PathType Leaf)) {
        throw "The configured Least Privilege Launcher executable is missing from the Agent bundle."
    }
    if (-not [string]::IsNullOrWhiteSpace($pythonScript.Sandbox.LeastPrivilegeArgumentsTemplate)) {
        throw "The Station Agent release bundle must not configure a custom Python least-privilege launcher template."
    }
    if ($pythonScript.Sandbox.LeastPrivilegeNoInteractivePrompt -ne $true) {
        throw "The Station Agent release bundle must require a non-interactive Python least-privilege launch."
    }

    if (-not ($agentConfigurationProperties -ccontains "SafetyExecutablePath")) {
        throw "The Station Agent release template must declare SafetyExecutablePath."
    }
    if ($configuration.OpenLineOps.Agent.SafetyExecutablePath -isnot [string] `
        -or $configuration.OpenLineOps.Agent.SafetyExecutablePath -cne "") {
        throw "OpenLineOps:Agent:SafetyExecutablePath must be empty in the release template and configured to the independently reviewed machine safety actuator during deployment."
    }

    $pythonSandbox = $pythonScript.Sandbox
    if ($null -eq $pythonSandbox `
        -or $pythonSandbox.RequireLeastPrivilegeExecution -ne $true `
        -or $pythonSandbox.IsolationMode -cne "LeastPrivilegeIdentity") {
        throw "The Station Agent release bundle must default Python execution to required LeastPrivilegeIdentity isolation."
    }

    $removedContainerProperties = @($pythonSandbox.PSObject.Properties.Name | Where-Object {
        $_ -clike "Container*" -or $_ -ceq "AdditionalContainerRunArguments"
    })
    if ($removedContainerProperties.Count -ne 0) {
        throw "The Station Agent release bundle must not expose removed Python Container settings."
    }

    $deploymentPath = Join-Path $Root "DEPLOYMENT.md"
    if (-not (Test-Path -LiteralPath $deploymentPath -PathType Leaf)) {
        throw "The Station Agent release bundle is missing DEPLOYMENT.md."
    }
    $deployment = Get-Content -LiteralPath $deploymentPath -Raw
    foreach ($requiredProvisioningContract in @(
            "--provision-content-cache",
            "--remove-content-cache-package",
            "OpenLineOps:WindowsServiceName",
            "OpenLineOps:Agent:PackageCacheDirectory",
            "dedicated content-cache namespace")) {
        if ($deployment -cnotmatch [regex]::Escape($requiredProvisioningContract)) {
            throw "The Station Agent deployment guide is missing content-cache provisioning contract '$requiredProvisioningContract'."
        }
    }

    $executablePaths = @(
        "OpenLineOps.Agent.exe",
        $runtimeExecutablePath,
        $pluginHostExecutablePath,
        $scriptWorkerExecutablePath,
        $leastPrivilegeLauncherPath
    )
    $executableHashes = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::Ordinal)
    foreach ($executablePath in $executablePaths) {
        $fullPath = Join-Path $Root $executablePath
        if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
            throw "The Station Agent executable is missing from its release bundle: $executablePath"
        }
        if (-not $executableHashes.Add((Get-FileSha256 $fullPath))) {
            throw "Every Station Agent executable payload must have a distinct SHA-256: $executablePath"
        }
    }
}

function Copy-DirectoryContents {
    param(
        [Parameter(Mandatory = $true)][string] $SourceDirectory,
        [Parameter(Mandatory = $true)][string] $DestinationDirectory
    )

    if (-not (Test-Path -LiteralPath $SourceDirectory -PathType Container)) {
        throw "Required directory does not exist: $SourceDirectory"
    }

    New-Item -ItemType Directory -Path $DestinationDirectory -Force | Out-Null
    Copy-Item -Path (Join-Path $SourceDirectory "*") -Destination $DestinationDirectory -Recurse -Force
}

function Compress-StagedDirectory {
    param(
        [Parameter(Mandatory = $true)][string] $SourceDirectory,
        [Parameter(Mandatory = $true)][string] $DestinationArchive
    )

    if (-not (Test-Path -LiteralPath $SourceDirectory -PathType Container)) {
        throw "Cannot package missing directory: $SourceDirectory"
    }

    $sourceFiles = @(Get-ChildItem -LiteralPath $SourceDirectory -Recurse -File -Force)
    if ($sourceFiles.Count -eq 0) {
        throw "Cannot package empty directory: $SourceDirectory"
    }

    if (Test-Path -LiteralPath $DestinationArchive) {
        Remove-Item -LiteralPath $DestinationArchive -Force
    }

    $archiveDirectory = Split-Path $DestinationArchive -Parent
    New-Item -ItemType Directory -Path $archiveDirectory -Force | Out-Null

    $archivePaths = [string[]]@(
        $sourceFiles |
            ForEach-Object {
                (Get-RelativePathUnderDirectory -Root $SourceDirectory -Path $_.FullName).Replace(
                    [System.IO.Path]::DirectorySeparatorChar,
                    '/')
            }
    )
    [System.Array]::Sort($archivePaths, [System.StringComparer]::Ordinal)
    if (($archivePaths | Select-Object -Unique).Count -ne $archivePaths.Count) {
        throw "Cannot package duplicate canonical paths from '$SourceDirectory'."
    }

    Add-Type -AssemblyName System.IO.Compression
    $archiveStream = [System.IO.File]::Open(
        $DestinationArchive,
        [System.IO.FileMode]::CreateNew,
        [System.IO.FileAccess]::ReadWrite,
        [System.IO.FileShare]::None)
    try {
        $archive = [System.IO.Compression.ZipArchive]::new(
            $archiveStream,
            [System.IO.Compression.ZipArchiveMode]::Create,
            $true)
        try {
            foreach ($archivePath in $archivePaths) {
                $sourcePath = Join-Path `
                    $SourceDirectory `
                    $archivePath.Replace('/', [System.IO.Path]::DirectorySeparatorChar)
                $sourceFile = Get-Item -LiteralPath $sourcePath
                $archiveEntry = $archive.CreateEntry(
                    $archivePath,
                    [System.IO.Compression.CompressionLevel]::Optimal)
                $archiveEntry.LastWriteTime = [System.DateTimeOffset]::new($sourceFile.LastWriteTimeUtc)
                $sourceStream = $sourceFile.OpenRead()
                try {
                    $entryStream = $archiveEntry.Open()
                    try {
                        $sourceStream.CopyTo($entryStream)
                    }
                    finally {
                        $entryStream.Dispose()
                    }
                }
                finally {
                    $sourceStream.Dispose()
                }
            }
        }
        finally {
            $archive.Dispose()
        }
    }
    finally {
        $archiveStream.Dispose()
    }

    $verificationStream = [System.IO.File]::OpenRead($DestinationArchive)
    try {
        $verificationArchive = [System.IO.Compression.ZipArchive]::new(
            $verificationStream,
            [System.IO.Compression.ZipArchiveMode]::Read,
            $true)
        try {
            $actualPaths = [string[]]@($verificationArchive.Entries | ForEach-Object { $_.FullName })
            if ($actualPaths.Count -ne $archivePaths.Count) {
                throw "Archive '$DestinationArchive' contains $($actualPaths.Count) entries; expected $($archivePaths.Count)."
            }

            for ($index = 0; $index -lt $archivePaths.Count; $index++) {
                if ($actualPaths[$index] -cne $archivePaths[$index] -or $actualPaths[$index].Contains('\')) {
                    throw "Archive '$DestinationArchive' contains a non-canonical or out-of-order entry '$($actualPaths[$index])'."
                }
            }
        }
        finally {
            $verificationArchive.Dispose()
        }
    }
    finally {
        $verificationStream.Dispose()
    }
}

function Get-FileSha256 {
    param([Parameter(Mandatory = $true)][string] $Path)

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Invoke-OptionalCommand {
    param(
        [Parameter(Mandatory = $true)][string] $FilePath,
        [string[]] $Arguments = @(),
        [string] $WorkingDirectory = $RepoRoot
    )

    $command = Get-Command $FilePath -ErrorAction SilentlyContinue
    if ($command -eq $null) {
        return $null
    }

    Push-Location $WorkingDirectory
    try {
        $previousErrorActionPreference = $ErrorActionPreference
        $ErrorActionPreference = "Continue"
        try {
            $output = & $FilePath @Arguments 2>&1
            if ($LASTEXITCODE -ne 0) {
                return $null
            }

            return (($output | Out-String).Trim())
        }
        finally {
            $ErrorActionPreference = $previousErrorActionPreference
        }
    }
    finally {
        Pop-Location
    }
}

function Get-GitProvenance {
    $insideWorkTree = Invoke-OptionalCommand -FilePath "git" -Arguments @("rev-parse", "--is-inside-work-tree")
    if ($insideWorkTree -ne "true") {
        return [ordered]@{
            available = $false
            commit = $null
            branch = $null
            dirty = $null
        }
    }

    $status = Invoke-OptionalCommand -FilePath "git" -Arguments @("status", "--porcelain")
    return [ordered]@{
        available = $true
        commit = Invoke-OptionalCommand -FilePath "git" -Arguments @("rev-parse", "HEAD")
        branch = Invoke-OptionalCommand -FilePath "git" -Arguments @("rev-parse", "--abbrev-ref", "HEAD")
        dirty = -not [string]::IsNullOrWhiteSpace($status)
    }
}

function Assert-GitSourceState {
    param(
        [Parameter(Mandatory = $true)]$Provenance,
        [switch] $RequireClean,
        [string] $ExpectedCommit
    )

    if ($Provenance.available -ne $true `
        -or $Provenance.commit -isnot [string] `
        -or $Provenance.commit -cnotmatch '^[0-9a-f]{40,64}$') {
        throw "Release source staging requires a Git worktree with a resolvable HEAD commit."
    }

    if (-not [string]::IsNullOrWhiteSpace($ExpectedCommit)) {
        $normalizedExpectedCommit = $ExpectedCommit.Trim().ToLowerInvariant()
        if ($normalizedExpectedCommit -cnotmatch '^[0-9a-f]{40,64}$') {
            throw "ExpectedGitCommit must be a full lowercase Git object id."
        }
        if ($Provenance.commit -cne $normalizedExpectedCommit) {
            throw "Release source HEAD '$($Provenance.commit)' does not match ExpectedGitCommit '$normalizedExpectedCommit'."
        }
    }

    if ($RequireClean -and $Provenance.dirty -ne $false) {
        throw "Formal release staging requires a clean Git worktree. Commit or discard every tracked and untracked change before publication."
    }
}

function Write-ReleaseProvenance {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][string] $ManifestPath,
        [Parameter(Mandatory = $true)][string] $ChecksumsPath,
        [Parameter(Mandatory = $true)][string] $ReleaseNotesPath,
        [Parameter(Mandatory = $true)][string] $DependencyInventoryPath,
        [Parameter(Mandatory = $true)]$SourceProvenance
    )

    $manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
    $artifacts = @($manifest.artifacts | ForEach-Object {
        [ordered]@{
            kind = $_.kind
            relativePath = $_.relativePath
            fileName = $_.fileName
            sizeBytes = $_.sizeBytes
            sha256 = $_.sha256
        }
    })

    $provenance = [ordered]@{
        schemaVersion = 1
        product = "OpenLineOps"
        version = $Version
        generatedAtUtc = [System.DateTimeOffset]::UtcNow.ToString("O")
        source = $SourceProvenance
        build = [ordered]@{
            configuration = $Configuration
            noRestore = [bool]$NoRestore
            skipDesktopBuild = [bool]$SkipDesktopBuild
            signWindowsPackages = [bool]$SignWindowsPackages
            requiredArtifactKinds = $RequiredKinds
        }
        tools = [ordered]@{
            powershell = $PSVersionTable.PSVersion.ToString()
            dotnetSdk = Invoke-OptionalCommand -FilePath "dotnet" -Arguments @("--version")
            node = Invoke-OptionalCommand -FilePath "node" -Arguments @("--version")
            npm = Invoke-OptionalCommand -FilePath "npm" -Arguments @("--version")
        }
        release = [ordered]@{
            manifest = [ordered]@{
                path = "release-manifest.json"
                sha256 = Get-FileSha256 $ManifestPath
            }
            checksums = [ordered]@{
                path = "checksums.sha256"
                sha256 = Get-FileSha256 $ChecksumsPath
            }
            notes = [ordered]@{
                path = "release-notes.md"
                sha256 = Get-FileSha256 $ReleaseNotesPath
            }
            dependencyInventory = [ordered]@{
                path = "release-dependency-inventory.json"
                sha256 = Get-FileSha256 $DependencyInventoryPath
            }
        }
        artifacts = $artifacts
    }

    $json = $provenance | ConvertTo-Json -Depth 12
    [System.IO.File]::WriteAllText($Path, $json + "`r`n", [System.Text.UTF8Encoding]::new($false))
}

function Write-ReleaseMetadataChecksums {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][string[]] $MetadataPaths
    )

    $lines = New-Object System.Collections.Generic.List[string]
    foreach ($metadataPath in $MetadataPaths) {
        if (-not (Test-Path -LiteralPath $metadataPath -PathType Leaf)) {
            throw "Cannot write release metadata checksums because a metadata file is missing: $metadataPath"
        }

        $relativePath = Get-RepoRelativePath $metadataPath
        $artifactRelativePath = $relativePath.Substring((Get-RepoRelativePath $resolvedArtifactsRoot).Length).TrimStart(
            [System.IO.Path]::DirectorySeparatorChar,
            [System.IO.Path]::AltDirectorySeparatorChar).Replace([System.IO.Path]::DirectorySeparatorChar, "/")
        $lines.Add("$(Get-FileSha256 $metadataPath)  $artifactRelativePath") | Out-Null
    }

    [System.IO.File]::WriteAllText(
        $Path,
        (($lines -join "`r`n") + "`r`n"),
        [System.Text.UTF8Encoding]::new($false))
}

function Test-IsExcludedSourcePath {
    param([Parameter(Mandatory = $true)][string] $RelativePath)

    $normalizedPath = $RelativePath.Replace([System.IO.Path]::DirectorySeparatorChar, '/')
    $segments = $normalizedPath.Split('/', [System.StringSplitOptions]::RemoveEmptyEntries)
    $excludedSegments = @(
        ".git",
        ".vs",
        ".vscode",
        ".idea",
        "artifacts",
        "output",
        "data",
    "logs",
    "node_modules",
    "bin",
    "obj",
    "dist",
    "dist-electron",
    "release",
    "TestResults",
    "coverage"
    )

    foreach ($segment in $segments) {
        if ($excludedSegments -contains $segment) {
            return $true
        }
    }

    $fileName = [System.IO.Path]::GetFileName($normalizedPath)
    if ($fileName -match '\.(user|suo|rsuser|userosscache|sln\.docstates|tsbuildinfo|trx|coverage)$') {
        return $true
    }

    if ($fileName -like ".env*" -and $fileName -ne ".env.example") {
        return $true
    }

    if (Test-IsSensitiveSourcePath $normalizedPath) {
        return $true
    }

    return $false
}

function Test-IsSensitiveSourcePath {
    param([Parameter(Mandatory = $true)][string] $RelativePath)

    $normalizedPath = $RelativePath.Replace([System.IO.Path]::DirectorySeparatorChar, '/')
    $segments = @($normalizedPath.Split('/', [System.StringSplitOptions]::RemoveEmptyEntries))
    $lowerSegments = @($segments | ForEach-Object { $_.ToLowerInvariant() })
    $fileName = [System.IO.Path]::GetFileName($normalizedPath)
    $lowerFileName = $fileName.ToLowerInvariant()

    if ($lowerFileName -like ".env*" -and $lowerFileName -ne ".env.example") {
        return $true
    }

    if ($lowerFileName -in @(
            ".npmrc",
            ".netrc",
            "credentials.json",
            "client_secret.json",
            "service-account.json",
            "token.json")) {
        return $true
    }

    if ($lowerFileName -match '\.(pfx|p12|key|snk|publishsettings)$') {
        return $true
    }

    if ($lowerFileName -match '^(id_rsa|id_dsa|id_ecdsa|id_ed25519)(\..*)?$') {
        return $true
    }

    if ($lowerFileName -match '\.pem$' -and $lowerFileName -match '(private|secret|signing|codesign|code-signing|certificate|cert|key)') {
        return $true
    }

    if ($lowerSegments -contains ".ssh" -or $lowerSegments -contains ".secrets") {
        return $true
    }

    return $false
}

function Copy-SourceArchiveContent {
    param([Parameter(Mandatory = $true)][string] $DestinationDirectory)

    New-Item -ItemType Directory -Path $DestinationDirectory -Force | Out-Null

    Push-Location $RepoRoot
    try {
        $previousErrorActionPreference = $ErrorActionPreference
        $ErrorActionPreference = "Continue"
        try {
            $trackedPaths = @(& git -c core.quotePath=false ls-files --cached --full-name 2>&1)
            $gitExitCode = $LASTEXITCODE
        }
        finally {
            $ErrorActionPreference = $previousErrorActionPreference
        }
    }
    finally {
        Pop-Location
    }

    if ($gitExitCode -ne 0) {
        throw "Cannot enumerate tracked source files from the Git index."
    }

    $canonicalTrackedPaths = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::Ordinal)
    foreach ($trackedPathValue in $trackedPaths) {
        $relativePath = $trackedPathValue.ToString().Replace('\', '/')
        if ([string]::IsNullOrWhiteSpace($relativePath) `
            -or [System.IO.Path]::IsPathRooted($relativePath) `
            -or $relativePath.StartsWith('../', [System.StringComparison]::Ordinal) `
            -or $relativePath.Contains('/../') `
            -or -not $canonicalTrackedPaths.Add($relativePath)) {
            throw "Git returned a non-canonical or duplicate tracked source path."
        }

        if (Test-IsExcludedSourcePath $relativePath) {
            continue
        }

        $sourcePath = [System.IO.Path]::GetFullPath((Join-Path $RepoRoot $relativePath))
        if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
            # A tracked path deleted in a developer worktree is intentionally absent.
            if ($RequireCleanGitWorkTree) {
                throw "Formal release source path '$relativePath' is absent from the worktree. Sparse or incomplete checkouts cannot be published."
            }
            continue
        }

        $sourcePathCursor = $RepoRoot
        foreach ($pathSegment in $relativePath.Split('/')) {
            $sourcePathCursor = Join-Path $sourcePathCursor $pathSegment
            $sourcePathItem = Get-Item -LiteralPath $sourcePathCursor -Force
            if ($sourcePathItem.Attributes.HasFlag([System.IO.FileAttributes]::ReparsePoint)) {
                throw "Tracked source path '$relativePath' traverses a reparse point and cannot be archived."
            }
        }

        $destinationPath = Join-Path $DestinationDirectory $relativePath
        $destinationParent = Split-Path $destinationPath -Parent
        New-Item -ItemType Directory -Path $destinationParent -Force | Out-Null
        Copy-Item -LiteralPath $sourcePath -Destination $destinationPath -Force
    }
}

$resolvedArtifactsRoot = Resolve-RepoPath $ArtifactsRoot
$resolvedWorkRoot = Resolve-RepoPath $WorkRoot
Assert-UnderRepoRoot $resolvedArtifactsRoot
Assert-UnderRepoRoot $resolvedWorkRoot

$sourceGitProvenance = Get-GitProvenance
Assert-GitSourceState `
    -Provenance $sourceGitProvenance `
    -RequireClean:$RequireCleanGitWorkTree `
    -ExpectedCommit $ExpectedGitCommit

$timestampUri = $null
if (-not [System.Uri]::TryCreate(
        $CodeSigningTimestampUrl,
        [System.UriKind]::Absolute,
        [ref]$timestampUri) `
    -or $timestampUri.Scheme -notin @("http", "https") `
    -or -not [string]::IsNullOrEmpty($timestampUri.UserInfo) `
    -or $timestampUri.Query -match '(?i)(password|passphrase|token|secret|credential|api[-_]?key)=') {
    throw "CodeSigningTimestampUrl must be an absolute HTTP(S) URL without embedded credentials or secret-bearing query parameters."
}

if ($SignWindowsPackages) {
    $signingSelectorCount = 0
    if (-not [string]::IsNullOrWhiteSpace($CodeSigningCertificateThumbprint)) {
        $signingSelectorCount++
    }
    if ($CodeSigningAutoSelectCertificate) {
        $signingSelectorCount++
    }
    if ($signingSelectorCount -ne 1) {
        throw "Signed release staging requires exactly one certificate-store selector: -CodeSigningCertificateThumbprint or -CodeSigningAutoSelectCertificate."
    }
}

New-CleanDirectory $resolvedArtifactsRoot
New-CleanDirectory $resolvedWorkRoot

$safeVersion = $Version -replace "[^A-Za-z0-9._-]", "_"

$apiPublish = Join-Path $resolvedWorkRoot "api"
$agentPublish = Join-Path $resolvedWorkRoot "agent"
$stationRuntimePublish = Join-Path $resolvedWorkRoot "station-runtime"
$stationScriptWorkerPublish = Join-Path $resolvedWorkRoot "station-script-worker"
$stationPluginHostPublish = Join-Path $resolvedWorkRoot "station-plugin-host"
$stationLeastPrivilegeLauncherPublish = Join-Path $resolvedWorkRoot "station-least-privilege-launcher"
$runnerPublish = Join-Path $resolvedWorkRoot "runner"
$pluginHostPublish = Join-Path $resolvedWorkRoot "plugin-host"
$scriptWorkerPublish = Join-Path $resolvedWorkRoot "script-worker"
$samplePluginPublish = Join-Path $resolvedWorkRoot "sample-plugin"
$desktopStage = Join-Path $resolvedWorkRoot "desktop"
$sourceStage = Join-Path $resolvedWorkRoot "source"

Publish-WindowsSelfContainedProject `
    -ProjectPath "src/OpenLineOps.Api/OpenLineOps.Api.csproj" `
    -OutputDirectory $apiPublish
Publish-WindowsSelfContainedProject `
    -ProjectPath "src/OpenLineOps.Agent/OpenLineOps.Agent.csproj" `
    -OutputDirectory $agentPublish
Publish-WindowsSelfContainedProject `
    -ProjectPath "src/OpenLineOps.StationRuntime/OpenLineOps.StationRuntime.csproj" `
    -OutputDirectory $stationRuntimePublish `
    -SingleFile
Merge-DirectoryContents `
    -SourceDirectory $stationRuntimePublish `
    -DestinationDirectory $agentPublish
Publish-WindowsSelfContainedProject `
    -ProjectPath "src/OpenLineOps.ScriptWorker/OpenLineOps.ScriptWorker.csproj" `
    -OutputDirectory $stationScriptWorkerPublish `
    -SingleFile
Merge-DirectoryContents `
    -SourceDirectory $stationScriptWorkerPublish `
    -DestinationDirectory $agentPublish
Publish-WindowsSelfContainedProject `
    -ProjectPath "src/OpenLineOps.PluginHost/OpenLineOps.PluginHost.csproj" `
    -OutputDirectory $stationPluginHostPublish `
    -SingleFile
Merge-DirectoryContents `
    -SourceDirectory $stationPluginHostPublish `
    -DestinationDirectory $agentPublish
Publish-WindowsSelfContainedProject `
    -ProjectPath "src/OpenLineOps.LeastPrivilegeLauncher/OpenLineOps.LeastPrivilegeLauncher.csproj" `
    -OutputDirectory $stationLeastPrivilegeLauncherPublish `
    -SingleFile
Merge-DirectoryContents `
    -SourceDirectory $stationLeastPrivilegeLauncherPublish `
    -DestinationDirectory $agentPublish
Publish-WindowsSelfContainedProject `
    -ProjectPath "src/OpenLineOps.Runner/OpenLineOps.Runner.csproj" `
    -OutputDirectory $runnerPublish
Publish-DotNetProject `
    -ProjectPath "src/OpenLineOps.PluginHost/OpenLineOps.PluginHost.csproj" `
    -OutputDirectory $pluginHostPublish
Publish-DotNetProject `
    -ProjectPath "src/OpenLineOps.ScriptWorker/OpenLineOps.ScriptWorker.csproj" `
    -OutputDirectory $scriptWorkerPublish
Publish-DotNetProject `
    -ProjectPath "samples/plugins/OpenLineOps.SamplePlugins.LoopbackDevice/OpenLineOps.SamplePlugins.LoopbackDevice.csproj" `
    -OutputDirectory $samplePluginPublish

Copy-Item `
    -LiteralPath (Resolve-RepoPath "samples/plugins/OpenLineOps.SamplePlugins.LoopbackDevice/manifest.json") `
    -Destination (Join-Path $samplePluginPublish "manifest.json") `
    -Force

foreach ($bundle in @($agentPublish, $runnerPublish)) {
    Copy-Item -LiteralPath (Resolve-RepoPath "LICENSE") -Destination (Join-Path $bundle "LICENSE.txt") -Force
    Copy-Item `
        -LiteralPath (Resolve-RepoPath "THIRD-PARTY-NOTICES.md") `
        -Destination (Join-Path $bundle "THIRD-PARTY-NOTICES.md") `
        -Force
}
Copy-Item `
    -LiteralPath (Resolve-RepoPath "docs/station-agent-deployment.md") `
    -Destination (Join-Path $agentPublish "DEPLOYMENT.md") `
    -Force
Copy-Item `
    -LiteralPath (Resolve-RepoPath "docs/headless-runner.md") `
    -Destination (Join-Path $runnerPublish "USAGE.md") `
    -Force
Assert-AgentBundleConfiguration -Root $agentPublish
Invoke-CheckedCommand `
    -FilePath (Join-Path $runnerPublish "OpenLineOps.Runner.exe") `
    -Arguments @("--help") `
    -WorkingDirectory $runnerPublish
Invoke-ExpectedExitCode `
    -FilePath (Join-Path $agentPublish "OpenLineOps.StationRuntime.exe") `
    -ExpectedExitCode 64 `
    -WorkingDirectory $agentPublish
if (-not $SkipDesktopBuild) {
    Invoke-CheckedCommand -FilePath "npm" -Arguments @("run", "build") -WorkingDirectory (Resolve-RepoPath "apps/desktop")
}

$desktopPackageOutput = Resolve-RepoPath "apps/desktop/release/desktop"
Assert-UnderRepoRoot $desktopPackageOutput
if (Test-Path -LiteralPath $desktopPackageOutput) {
    Remove-Item -LiteralPath $desktopPackageOutput -Recurse -Force
}

Invoke-CheckedCommand -FilePath "npm" -Arguments @("run", "package:win:ci") -WorkingDirectory (Resolve-RepoPath "apps/desktop")

if ($SignWindowsPackages) {
    $signingRoots = @(
        (Join-Path $desktopPackageOutput "win-unpacked"),
        $agentPublish,
        $runnerPublish
    )
    foreach ($signingRoot in $signingRoots) {
        $signArguments = @(
        "-NoProfile",
        "-ExecutionPolicy",
        "Bypass",
        "-File",
        (Resolve-RepoPath "eng/sign-windows-package.ps1"),
        "-PackageRoot",
        $signingRoot,
        "-TimestampUrl",
        $CodeSigningTimestampUrl
        )

        if (-not [string]::IsNullOrWhiteSpace($CodeSigningSignToolPath)) {
            $signArguments += @("-SignToolPath", $CodeSigningSignToolPath)
        }

        if (-not [string]::IsNullOrWhiteSpace($CodeSigningCertificateThumbprint)) {
            $signArguments += @("-CertificateThumbprint", $CodeSigningCertificateThumbprint)
        }

        if ($CodeSigningAutoSelectCertificate) {
            $signArguments += "-AutoSelectCertificate"
        }

        if ($CodeSigningStoreMachine) {
            $signArguments += "-StoreMachine"
        }

        Invoke-CheckedCommand -FilePath "powershell" -Arguments $signArguments
    }
}

Invoke-CheckedCommand `
    -FilePath "node" `
    -Arguments @(
        "scripts/write-package-content-manifest.mjs",
        "--package-root",
        (Join-Path $desktopPackageOutput "win-unpacked")) `
    -WorkingDirectory (Resolve-RepoPath "apps/desktop")

Write-WindowsBundleMetadata `
    -Root $agentPublish `
    -ArtifactKind "agent" `
    -EntryPoints @(
        [ordered]@{ role = "station-agent-service"; relativePath = "OpenLineOps.Agent.exe" },
        [ordered]@{ role = "station-runtime"; relativePath = "OpenLineOps.StationRuntime.exe" },
        [ordered]@{ role = "plugin-host"; relativePath = "OpenLineOps.PluginHost.exe" },
        [ordered]@{ role = "python-script-worker"; relativePath = "OpenLineOps.ScriptWorker.exe" }
    )
Write-WindowsBundleMetadata `
    -Root $runnerPublish `
    -ArtifactKind "runner" `
    -EntryPoints @(
        [ordered]@{ role = "headless-runner"; relativePath = "OpenLineOps.Runner.exe" }
    )

New-Item -ItemType Directory -Path $desktopStage -Force | Out-Null
Copy-DirectoryContents -SourceDirectory $desktopPackageOutput -DestinationDirectory (Join-Path $desktopStage "package")
Copy-DirectoryContents -SourceDirectory (Resolve-RepoPath "apps/desktop/dist") -DestinationDirectory (Join-Path $desktopStage "dist")
Copy-DirectoryContents -SourceDirectory (Resolve-RepoPath "apps/desktop/dist-electron") -DestinationDirectory (Join-Path $desktopStage "dist-electron")
Copy-Item -LiteralPath (Resolve-RepoPath "apps/desktop/package.json") -Destination (Join-Path $desktopStage "package.json") -Force
Copy-Item -LiteralPath (Resolve-RepoPath "apps/desktop/README.md") -Destination (Join-Path $desktopStage "README.md") -Force

$currentSourceProvenance = Get-GitProvenance
Assert-GitSourceState `
    -Provenance $currentSourceProvenance `
    -RequireClean:$RequireCleanGitWorkTree `
    -ExpectedCommit $sourceGitProvenance.commit
Copy-SourceArchiveContent -DestinationDirectory $sourceStage
$finalSourceProvenance = Get-GitProvenance
Assert-GitSourceState `
    -Provenance $finalSourceProvenance `
    -RequireClean:$RequireCleanGitWorkTree `
    -ExpectedCommit $sourceGitProvenance.commit

$archives = @(
    @{
        Kind = "source"
        Source = $sourceStage
        Archive = "source-openlineops-$safeVersion.zip"
    },
    @{
        Kind = "api"
        Source = $apiPublish
        Archive = "api-openlineops-$safeVersion.zip"
    },
    @{
        Kind = "agent"
        Source = $agentPublish
        Archive = "agent-openlineops-win-x64-$safeVersion.zip"
    },
    @{
        Kind = "runner"
        Source = $runnerPublish
        Archive = "runner-openlineops-win-x64-$safeVersion.zip"
    },
    @{
        Kind = "desktop"
        Source = $desktopStage
        Archive = "desktop-openlineops-$safeVersion.zip"
    },
    @{
        Kind = "plugin-host"
        Source = $pluginHostPublish
        Archive = "plugin-host-openlineops-$safeVersion.zip"
    },
    @{
        Kind = "script-worker"
        Source = $scriptWorkerPublish
        Archive = "script-worker-openlineops-$safeVersion.zip"
    },
    @{
        Kind = "sample-plugin"
        Source = $samplePluginPublish
        Archive = "sample-plugin-loopback-device-$safeVersion.zip"
    }
)

foreach ($archive in $archives) {
    $artifactKindDirectory = Join-Path $resolvedArtifactsRoot $archive.Kind
    Compress-StagedDirectory `
        -SourceDirectory $archive.Source `
        -DestinationArchive (Join-Path $artifactKindDirectory $archive.Archive)
}

$manifestPath = Join-Path $resolvedArtifactsRoot "release-manifest.json"
$checksumsPath = Join-Path $resolvedArtifactsRoot "checksums.sha256"
$notesPath = Join-Path $resolvedArtifactsRoot "release-notes.md"
$dependencyInventoryPath = Join-Path $resolvedArtifactsRoot "release-dependency-inventory.json"
$provenancePath = Join-Path $resolvedArtifactsRoot "release-provenance.json"
$metadataChecksumsPath = Join-Path $resolvedArtifactsRoot "release-metadata-checksums.sha256"
$manifestProject = Resolve-RepoPath "tools/OpenLineOps.ReleaseManifest/OpenLineOps.ReleaseManifest.csproj"

$manifestArguments = @(
    "run",
    "--project",
    $manifestProject,
    "--configuration",
    $Configuration,
    "--",
    "--version",
    $Version,
    "--artifacts",
    $resolvedArtifactsRoot,
    "--output",
    $manifestPath,
    "--checksums",
    $checksumsPath,
    "--notes",
    $notesPath
)

foreach ($kind in $RequiredKinds) {
    $manifestArguments += @("--require-kind", $kind)
}

Invoke-CheckedCommand -FilePath "dotnet" -Arguments $manifestArguments

$dependencyInventoryArguments = @(
    "-NoProfile",
    "-ExecutionPolicy",
    "Bypass",
    "-File",
    (Resolve-RepoPath "eng/verify-third-party-license-metadata.ps1"),
    "-InventoryPath",
    $dependencyInventoryPath,
    "-InventoryVersion",
    $Version,
    "-UpdateInventory"
)

Invoke-CheckedCommand -FilePath "powershell" -Arguments $dependencyInventoryArguments

$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.schemaVersion -ne 1) {
    throw "Expected release manifest schemaVersion 1, found $($manifest.schemaVersion)."
}

$actualKinds = @($manifest.artifacts | ForEach-Object { $_.kind } | Sort-Object -Unique)
foreach ($kind in $RequiredKinds) {
    if ($actualKinds -notcontains $kind) {
        throw "Release staging is missing required artifact kind '$kind'."
    }
}

Write-ReleaseProvenance `
    -Path $provenancePath `
    -ManifestPath $manifestPath `
    -ChecksumsPath $checksumsPath `
    -ReleaseNotesPath $notesPath `
    -DependencyInventoryPath $dependencyInventoryPath `
    -SourceProvenance $sourceGitProvenance

Write-ReleaseMetadataChecksums `
    -Path $metadataChecksumsPath `
    -MetadataPaths @(
        $manifestPath,
        $checksumsPath,
        $notesPath,
        $dependencyInventoryPath,
        $provenancePath
    )

Write-Host "Release artifacts staged successfully."
Write-Host "Artifacts: $resolvedArtifactsRoot"
Write-Host "Manifest: $manifestPath"
Write-Host "Dependency inventory: $dependencyInventoryPath"
Write-Host "Provenance: $provenancePath"
Write-Host "Metadata checksums: $metadataChecksumsPath"

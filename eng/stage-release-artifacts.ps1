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

    [string] $CodeSigningCertificatePath,

    [string] $CodeSigningCertificatePassword,

    [string] $CodeSigningCertificateThumbprint,

    [switch] $CodeSigningAutoSelectCertificate,

    [switch] $CodeSigningStoreMachine,

    [string] $CodeSigningTimestampUrl = "http://timestamp.digicert.com"
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

        $formatted += $argument
        if ($argument -in @("-CertificatePassword", "/p")) {
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
    $process = Start-Process `
        -FilePath $resolvedFilePath `
        -ArgumentList $Arguments `
        -WorkingDirectory $WorkingDirectory `
        -NoNewWindow `
        -Wait `
        -PassThru

    if ($process.ExitCode -ne 0) {
        throw "Command failed with exit code $($process.ExitCode): $FilePath $($Arguments -join ' ')"
    }
}

function Invoke-ExpectedExitCode {
    param(
        [Parameter(Mandatory = $true)][string] $FilePath,
        [string[]] $Arguments = @(),
        [Parameter(Mandatory = $true)][int] $ExpectedExitCode,
        [string] $WorkingDirectory = $RepoRoot
    )

    Write-Host "> $FilePath $($Arguments -join ' ') (expected exit $ExpectedExitCode)"
    $startProcessArguments = @{
        FilePath = $FilePath
        WorkingDirectory = $WorkingDirectory
        NoNewWindow = $true
        Wait = $true
        PassThru = $true
    }
    if ($Arguments.Count -gt 0) {
        $startProcessArguments.ArgumentList = $Arguments
    }
    $process = Start-Process @startProcessArguments
    if ($process.ExitCode -ne $ExpectedExitCode) {
        throw "Command exited $($process.ExitCode), expected ${ExpectedExitCode}: $FilePath"
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
        "/p:UseAppHost=false"
    )

    if ($NoRestore) {
        $arguments += "--no-restore"
    }

    Invoke-CheckedCommand -FilePath "dotnet" -Arguments $arguments
}

function Publish-WindowsSelfContainedProject {
    param(
        [Parameter(Mandatory = $true)][string] $ProjectPath,
        [Parameter(Mandatory = $true)][string] $OutputDirectory
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
        "/p:UseAppHost=true"
    )

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
    $runtimeExecutablePath = $configuration.OpenLineOps.Agent.RuntimeExecutablePath
    if ($runtimeExecutablePath -cne "OpenLineOps.StationRuntime.exe") {
        throw "OpenLineOps:Agent:RuntimeExecutablePath must be exactly 'OpenLineOps.StationRuntime.exe' in the release bundle."
    }

    if (-not (Test-Path -LiteralPath (Join-Path $Root $runtimeExecutablePath) -PathType Leaf)) {
        throw "The configured Station Runtime executable is missing from the Agent bundle."
    }

    $agentExecutablePath = Join-Path $Root "OpenLineOps.Agent.exe"
    $runtimeExecutableFullPath = Join-Path $Root $runtimeExecutablePath
    if (-not (Test-Path -LiteralPath $agentExecutablePath -PathType Leaf)) {
        throw "The Station Agent executable is missing from its release bundle."
    }
    if ((Get-FileSha256 $agentExecutablePath) -ceq (Get-FileSha256 $runtimeExecutableFullPath)) {
        throw "The Station Agent and Station Runtime executable payloads must be distinct."
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

function Write-ReleaseProvenance {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][string] $ManifestPath,
        [Parameter(Mandatory = $true)][string] $ChecksumsPath,
        [Parameter(Mandatory = $true)][string] $ReleaseNotesPath,
        [Parameter(Mandatory = $true)][string] $DependencyInventoryPath
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
        source = Get-GitProvenance
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

    $sourceFiles = @(
        foreach ($topLevelEntry in Get-ChildItem -LiteralPath $RepoRoot -Force) {
            $topLevelRelativePath = Get-RepoRelativePath $topLevelEntry.FullName
            if (Test-IsExcludedSourcePath $topLevelRelativePath) {
                continue
            }

            if (-not $topLevelEntry.PSIsContainer) {
                $topLevelEntry
                continue
            }

            Get-ChildItem -LiteralPath $topLevelEntry.FullName -Recurse -File -Force |
                Where-Object {
                    $relativePath = Get-RepoRelativePath $_.FullName
                    -not (Test-IsExcludedSourcePath $relativePath)
                }
        }
    )

    foreach ($file in $sourceFiles) {
        $relativePath = Get-RepoRelativePath $file.FullName
        $destinationPath = Join-Path $DestinationDirectory $relativePath
        $destinationParent = Split-Path $destinationPath -Parent
        New-Item -ItemType Directory -Path $destinationParent -Force | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $destinationPath -Force
    }
}

$resolvedArtifactsRoot = Resolve-RepoPath $ArtifactsRoot
$resolvedWorkRoot = Resolve-RepoPath $WorkRoot
Assert-UnderRepoRoot $resolvedArtifactsRoot
Assert-UnderRepoRoot $resolvedWorkRoot

New-CleanDirectory $resolvedArtifactsRoot
New-CleanDirectory $resolvedWorkRoot

$safeVersion = $Version -replace "[^A-Za-z0-9._-]", "_"

$apiPublish = Join-Path $resolvedWorkRoot "api"
$agentPublish = Join-Path $resolvedWorkRoot "agent"
$stationRuntimePublish = Join-Path $resolvedWorkRoot "station-runtime"
$runnerPublish = Join-Path $resolvedWorkRoot "runner"
$pluginHostPublish = Join-Path $resolvedWorkRoot "plugin-host"
$scriptWorkerPublish = Join-Path $resolvedWorkRoot "script-worker"
$samplePluginPublish = Join-Path $resolvedWorkRoot "sample-plugin"
$desktopStage = Join-Path $resolvedWorkRoot "desktop"
$sourceStage = Join-Path $resolvedWorkRoot "source"

Publish-DotNetProject `
    -ProjectPath "src/OpenLineOps.Api/OpenLineOps.Api.csproj" `
    -OutputDirectory $apiPublish
Publish-WindowsSelfContainedProject `
    -ProjectPath "src/OpenLineOps.Agent/OpenLineOps.Agent.csproj" `
    -OutputDirectory $agentPublish
Publish-WindowsSelfContainedProject `
    -ProjectPath "src/OpenLineOps.StationRuntime/OpenLineOps.StationRuntime.csproj" `
    -OutputDirectory $stationRuntimePublish
Merge-DirectoryContents `
    -SourceDirectory $stationRuntimePublish `
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

        if (-not [string]::IsNullOrWhiteSpace($CodeSigningCertificatePath)) {
            $signArguments += @("-CertificatePath", $CodeSigningCertificatePath)
        }

        if (-not [string]::IsNullOrWhiteSpace($CodeSigningCertificatePassword)) {
            $signArguments += @("-CertificatePassword", $CodeSigningCertificatePassword)
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

Write-WindowsBundleMetadata `
    -Root $agentPublish `
    -ArtifactKind "agent" `
    -EntryPoints @(
        [ordered]@{ role = "station-agent-service"; relativePath = "OpenLineOps.Agent.exe" },
        [ordered]@{ role = "station-runtime"; relativePath = "OpenLineOps.StationRuntime.exe" }
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

Copy-SourceArchiveContent -DestinationDirectory $sourceStage

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
    -DependencyInventoryPath $dependencyInventoryPath

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

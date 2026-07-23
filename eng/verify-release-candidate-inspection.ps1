param(
    [string] $WorkRoot = "output/release-candidate-inspection-verification",

    [switch] $SkipClean
)

$ErrorActionPreference = "Stop"

$RepoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$RequiredKinds = @("source", "api", "agent", "runner", "desktop", "plugin-host", "script-worker", "sample-plugin")
$FixtureIndexEntries = [System.Collections.Generic.List[object]]::new()

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

    if (-not $fullPath.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to write outside the repository root: $fullPath"
    }
}

function Assert-DirectChildDirectory {
    param(
        [Parameter(Mandatory = $true)][string] $Parent,
        [Parameter(Mandatory = $true)][string] $Path
    )

    $resolvedParent = [System.IO.Path]::GetFullPath($Parent).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
    $resolvedPath = [System.IO.Path]::GetFullPath($Path).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
    $resolvedPathParent = [System.IO.Path]::GetDirectoryName($resolvedPath)
    if (-not $resolvedPathParent.Equals(
            $resolvedParent,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Fixture directory must be a direct child of its dedicated work root: $resolvedPath"
    }
}

function New-CleanDirectory {
    param([Parameter(Mandatory = $true)][string] $Path)

    Assert-UnderRepoRoot $Path
    if ((Test-Path -LiteralPath $Path) -and -not $SkipClean) {
        Remove-Item -LiteralPath $Path -Recurse -Force
    }

    New-Item -ItemType Directory -Path $Path -Force | Out-Null
}

function Get-FileSha256 {
    param([Parameter(Mandatory = $true)][string] $Path)

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-FixturePhysicalId {
    param([Parameter(Mandatory = $true)][string] $Name)

    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try {
        $nameBytes = [System.Text.Encoding]::UTF8.GetBytes($Name)
        $hash = $sha256.ComputeHash($nameBytes)
        $shortHash = [System.BitConverter]::ToString($hash, 0, 8).Replace("-", "").ToLowerInvariant()
        return "f-$shortHash"
    }
    finally {
        $sha256.Dispose()
    }
}

function Write-FixtureIndex {
    $fixtureNames = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::Ordinal)
    $fixturePaths = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::Ordinal)
    foreach ($entry in $FixtureIndexEntries) {
        if (-not $fixtureNames.Add($entry.name)) {
            throw "Fixture index contains duplicate logical name '$($entry.name)'."
        }

        if (-not $fixturePaths.Add($entry.relativeDirectory)) {
            throw "Fixture index contains duplicate physical directory '$($entry.relativeDirectory)'."
        }

        $fixtureRoot = Join-Path $ResolvedWorkRoot $entry.relativeDirectory
        Assert-DirectChildDirectory -Parent $ResolvedWorkRoot -Path $fixtureRoot
        $expectedManifestRelativePath = "$($entry.relativeDirectory)/release-manifest.json"
        if ($entry.manifestRelativePath -cne $expectedManifestRelativePath) {
            throw "Fixture '$($entry.name)' has a non-canonical manifest path '$($entry.manifestRelativePath)'."
        }

        if (-not (Test-Path -LiteralPath (Join-Path $fixtureRoot "release-manifest.json") -PathType Leaf)) {
            throw "Fixture '$($entry.name)' is missing release-manifest.json."
        }
    }

    $index = [ordered]@{
        schema = "openlineops.release-candidate-inspection-fixture-index"
        schemaVersion = 1
        fixtures = @($FixtureIndexEntries)
    }
    [System.IO.File]::WriteAllText(
        (Join-Path $ResolvedWorkRoot "fixture-index.json"),
        (($index | ConvertTo-Json -Depth 6) + "`r`n"),
        [System.Text.UTF8Encoding]::new($false))
}

function New-TestZip {
    param(
        [Parameter(Mandatory = $true)][string] $Root,
        [Parameter(Mandatory = $true)][string] $Name,
        [Parameter(Mandatory = $true)][string[]] $Entries
    )

    $zipPath = Join-Path $Root $Name
    $zipDirectory = Split-Path $zipPath -Parent
    New-Item -ItemType Directory -Path $zipDirectory -Force | Out-Null
    if (Test-Path -LiteralPath $zipPath) {
        Remove-Item -LiteralPath $zipPath -Force
    }

    $archive = [System.IO.Compression.ZipFile]::Open($zipPath, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($entryName in $Entries) {
            $entry = $archive.CreateEntry($entryName)
            if ([System.IO.Path]::GetExtension($entryName) -ceq ".exe") {
                $fixtureExecutable = Join-Path $env:SystemRoot "System32/where.exe"
                $bytes = [System.IO.File]::ReadAllBytes($fixtureExecutable)
                $stream = $entry.Open()
                try { $stream.Write($bytes, 0, $bytes.Length) }
                finally { $stream.Dispose() }
            }
            else {
                $writer = [System.IO.StreamWriter]::new($entry.Open())
                try {
                    $writer.WriteLine("test content for $entryName")
                }
                finally {
                    $writer.Dispose()
                }
            }
        }
    }
    finally {
        $archive.Dispose()
    }
}

function New-TestWindowsBundleZip {
    param(
        [Parameter(Mandatory = $true)][string] $Root,
        [Parameter(Mandatory = $true)][string] $Name,
        [Parameter(Mandatory = $true)][string] $ArtifactKind,
        [Parameter(Mandatory = $true)][string[]] $Files,
        [Parameter(Mandatory = $true)][object[]] $EntryPoints,
        [hashtable] $FileSourceOverrides = @{},
        [string] $TamperPath,
        [switch] $ExposeRemovedAgentContainerSetting,
        [switch] $OmitAgentSafetyExecutablePath,
        [switch] $OmitAgentStationSystemId,
        [switch] $OmitAgentPackageCacheDirectory,
        [switch] $OmitAgentWindowsServiceName,
        [string] $AgentWindowsServiceName = "",
        [string] $AgentPackageCacheDirectory = "",
        [string] $AgentHeartbeatInterval = "00:00:05",
        [string] $AgentBrokerUri = "amqps://localhost:5671",
        [bool] $AgentRequireBrokerTls = $true,
        [string] $AgentCoordinatorBaseUri = "https://localhost:7443/",
        [string] $AgentArtifactUploadTimeout = "00:05:00",
        [switch] $EmbedAgentArtifactUploadBearerToken,
        [string] $AgentSafetyExecutablePath = ""
    )

    $stagingRoot = Join-Path $ResolvedWorkRoot ("bundle-staging/" + [System.Guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $stagingRoot -Force | Out-Null
    foreach ($relativePath in $Files) {
        $path = Join-Path $stagingRoot $relativePath.Replace('/', [System.IO.Path]::DirectorySeparatorChar)
        New-Item -ItemType Directory -Path (Split-Path $path -Parent) -Force | Out-Null
        if ($FileSourceOverrides.ContainsKey($relativePath)) {
            Copy-Item `
                -LiteralPath $FileSourceOverrides[$relativePath] `
                -Destination $path
            continue
        }
        if ([System.IO.Path]::GetExtension($relativePath) -ceq ".exe") {
            Copy-Item `
                -LiteralPath (Join-Path $env:SystemRoot "System32/where.exe") `
                -Destination $path
            continue
        }
        $content = if ($relativePath -ceq "appsettings.json") {
            $pythonSandbox = [ordered]@{
                RequireLeastPrivilegeExecution = $true
                IsolationMode = "LeastPrivilegeIdentity"
                LeastPrivilegeIdentity = "PerExecutionAppContainer"
                LeastPrivilegeLauncherExecutable = "OpenLineOps.LeastPrivilegeLauncher.exe"
                LeastPrivilegeNoInteractivePrompt = $true
            }
            if ($ExposeRemovedAgentContainerSetting) {
                $pythonSandbox["ContainerImage"] =
                    "openlineops/python@sha256:0000000000000000000000000000000000000000000000000000000000000000"
            }

            $agentConfiguration = [ordered]@{
                HeartbeatInterval = $AgentHeartbeatInterval
                BrokerUri = $AgentBrokerUri
                RequireBrokerTls = $AgentRequireBrokerTls
                CoordinatorBaseUri = $AgentCoordinatorBaseUri
                ArtifactUploadTimeout = $AgentArtifactUploadTimeout
                RuntimeExecutablePath = "OpenLineOps.StationRuntime.exe"
                PluginHostExecutablePath = "OpenLineOps.PluginHost.exe"
                PythonScript = [ordered]@{
                    WorkerExecutablePath = "OpenLineOps.ScriptWorker.exe"
                    HostPythonRuntimeDllPath = ""
                    Sandbox = $pythonSandbox
                }
            }
            if (-not $OmitAgentStationSystemId) {
                $agentConfiguration["StationSystemId"] = ""
            }
            if (-not $OmitAgentPackageCacheDirectory) {
                $agentConfiguration["PackageCacheDirectory"] = $AgentPackageCacheDirectory
            }
            if ($EmbedAgentArtifactUploadBearerToken) {
                $agentConfiguration["ArtifactUploadBearerToken"] =
                    "embedded-release-template-secret"
            }
            if (-not $OmitAgentSafetyExecutablePath) {
                $agentConfiguration["SafetyExecutablePath"] = $AgentSafetyExecutablePath
            }

            $openLineOpsConfiguration = [ordered]@{
                Agent = $agentConfiguration
            }
            if (-not $OmitAgentWindowsServiceName) {
                $openLineOpsConfiguration["WindowsServiceName"] = $AgentWindowsServiceName
            }
            ([ordered]@{
                OpenLineOps = $openLineOpsConfiguration
            } | ConvertTo-Json -Depth 8 -Compress)
        }
        elseif ($relativePath -ceq "DEPLOYMENT.md" -and $ArtifactKind -ceq "agent") {
            @'
# Station Agent Deployment

Provision the dedicated content-cache namespace from an elevated Windows prompt:

OpenLineOps.Agent.exe --provision-content-cache --OpenLineOps:WindowsServiceName OpenLineOpsStationAgent-LineA --OpenLineOps:Agent:PackageCacheDirectory C:\ProgramData\OpenLineOps\StationCaches\LineA\content-anchor\content

OpenLineOps.Agent.exe --remove-content-cache-package 0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef --OpenLineOps:WindowsServiceName OpenLineOpsStationAgent-LineA --OpenLineOps:Agent:PackageCacheDirectory C:\ProgramData\OpenLineOps\StationCaches\LineA\content-anchor\content
'@
        }
        else {
            "test content for $relativePath"
        }
        [System.IO.File]::WriteAllText($path, $content, [System.Text.UTF8Encoding]::new($false))
    }

    $relativePaths = [string[]]@($Files)
    [System.Array]::Sort($relativePaths, [System.StringComparer]::Ordinal)
    $fileRecords = @($relativePaths | ForEach-Object {
        $path = Join-Path $stagingRoot $_.Replace('/', [System.IO.Path]::DirectorySeparatorChar)
        $file = Get-Item -LiteralPath $path
        [ordered]@{
            relativePath = $_
            sizeBytes = $file.Length
            sha256 = Get-FileSha256 $path
        }
    })
    $manifest = [ordered]@{
        schemaVersion = 1
        product = "OpenLineOps"
        artifactKind = $ArtifactKind
        runtimeIdentifier = "win-x64"
        selfContained = $true
        entryPoints = $EntryPoints
        files = $fileRecords
    }
    [System.IO.File]::WriteAllText(
        (Join-Path $stagingRoot "bundle-manifest.json"),
        (($manifest | ConvertTo-Json -Depth 8) + "`r`n"),
        [System.Text.UTF8Encoding]::new($false))
    [System.IO.File]::WriteAllText(
        (Join-Path $stagingRoot "bundle-checksums.sha256"),
        ((@($fileRecords | ForEach-Object { "$($_.sha256)  $($_.relativePath)" }) -join "`r`n") + "`r`n"),
        [System.Text.UTF8Encoding]::new($false))

    $zipPath = Join-Path $Root $Name
    New-Item -ItemType Directory -Path (Split-Path $zipPath -Parent) -Force | Out-Null
    Compress-Archive -Path (Join-Path $stagingRoot "*") -DestinationPath $zipPath -Force

    if (-not [string]::IsNullOrWhiteSpace($TamperPath)) {
        $archive = [System.IO.Compression.ZipFile]::Open($zipPath, [System.IO.Compression.ZipArchiveMode]::Update)
        try {
            $entry = $archive.GetEntry($TamperPath)
            if ($null -eq $entry) {
                throw "Cannot tamper missing bundle entry '$TamperPath'."
            }
            $entry.Delete()
            $replacement = $archive.CreateEntry($TamperPath)
            $writer = [System.IO.StreamWriter]::new($replacement.Open())
            try {
                $writer.Write("tampered payload")
            }
            finally {
                $writer.Dispose()
            }
        }
        finally {
            $archive.Dispose()
        }
    }
}

function Invoke-ReleaseManifestGeneration {
    param(
        [Parameter(Mandatory = $true)][string] $Root,
        [Parameter(Mandatory = $true)][string] $Version
    )

    $manifestPath = Join-Path $Root "release-manifest.json"
    $checksumsPath = Join-Path $Root "checksums.sha256"
    $notesPath = Join-Path $Root "release-notes.md"
    $manifestProject = Resolve-RepoPath "tools/OpenLineOps.ReleaseManifest/OpenLineOps.ReleaseManifest.csproj"

    $arguments = @(
        "run",
        "--project",
        $manifestProject,
        "--",
        "--version",
        $Version,
        "--artifacts",
        $Root,
        "--output",
        $manifestPath,
        "--checksums",
        $checksumsPath,
        "--notes",
        $notesPath
    )

    foreach ($kind in $RequiredKinds) {
        $arguments += @("--require-kind", $kind)
    }

    $output = & dotnet @arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Host ($output | Out-String)
        throw "Failed to generate release manifest for fixture '$Root'."
    }
}

function Write-TestProvenance {
    param(
        [Parameter(Mandatory = $true)][string] $Root,
        [Parameter(Mandatory = $true)][string] $Version,
        [string] $RecordedVersion,
        [string] $Mutation
    )

    $manifestPath = Join-Path $Root "release-manifest.json"
    $checksumsPath = Join-Path $Root "checksums.sha256"
    $notesPath = Join-Path $Root "release-notes.md"
    $dependencyInventoryPath = Join-Path $Root "release-dependency-inventory.json"
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json

    if ([string]::IsNullOrWhiteSpace($RecordedVersion)) {
        $RecordedVersion = $Version
    }

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
        version = $RecordedVersion
        generatedAtUtc = [System.DateTimeOffset]::UtcNow.ToString("O")
        source = [ordered]@{
            available = $true
            commit = "0123456789abcdef0123456789abcdef01234567"
            branch = "fixture"
            dirty = $false
        }
        build = [ordered]@{
            configuration = "Release"
            noRestore = $true
            skipDesktopBuild = $true
            signWindowsPackages = $false
            requiredArtifactKinds = $RequiredKinds
        }
        tools = [ordered]@{
            powershell = $PSVersionTable.PSVersion.ToString()
            dotnetSdk = "test"
            node = "test"
            npm = "test"
        }
        release = [ordered]@{
            manifest = [ordered]@{
                path = "release-manifest.json"
                sha256 = Get-FileSha256 $manifestPath
            }
            checksums = [ordered]@{
                path = "checksums.sha256"
                sha256 = Get-FileSha256 $checksumsPath
            }
            notes = [ordered]@{
                path = "release-notes.md"
                sha256 = Get-FileSha256 $notesPath
            }
            dependencyInventory = [ordered]@{
                path = "release-dependency-inventory.json"
                sha256 = Get-FileSha256 $dependencyInventoryPath
            }
        }
        artifacts = $artifacts
    }

    switch ($Mutation) {
        "property-case" {
            $schemaVersion = $provenance["schemaVersion"]
            $provenance.Remove("schemaVersion")
            $provenance["SchemaVersion"] = $schemaVersion
        }
        "product-case" {
            $provenance["product"] = "openlineops"
        }
        "release-path-backslash" {
            $provenance["release"]["manifest"]["path"] = "metadata\release-manifest.json"
        }
        "artifact-sha-case" {
            $artifacts[0]["sha256"] = $artifacts[0]["sha256"].ToUpperInvariant()
        }
        "unexpected-property" {
            $provenance["manifestPath"] = "release-manifest.json"
        }
        "" {
        }
        default {
            throw "Unsupported provenance mutation '$Mutation'."
        }
    }

    $provenancePath = Join-Path $Root "release-provenance.json"
    [System.IO.File]::WriteAllText(
        $provenancePath,
        (($provenance | ConvertTo-Json -Depth 12) + "`r`n"),
        [System.Text.UTF8Encoding]::new($false))
}

function Write-TestMetadataChecksums {
    param(
        [Parameter(Mandatory = $true)][string] $Root,
        [switch] $Tamper
    )

    $metadata = [ordered]@{
        "release-manifest.json" = (Join-Path $Root "release-manifest.json")
        "checksums.sha256" = (Join-Path $Root "checksums.sha256")
        "release-notes.md" = (Join-Path $Root "release-notes.md")
        "release-dependency-inventory.json" = (Join-Path $Root "release-dependency-inventory.json")
        "release-provenance.json" = (Join-Path $Root "release-provenance.json")
    }

    $lines = New-Object System.Collections.Generic.List[string]
    foreach ($entry in $metadata.GetEnumerator()) {
        $hash = Get-FileSha256 $entry.Value
        if ($Tamper -and $entry.Key -eq "release-provenance.json") {
            $hash = "0000000000000000000000000000000000000000000000000000000000000000"
        }

        $lines.Add("$hash  $($entry.Key)") | Out-Null
    }

    [System.IO.File]::WriteAllText(
        (Join-Path $Root "release-metadata-checksums.sha256"),
        (($lines -join "`r`n") + "`r`n"),
        [System.Text.UTF8Encoding]::new($false))
}

function Set-TestMetadataChecksumMutation {
    param(
        [Parameter(Mandatory = $true)][string] $Root,
        [Parameter(Mandatory = $true)][string] $Mutation
    )

    $path = Join-Path $Root "release-metadata-checksums.sha256"
    $lines = @(Get-Content -LiteralPath $path)
    if ($lines.Count -eq 0) {
        throw "Cannot mutate empty metadata checksums fixture: $path"
    }

    switch ($Mutation) {
        "uppercase-hash" {
            $lines[0] = $lines[0].Substring(0, 64).ToUpperInvariant() + $lines[0].Substring(64)
        }
        "path-case" {
            $lines[0] = $lines[0].Substring(0, 66) + "Release-Manifest.json"
        }
        default {
            throw "Unsupported metadata checksum mutation '$Mutation'."
        }
    }

    [System.IO.File]::WriteAllText(
        $path,
        (($lines -join "`r`n") + "`r`n"),
        [System.Text.UTF8Encoding]::new($false))
}

function Add-DuplicateSchemaVersionProperty {
    param([Parameter(Mandatory = $true)][string] $Path)

    $json = [System.IO.File]::ReadAllText($Path)
    if (-not $json.StartsWith("{", [System.StringComparison]::Ordinal)) {
        throw "Cannot add a duplicate property to non-object JSON: $Path"
    }

    [System.IO.File]::WriteAllText(
        $Path,
        ('{"schemaVersion":1,' + $json.Substring(1)),
        [System.Text.UTF8Encoding]::new($false))
}

function Write-TestDependencyInventory {
    param(
        [Parameter(Mandatory = $true)][string] $Root,
        [Parameter(Mandatory = $true)][string] $Version,
        [string] $RecordedVersion
    )

    if ([string]::IsNullOrWhiteSpace($RecordedVersion)) {
        $RecordedVersion = $Version
    }

    $inventory = [ordered]@{
        schemaVersion = 1
        product = "OpenLineOps"
        version = $RecordedVersion
        generatedAtUtc = [System.DateTimeOffset]::UtcNow.ToString("O")
        packageCounts = [ordered]@{
            total = 2
            nuget = 1
            npm = 1
            uniqueLicenseValues = 1
        }
        reviewPolicy = [ordered]@{
            blockedLicensePatterns = @("AGPL", "GPL", "LGPL", "SSPL")
        }
        packages = @(
            [ordered]@{
                ecosystem = "nuget"
                name = "Example.NuGet"
                version = "1.0.0"
                license = "MIT"
                licenseSource = "license:expression"
            },
            [ordered]@{
                ecosystem = "npm"
                name = "example-npm"
                version = "1.0.0"
                license = "MIT"
                licenseSource = "package-lock"
            }
        )
    }

    $inventoryPath = Join-Path $Root "release-dependency-inventory.json"
    [System.IO.File]::WriteAllText(
        $inventoryPath,
        (($inventory | ConvertTo-Json -Depth 8) + "`r`n"),
        [System.Text.UTF8Encoding]::new($false))
}

function New-MinimalReleaseCandidate {
    param(
        [Parameter(Mandatory = $true)][string] $Name,
        [string[]] $ExtraSourceEntries = @(),
        [string] $OmitSourceEntry = "",
        [string] $ProvenanceVersion,
        [string] $ProvenanceMutation,
        [string] $DependencyInventoryVersion,
        [string] $MetadataChecksumMutation,
        [switch] $BackslashDesktopEntry,
        [switch] $WrongCaseDesktopEntry,
        [switch] $TamperAgentBundle,
        [switch] $IncludeServiceTokenTestHelperInAgent,
        [switch] $IncludeRenamedServiceTokenTestHelperDirectoryInAgent,
        [switch] $IncludeRenamedServiceTokenTestHelperBinaryInAgent,
        [switch] $IncludeUnmanifestedServiceTokenTestHelperBinary,
        [switch] $OmitServiceTokenTestHelperSource,
        [switch] $ExposeRemovedAgentContainerSetting,
        [switch] $OmitAgentSafetyExecutablePath,
        [switch] $OmitAgentStationSystemId,
        [switch] $OmitAgentPackageCacheDirectory,
        [switch] $OmitAgentWindowsServiceName,
        [string] $AgentWindowsServiceName = "",
        [string] $AgentPackageCacheDirectory = "",
        [string] $AgentHeartbeatInterval = "00:00:05",
        [string] $AgentBrokerUri = "amqps://localhost:5671",
        [bool] $AgentRequireBrokerTls = $true,
        [string] $AgentCoordinatorBaseUri = "https://localhost:7443/",
        [string] $AgentArtifactUploadTimeout = "00:05:00",
        [switch] $EmbedAgentArtifactUploadBearerToken,
        [string] $AgentSafetyExecutablePath = "",
        [switch] $SkipDependencyInventory,
        [switch] $SkipMetadataChecksums,
        [switch] $TamperMetadataChecksums,
        [switch] $SkipProvenance
    )

    $fixturePhysicalId = Get-FixturePhysicalId $Name
    $root = Join-Path $ResolvedWorkRoot $fixturePhysicalId
    Assert-DirectChildDirectory -Parent $ResolvedWorkRoot -Path $root
    New-CleanDirectory $root

    $version = "0.0.0-$fixturePhysicalId"
    $sourceEntries = @(
        "README.md",
        "THIRD-PARTY-NOTICES.md",
        "Directory.Build.props",
        "OpenLineOps.sln",
        "OpenLineOps.slnx",
        "tests/OpenLineOps.WindowsServiceToken.TestHelper/OpenLineOps.WindowsServiceToken.TestHelper.csproj",
        "tests/OpenLineOps.WindowsServiceToken.TestHelper/Program.cs",
        "tests/OpenLineOps.WindowsServiceToken.TestHelper/TokenTransferProtocol.cs",
        "tests/OpenLineOps.WindowsServiceToken.TestHelper/AtomicTokenTransferResult.cs",
        "tests/OpenLineOps.WindowsServiceToken.TestHelper/OneShotWindowsServiceWorker.cs",
        "tests/OpenLineOps.WindowsServiceToken.TestHelper/WindowsNative.cs",
        "tests/OpenLineOps.WindowsServiceToken.TestHelper/WindowsServiceTokenTransferOperation.cs",
        "tests/OpenLineOps.WindowsServiceToken.TestHelper/SourceTokenRelayOperation.cs",
        "tests/OpenLineOps.WindowsServiceToken.TestHelper/SourceTokenRelayProcess.cs",
        "docs/development-execution-plan.md",
        "eng/stage-release-artifacts.ps1",
        "eng/verify-ci-workflow-actions.ps1",
        "eng/verify-staged-agent-bundle-e2e.ps1",
        "eng/verify-staged-agent-rabbitmq-e2e.ps1",
        "eng/invoke-run-scoped-agent-service-cleanup.ps1",
        "eng/verify-agent-service-external-abort-cleanup.ps1",
        "eng/verify-staged-agent-evidence.ps1",
        "eng/verify-production-closure-evidence.ps1",
        "eng/verify-studio-two-agent-production-closure.ps1",
        "eng/verify-studio-two-agent-production-evidence.ps1",
        "eng/verify-studio-two-agent-production-evidence.tests.ps1",
        "eng/verify-runner-staged-agent-e2e.ps1",
        "eng/verify-runner-staged-agent-evidence.ps1",
        "eng/verify-runner-staged-agent-evidence.tests.ps1",
        "eng/verify-evidence-validation.tests.ps1",
        "eng/evidence-validation-test-fixtures.ps1",
        "eng/verify-solution-project-coverage.ps1",
        "eng/verify-station-agent-content-cache-contract.ps1",
        "eng/inspect-ci-release-artifact.ps1",
        "eng/inspect-release-candidate.ps1",
        "eng/prepare-final-publication.ps1",
        "eng/verify-final-publication-preflight.ps1",
        "eng/write-publication-evidence.ps1",
        "eng/verify-publication-evidence.ps1",
        "eng/verify-release-candidate-inspection.ps1",
        "eng/verify-open-source-metadata.ps1",
        "eng/verify-third-party-license-metadata.ps1",
        "eng/sign-windows-package.ps1",
        "eng/verify-windows-signing-readiness.ps1",
        "docs/station-agent-deployment.md",
        "docs/coordinator-deployment.md",
        "docs/coordinator-api-security.md",
        "docs/trace-projection-recovery.md",
        "apps/desktop/scripts/production-closure-e2e.mjs",
        "docs/headless-runner.md"
    )
    if (-not [string]::IsNullOrEmpty($OmitSourceEntry)) {
        $sourceEntryCount = $sourceEntries.Count
        $sourceEntries = @($sourceEntries | Where-Object {
                $_ -cne $OmitSourceEntry
            })
        if ($sourceEntries.Count -ne ($sourceEntryCount - 1)) {
            throw "Fixture source entry '$OmitSourceEntry' is not present exactly once."
        }
    }
    if ($OmitServiceTokenTestHelperSource) {
        $sourceEntries = @($sourceEntries | Where-Object {
                -not $_.StartsWith(
                    "tests/OpenLineOps.WindowsServiceToken.TestHelper/",
                    [System.StringComparison]::Ordinal)
            })
    }
    New-TestZip `
        -Root $root `
        -Name "source/source-openlineops-$version.zip" `
        -Entries ($sourceEntries + $ExtraSourceEntries)
    New-TestZip -Root $root -Name "api/api-openlineops-$version.zip" -Entries @(
        "OpenLineOps.Api.dll",
        "appsettings.json")
    $agentFiles = @(
        "OpenLineOps.Agent.exe",
        "OpenLineOps.Agent.deps.json",
        "OpenLineOps.Agent.runtimeconfig.json",
        "OpenLineOps.StationRuntime.exe",
        "OpenLineOps.PluginHost.exe",
        "OpenLineOps.ScriptWorker.exe",
        "OpenLineOps.LeastPrivilegeLauncher.exe",
        "appsettings.json",
        "coreclr.dll",
        "hostfxr.dll",
        "DEPLOYMENT.md",
        "LICENSE.txt",
        "THIRD-PARTY-NOTICES.md")
    $agentFileSourceOverrides = @{}
    if ($IncludeServiceTokenTestHelperInAgent) {
        $helperPath = "OpenLineOps.WindowsServiceToken.TestHelper.exe"
        $agentFiles += $helperPath
        $agentFileSourceOverrides[$helperPath] =
            $script:ServiceTokenHelperFixtureExecutablePath
    }
    if ($IncludeRenamedServiceTokenTestHelperDirectoryInAgent) {
        $helperPath = "WINDOWS-SERVICE-TOKEN-TEST-HELPER/bridge-host.exe"
        $agentFiles += $helperPath
        $agentFileSourceOverrides[$helperPath] =
            $script:ServiceTokenHelperFixtureExecutablePath
    }
    if ($IncludeRenamedServiceTokenTestHelperBinaryInAgent) {
        $helperPath = "support/bridge-host.bin"
        $agentFiles += $helperPath
        $agentFileSourceOverrides[$helperPath] =
            $script:ServiceTokenHelperFixtureExecutablePath
    }

    New-TestWindowsBundleZip `
        -Root $root `
        -Name "agent/agent-openlineops-win-x64-$version.zip" `
        -ArtifactKind "agent" `
        -Files $agentFiles `
        -FileSourceOverrides $agentFileSourceOverrides `
        -EntryPoints @(
            [ordered]@{ role = "station-agent-service"; relativePath = "OpenLineOps.Agent.exe" },
            [ordered]@{ role = "station-runtime"; relativePath = "OpenLineOps.StationRuntime.exe" },
            [ordered]@{ role = "plugin-host"; relativePath = "OpenLineOps.PluginHost.exe" },
            [ordered]@{ role = "python-script-worker"; relativePath = "OpenLineOps.ScriptWorker.exe" }) `
        -TamperPath $(if ($TamperAgentBundle) { "OpenLineOps.Agent.exe" } else { "" }) `
        -ExposeRemovedAgentContainerSetting:$ExposeRemovedAgentContainerSetting `
        -OmitAgentSafetyExecutablePath:$OmitAgentSafetyExecutablePath `
        -OmitAgentStationSystemId:$OmitAgentStationSystemId `
        -OmitAgentPackageCacheDirectory:$OmitAgentPackageCacheDirectory `
        -OmitAgentWindowsServiceName:$OmitAgentWindowsServiceName `
        -AgentWindowsServiceName $AgentWindowsServiceName `
        -AgentPackageCacheDirectory $AgentPackageCacheDirectory `
        -AgentHeartbeatInterval $AgentHeartbeatInterval `
        -AgentBrokerUri $AgentBrokerUri `
        -AgentRequireBrokerTls $AgentRequireBrokerTls `
        -AgentCoordinatorBaseUri $AgentCoordinatorBaseUri `
        -AgentArtifactUploadTimeout $AgentArtifactUploadTimeout `
        -EmbedAgentArtifactUploadBearerToken:$EmbedAgentArtifactUploadBearerToken `
        -AgentSafetyExecutablePath $AgentSafetyExecutablePath
    New-TestWindowsBundleZip `
        -Root $root `
        -Name "runner/runner-openlineops-win-x64-$version.zip" `
        -ArtifactKind "runner" `
        -Files @(
            "OpenLineOps.Runner.exe",
            "OpenLineOps.Runner.deps.json",
            "OpenLineOps.Runner.runtimeconfig.json",
            "coreclr.dll",
            "hostfxr.dll",
            "USAGE.md",
            "LICENSE.txt",
            "THIRD-PARTY-NOTICES.md") `
        -EntryPoints @(
            [ordered]@{ role = "headless-runner"; relativePath = "OpenLineOps.Runner.exe" })
    $desktopEntries = @(
        "dist/index.html",
        "dist-electron/main/api-credential-security.js",
        "dist-electron/main/application-extension-import-security.js",
        "dist-electron/main/backend-api-security.js",
        "dist-electron/main/backend-process-handshake.js",
        "dist-electron/main/local-sqlite-connection.js",
        "dist-electron/main/main.js",
        "dist-electron/main/renderer-navigation-security.js",
        "dist-electron/main/trace-artifact-save.js",
        "dist-electron/main/trace-artifact-save-core.js",
        "dist-electron/preload/preload.cjs",
        "package/win-unpacked/OpenLineOps.exe",
        "package/win-unpacked/OPENLINEOPS-PACKAGE-NOTES.txt",
        "package/win-unpacked/resources/app/package.json",
        "package/win-unpacked/resources/app/dist/index.html",
        "package/win-unpacked/resources/app/dist-electron/main/api-credential-security.js",
        "package/win-unpacked/resources/app/dist-electron/main/application-extension-import-security.js",
        "package/win-unpacked/resources/app/dist-electron/main/backend-api-security.js",
        "package/win-unpacked/resources/app/dist-electron/main/backend-process-handshake.js",
        "package/win-unpacked/resources/app/dist-electron/main/local-sqlite-connection.js",
        "package/win-unpacked/resources/app/dist-electron/main/main.js",
        "package/win-unpacked/resources/app/dist-electron/main/renderer-navigation-security.js",
        "package/win-unpacked/resources/app/dist-electron/main/trace-artifact-save.js",
        "package/win-unpacked/resources/app/dist-electron/main/trace-artifact-save-core.js",
        "package/win-unpacked/resources/app/dist-electron/preload/preload.cjs",
        "package/win-unpacked/resources/app/runtime/api/OpenLineOps.Api.exe",
        "package/win-unpacked/resources/app/runtime/api/appsettings.json",
        "package/win-unpacked/resources/app/runtime/plugin-host/OpenLineOps.PluginHost.exe",
        "package/win-unpacked/resources/app/runtime/script-worker/OpenLineOps.ScriptWorker.exe")
    if ($BackslashDesktopEntry) {
        $desktopEntries[0] = "dist\index.html"
    }
    if ($WrongCaseDesktopEntry) {
        $desktopEntries[0] = "Dist/index.html"
    }

    New-TestZip -Root $root -Name "desktop/desktop-openlineops-$version.zip" -Entries $desktopEntries
    New-TestZip -Root $root -Name "plugin-host/plugin-host-openlineops-$version.zip" -Entries @("OpenLineOps.PluginHost.dll")
    New-TestZip -Root $root -Name "script-worker/script-worker-openlineops-$version.zip" -Entries @("OpenLineOps.ScriptWorker.dll")
    New-TestZip -Root $root -Name "sample-plugin/sample-plugin-loopback-device-$version.zip" -Entries @(
        "manifest.json",
        "OpenLineOps.SamplePlugins.LoopbackDevice.dll")

    Invoke-ReleaseManifestGeneration -Root $root -Version $version
    if (-not $SkipDependencyInventory) {
        Write-TestDependencyInventory -Root $root -Version $version -RecordedVersion $DependencyInventoryVersion
    }

    if (-not $SkipProvenance) {
        Write-TestProvenance `
            -Root $root `
            -Version $version `
            -RecordedVersion $ProvenanceVersion `
            -Mutation $ProvenanceMutation
    }

    if (-not $SkipMetadataChecksums) {
        Write-TestMetadataChecksums -Root $root -Tamper:$TamperMetadataChecksums
        if (-not [string]::IsNullOrWhiteSpace($MetadataChecksumMutation)) {
            Set-TestMetadataChecksumMutation `
                -Root $root `
                -Mutation $MetadataChecksumMutation
        }
    }

    if ($IncludeUnmanifestedServiceTokenTestHelperBinary) {
        $unmanifestedPath = Join-Path $root "agent/renamed-host.bin"
        Copy-Item `
            -LiteralPath $script:ServiceTokenHelperFixtureExecutablePath `
            -Destination $unmanifestedPath
    }

    $FixtureIndexEntries.Add([ordered]@{
        name = $Name
        relativeDirectory = $fixturePhysicalId
        manifestRelativePath = "$fixturePhysicalId/release-manifest.json"
    }) | Out-Null

    return $root
}

function Invoke-Inspection {
    param([Parameter(Mandatory = $true)][string] $Root)

    $inspectionScript = Resolve-RepoPath "eng/inspect-release-candidate.ps1"
    $output = & powershell -NoProfile -ExecutionPolicy Bypass -File $inspectionScript -ArtifactsRoot $Root 2>&1
    return [pscustomobject]@{
        ExitCode = $LASTEXITCODE
        Text = ($output | Out-String)
    }
}

function Assert-InspectionPasses {
    param(
        [Parameter(Mandatory = $true)][string] $Root,
        [Parameter(Mandatory = $true)][string] $Name
    )

    $result = Invoke-Inspection -Root $Root
    if ($result.ExitCode -ne 0) {
        Write-Host $result.Text
        throw "Expected fixture '$Name' to pass release candidate inspection."
    }

    Write-Host "Fixture '$Name' passed inspection."
}

function Assert-InspectionFails {
    param(
        [Parameter(Mandatory = $true)][string] $Root,
        [Parameter(Mandatory = $true)][string] $Name,
        [Parameter(Mandatory = $true)][string] $ExpectedPattern
    )

    $result = Invoke-Inspection -Root $Root
    if ($result.ExitCode -eq 0) {
        throw "Expected fixture '$Name' to fail release candidate inspection."
    }

    if ($result.Text -notmatch $ExpectedPattern) {
        Write-Host $result.Text
        throw "Fixture '$Name' failed for an unexpected reason."
    }

    Write-Host "Fixture '$Name' failed as expected."
}

$ResolvedWorkRoot = Resolve-RepoPath $WorkRoot
Assert-UnderRepoRoot $ResolvedWorkRoot
New-CleanDirectory $ResolvedWorkRoot
$serviceTokenHelperFixtureOutput = Join-Path `
    $ResolvedWorkRoot `
    "service-token-helper-fixture"
$serviceTokenHelperFixtureProject = Resolve-RepoPath `
    "tests/OpenLineOps.WindowsServiceToken.TestHelper/OpenLineOps.WindowsServiceToken.TestHelper.csproj"
$serviceTokenHelperBuildOutput = @(& dotnet build `
        $serviceTokenHelperFixtureProject `
        --configuration Release `
        --runtime win-x64 `
        --self-contained true `
        --output $serviceTokenHelperFixtureOutput `
        -p:DebugSymbols=false `
        -p:DebugType=None 2>&1)
if ($LASTEXITCODE -ne 0) {
    Write-Host ($serviceTokenHelperBuildOutput | Out-String)
    throw "Could not build the real Windows service-token helper payload fixture."
}
$script:ServiceTokenHelperFixtureExecutablePath = Join-Path `
    $serviceTokenHelperFixtureOutput `
    "OpenLineOps.WindowsServiceToken.TestHelper.exe"
if (-not (Test-Path `
        -LiteralPath $script:ServiceTokenHelperFixtureExecutablePath `
        -PathType Leaf)) {
    throw "The real Windows service-token helper payload fixture is missing its executable."
}

$positiveRoot = New-MinimalReleaseCandidate -Name "positive"
Assert-InspectionPasses -Root $positiveRoot -Name "positive"

$tamperedAgentBundleRoot = New-MinimalReleaseCandidate `
    -Name "tampered-agent-bundle" `
    -TamperAgentBundle
Assert-InspectionFails `
    -Root $tamperedAgentBundleRoot `
    -Name "tampered-agent-bundle" `
    -ExpectedPattern "bundle (size|hash) mismatch for 'OpenLineOps\.Agent\.exe'"

$serviceTokenHelperLeakRoot = New-MinimalReleaseCandidate `
    -Name "test-only-service-token-helper-leak" `
    -IncludeServiceTokenTestHelperInAgent
Assert-InspectionFails `
    -Root $serviceTokenHelperLeakRoot `
    -Name "test-only-service-token-helper-leak" `
    -ExpectedPattern "test-only Windows service-token helper in a deployable artifact"

$renamedServiceTokenHelperLeakRoot = New-MinimalReleaseCandidate `
    -Name "renamed-test-only-service-token-helper-directory-leak" `
    -IncludeRenamedServiceTokenTestHelperDirectoryInAgent
Assert-InspectionFails `
    -Root $renamedServiceTokenHelperLeakRoot `
    -Name "renamed-test-only-service-token-helper-directory-leak" `
    -ExpectedPattern "test-only Windows service-token helper in a deployable artifact"

$renamedServiceTokenHelperBinaryLeakRoot = New-MinimalReleaseCandidate `
    -Name "renamed-test-only-service-token-helper-binary-leak" `
    -IncludeRenamedServiceTokenTestHelperBinaryInAgent
Assert-InspectionFails `
    -Root $renamedServiceTokenHelperBinaryLeakRoot `
    -Name "renamed-test-only-service-token-helper-binary-leak" `
    -ExpectedPattern "test-only Windows service-token helper in a deployable artifact"

$unmanifestedServiceTokenHelperBinaryLeakRoot = New-MinimalReleaseCandidate `
    -Name "unmanifested-test-only-service-token-helper-binary-leak" `
    -IncludeUnmanifestedServiceTokenTestHelperBinary
Assert-InspectionFails `
    -Root $unmanifestedServiceTokenHelperBinaryLeakRoot `
    -Name "unmanifested-test-only-service-token-helper-binary-leak" `
    -ExpectedPattern "unmanifested file"

$missingServiceTokenHelperSourceRoot = New-MinimalReleaseCandidate `
    -Name "missing-service-token-helper-source" `
    -OmitServiceTokenTestHelperSource
Assert-InspectionFails `
    -Root $missingServiceTokenHelperSourceRoot `
    -Name "missing-service-token-helper-source" `
    -ExpectedPattern "missing expected entry: tests/OpenLineOps\.WindowsServiceToken\.TestHelper/"

foreach ($relaySourceFile in @(
        "SourceTokenRelayOperation.cs",
        "SourceTokenRelayProcess.cs")) {
    $relaySourceEntry =
        "tests/OpenLineOps.WindowsServiceToken.TestHelper/$relaySourceFile"
    $fixtureName =
        "missing-" + [System.IO.Path]::GetFileNameWithoutExtension($relaySourceFile).ToLowerInvariant()
    $missingRelaySourceRoot = New-MinimalReleaseCandidate `
        -Name $fixtureName `
        -OmitSourceEntry $relaySourceEntry
    Assert-InspectionFails `
        -Root $missingRelaySourceRoot `
        -Name $fixtureName `
        -ExpectedPattern ([regex]::Escape("missing expected entry: $relaySourceEntry"))
}

$removedAgentContainerSettingRoot = New-MinimalReleaseCandidate `
    -Name "removed-agent-container-setting" `
    -ExposeRemovedAgentContainerSetting
Assert-InspectionFails `
    -Root $removedAgentContainerSettingRoot `
    -Name "removed-agent-container-setting" `
    -ExpectedPattern "removed Station Agent Python Container settings"

$missingAgentSafetyExecutablePathRoot = New-MinimalReleaseCandidate `
    -Name "missing-agent-safety-executable-path" `
    -OmitAgentSafetyExecutablePath
Assert-InspectionFails `
    -Root $missingAgentSafetyExecutablePathRoot `
    -Name "missing-agent-safety-executable-path" `
    -ExpectedPattern "must declare SafetyExecutablePath"

$configuredAgentSafetyExecutablePathRoot = New-MinimalReleaseCandidate `
    -Name "configured-agent-safety-executable-path" `
    -AgentSafetyExecutablePath "C:\\MachineSafety\\station-safety.exe"
Assert-InspectionFails `
    -Root $configuredAgentSafetyExecutablePathRoot `
    -Name "configured-agent-safety-executable-path" `
    -ExpectedPattern "SafetyExecutablePath release template must be empty"

$missingAgentPackageCacheDirectoryRoot = New-MinimalReleaseCandidate `
    -Name "missing-agent-package-cache-directory" `
    -OmitAgentPackageCacheDirectory
Assert-InspectionFails `
    -Root $missingAgentPackageCacheDirectoryRoot `
    -Name "missing-agent-package-cache-directory" `
    -ExpectedPattern "PackageCacheDirectory release template must be present and empty"

$configuredAgentPackageCacheDirectoryRoot = New-MinimalReleaseCandidate `
    -Name "configured-agent-package-cache-directory" `
    -AgentPackageCacheDirectory "C:\\ProgramData\\OpenLineOps\\content"
Assert-InspectionFails `
    -Root $configuredAgentPackageCacheDirectoryRoot `
    -Name "configured-agent-package-cache-directory" `
    -ExpectedPattern "PackageCacheDirectory release template must be present and empty"

$missingAgentWindowsServiceNameRoot = New-MinimalReleaseCandidate `
    -Name "missing-agent-windows-service-name" `
    -OmitAgentWindowsServiceName
Assert-InspectionFails `
    -Root $missingAgentWindowsServiceNameRoot `
    -Name "missing-agent-windows-service-name" `
    -ExpectedPattern "WindowsServiceName release template must be present and empty"

$configuredAgentWindowsServiceNameRoot = New-MinimalReleaseCandidate `
    -Name "configured-agent-windows-service-name" `
    -AgentWindowsServiceName "OpenLineOpsStationAgent-LineA"
Assert-InspectionFails `
    -Root $configuredAgentWindowsServiceNameRoot `
    -Name "configured-agent-windows-service-name" `
    -ExpectedPattern "WindowsServiceName release template must be present and empty"

$missingAgentStationSystemIdRoot = New-MinimalReleaseCandidate `
    -Name "missing-agent-station-system-id" `
    -OmitAgentStationSystemId
Assert-InspectionFails `
    -Root $missingAgentStationSystemIdRoot `
    -Name "missing-agent-station-system-id" `
    -ExpectedPattern "StationSystemId release template must be present and empty"

$invalidAgentHeartbeatRoot = New-MinimalReleaseCandidate `
    -Name "invalid-agent-heartbeat" `
    -AgentHeartbeatInterval "00:00:30"
Assert-InspectionFails `
    -Root $invalidAgentHeartbeatRoot `
    -Name "invalid-agent-heartbeat" `
    -ExpectedPattern "HeartbeatInterval release template must be exactly"

$insecureAgentBrokerRoot = New-MinimalReleaseCandidate `
    -Name "insecure-agent-broker" `
    -AgentBrokerUri "amqp://guest:guest@localhost:5672" `
    -AgentRequireBrokerTls $false
Assert-InspectionFails `
    -Root $insecureAgentBrokerRoot `
    -Name "insecure-agent-broker" `
    -ExpectedPattern "must use an amqps broker template without embedded placeholder credentials"

$insecureAgentCoordinatorRoot = New-MinimalReleaseCandidate `
    -Name "insecure-agent-coordinator" `
    -AgentCoordinatorBaseUri "http://coordinator.internal:7443/"
Assert-InspectionFails `
    -Root $insecureAgentCoordinatorRoot `
    -Name "insecure-agent-coordinator" `
    -ExpectedPattern "must use a credential-free HTTPS CoordinatorBaseUri"

$embeddedAgentArtifactTokenRoot = New-MinimalReleaseCandidate `
    -Name "embedded-agent-artifact-token" `
    -EmbedAgentArtifactUploadBearerToken
Assert-InspectionFails `
    -Root $embeddedAgentArtifactTokenRoot `
    -Name "embedded-agent-artifact-token" `
    -ExpectedPattern "must not embed ArtifactUploadBearerToken"

$unsafePathRoot = New-MinimalReleaseCandidate -Name "unsafe-path" -ExtraSourceEntries @("../evil.txt")
Assert-InspectionFails `
    -Root $unsafePathRoot `
    -Name "unsafe-path" `
    -ExpectedPattern "unsafe zip entry path segment|path traversal zip entry"

$windowsCanonicalAliasRoot = New-MinimalReleaseCandidate `
    -Name "windows-canonical-alias-path" `
    -ExtraSourceEntries @("windows-service-token-test-helper./bridge-host.exe")
Assert-InspectionFails `
    -Root $windowsCanonicalAliasRoot `
    -Name "windows-canonical-alias-path" `
    -ExpectedPattern "unsafe zip entry path segment"

$windowsSuperscriptDeviceAliasRoot = New-MinimalReleaseCandidate `
    -Name "windows-superscript-device-alias-path" `
    -ExtraSourceEntries @(("COM{0}.txt" -f [char]0x00B9))
Assert-InspectionFails `
    -Root $windowsSuperscriptDeviceAliasRoot `
    -Name "windows-superscript-device-alias-path" `
    -ExpectedPattern "unsafe zip entry path segment"

$sensitiveSourceRoot = New-MinimalReleaseCandidate -Name "sensitive-source" -ExtraSourceEntries @("certs/openlineops-code-signing.pfx")
Assert-InspectionFails `
    -Root $sensitiveSourceRoot `
    -Name "sensitive-source" `
    -ExpectedPattern "sensitive source archive entry"

$badProvenanceRoot = New-MinimalReleaseCandidate -Name "bad-provenance" -ProvenanceVersion "0.0.0-wrong"
Assert-InspectionFails `
    -Root $badProvenanceRoot `
    -Name "bad-provenance" `
    -ExpectedPattern "Release provenance version"

$missingProvenanceRoot = New-MinimalReleaseCandidate -Name "missing-provenance" -SkipProvenance -SkipMetadataChecksums
Assert-InspectionFails `
    -Root $missingProvenanceRoot `
    -Name "missing-provenance" `
    -ExpectedPattern "release-provenance\.json"

$missingDependencyInventoryRoot = New-MinimalReleaseCandidate -Name "missing-dependency-inventory" -SkipDependencyInventory -SkipProvenance -SkipMetadataChecksums
Assert-InspectionFails `
    -Root $missingDependencyInventoryRoot `
    -Name "missing-dependency-inventory" `
    -ExpectedPattern "release-dependency-inventory\.json"

$badDependencyInventoryRoot = New-MinimalReleaseCandidate -Name "bad-dependency-inventory" -DependencyInventoryVersion "0.0.0-wrong"
Assert-InspectionFails `
    -Root $badDependencyInventoryRoot `
    -Name "bad-dependency-inventory" `
    -ExpectedPattern "Dependency inventory version"

$missingMetadataChecksumsRoot = New-MinimalReleaseCandidate -Name "missing-metadata-checksums" -SkipMetadataChecksums
Assert-InspectionFails `
    -Root $missingMetadataChecksumsRoot `
    -Name "missing-metadata-checksums" `
    -ExpectedPattern "release-metadata-checksums\.sha256"

$badMetadataChecksumsRoot = New-MinimalReleaseCandidate -Name "bad-metadata-checksums" -TamperMetadataChecksums
Assert-InspectionFails `
    -Root $badMetadataChecksumsRoot `
    -Name "bad-metadata-checksums" `
    -ExpectedPattern "Metadata checksum for release-provenance\.json"

$uppercaseMetadataHashRoot = New-MinimalReleaseCandidate `
    -Name "uppercase-metadata-hash" `
    -MetadataChecksumMutation "uppercase-hash"
Assert-InspectionFails `
    -Root $uppercaseMetadataHashRoot `
    -Name "uppercase-metadata-hash" `
    -ExpectedPattern "Invalid metadata checksum line"

$metadataPathCaseRoot = New-MinimalReleaseCandidate `
    -Name "metadata-path-case" `
    -MetadataChecksumMutation "path-case"
Assert-InspectionFails `
    -Root $metadataPathCaseRoot `
    -Name "metadata-path-case" `
    -ExpectedPattern "Metadata checksums are missing release-manifest\.json"

$provenancePropertyCaseRoot = New-MinimalReleaseCandidate `
    -Name "provenance-property-case" `
    -ProvenanceMutation "property-case"
Assert-InspectionFails `
    -Root $provenancePropertyCaseRoot `
    -Name "provenance-property-case" `
    -ExpectedPattern "missing exact property name\(s\): schemaVersion"

$provenanceProductCaseRoot = New-MinimalReleaseCandidate `
    -Name "provenance-product-case" `
    -ProvenanceMutation "product-case"
Assert-InspectionFails `
    -Root $provenanceProductCaseRoot `
    -Name "provenance-product-case" `
    -ExpectedPattern "product must be exactly 'OpenLineOps'"

$provenancePathAliasRoot = New-MinimalReleaseCandidate `
    -Name "provenance-path-alias" `
    -ProvenanceMutation "release-path-backslash"
Assert-InspectionFails `
    -Root $provenancePathAliasRoot `
    -Name "provenance-path-alias" `
    -ExpectedPattern "manifest path must be exactly 'release-manifest\.json'"

$provenanceArtifactShaCaseRoot = New-MinimalReleaseCandidate `
    -Name "provenance-artifact-sha-case" `
    -ProvenanceMutation "artifact-sha-case"
Assert-InspectionFails `
    -Root $provenanceArtifactShaCaseRoot `
    -Name "provenance-artifact-sha-case" `
    -ExpectedPattern "mismatched sha256"

$provenanceUnexpectedPropertyRoot = New-MinimalReleaseCandidate `
    -Name "provenance-unexpected-property" `
    -ProvenanceMutation "unexpected-property"
Assert-InspectionFails `
    -Root $provenanceUnexpectedPropertyRoot `
    -Name "provenance-unexpected-property" `
    -ExpectedPattern "unexpected or non-canonical property name\(s\): manifestPath"

$duplicateProvenancePropertyRoot = New-MinimalReleaseCandidate `
    -Name "duplicate-provenance-property"
Add-DuplicateSchemaVersionProperty `
    -Path (Join-Path $duplicateProvenancePropertyRoot "release-provenance.json")
Assert-InspectionFails `
    -Root $duplicateProvenancePropertyRoot `
    -Name "duplicate-provenance-property" `
    -ExpectedPattern "duplicate property 'schemaVersion'"

$duplicateInventoryPropertyRoot = New-MinimalReleaseCandidate `
    -Name "duplicate-inventory-property"
Add-DuplicateSchemaVersionProperty `
    -Path (Join-Path $duplicateInventoryPropertyRoot "release-dependency-inventory.json")
Assert-InspectionFails `
    -Root $duplicateInventoryPropertyRoot `
    -Name "duplicate-inventory-property" `
    -ExpectedPattern "duplicate property 'schemaVersion'"

$backslashZipEntryRoot = New-MinimalReleaseCandidate `
    -Name "backslash-zip-entry" `
    -BackslashDesktopEntry
Assert-InspectionFails `
    -Root $backslashZipEntryRoot `
    -Name "backslash-zip-entry" `
    -ExpectedPattern "non-canonical backslash zip entry path"

$wrongCaseZipEntryRoot = New-MinimalReleaseCandidate `
    -Name "wrong-case-zip-entry" `
    -WrongCaseDesktopEntry
Assert-InspectionFails `
    -Root $wrongCaseZipEntryRoot `
    -Name "wrong-case-zip-entry" `
    -ExpectedPattern "missing expected entry: dist/index\.html"

Write-FixtureIndex
Write-Host "Release candidate inspection verification passed."
exit 0

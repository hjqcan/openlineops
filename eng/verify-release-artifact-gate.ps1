param(
    [ValidateSet("Debug", "Release")]
    [string] $Configuration = "Debug",

    [string] $Version = "0.0.0-local",

    [string] $ArtifactsRoot = "artifacts/release-gate",

    [switch] $SkipClean
)

$ErrorActionPreference = "Stop"

$RepoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$RequiredKinds = @("source", "api", "desktop", "plugin-host", "script-worker", "sample-plugin")

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

$ResolvedArtifactsRoot = Resolve-RepoPath $ArtifactsRoot
Assert-UnderRepoRoot $ResolvedArtifactsRoot

if ((Test-Path -LiteralPath $ResolvedArtifactsRoot) -and -not $SkipClean) {
    Remove-Item -LiteralPath $ResolvedArtifactsRoot -Recurse -Force
}

New-Item -ItemType Directory -Path $ResolvedArtifactsRoot -Force | Out-Null

$SourceArtifacts = @(
    @{
        Kind = "source"
        Source = "README.md"
    },
    @{
        Kind = "api"
        Source = "src/OpenLineOps.Api/bin/$Configuration/net10.0/OpenLineOps.Api.dll"
    },
    @{
        Kind = "desktop"
        Source = "apps/desktop/dist/index.html"
    },
    @{
        Kind = "plugin-host"
        Source = "src/OpenLineOps.PluginHost/bin/$Configuration/net10.0/OpenLineOps.PluginHost.dll"
    },
    @{
        Kind = "script-worker"
        Source = "src/OpenLineOps.ScriptWorker/bin/$Configuration/net10.0/OpenLineOps.ScriptWorker.dll"
    },
    @{
        Kind = "sample-plugin"
        Source = "samples/plugins/OpenLineOps.SamplePlugins.LoopbackDevice/bin/$Configuration/net10.0/OpenLineOps.SamplePlugins.LoopbackDevice.dll"
    }
)

foreach ($artifact in $SourceArtifacts) {
    $sourcePath = Resolve-RepoPath $artifact.Source
    if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
        throw "Missing built artifact for kind '$($artifact.Kind)': $sourcePath. Run .NET build and desktop build before this gate."
    }

    $destinationDirectory = Join-Path $ResolvedArtifactsRoot $artifact.Kind
    New-Item -ItemType Directory -Path $destinationDirectory -Force | Out-Null
    Copy-Item -LiteralPath $sourcePath -Destination (Join-Path $destinationDirectory (Split-Path $sourcePath -Leaf)) -Force
}

$manifestPath = Join-Path $ResolvedArtifactsRoot "release-manifest.json"
$checksumsPath = Join-Path $ResolvedArtifactsRoot "checksums.sha256"
$notesPath = Join-Path $ResolvedArtifactsRoot "release-notes.md"
$manifestProject = Resolve-RepoPath "tools/OpenLineOps.ReleaseManifest/OpenLineOps.ReleaseManifest.csproj"

$manifestArguments = @(
    "run",
    "--project",
    $manifestProject,
    "--no-build",
    "--",
    "--version",
    $Version,
    "--artifacts",
    $ResolvedArtifactsRoot,
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

& dotnet @manifestArguments
if ($LASTEXITCODE -ne 0) {
    throw "Release manifest kind gate failed with exit code $LASTEXITCODE."
}

$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.schemaVersion -ne 2) {
    throw "Expected release manifest schemaVersion 2, found $($manifest.schemaVersion)."
}

$actualKinds = @($manifest.artifacts | ForEach-Object { $_.kind } | Sort-Object -Unique)
foreach ($kind in $RequiredKinds) {
    if ($actualKinds -notcontains $kind) {
        throw "Release manifest is missing required artifact kind '$kind'."
    }
}

Write-Host "Release artifact kind gate passed."
Write-Host "Manifest: $manifestPath"

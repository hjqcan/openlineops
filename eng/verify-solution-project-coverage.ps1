param(
    [string] $Solution = "OpenLineOps.sln"
)

$ErrorActionPreference = "Stop"

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$solutionPath = [System.IO.Path]::GetFullPath((Join-Path $repoRoot $Solution))
$sourceRoots = @("modules", "shared", "src", "tests", "tools", "samples")

if (-not (Test-Path -LiteralPath $solutionPath -PathType Leaf)) {
    throw "Solution does not exist: $solutionPath"
}

function Get-NormalizedRepoPath {
    param([Parameter(Mandatory = $true)][string] $Path)

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $rootPrefix = $repoRoot.TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar) +
        [System.IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Project path is outside the repository: $fullPath"
    }

    return $fullPath.Substring($rootPrefix.Length).Replace('\', '/')
}

$expectedProjects = @(
    foreach ($sourceRoot in $sourceRoots) {
        $rootPath = Join-Path $repoRoot $sourceRoot
        if (-not (Test-Path -LiteralPath $rootPath -PathType Container)) {
            throw "Required source root does not exist: $rootPath"
        }

        Get-ChildItem -LiteralPath $rootPath -Filter "*.csproj" -File -Recurse |
            Where-Object {
                $_.FullName -notmatch "[\\/](?:bin|obj)[\\/]"
            } |
            ForEach-Object {
                Get-NormalizedRepoPath -Path $_.FullName
            }
    }
) | Sort-Object -Unique

$solutionOutput = @(& dotnet sln $solutionPath list 2>&1)
if ($LASTEXITCODE -ne 0) {
    throw "dotnet sln list failed:`n$($solutionOutput -join [Environment]::NewLine)"
}

$listedProjects = @(
    $solutionOutput |
        ForEach-Object { $_.ToString().Trim() } |
        Where-Object { $_ -match "\.csproj$" } |
        ForEach-Object {
            Get-NormalizedRepoPath -Path (Join-Path $repoRoot $_)
        }
) | Sort-Object -Unique

$listedSet = [System.Collections.Generic.HashSet[string]]::new(
    [System.StringComparer]::OrdinalIgnoreCase)
foreach ($project in $listedProjects) {
    [void] $listedSet.Add($project)
}

$missing = @($expectedProjects | Where-Object { -not $listedSet.Contains($_) })
if ($missing.Count -gt 0) {
    throw "OpenLineOps.sln omits formal projects:`n - $($missing -join "`n - ")"
}

Write-Host "Solution project coverage passed: $($expectedProjects.Count) formal projects are included."

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

function Get-SourceProjectFiles {
    param([Parameter(Mandatory = $true)][string] $RootPath)

    $pending = [System.Collections.Generic.Stack[string]]::new()
    $pending.Push([System.IO.Path]::GetFullPath($RootPath))
    while ($pending.Count -gt 0) {
        $directory = $pending.Pop()
        foreach ($entry in Get-ChildItem -LiteralPath $directory -Force) {
            if ($entry.PSIsContainer) {
                if ($entry.Name -cin @("bin", "obj") `
                    -or ($entry.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                    continue
                }

                $pending.Push($entry.FullName)
                continue
            }

            if ($entry.Extension -ceq ".csproj") {
                $entry
            }
        }
    }
}

$expectedProjects = @(
    foreach ($sourceRoot in $sourceRoots) {
        $rootPath = Join-Path $repoRoot $sourceRoot
        if (-not (Test-Path -LiteralPath $rootPath -PathType Container)) {
            throw "Required source root does not exist: $rootPath"
        }

        Get-SourceProjectFiles -RootPath $rootPath |
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

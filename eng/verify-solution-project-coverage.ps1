param(
    [string[]] $Solution = @("OpenLineOps.sln", "OpenLineOps.slnx")
)

$ErrorActionPreference = "Stop"

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$sourceRoots = @("modules", "shared", "src", "tests", "tools", "samples")
$pathComparison = if ([System.IO.Path]::DirectorySeparatorChar -eq '\') {
    [System.StringComparison]::OrdinalIgnoreCase
}
else {
    [System.StringComparison]::Ordinal
}
$pathComparer = if ([System.IO.Path]::DirectorySeparatorChar -eq '\') {
    [System.StringComparer]::OrdinalIgnoreCase
}
else {
    [System.StringComparer]::Ordinal
}

function Get-NormalizedRepoPath {
    param([Parameter(Mandatory = $true)][string] $Path)

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $rootPrefix = $repoRoot.TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar) +
        [System.IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith($rootPrefix, $pathComparison)) {
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

$listedProjectsBySolution = [ordered]@{}
foreach ($solutionName in $Solution) {
    $solutionPath = [System.IO.Path]::GetFullPath((Join-Path $repoRoot $solutionName))
    if (-not (Test-Path -LiteralPath $solutionPath -PathType Leaf)) {
        throw "Solution does not exist: $solutionPath"
    }

    $solutionOutput = @(& dotnet sln $solutionPath list 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet sln list failed for '$solutionName':`n$($solutionOutput -join [Environment]::NewLine)"
    }

    $listedProjects = @(
        $solutionOutput |
            ForEach-Object { $_.ToString().Trim() } |
            Where-Object { $_ -match "\.csproj$" } |
            ForEach-Object {
                $listedPath = [System.IO.Path]::GetFullPath((Join-Path $repoRoot $_))
                if (-not (Test-Path -LiteralPath $listedPath -PathType Leaf)) {
                    throw "$solutionName references a project path that does not exist with the filesystem's exact path semantics: $listedPath"
                }

                Get-NormalizedRepoPath -Path $listedPath
            }
    ) | Sort-Object -Unique

    $listedSet = [System.Collections.Generic.HashSet[string]]::new(
        $pathComparer)
    foreach ($project in $listedProjects) {
        [void] $listedSet.Add($project)
    }

    $missing = @($expectedProjects | Where-Object { -not $listedSet.Contains($_) })
    if ($missing.Count -gt 0) {
        throw "$solutionName omits formal projects:`n - $($missing -join "`n - ")"
    }

    $listedProjectsBySolution[$solutionName] = $listedSet
}

if ($listedProjectsBySolution.Count -gt 1) {
    $baselineName = [string]@($listedProjectsBySolution.Keys)[0]
    $baseline = $listedProjectsBySolution[$baselineName]
    foreach ($solutionName in @($listedProjectsBySolution.Keys)[1..($listedProjectsBySolution.Count - 1)]) {
        $candidate = $listedProjectsBySolution[$solutionName]
        $missingFromCandidate = @($baseline | Where-Object { -not $candidate.Contains($_) })
        $extraInCandidate = @($candidate | Where-Object { -not $baseline.Contains($_) })
        if ($missingFromCandidate.Count -gt 0 -or $extraInCandidate.Count -gt 0) {
            throw "$solutionName does not match $baselineName project membership. Missing:`n - $($missingFromCandidate -join "`n - ")`nExtra:`n - $($extraInCandidate -join "`n - ")"
        }
    }
}

Write-Host "Solution project coverage passed: $($expectedProjects.Count) formal projects are included in $($Solution -join ', ')."

param()

$ErrorActionPreference = "Stop"

$RepoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$SourceExtensions = [System.Collections.Generic.HashSet[string]]::new(
    [System.StringComparer]::OrdinalIgnoreCase)
@(
    ".cs", ".csproj", ".props", ".targets",
    ".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs",
    ".ps1", ".json", ".yml", ".yaml"
) | ForEach-Object { $SourceExtensions.Add($_) | Out-Null }

$ExcludedRoots = @(
    ".git/",
    "artifacts/",
    "lib/NetDevPack/",
    "lib/pythonscript/",
    "node_modules/",
    "output/"
)

$Failures = [System.Collections.Generic.List[string]]::new()
$trackedFiles = & git -C $RepoRoot ls-files --cached --others --exclude-standard
if ($LASTEXITCODE -ne 0) {
    throw "Could not enumerate repository files."
}

foreach ($relativePath in $trackedFiles) {
    $portablePath = $relativePath.Replace('\', '/')
    if ($ExcludedRoots.Where({ $portablePath.StartsWith($_, [System.StringComparison]::OrdinalIgnoreCase) }).Count -gt 0) {
        continue
    }
    if ($portablePath.EndsWith("package-lock.json", [System.StringComparison]::OrdinalIgnoreCase)) {
        continue
    }

    $extension = [System.IO.Path]::GetExtension($portablePath)
    if (-not $SourceExtensions.Contains($extension)) {
        continue
    }

    $fileNameWithoutExtension = [System.IO.Path]::GetFileNameWithoutExtension($portablePath)
    if ($fileNameWithoutExtension -match '(?i)(?:^|[._-])v[1-9][0-9]*$') {
        $Failures.Add("Version-suffixed implementation filename: $portablePath") | Out-Null
    }

    $fullPath = Join-Path $RepoRoot $relativePath
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        continue
    }

    $lineNumber = 0
    foreach ($line in Get-Content -LiteralPath $fullPath) {
        $lineNumber += 1
        if ($line -cmatch '\b[A-Za-z_][A-Za-z0-9_]*V[1-9][0-9]*\b') {
            $Failures.Add("Version-suffixed implementation identifier: ${portablePath}:$lineNumber") | Out-Null
        }

        if ($line -match '(?i)(?:openlineops|flow-ir|runtime-action-contract)[a-z0-9./_-]*(?:/|-|_)v[1-9][0-9]*|(?:platform|devices|engineering|operations|plugins|production|projects|processes|runtime|topology|traceability|health)-v[1-9][0-9]*') {
            $Failures.Add("Forbidden internal version token: ${portablePath}:$lineNumber") | Out-Null
        }

        if ($line -match '["'']/(?:api/)?v[1-9][0-9]*(?:/|["''])') {
            $Failures.Add("Versioned pre-release route: ${portablePath}:$lineNumber") | Out-Null
        }
    }
}

if ($Failures.Count -gt 0) {
    $Failures | Sort-Object -Unique | ForEach-Object { Write-Error $_ }
    throw "Version-suffixed implementations are forbidden. Replace the current model directly."
}

Write-Host "No forbidden internal version tokens, version-suffixed implementations, or pre-release routes found."

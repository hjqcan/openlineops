param()

$ErrorActionPreference = "Stop"

$RepoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$SourceExtensions = [System.Collections.Generic.HashSet[string]]::new(
    [System.StringComparer]::OrdinalIgnoreCase)
@(
    ".cs", ".csproj", ".props", ".targets",
    ".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs",
    ".ps1", ".json", ".yml", ".yaml", ".md"
) | ForEach-Object { $SourceExtensions.Add($_) | Out-Null }

$ExcludedRoots = @(
    ".git/",
    "artifacts/",
    "lib/NetDevPack/",
    "lib/pythonscript/",
    "node_modules/",
    "output/"
)
$ThisScript = "eng/verify-no-technical-debt-markers.ps1"
$MarkerPattern = '\b(?:TO' + 'DO|FIX' + 'ME|HA' + 'CK|X' + 'XX)\b|Not' + 'ImplementedException'
$Failures = [System.Collections.Generic.List[string]]::new()
$trackedFiles = & git -C $RepoRoot ls-files --cached --others --exclude-standard
if ($LASTEXITCODE -ne 0) {
    throw "Could not enumerate repository files."
}

foreach ($relativePath in $trackedFiles) {
    $portablePath = $relativePath.Replace('\', '/')
    $isExcludedRoot = $ExcludedRoots.Where({
        $portablePath.StartsWith($_, [System.StringComparison]::OrdinalIgnoreCase)
    }).Count -gt 0
    if ($portablePath.Equals($ThisScript, [System.StringComparison]::OrdinalIgnoreCase) `
        -or $isExcludedRoot `
        -or $portablePath.EndsWith("package-lock.json", [System.StringComparison]::OrdinalIgnoreCase)) {
        continue
    }

    if (-not $SourceExtensions.Contains([System.IO.Path]::GetExtension($portablePath))) {
        continue
    }

    $fullPath = Join-Path $RepoRoot $relativePath
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        continue
    }

    $lineNumber = 0
    foreach ($line in Get-Content -LiteralPath $fullPath) {
        $lineNumber += 1
        if ($line -cmatch $MarkerPattern) {
            $Failures.Add("Technical-debt marker: ${portablePath}:$lineNumber") | Out-Null
        }
    }
}

if ($Failures.Count -gt 0) {
    $Failures | Sort-Object -Unique | ForEach-Object { Write-Error $_ }
    throw "Technical-debt markers are forbidden in the pre-release codebase."
}

Write-Host "No technical-debt markers or unimplemented code paths found."

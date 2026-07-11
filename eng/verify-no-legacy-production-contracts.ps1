param()

$ErrorActionPreference = "Stop"

$RepoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$ImplementationRoots = @("apps/", "modules/", "shared/", "src/", "tools/", "scripts/")
$SourceExtensions = [System.Collections.Generic.HashSet[string]]::new(
    [System.StringComparer]::OrdinalIgnoreCase)
@(
    ".cs", ".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs",
    ".json", ".yml", ".yaml", ".ps1"
) | ForEach-Object { $SourceExtensions.Add($_) | Out-Null }
$ExcludedSegments = @("/bin/", "/obj/", "/dist/", "/node_modules/")
$LegacyPattern = '\b(?:D' + 'UT|D' + 'ut|D' + 'utModelDefinition|WorkstationDefinition|ProcessStage|StageDefinition)\b|semantic' + 'Outcome|batch' + 'Id|Batch' + 'Id|External' + 'TestProgram|external' + 'TestProgram|adapter' + 'Id|openlineops_run_external_' + 'test|ADAPTER_' + 'ID'
$Failures = [System.Collections.Generic.List[string]]::new()
$trackedFiles = & git -C $RepoRoot ls-files --cached --others --exclude-standard
if ($LASTEXITCODE -ne 0) {
    throw "Could not enumerate repository files."
}

foreach ($relativePath in $trackedFiles) {
    $portablePath = $relativePath.Replace('\', '/')
    $isImplementation = $ImplementationRoots.Where({
        $portablePath.StartsWith($_, [System.StringComparison]::OrdinalIgnoreCase)
    }).Count -gt 0
    $isGenerated = $ExcludedSegments.Where({
        $portablePath.IndexOf($_, [System.StringComparison]::OrdinalIgnoreCase) -ge 0
    }).Count -gt 0
    $hasSourceExtension = $SourceExtensions.Contains([System.IO.Path]::GetExtension($portablePath))
    if (-not $isImplementation -or $isGenerated -or -not $hasSourceExtension) {
        continue
    }

    $fullPath = Join-Path $RepoRoot $relativePath
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        continue
    }

    $lineNumber = 0
    foreach ($line in Get-Content -LiteralPath $fullPath) {
        $lineNumber += 1
        if ($line -cmatch $LegacyPattern) {
            $Failures.Add("Legacy production contract: ${portablePath}:$lineNumber") | Out-Null
        }
    }
}

if ($Failures.Count -gt 0) {
    $Failures | Sort-Object -Unique | ForEach-Object { Write-Error $_ }
    throw "Legacy production contracts and compatibility aliases are forbidden."
}

Write-Host "No legacy production contracts or compatibility aliases found."

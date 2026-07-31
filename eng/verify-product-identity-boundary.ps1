param(
    [string] $RepositoryRoot
)

$ErrorActionPreference = "Stop"

$RepoRoot = if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
}
else {
    [System.IO.Path]::GetFullPath($RepositoryRoot)
}

if (-not (Test-Path -LiteralPath $RepoRoot -PathType Container)) {
    throw "Repository root does not exist: $RepoRoot"
}

$gitDirectory = Join-Path $RepoRoot ".git"
if (-not (Test-Path -LiteralPath $gitDirectory)) {
    throw "Repository root is not a Git working tree: $RepoRoot"
}

$ForbiddenProductIdentity = @("Smart", "Matri", "X") -join ""
$Failures = [System.Collections.Generic.List[string]]::new()

$repositoryFiles = & git -C $RepoRoot ls-files --cached --others --exclude-standard
if ($LASTEXITCODE -ne 0) {
    throw "Could not enumerate repository files."
}

foreach ($relativePath in $repositoryFiles) {
    if ($relativePath.IndexOf(
            $ForbiddenProductIdentity,
            [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
        $Failures.Add("Forbidden predecessor product identity in path: $relativePath") | Out-Null
    }
}

$matches = & git -C $RepoRoot grep --untracked -n -i -I -- $ForbiddenProductIdentity -- . 2>$null
if ($LASTEXITCODE -eq 0) {
    foreach ($match in $matches) {
        $Failures.Add("Forbidden predecessor product identity in content: $match") | Out-Null
    }
}
elseif ($LASTEXITCODE -ne 1) {
    throw "Could not scan repository content."
}

if ($Failures.Count -gt 0) {
    $Failures |
        Sort-Object -Unique |
        ForEach-Object { Write-Error $_ -ErrorAction Continue }
    throw "The repository must expose only the OpenLineOps product identity."
}

Write-Host "OpenLineOps product identity boundary verified."

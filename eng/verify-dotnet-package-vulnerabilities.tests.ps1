param(
    [string] $WorkRoot = "output/dotnet-package-vulnerability-verification"
)

$ErrorActionPreference = "Stop"
$RepoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$Verifier = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "verify-dotnet-package-vulnerabilities.ps1"))
$ResolvedWorkRoot = [System.IO.Path]::GetFullPath((Join-Path $RepoRoot $WorkRoot))
$rootPrefix = $RepoRoot.TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
if (-not $ResolvedWorkRoot.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Verification work root must remain inside the repository."
}

if (Test-Path -LiteralPath $ResolvedWorkRoot) {
    Remove-Item -LiteralPath $ResolvedWorkRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $ResolvedWorkRoot -Force | Out-Null

$fakeDotNet = Join-Path $ResolvedWorkRoot "dotnet-fixture.cmd"
[System.IO.File]::WriteAllText(
    $fakeDotNet,
    "@echo off`r`ntype `"%OPENLINEOPS_DOTNET_AUDIT_FIXTURE%`"`r`nexit /b %OPENLINEOPS_DOTNET_AUDIT_EXIT_CODE%`r`n",
    [System.Text.Encoding]::ASCII)

function Invoke-Case {
    param(
        [Parameter(Mandatory = $true)][string] $Name,
        [Parameter(Mandatory = $true)][string] $Json,
        [Parameter(Mandatory = $true)][int] $CommandExitCode,
        [Parameter(Mandatory = $true)][int] $ExpectedExitCode,
        [string] $ExpectedPattern
    )

    $fixturePath = Join-Path $ResolvedWorkRoot "$Name.json"
    [System.IO.File]::WriteAllText(
        $fixturePath,
        $Json,
        [System.Text.UTF8Encoding]::new($false))
    $previousFixture = $env:OPENLINEOPS_DOTNET_AUDIT_FIXTURE
    $previousExitCode = $env:OPENLINEOPS_DOTNET_AUDIT_EXIT_CODE
    try {
        $env:OPENLINEOPS_DOTNET_AUDIT_FIXTURE = $fixturePath
        $env:OPENLINEOPS_DOTNET_AUDIT_EXIT_CODE = $CommandExitCode.ToString([System.Globalization.CultureInfo]::InvariantCulture)
        $previousErrorActionPreference = $ErrorActionPreference
        $ErrorActionPreference = "Continue"
        try {
            $output = & powershell `
                -NoProfile `
                -ExecutionPolicy Bypass `
                -File $Verifier `
                -TargetPaths "OpenLineOps.sln" `
                -DotNetCommand $fakeDotNet 2>&1
            $exitCode = $LASTEXITCODE
        }
        finally {
            $ErrorActionPreference = $previousErrorActionPreference
        }
    }
    finally {
        $env:OPENLINEOPS_DOTNET_AUDIT_FIXTURE = $previousFixture
        $env:OPENLINEOPS_DOTNET_AUDIT_EXIT_CODE = $previousExitCode
    }

    $text = ($output | Out-String)
    if ($exitCode -ne $ExpectedExitCode) {
        Write-Host $text
        throw "Case '$Name' exited with $exitCode; expected $ExpectedExitCode."
    }
    if (-not [string]::IsNullOrWhiteSpace($ExpectedPattern) -and $text -cnotmatch $ExpectedPattern) {
        Write-Host $text
        throw "Case '$Name' did not report the expected failure."
    }

    Write-Host "Case '$Name' passed."
}

$cleanJson = '{"version":1,"parameters":"--vulnerable --include-transitive","projects":[{"path":"fixture.csproj"}]}'
$vulnerableJson = '{"version":1,"parameters":"--vulnerable --include-transitive","projects":[{"path":"fixture.csproj","frameworks":[{"framework":"net10.0","topLevelPackages":[{"id":"Unsafe.Package","resolvedVersion":"1.0.0","vulnerabilities":[{"severity":"High","advisoryUrl":"https://example.invalid/advisory"}]}]}]}]}'

Invoke-Case -Name "clean" -Json $cleanJson -CommandExitCode 0 -ExpectedExitCode 0
Invoke-Case -Name "vulnerable" -Json $vulnerableJson -CommandExitCode 0 -ExpectedExitCode 1 -ExpectedPattern "Vulnerable .NET packages were reported"
Invoke-Case -Name "invalid-json" -Json '{' -CommandExitCode 0 -ExpectedExitCode 1 -ExpectedPattern "invalid JSON"
Invoke-Case -Name "command-failure" -Json $cleanJson -CommandExitCode 23 -ExpectedExitCode 1 -ExpectedPattern "exit code 23"

Write-Host ".NET package vulnerability audit regression tests passed."

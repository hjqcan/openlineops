param()

$ErrorActionPreference = "Stop"

$Verifier = Join-Path $PSScriptRoot "verify-product-identity-boundary.ps1"
$TestRoot = Join-Path `
    ([System.IO.Path]::GetTempPath()) `
    ("openlineops-identity-boundary-tests-" + [System.Guid]::NewGuid().ToString("N"))

function Invoke-Git {
    param(
        [Parameter(Mandatory)]
        [string[]] $Arguments
    )

    & git -C $TestRoot @Arguments | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Git command failed: git $($Arguments -join ' ')"
    }
}

function Invoke-ExpectedFailure {
    param(
        [Parameter(Mandatory)]
        [scriptblock] $Action,

        [Parameter(Mandatory)]
        [string] $ExpectedText
    )

    try {
        & $Action
    }
    catch {
        if ($_.Exception.Message -notlike "*$ExpectedText*") {
            throw "Expected failure containing '$ExpectedText', got: $($_.Exception.Message)"
        }

        return
    }

    throw "Expected verifier failure containing '$ExpectedText'."
}

try {
    New-Item -ItemType Directory -Path $TestRoot -Force | Out-Null
    Invoke-Git -Arguments @("init", "--quiet")

    $neutralFile = Join-Path $TestRoot "README.md"
    Set-Content -LiteralPath $neutralFile -Value "# OpenLineOps" -Encoding utf8
    & $Verifier -RepositoryRoot $TestRoot

    $forbiddenIdentity = @("Smart", "Matri", "X") -join ""
    Set-Content `
        -LiteralPath $neutralFile `
        -Value "Unrelated text followed by $forbiddenIdentity." `
        -Encoding utf8
    Invoke-ExpectedFailure `
        -ExpectedText "repository must expose only the OpenLineOps product identity" `
        -Action { & $Verifier -RepositoryRoot $TestRoot }

    Set-Content -LiteralPath $neutralFile -Value "# OpenLineOps" -Encoding utf8
    $forbiddenPath = Join-Path $TestRoot "$forbiddenIdentity.txt"
    Set-Content -LiteralPath $forbiddenPath -Value "neutral content" -Encoding utf8
    Invoke-ExpectedFailure `
        -ExpectedText "repository must expose only the OpenLineOps product identity" `
        -Action { & $Verifier -RepositoryRoot $TestRoot }

    Write-Host "Product identity boundary verifier tests passed."
}
finally {
    if (Test-Path -LiteralPath $TestRoot -PathType Container) {
        Remove-Item -LiteralPath $TestRoot -Recurse -Force
    }
}

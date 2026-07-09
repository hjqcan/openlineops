param(
    [Parameter(Mandatory = $true)]
    [string] $RepositoryUrl,

    [Parameter(Mandatory = $true)]
    [string] $SecurityContact,

    [Parameter(Mandatory = $true)]
    [string] $ConductContact,

    [string] $RepoRoot,

    [switch] $SkipReadinessGate
)

$ErrorActionPreference = "Stop"

function Normalize-RepositoryUrl {
    param([Parameter(Mandatory = $true)][string] $Value)

    $trimmed = $Value.Trim().TrimEnd([char[]]@("/"))
    $uri = [System.Uri] $null
    if (-not [System.Uri]::TryCreate($trimmed, [System.UriKind]::Absolute, [ref] $uri)) {
        throw "RepositoryUrl must be an absolute URL."
    }

    if ($uri.Scheme -ne "https") {
        throw "RepositoryUrl must use https."
    }

    if (-not [string]::Equals($uri.Host, "github.com", [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "RepositoryUrl must point to github.com."
    }

    $segments = $uri.AbsolutePath.Trim([char[]]@("/")).Split(
        [char[]]@("/"),
        [System.StringSplitOptions]::RemoveEmptyEntries)
    if ($segments.Length -ne 2) {
        throw "RepositoryUrl must have the form https://github.com/<owner>/<repository>."
    }

    return "https://github.com/$($segments[0])/$($segments[1])"
}

function Assert-FinalContact {
    param(
        [Parameter(Mandatory = $true)][string] $Value,
        [Parameter(Mandatory = $true)][string] $Name
    )

    if ([string]::IsNullOrWhiteSpace($Value)) {
        throw "$Name is required."
    }

    if ($Value -match "(?i)\b(todo|tbd|placeholder|maintainers?)\b") {
        throw "$Name must be a final private contact, not '$Value'."
    }
}

function Resolve-RepoPath {
    param([Parameter(Mandatory = $true)][string] $Path)

    return [System.IO.Path]::GetFullPath((Join-Path $ResolvedRepoRoot $Path))
}

function Write-Utf8NoBom {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][string] $Content
    )

    $directory = [System.IO.Path]::GetDirectoryName($Path)
    if (-not [string]::IsNullOrWhiteSpace($directory)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }

    $encoding = [System.Text.UTF8Encoding]::new($false)
    [System.IO.File]::WriteAllText($Path, $Content, $encoding)
}

if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $RepoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
}

$ResolvedRepoRoot = [System.IO.Path]::GetFullPath($RepoRoot)
$FinalRepositoryUrl = Normalize-RepositoryUrl $RepositoryUrl
Assert-FinalContact -Value $SecurityContact -Name "SecurityContact"
Assert-FinalContact -Value $ConductContact -Name "ConductContact"

$securityPolicyUrl = "$FinalRepositoryUrl/security/policy"

$security = @"
# Security Policy

OpenLineOps integrates production-line workflows, devices, plugins, and trace data. Security issues should be handled carefully.

## Supported Versions

During early development, only the default branch is supported.

## Reporting a Vulnerability

Report vulnerabilities through GitHub private vulnerability reporting:

$securityPolicyUrl

If GitHub private vulnerability reporting is unavailable, contact the maintainers privately at:

$SecurityContact

Please include:

- Affected component and version or commit.
- Steps to reproduce.
- Impact assessment.
- Any suggested mitigation.

Do not publish working exploit details before maintainers have had time to assess and patch the issue.

## Security Areas

- Plugin loading, manifest validation, and external process sandboxing.
- Electron preload and renderer boundaries.
- Local API CORS and authentication strategy.
- Trace artifact storage path confinement.
- Device adapter command execution.
- Database connection and migration handling.
"@

$codeOfConduct = @"
# Code of Conduct

OpenLineOps should be a practical and respectful engineering project.

## Expected Behavior

- Discuss ideas and code with technical clarity.
- Give actionable feedback and assume good intent.
- Keep disagreements focused on evidence, requirements, and maintainability.
- Respect different levels of experience and domain background.
- Avoid harassment, insults, threats, and personal attacks.

## Unacceptable Behavior

- Discriminatory or demeaning language.
- Sustained disruption of project discussion.
- Publishing private information without permission.
- Sexualized attention or unwelcome personal comments.
- Retaliation against people who report a concern.

## Reporting

Report conduct concerns privately to:

$ConductContact

Security vulnerabilities should follow SECURITY.md.

Maintainers should review reports promptly, keep details private, and take proportionate action.

## Enforcement

Maintainers may remove comments, close issues, block participation, or escalate to platform moderation when required to keep the project safe and productive.
"@

$issueTemplateConfig = @"
blank_issues_enabled: true
contact_links:
  - name: Security policy
    url: $securityPolicyUrl
    about: Use GitHub private vulnerability reporting for security issues.
"@

Write-Utf8NoBom -Path (Resolve-RepoPath "SECURITY.md") -Content ($security.TrimEnd() + [Environment]::NewLine)
Write-Utf8NoBom -Path (Resolve-RepoPath "CODE_OF_CONDUCT.md") -Content ($codeOfConduct.TrimEnd() + [Environment]::NewLine)
Write-Utf8NoBom -Path (Resolve-RepoPath ".github/ISSUE_TEMPLATE/config.yml") -Content ($issueTemplateConfig.TrimEnd() + [Environment]::NewLine)

Write-Host "Publication metadata finalized."
Write-Host "Repository: $FinalRepositoryUrl"
Write-Host "Security policy: $securityPolicyUrl"

if (-not $SkipReadinessGate) {
    $readinessScript = Resolve-RepoPath "eng/verify-publication-readiness.ps1"
    if (-not (Test-Path -LiteralPath $readinessScript -PathType Leaf)) {
        throw "Cannot run publication readiness gate; missing $readinessScript"
    }

    & powershell -NoProfile -ExecutionPolicy Bypass -File $readinessScript
    if ($LASTEXITCODE -ne 0) {
        throw "Strict publication readiness gate failed with exit code $LASTEXITCODE."
    }
}

<#
.SYNOPSIS
    Installs the PowerShell modules that entra-shared-mailbox-manager requires at runtime.

.DESCRIPTION
    The WPF app hosts a PowerShell runspace and imports the modules below at startup.
    If either module is missing, the app's IPowerShellHost.InitializeAsync throws with
    a clear "Install-Module ... -Scope CurrentUser -Force" message. This script
    automates that install so end users (and Intune) don't need to copy-paste anything.

    Modules installed:
      * ExchangeOnlineManagement   (Exchange Online cmdlets)
      * Microsoft.Graph            (Graph SDK meta-module; pulls in
                                    Microsoft.Graph.Authentication / Users / Groups)

    Footprint: ~250 MB on disk. Time: 2-5 minutes on a typical connection.
    Idempotent: re-running is a no-op when modules are already at the latest version.

.PARAMETER Scope
    PowerShell module install scope. Defaults to CurrentUser. Use AllUsers if you
    are deploying via Intune as SYSTEM, or if multiple Windows accounts share the
    same machine.

.PARAMETER Force
    Always reinstall, even when a current version is already present. Useful when
    upgrading after a major Microsoft.Graph version bump.

.EXAMPLE
    .\Install-Prerequisites.ps1
    Installs both modules for the current user.

.EXAMPLE
    .\Install-Prerequisites.ps1 -Scope AllUsers
    Run from an elevated prompt to install for every account on the machine.
    This is the recommended scope for Intune platform-script deployment as SYSTEM.

.EXAMPLE
    .\Install-Prerequisites.ps1 -Force
    Reinstall both modules at their current latest versions.

.NOTES
    Intune deployment:
        Devices -> Scripts and remediations -> Add (Windows) -> Add platform script.
        Run this script as: SYSTEM (recommended) with Scope = AllUsers, OR
                            as the signed-in user with Scope = CurrentUser.
        64-bit: Yes. Enforce signature check: optional (script is unsigned by default).

    Exit codes:
        0 - all prerequisites satisfied (installed or already present).
        1 - install failed for one or more modules; see error output.
#>

[CmdletBinding()]
param(
    [ValidateSet('CurrentUser', 'AllUsers')]
    [string]$Scope = 'CurrentUser',

    [switch]$Force
)

$ErrorActionPreference = 'Stop'

$required = @(
    'ExchangeOnlineManagement',
    'Microsoft.Graph'
)

function Write-Step {
    param([string]$Message)
    Write-Host "==> $Message" -ForegroundColor Cyan
}

# -----------------------------------------------------------------------------
# 1. Ensure the NuGet package provider exists so Install-Module can fetch from
#    PSGallery without an interactive "do you want to install NuGet?" prompt.
# -----------------------------------------------------------------------------
if (-not (Get-PackageProvider -Name NuGet -ErrorAction SilentlyContinue)) {
    Write-Step "Installing NuGet package provider"
    Install-PackageProvider -Name NuGet -MinimumVersion 2.8.5.201 -Force -Scope $Scope | Out-Null
}

# -----------------------------------------------------------------------------
# 2. Trust PSGallery so Install-Module doesn't prompt for an untrusted repo.
# -----------------------------------------------------------------------------
$psg = Get-PSRepository -Name PSGallery -ErrorAction SilentlyContinue
if ($psg -and $psg.InstallationPolicy -ne 'Trusted') {
    Write-Step "Trusting PSGallery repository"
    Set-PSRepository -Name PSGallery -InstallationPolicy Trusted
}

# -----------------------------------------------------------------------------
# 3. Install each required module if missing (or always when -Force is supplied).
# -----------------------------------------------------------------------------
$failed = @()

foreach ($name in $required) {
    $existing = Get-Module -ListAvailable -Name $name |
                Sort-Object Version -Descending |
                Select-Object -First 1

    if ($existing -and -not $Force) {
        Write-Host "    $name $($existing.Version) already installed; skipping."
        continue
    }

    try {
        Write-Step "Installing $name (scope=$Scope)"
        Install-Module -Name $name -Scope $Scope -Force -AllowClobber -SkipPublisherCheck
        $installed = Get-Module -ListAvailable -Name $name |
                     Sort-Object Version -Descending |
                     Select-Object -First 1
        Write-Host "    $name $($installed.Version) installed." -ForegroundColor Green
    }
    catch {
        Write-Warning "Failed to install ${name}: $($_.Exception.Message)"
        $failed += $name
    }
}

# -----------------------------------------------------------------------------
# 4. Report result.
# -----------------------------------------------------------------------------
Write-Host ""
if ($failed.Count -eq 0) {
    Write-Host "Prerequisites OK. You can now launch entra-shared-mailbox-manager." -ForegroundColor Green
    exit 0
}
else {
    Write-Host "One or more modules failed to install: $($failed -join ', ')" -ForegroundColor Red
    Write-Host "Open an elevated PowerShell prompt and retry, or see https://aka.ms/posh-modules-troubleshoot."
    exit 1
}

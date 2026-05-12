# add-user-to-all-by-shared-mail-group.ps1 (name update needed)

###################----BUG-FIXES----###################
#
# Pending updates:
#
# 1. Fix CSV logs for user UPN that doesn't exist in the tenant
#      * Currently, it shows that both permissions were added in the export log, but shows the user wasn't found on the console output.
#
# 2. Validate all users from bulk CSV upload before attempting to add to all mailboxes in group.
#
#######################################################

#Enable '-verbose' arg to initiate verbose stdOut
[CmdletBinding()]
param()

# -----------------------------
# Bootstrapping / Connections
# -----------------------------

# Ensure the PackageManagement module is loaded
if (!(Get-Module -ListAvailable -Name PackageManagement)) {
    Write-Verbose "Installing PackageManagement module..."
    Install-Module PackageManagement -Force -AllowClobber
    Import-Module PackageManagement
}

# Ensure the PowerShellGet module is loaded
if (!(Get-Module -ListAvailable -Name PowerShellGet)) {
    Write-Verbose "Installing PowerShellGet module..."
    Install-Module PowerShellGet -Force -AllowClobber
    Import-Module PowerShellGet
}

# Import the Exchange Online Management module only if not already loaded
if (!(Get-Module -Name ExchangeOnlineManagement)) {
    Write-Verbose "Importing ExchangeOnlineManagement module..."
    Import-Module ExchangeOnlineManagement
}

# Check if already connected to Exchange Online, if not, connect
if (-not (Get-ConnectionInformation | Where-Object { $_.Name -eq "ExchangeOnline_1" })) {
    Write-Verbose "Connecting to Exchange Online..."
    Connect-ExchangeOnline
}
else {
    Write-Verbose "Already connected to Exchange Online."
}

# Connect to Microsoft Graph only if not already connected
# IMPORTANT: Add User.Read.All so we can retrieve accountEnabled
if (!(Get-MgContext)) {
    Write-Verbose "Connecting to Microsoft Graph..."
    Connect-MgGraph -Scopes "Group.Read.All", "User.Read.All" -NoWelcome
}
else {
    Write-Verbose "Already connected to Microsoft Graph."
}

# -----------------------------
# Shared helper functions (new)
# -----------------------------

function Select-SharedMailGroup {
    param(
        [Parameter(Mandatory)]
        [array]$GroupOptions
    )

    Write-Host "`nAvailable SharedMail- Security Groups:"
    for ($i = 0; $i -lt $GroupOptions.Count; $i++) {
        Write-Host "$(($i + 1)). $($GroupOptions[$i].Name)"
    }
    Write-Host "$(($GroupOptions.Count + 1)). Enter a Group Object ID manually"

    while ($true) {
        $selection = Read-Host "Enter the number of the group you would like to select"
        if ($selection -match '^[0-9]+$' -and [int]$selection -gt 0 -and [int]$selection -le ($GroupOptions.Count + 1)) {
            if ([int]$selection -eq ($GroupOptions.Count + 1)) {
                $selectedGroupId = Read-Host "Enter the Group Object ID manually"
                $selectedGroupName = "Custom Group"
                Write-Verbose "You have selected a custom group with ID $selectedGroupId."
            }
            else {
                $selectedGroup = $GroupOptions[[int]$selection - 1]
                $selectedGroupName = $selectedGroup.Name
                $selectedGroupId = $selectedGroup.Id
                Write-Verbose "You have selected the $selectedGroupName group."
            }

            return [pscustomobject]@{
                GroupId   = $selectedGroupId
                GroupName = $selectedGroupName
            }
        }
        else {
            Write-Host "Invalid selection. Please enter a valid number from the list." -ForegroundColor Red
        }
    }
}

function Get-GroupMailboxes {
    param(
        [Parameter(Mandatory)][string]$GroupId
    )

    try {
        # Attempt to get members of the selected security group using Microsoft Graph
        $groupMembers = Get-MgGroupMember -GroupId $GroupId -All -ErrorAction Stop |
        Where-Object { $_.AdditionalProperties['userPrincipalName'] -like '*@*' }

        return $groupMembers
    }
    catch {
        Write-Host "`nFailed to retrieve members of the selected group. Please ensure the group ID is correct." -ForegroundColor Red
        return $null
    }
}

function Select-MailboxFromGroupMembers {
    param(
        [Parameter(Mandatory)][array]$GroupMembers
    )

    $mailboxes = foreach ($m in $GroupMembers) {
        $upn = $m.AdditionalProperties['userPrincipalName']
        if (-not [string]::IsNullOrWhiteSpace($upn)) {
            [pscustomobject]@{
                MailboxUpn = $upn
                Id         = $m.Id
            }
        }
    }

    if (-not $mailboxes -or $mailboxes.Count -eq 0) {
        Write-Host "No mailboxes found in group members." -ForegroundColor Red
        return $null
    }

    Write-Host "`nSelect a shared mailbox:"
    for ($i = 0; $i -lt $mailboxes.Count; $i++) {
        Write-Host "$(($i + 1)). $($mailboxes[$i].MailboxUpn)"
    }
    Write-Host "$(($mailboxes.Count + 1)). Cancel"

    while ($true) {
        $sel = Read-Host "Enter number"
        if ($sel -match '^[0-9]+$') {
            $n = [int]$sel
            if ($n -ge 1 -and $n -le $mailboxes.Count) {
                return $mailboxes[$n - 1]
            }
            elseif ($n -eq ($mailboxes.Count + 1)) {
                return $null
            }
        }
        Write-Host "Invalid selection." -ForegroundColor Yellow
    }
}

function Try-ResolveUpn {
    param([Parameter(Mandatory)][string]$Identity)

    # Most of the time trustees are already UPNs. If not, attempt to resolve.
    try {
        $r = Get-EXORecipient -Identity $Identity -ErrorAction Stop
        if ($r.UserPrincipalName) { return $r.UserPrincipalName }
        if ($r.PrimarySmtpAddress) { return $r.PrimarySmtpAddress.ToString() }
    }
    catch {}

    try {
        $r2 = Get-Recipient -Identity $Identity -ErrorAction Stop
        if ($r2.PrimarySmtpAddress) { return $r2.PrimarySmtpAddress.ToString() }
    }
    catch {}

    return $Identity
}

$script:GraphUserCache = @{} # Cache to store user info retrieved from Graph (keyed by normalized UserId) to minimize API calls during report generation
function Get-UserSignInStatus {
    param([Parameter(Mandatory)][string]$UserId)

    # Normalize key
    $key = $UserId.Trim().ToLower()

    if ($script:GraphUserCache.ContainsKey($key)) {
        return $script:GraphUserCache[$key]
    }

    try {
        # Explicit property request required for accountEnabled
        $u = Get-MgUser -UserId $UserId -Property "displayName,userPrincipalName,accountEnabled" -ErrorAction Stop

        $result = [pscustomobject]@{
            LookupStatus      = "OK"
            DisplayName       = $u.DisplayName
            UserPrincipalName = $u.UserPrincipalName
            AccountEnabled    = $u.AccountEnabled
        }
    }
    catch {
        $result = [pscustomobject]@{
            LookupStatus      = "LOOKUP_FAILED"
            DisplayName       = $null
            UserPrincipalName = $null
            AccountEnabled    = $null
        }
    }

    $script:GraphUserCache[$key] = $result
    return $result
}

function Get-MailboxDelegatesAndStatus {
    param(
        [Parameter(Mandatory)][string]$MailboxUpn,
        [switch]$IncludeSendAs
    )

    # FullAccess trustees
    $fullAccess = Get-MailboxPermission -Identity $MailboxUpn -ErrorAction SilentlyContinue |
    Where-Object {
        $_.AccessRights -contains "FullAccess" -and
        $_.IsInherited -eq $false -and
        $_.User -notlike "NT AUTHORITY\*" -and
        $_.User -notlike "S-1-5-*" -and
        $_.User -notlike "Microsoft Exchange\*" -and
        $_.User -ne "Default"
    } |
    ForEach-Object { Try-ResolveUpn ($_.User.ToString().Trim()) }

    # SendAs trustees (optional)
    $sendAs = @()
    if ($IncludeSendAs) {
        $sendAs = Get-RecipientPermission -Identity $MailboxUpn -ErrorAction SilentlyContinue |
        Where-Object {
            $_.AccessRights -contains "SendAs" -and
            $_.Trustee -notlike "NT AUTHORITY\*" -and
            $_.Trustee -notlike "S-1-5-*" -and
            $_.Trustee -notlike "Microsoft Exchange\*"
        } |
        ForEach-Object { Try-ResolveUpn ($_.Trustee.ToString().Trim()) }
    }

    # SendOnBehalf trustees
    $mbx = Get-Mailbox -Identity $MailboxUpn -ErrorAction SilentlyContinue
    $sendOnBehalf = @()
    if ($mbx -and $mbx.GrantSendOnBehalfTo) {
        foreach ($t in $mbx.GrantSendOnBehalfTo) {
            $sendOnBehalf += (Try-ResolveUpn ($t.ToString()))
        }
    }

    $all = @($fullAccess + $sendAs + $sendOnBehalf) | Where-Object { $_ } | Sort-Object -Unique

    $report = foreach ($id in $all) {
        $hasFull = $fullAccess -contains $id
        $hasSendAs = if (-not $IncludeSendAs) { "Skipped" } else { [bool]($sendAs -contains $id) }
        $hasSobo = $sendOnBehalf -contains $id

        $g = Get-UserSignInStatus -UserId $id

        $blocked = $null
        if ($g.LookupStatus -eq "OK") {
            $blocked = ($g.AccountEnabled -eq $false)
        }

        [pscustomobject]@{
            Mailbox        = $MailboxUpn
            Trustee        = $id
            DisplayName    = $g.DisplayName
            AccountEnabled = $g.AccountEnabled
            SignInBlocked  = $blocked
            FullAccess     = $hasFull
            SendAs         = $hasSendAs
            SendOnBehalf   = $hasSobo
            LookupStatus   = $g.LookupStatus
        }
    }

    return $report
}

function Remove-BlockedDelegates {
    param(
        [Parameter(Mandatory)][string]$MailboxUpn,
        [Parameter(Mandatory)][array]$DelegateReport,
        [switch]$IncludeSendAs
    )

    # Collect structured action results for CSV export (Path 3 will aggregate these)
    $actionsTaken = New-Object System.Collections.Generic.List[object]

    $blocked = $DelegateReport | Where-Object { $_.LookupStatus -eq "OK" -and $_.SignInBlocked -eq $true }
    if (-not $blocked -or $blocked.Count -eq 0) {
        Write-Host "No blocked delegates found for $MailboxUpn." -ForegroundColor Green
        return $actionsTaken
    }

    Write-Host "`nBlocked delegates detected for ${MailboxUpn}:" -ForegroundColor Yellow
    $blocked | Select Trustee, DisplayName, FullAccess, SendAs, SendOnBehalf | Format-Table -AutoSize | Out-Host

    Write-Host ""
    Write-Host "Removal Options:" -ForegroundColor Cyan
    Write-Host "  Y = Remove this user"
    Write-Host "  N = Skip this user"
    Write-Host "  A = Approve removal of ALL remaining blocked users"
    Write-Host "  Q = Quit cleanup for this mailbox"
    Write-Host ""

    $approveAll = $false

    foreach ($row in $blocked) {
        $target = $row.Trustee

        if (-not $approveAll) {
            $choice = Read-Host "Remove delegated permissions for $target ? (Y/N/A/Q)"

            switch ($choice.ToUpper()) {
                "Y" { }
                "A" { $approveAll = $true }
                "N" { continue }
                "Q" {
                    Write-Host "Cleanup aborted for $MailboxUpn." -ForegroundColor Yellow
                    return $actionsTaken
                }
                default {
                    Write-Host "Invalid option. Skipping $target." -ForegroundColor Yellow
                    continue
                }
            }
        }

        # FullAccess
        if ($row.FullAccess -eq $true) {
            try {
                Remove-MailboxPermission -Identity $MailboxUpn -User $target -AccessRights FullAccess -Confirm:$false -ErrorAction Stop
                Write-Host "Removed FullAccess: $target" -ForegroundColor Cyan

                $actionsTaken.Add([pscustomobject]@{
                        Mailbox = $MailboxUpn
                        Trustee = $target
                        Action  = "RemoveFullAccess"
                        Result  = "Success"
                        Notes   = $null
                    }) | Out-Null
            }
            catch {
                $msg = $_.Exception.Message
                Write-Warning "Failed to remove FullAccess for $target on ${MailboxUpn}: $msg"

                $actionsTaken.Add([pscustomobject]@{
                        Mailbox = $MailboxUpn
                        Trustee = $target
                        Action  = "RemoveFullAccess"
                        Result  = "Failed"
                        Notes   = $msg
                    }) | Out-Null
            }
        }

        # SendAs (only if SendAs was scanned AND it was actually True)
        if ($IncludeSendAs -and ($row.SendAs -eq $true)) {
            try {
                Remove-RecipientPermission -Identity $MailboxUpn -Trustee $target -AccessRights SendAs -Confirm:$false -ErrorAction Stop
                Write-Host "Removed SendAs: $target" -ForegroundColor Cyan

                $actionsTaken.Add([pscustomobject]@{
                        Mailbox = $MailboxUpn
                        Trustee = $target
                        Action  = "RemoveSendAs"
                        Result  = "Success"
                        Notes   = $null
                    }) | Out-Null
            }
            catch {
                $msg = $_.Exception.Message
                Write-Warning "Failed to remove SendAs for $target on ${MailboxUpn}: $msg"

                $actionsTaken.Add([pscustomobject]@{
                        Mailbox = $MailboxUpn
                        Trustee = $target
                        Action  = "RemoveSendAs"
                        Result  = "Failed"
                        Notes   = $msg
                    }) | Out-Null
            }
        }

        # SendOnBehalf
        if ($row.SendOnBehalf -eq $true) {
            try {
                Set-Mailbox -Identity $MailboxUpn -GrantSendOnBehalfTo @{ Remove = $target } -ErrorAction Stop
                Write-Host "Removed SendOnBehalf: $target" -ForegroundColor Cyan

                $actionsTaken.Add([pscustomobject]@{
                        Mailbox = $MailboxUpn
                        Trustee = $target
                        Action  = "RemoveSendOnBehalf"
                        Result  = "Success"
                        Notes   = $null
                    }) | Out-Null
            }
            catch {
                $msg = $_.Exception.Message
                Write-Warning "Failed to remove SendOnBehalf for $target on ${MailboxUpn}: $msg"

                $actionsTaken.Add([pscustomobject]@{
                        Mailbox = $MailboxUpn
                        Trustee = $target
                        Action  = "RemoveSendOnBehalf"
                        Result  = "Failed"
                        Notes   = $msg
                    }) | Out-Null
            }
        }
    }

    Write-Host "`nCleanup completed for $MailboxUpn." -ForegroundColor Green
    return $actionsTaken
}

function Get-SharedMailboxesOnly {
    param(
        [Parameter(Mandatory)][string[]]$Mailboxes
    )

    $valid = New-Object System.Collections.Generic.List[string]

    foreach ($mbx in $Mailboxes) {
        try {
            $t = (Get-Mailbox -Identity $mbx -ErrorAction Stop).RecipientTypeDetails
            if ($t -eq "SharedMailbox") {
                $valid.Add($mbx) | Out-Null
            }
            else {
                Write-Host "$mbx is not a Shared Mailbox (skipping)." -ForegroundColor DarkYellow
            }
        }
        catch {
            Write-Host "Unable to retrieve mailbox $mbx (skipping)." -ForegroundColor Yellow
        }
    }

    return $valid.ToArray()
}


# -----------------------------
# Logging helpers
# -----------------------------
$script:LogDir = Join-Path -Path (Get-Location) -ChildPath "Logs"
if (-not (Test-Path -Path $script:LogDir)) {
    New-Item -ItemType Directory -Path $script:LogDir | Out-Null
}

function New-LogPath {
    param(
        [Parameter(Mandatory)][string]$Prefix
    )

    $ts = Get-Date -Format "yyyyMMdd-HHmmss"
    return (Join-Path -Path $script:LogDir -ChildPath "$Prefix-$ts.csv")
}

# ---------------------------------------------------------
# Existing behavior moved into a wrapper (logic unchanged)
# ---------------------------------------------------------
function Invoke-AddUsersToAllMailboxesInGroup {
    param(
        [Parameter(Mandatory)][array]$groupMembers
    )

    # Prompt for single user vs multiple users via CSV
    Write-Host "`nDo you want to add a single user or multiple users to the shared mailboxes in this group?"
    Write-Host "1. Single User"
    Write-Host "2. Multiple Users (via CSV)"

    $mode = $null
    while (-not $mode) {
        $choice = Read-Host "Enter 1 or 2"
        switch ($choice) {
            '1' { $mode = 'Single' }
            '2' { $mode = 'Multiple' }
            default { Write-Host "Invalid selection. Please enter 1 or 2." -ForegroundColor Yellow }
        }
    }

    # Build list of user UPNs to process
    $userUpns = @()

    if ($mode -eq 'Single') {
        $singleUser = Read-Host "Enter the UPN of the user to add to the shared mailboxes"
        if ([string]::IsNullOrWhiteSpace($singleUser)) {
            Write-Host "No UPN provided. Exiting." -ForegroundColor Red
            return
        }
        $userUpns = @($singleUser.Trim())
    }
    else {
        # Multiple users via CSV
        while (-not $userUpns -or $userUpns.Count -eq 0) {
            $csvInput = Read-Host "Enter the file name (in the current directory) or full path of the CSV file that contains the list of user UPNs (header = 'UPN')"

            if ([string]::IsNullOrWhiteSpace($csvInput)) {
                Write-Host "No file path provided. Please try again." -ForegroundColor Yellow
                continue
            }

            if ([System.IO.Path]::IsPathRooted($csvInput)) {
                $csvPath = $csvInput
            }
            else {
                $csvPath = Join-Path -Path (Get-Location) -ChildPath $csvInput
            }

            if (-not (Test-Path -Path $csvPath)) {
                Write-Host "File not found at '$csvPath'. Please try again." -ForegroundColor Red
                continue
            }

            try {
                $csvData = Import-Csv -Path $csvPath -ErrorAction Stop
            }
            catch {
                Write-Host "Failed to read CSV file: $($_.Exception.Message)" -ForegroundColor Red
                continue
            }

            if (-not $csvData -or -not ($csvData[0].PSObject.Properties.Name -contains 'UPN')) {
                Write-Host "CSV must contain a column named 'UPN'. Please correct the file and try again." -ForegroundColor Red
                continue
            }

            $userUpns = $csvData.UPN | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | ForEach-Object { $_.Trim() } | Sort-Object -Unique

            if ($userUpns.Count -eq 0) {
                Write-Host "No valid UPNs found in the 'UPN' column. Please check the file and try again." -ForegroundColor Red
            }
        }
    }

    Write-Host "`nProcessing permissions for the following user(s):"
    $userUpns | ForEach-Object { Write-Host " - $_" }

    # Prepare log collection
    $logEntries = New-Object System.Collections.Generic.List[object]

    # Loop through each user and each group member (shared mailbox)
    foreach ($userToAdd in $userUpns) {
        Write-Host "`n=== Processing user: $userToAdd ===" -ForegroundColor Cyan

        foreach ($member in $groupMembers) {
            $upn = $member.AdditionalProperties['userPrincipalName']
            $sharedMailboxId = $member.Id

            if ([string]::IsNullOrWhiteSpace($upn)) {
                continue
            }

            Write-Host "  -> Adding $userToAdd to $upn..."

            $accessStatus = ""
            $sendAsStatus = ""

            # Check if the user already has Full Access to the shared mailbox
            $hasFullAccess = Get-MailboxPermission -Identity $sharedMailboxId -ErrorAction SilentlyContinue |
            Where-Object {
                $_.User -eq $userToAdd -and -not $_.IsInherited -and -not $_.Deny -and
                ($_.AccessRights -contains 'FullAccess')
            }

            if (-not $hasFullAccess) {
                try {
                    $null = Add-MailboxPermission -Identity $sharedMailboxId `
                        -User $userToAdd -AccessRights FullAccess -InheritanceType All `
                        -Confirm:$false -WarningAction SilentlyContinue
                    $accessStatus = "FullAccess granted"
                }
                catch {
                    $errorMessage = $_.Exception.Message
                    Write-Warning "Failed to grant FullAccess on [$upn] for [$userToAdd]: $errorMessage"
                    $accessStatus = "Failed to grant FullAccess: $errorMessage"
                }
            }
            else {
                $accessStatus = "FullAccess already present"
            }

            # Check current Send As
            $hasSendAs = Get-RecipientPermission -Identity $sharedMailboxId -ErrorAction SilentlyContinue |
            Where-Object {
                $_.Trustee -eq $userToAdd -and -not $_.IsInherited -and -not $_.Deny -and
                ($_.AccessRights -contains 'SendAs')
            }

            if (-not $hasSendAs) {
                try {
                    $null = Add-RecipientPermission -Identity $sharedMailboxId `
                        -Trustee $userToAdd -AccessRights SendAs -Confirm:$false
                    $sendAsStatus = "SendAs granted"
                }
                catch {
                    $errorMessage = $_.Exception.Message
                    Write-Warning "Failed to grant SendAs on [$upn] for [$userToAdd]: $errorMessage"
                    $sendAsStatus = "Failed to grant SendAs: $errorMessage"
                }
            }
            else {
                $sendAsStatus = "SendAs already present"
            }

            Write-Verbose "Result for user [$userToAdd] on mailbox [$upn] - Access: $accessStatus; SendAs: $sendAsStatus"

            # Add log entry
            $logEntries.Add([PSCustomObject]@{
                    User_UPN               = $userToAdd
                    Access_Status          = $accessStatus
                    SendAs_Status          = $sendAsStatus
                    Shared_Mailbox_Address = $upn
                }) | Out-Null
        }
    }

    # Export log to CSV in the current directory with timestamped filename
    $timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $logFileName = "SharedMail-BulkAction-$timestamp.csv"
    $logFilePath = Join-Path -Path (Get-Location) -ChildPath $logFileName

    $logEntries | Export-Csv -Path $logFilePath -NoTypeInformation -Encoding UTF8

    Write-Host "`nScript completed. Detailed log saved to: $logFilePath" -ForegroundColor Green
}

# -----------------------------
# Define known security groups with IDs in an ordered list
#
# NOTE: The values below have been REDACTED for public release.
# In the original internal version of this script, these entries
# pointed at five real SharedMail- security groups in a single
# Microsoft 365 tenant. The hardcoded nature of this list is one
# of the central motivations for the v2 product's per-deployment
# configuration model — see ../docs/Architecture.md.
#
# To run this legacy script in your own environment, replace the
# placeholder entries below with your real security group display
# names and object IDs. Or skip them entirely and choose the
# "Enter a Group Object ID manually" option at the prompt.
# -----------------------------
$groupOptions = @(
    @{ Name = "SharedMail-GroupOne";   Id = "00000000-0000-0000-0000-000000000001" },
    @{ Name = "SharedMail-GroupTwo";   Id = "00000000-0000-0000-0000-000000000002" },
    @{ Name = "SharedMail-GroupThree"; Id = "00000000-0000-0000-0000-000000000003" },
    @{ Name = "SharedMail-GroupFour";  Id = "00000000-0000-0000-0000-000000000004" },
    @{ Name = "SharedMail-GroupFive";  Id = "00000000-0000-0000-0000-000000000005" }
)

# -----------------------------
# Main Menu (NEW)
# -----------------------------
Write-Host "=====================================" -ForegroundColor DarkYellow
Write-Host " SharedMail Tool" -ForegroundColor DarkYellow
Write-Host "=====================================" -ForegroundColor DarkYellow
Write-Host "1. Add user(s) to ALL shared mailboxes in a SharedMail- group (existing)" -ForegroundColor Cyan
Write-Host "2. View delegated users + sign-in blocked status (per mailbox / all)" -ForegroundColor Cyan
Write-Host "3. Cleanup blocked delegates (remove FullAccess/SendAs/SendOnBehalf)" -ForegroundColor Cyan
Write-Host "4. Exit" -ForegroundColor Cyan

$mainChoice = $null
while (-not $mainChoice) {
    $c = Read-Host "Select an option (1-4)"
    switch ($c) {
        '1' { $mainChoice = 1 }
        '2' { $mainChoice = 2 }
        '3' { $mainChoice = 3 }
        '4' { return }
        default { Write-Host "Invalid selection. Choose 1-4." -ForegroundColor Yellow }
    }
}

# Select group (used by all paths)
$groupSelection = Select-SharedMailGroup -GroupOptions $groupOptions
$selectedGroupId = $groupSelection.GroupId
$selectedGroupName = $groupSelection.GroupName

$groupMembers = Get-GroupMailboxes -GroupId $selectedGroupId
if (-not $groupMembers) { return }

# -----------------------------
# Path 1: Existing add-user flow
# -----------------------------
if ($mainChoice -eq 1) {
    Invoke-AddUsersToAllMailboxesInGroup -groupMembers $groupMembers
    return
}

# Ask whether to include SendAs checks/removals
Write-Host "`nInclude SendAs permissions in audit/cleanup?"
Write-Host "1. Yes (include SendAs)"
Write-Host "2. No  (FullAccess + SendOnBehalf only)"
$includeSendAs = $false
while ($true) {
    $inc = Read-Host "Enter 1 or 2"
    if ($inc -eq '1') { $includeSendAs = $true; break }
    if ($inc -eq '2') { $includeSendAs = $false; break }
    Write-Host "Invalid selection." -ForegroundColor Yellow
}

# Choose scope: one mailbox vs all
Write-Host "`nAudit/cleanup scope:"
Write-Host "1. Single mailbox (pick from group)"
Write-Host "2. ALL mailboxes in the group"
$scope = $null
while (-not $scope) {
    $s = Read-Host "Enter 1 or 2"
    switch ($s) {
        '1' { $scope = 'Single' }
        '2' { $scope = 'All' }
        default { Write-Host "Invalid selection." -ForegroundColor Yellow }
    }
}

$mailboxesToProcess = @()
if ($scope -eq 'Single') {
    $picked = Select-MailboxFromGroupMembers -GroupMembers $groupMembers
    if (-not $picked) { return }
    $mailboxesToProcess = @($picked.MailboxUpn)
}
else {
    $mailboxesToProcess = foreach ($m in $groupMembers) {
        $upn = $m.AdditionalProperties['userPrincipalName']
        if (-not [string]::IsNullOrWhiteSpace($upn)) { $upn }
    }
}

# Filter to only shared mailboxes and validate existence before proceeding with either report or cleanup paths
$mailboxesToProcess = Get-SharedMailboxesOnly -Mailboxes $mailboxesToProcess

# Validate that we have at least one mailbox to process after filtering
if (-not $mailboxesToProcess -or $mailboxesToProcess.Count -eq 0) {
    Write-Host "No shared mailboxes to process after filtering." -ForegroundColor Yellow
    return
}

# -----------------------------
# Path 2: View report
# -----------------------------
if ($mainChoice -eq 2) {
    $allReports = New-Object System.Collections.Generic.List[object]
    $i = 0
    $total = [math]::Max($mailboxesToProcess.Count, 1)

    foreach ($mbx in $mailboxesToProcess) {
        $i++

        Write-Progress `
            -Activity "Auditing mailbox delegates" `
            -Status "Processing $mbx ($i of $total)" `
            -PercentComplete (($i / $total) * 100)

        $r = Get-MailboxDelegatesAndStatus -MailboxUpn $mbx -IncludeSendAs:($includeSendAs)

        if ($r) {
            $r | ForEach-Object { $allReports.Add($_) | Out-Null }
        }
    }

    Write-Progress -Activity "Auditing mailbox delegates" -Completed

    if ($allReports.Count -eq 0) {
        Write-Host "No delegates found (or unable to retrieve data)." -ForegroundColor Yellow
        return
    }

    $sorted = $allReports | Sort-Object @{Expression = "SignInBlocked"; Descending = $true }, Mailbox, Trustee

    $exportPath = New-LogPath -Prefix "mailbox-audit"
    $sorted | Export-Csv -Path $exportPath -NoTypeInformation -Encoding UTF8
    Write-Host "Audit report exported to: $exportPath" -ForegroundColor Green

    $sorted | Format-Table -AutoSize Mailbox, Trustee, DisplayName, AccountEnabled, SignInBlocked, FullAccess, SendAs, SendOnBehalf, LookupStatus

    return
}

# -----------------------------
# Path 3: Cleanup blocked delegates
# -----------------------------
if ($mainChoice -eq 3) {

    $cleanupLog = New-Object System.Collections.Generic.List[object]

    $i = 0
    $total = [math]::Max($mailboxesToProcess.Count, 1)

    foreach ($mbx in $mailboxesToProcess) {
        $i++

        Write-Progress `
            -Activity "Cleaning blocked mailbox delegates" `
            -Status "Processing $mbx ($i of $total)" `
            -PercentComplete (($i / $total) * 100)

        $r = Get-MailboxDelegatesAndStatus -MailboxUpn $mbx -IncludeSendAs:$includeSendAs

        if (-not $r) {
            Write-Host "No report generated for $mbx (skipping)." -ForegroundColor Yellow
            continue
        }

        $actions = Remove-BlockedDelegates -MailboxUpn $mbx -DelegateReport $r -IncludeSendAs:$includeSendAs

        if ($actions -and $actions.Count -gt 0) {
            $actions | ForEach-Object { $cleanupLog.Add($_) | Out-Null }
        }
    }

    Write-Progress -Activity "Cleaning blocked mailbox delegates" -Completed

    # Export cleanup log (once)
    if ($cleanupLog.Count -gt 0) {
        $exportPath = New-LogPath -Prefix "mailbox-cleanup"
        $cleanupLog | Export-Csv -Path $exportPath -NoTypeInformation -Encoding UTF8
        Write-Host "Cleanup report exported to: $exportPath" -ForegroundColor Green
    }
    else {
        Write-Host "No cleanup actions were taken; no cleanup log exported." -ForegroundColor Yellow
    }

    return
}
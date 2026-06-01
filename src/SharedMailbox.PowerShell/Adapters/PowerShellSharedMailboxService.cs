using System.Collections;
using Microsoft.Extensions.Logging;
using SharedMailbox.Core.Domain;
using SharedMailbox.Core.Services;
using SharedMailbox.PowerShell.Hosting;

namespace SharedMailbox.PowerShell.Adapters;

/// <summary>
/// Default <see cref="ISharedMailboxService"/>. Drives all four major flows from the
/// original PowerShell script through an <see cref="IPowerShellHost"/>:
///
///   * GetGroupMembersAsync       → Get-MgGroupMember
///   * FilterSharedMailboxesAsync → Get-Mailbox  (filters to RecipientTypeDetails = SharedMailbox)
///   * AuditAsync                 → Get-MailboxPermission + Get-RecipientPermission + Get-Mailbox.GrantSendOnBehalfTo
///   * RemoveDelegatesAsync       → Remove-MailboxPermission / Remove-RecipientPermission / Set-Mailbox
///   * AddUsersToMailboxesAsync   → Add-MailboxPermission + Add-RecipientPermission
///
/// All PowerShell strings are kept as close to the original script as possible — same
/// filter predicates, same -ErrorAction conventions, same Where-Object clauses — so the
/// behaviour matches one-for-one. Where the original prompted interactively (Y/N/A/Q),
/// the corresponding logic moves to the view; this service just executes the caller's
/// already-made decisions.
/// </summary>
public sealed class PowerShellSharedMailboxService : ISharedMailboxService
{
    private readonly IPowerShellHost _host;
    private readonly IGraphUserLookup _userLookup;
    private readonly ILogger<PowerShellSharedMailboxService> _logger;

    public PowerShellSharedMailboxService(
        IPowerShellHost host,
        IGraphUserLookup userLookup,
        ILogger<PowerShellSharedMailboxService> logger)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _userLookup = userLookup ?? throw new ArgumentNullException(nameof(userLookup));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    // -----------------------------------------------------------------------
    // 1. Get group members
    // -----------------------------------------------------------------------

    public async Task<IReadOnlyList<Mailbox>> GetGroupMembersAsync(
        Guid groupId,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Listing members of group {GroupId}", groupId);

        // Get-MgGroupMember returns DirectoryObject items; the UPN lives in
        // $_.AdditionalProperties['userPrincipalName']. We filter out anything without an '@'
        // to match the script's `-like '*@*'` predicate.
        var output = await _host.InvokeAsync(
            "Get-MgGroupMember -GroupId $GroupId -All -ErrorAction Stop",
            new Dictionary<string, object?> { ["GroupId"] = groupId.ToString() },
            streams: null,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var mailboxes = new List<Mailbox>(output.Count);
        foreach (var member in output)
        {
            var addl = member.Properties["AdditionalProperties"]?.Value as IDictionary;
            if (addl is null) continue;

            var upnObj = addl["userPrincipalName"];
            var upn = upnObj?.ToString();
            if (string.IsNullOrWhiteSpace(upn) || !upn.Contains('@')) continue;

            var idStr = member.Properties["Id"]?.Value?.ToString();
            var id = Guid.TryParse(idStr, out var guid) ? guid : Guid.Empty;

            mailboxes.Add(new Mailbox(id, upn, RecipientTypeDetails.Unknown));
        }

        _logger.LogInformation("Group {GroupId} has {Count} member(s) with a UPN", groupId, mailboxes.Count);
        return mailboxes;
    }

    // -----------------------------------------------------------------------
    // 2. Filter to shared mailboxes only
    // -----------------------------------------------------------------------

    public async Task<IReadOnlyList<Mailbox>> FilterSharedMailboxesAsync(
        IReadOnlyList<string> upns,
        IProgress<MailboxOperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(upns);
        var result = new List<Mailbox>(upns.Count);
        var total = upns.Count;

        for (var i = 0; i < total; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var upn = upns[i];
            progress?.Report(new MailboxOperationProgress(i + 1, total, "Validating mailbox type", upn));

            try
            {
                var output = await _host.InvokeAsync(
                    "Get-Mailbox -Identity $Identity -ErrorAction Stop",
                    new Dictionary<string, object?> { ["Identity"] = upn },
                    streams: null,
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                if (output.Count == 0)
                {
                    _logger.LogDebug("{Upn} returned no mailbox (skipping)", upn);
                    continue;
                }

                var mbx = output[0];
                var typeStr = mbx.Properties["RecipientTypeDetails"]?.Value?.ToString();
                var type = ParseRecipientType(typeStr);

                if (type != RecipientTypeDetails.SharedMailbox)
                {
                    _logger.LogDebug("{Upn} is {Type} (skipping; not a shared mailbox)", upn, type);
                    continue;
                }

                var idStr = mbx.Properties["ExchangeObjectId"]?.Value?.ToString()
                            ?? mbx.Properties["Guid"]?.Value?.ToString();
                var id = Guid.TryParse(idStr, out var guid) ? guid : Guid.Empty;
                result.Add(new Mailbox(id, upn, type));
            }
            catch (PowerShellInvocationException ex)
            {
                _logger.LogWarning("Unable to retrieve mailbox {Upn} (skipping): {Error}", upn, ex.Message);
            }
        }

        return result;
    }

    // -----------------------------------------------------------------------
    // 3. Audit
    // -----------------------------------------------------------------------

    public async Task<IReadOnlyList<DelegateReport>> AuditAsync(
        IReadOnlyList<string> mailboxUpns,
        bool includeSendAs,
        IProgress<MailboxOperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mailboxUpns);
        var rows = new List<DelegateReport>();
        var total = mailboxUpns.Count;

        for (var i = 0; i < total; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var mbx = mailboxUpns[i];
            progress?.Report(new MailboxOperationProgress(i + 1, total, "Auditing mailbox", mbx));

            var perMailbox = await AuditSingleMailboxAsync(mbx, includeSendAs, cancellationToken).ConfigureAwait(false);
            rows.AddRange(perMailbox);
        }

        return rows;
    }

    private async Task<IReadOnlyList<DelegateReport>> AuditSingleMailboxAsync(
        string mailboxUpn,
        bool includeSendAs,
        CancellationToken cancellationToken)
    {
        // FullAccess trustees. The filter predicates match the script exactly.
        var fullAccessRaw = await _host.InvokeAsync(@"
            Get-MailboxPermission -Identity $Identity -ErrorAction SilentlyContinue |
                Where-Object {
                    $_.AccessRights -contains 'FullAccess' -and
                    $_.IsInherited -eq $false -and
                    $_.User -notlike 'NT AUTHORITY\*' -and
                    $_.User -notlike 'S-1-5-*' -and
                    $_.User -notlike 'Microsoft Exchange\*' -and
                    $_.User -ne 'Default'
                }",
            new Dictionary<string, object?> { ["Identity"] = mailboxUpn },
            streams: null, cancellationToken: cancellationToken).ConfigureAwait(false);

        var fullAccess = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in fullAccessRaw)
        {
            var user = row.Properties["User"]?.Value?.ToString();
            if (string.IsNullOrWhiteSpace(user)) continue;
            fullAccess.Add(await ResolveUpnAsync(user, cancellationToken).ConfigureAwait(false));
        }

        // SendAs trustees (optional, matches -switch IncludeSendAs in the script).
        var sendAs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (includeSendAs)
        {
            var sendAsRaw = await _host.InvokeAsync(@"
                Get-RecipientPermission -Identity $Identity -ErrorAction SilentlyContinue |
                    Where-Object {
                        $_.AccessRights -contains 'SendAs' -and
                        $_.Trustee -notlike 'NT AUTHORITY\*' -and
                        $_.Trustee -notlike 'S-1-5-*' -and
                        $_.Trustee -notlike 'Microsoft Exchange\*'
                    }",
                new Dictionary<string, object?> { ["Identity"] = mailboxUpn },
                streams: null, cancellationToken: cancellationToken).ConfigureAwait(false);

            foreach (var row in sendAsRaw)
            {
                var trustee = row.Properties["Trustee"]?.Value?.ToString();
                if (string.IsNullOrWhiteSpace(trustee)) continue;
                sendAs.Add(await ResolveUpnAsync(trustee, cancellationToken).ConfigureAwait(false));
            }
        }

        // SendOnBehalf — read from the mailbox's GrantSendOnBehalfTo MultiValuedProperty.
        var sendOnBehalf = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var mailboxRaw = await _host.InvokeAsync(
            "Get-Mailbox -Identity $Identity -ErrorAction SilentlyContinue",
            new Dictionary<string, object?> { ["Identity"] = mailboxUpn },
            streams: null, cancellationToken: cancellationToken).ConfigureAwait(false);

        if (mailboxRaw.Count > 0)
        {
            if (mailboxRaw[0].Properties["GrantSendOnBehalfTo"]?.Value is IEnumerable sobo)
            {
                foreach (var item in sobo)
                {
                    var s = item?.ToString();
                    if (string.IsNullOrWhiteSpace(s)) continue;
                    sendOnBehalf.Add(await ResolveUpnAsync(s, cancellationToken).ConfigureAwait(false));
                }
            }
        }

        // Union of all trustees, dedup, sort — produces one report row per (mailbox, trustee).
        var allTrustees = fullAccess
            .Concat(sendAs)
            .Concat(sendOnBehalf)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var rows = new List<DelegateReport>(allTrustees.Count);
        foreach (var trustee in allTrustees)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var u = await _userLookup.GetAsync(trustee, cancellationToken).ConfigureAwait(false);

            rows.Add(new DelegateReport
            {
                Mailbox        = mailboxUpn,
                Trustee        = trustee,
                DisplayName    = u.DisplayName,
                AccountEnabled = u.AccountEnabled,
                SignInBlocked  = u.SignInBlocked,
                FullAccess     = fullAccess.Contains(trustee),
                SendAs         = sendAs.Contains(trustee),
                SendOnBehalf   = sendOnBehalf.Contains(trustee),
                SendAsScanned  = includeSendAs,
                LookupStatus   = u.LookupStatus,
            });
        }

        return rows;
    }

    // -----------------------------------------------------------------------
    // 4. Remove delegates (cleanup)
    // -----------------------------------------------------------------------

    public async Task<IReadOnlyList<CleanupAction>> RemoveDelegatesAsync(
        IReadOnlyList<DelegateReport> targets,
        bool includeSendAs,
        IProgress<MailboxOperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(targets);
        var actions = new List<CleanupAction>();
        var total = targets.Count;

        for (var i = 0; i < total; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = targets[i];
            progress?.Report(new MailboxOperationProgress(
                i + 1, total, "Removing delegated permissions", $"{row.Trustee} on {row.Mailbox}"));

            // FullAccess
            if (row.FullAccess)
            {
                actions.Add(await TryRemoveAsync(
                    mailbox: row.Mailbox,
                    trustee: row.Trustee,
                    right: AccessRight.FullAccess,
                    script: "Remove-MailboxPermission -Identity $Identity -User $Trustee -AccessRights FullAccess -Confirm:$false -ErrorAction Stop",
                    cancellationToken: cancellationToken).ConfigureAwait(false));
            }

            // SendAs (only when the audit actually scanned it AND the row has it)
            if (includeSendAs && row.SendAs)
            {
                actions.Add(await TryRemoveAsync(
                    mailbox: row.Mailbox,
                    trustee: row.Trustee,
                    right: AccessRight.SendAs,
                    script: "Remove-RecipientPermission -Identity $Identity -Trustee $Trustee -AccessRights SendAs -Confirm:$false -ErrorAction Stop",
                    cancellationToken: cancellationToken).ConfigureAwait(false));
            }

            // SendOnBehalf
            if (row.SendOnBehalf)
            {
                actions.Add(await TryRemoveAsync(
                    mailbox: row.Mailbox,
                    trustee: row.Trustee,
                    right: AccessRight.SendOnBehalf,
                    script: "Set-Mailbox -Identity $Identity -GrantSendOnBehalfTo @{ Remove = $Trustee } -ErrorAction Stop",
                    cancellationToken: cancellationToken).ConfigureAwait(false));
            }
        }

        return actions;
    }

    private async Task<CleanupAction> TryRemoveAsync(
        string mailbox,
        string trustee,
        AccessRight right,
        string script,
        CancellationToken cancellationToken)
    {
        try
        {
            await _host.InvokeAsync(
                script,
                new Dictionary<string, object?>
                {
                    ["Identity"] = mailbox,
                    ["Trustee"]  = trustee,
                },
                streams: null,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            _logger.LogInformation("Removed {Right} for {Trustee} on {Mailbox}", right, trustee, mailbox);
            return new CleanupAction(mailbox, trustee, right, ActionResult.Success, Notes: null);
        }
        catch (PowerShellInvocationException ex)
        {
            _logger.LogWarning("Failed to remove {Right} for {Trustee} on {Mailbox}: {Error}",
                right, trustee, mailbox, ex.Message);
            return new CleanupAction(mailbox, trustee, right, ActionResult.Failed, Notes: ex.Message);
        }
    }

    // -----------------------------------------------------------------------
    // 5. Add users to mailboxes (bulk grant)
    // -----------------------------------------------------------------------

    public async Task<IReadOnlyList<BulkAddResult>> AddUsersToMailboxesAsync(
        IReadOnlyList<string> userUpns,
        IReadOnlyList<string> mailboxUpns,
        bool grantSendAs = true,
        IProgress<MailboxOperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(userUpns);
        ArgumentNullException.ThrowIfNull(mailboxUpns);

        var rows = new List<BulkAddResult>(userUpns.Count * mailboxUpns.Count);
        var total = userUpns.Count * mailboxUpns.Count;
        var done = 0;

        foreach (var user in userUpns)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _logger.LogInformation("Processing user {User}", user);

            foreach (var mailbox in mailboxUpns)
            {
                cancellationToken.ThrowIfCancellationRequested();
                done++;
                progress?.Report(new MailboxOperationProgress(
                    done, total, "Granting permissions", $"{user} -> {mailbox}"));

                var (faOutcome, faMsg) = await GrantFullAccessAsync(user, mailbox, cancellationToken).ConfigureAwait(false);

                PermissionOutcome saOutcome;
                string? saMsg;
                if (grantSendAs)
                {
                    (saOutcome, saMsg) = await GrantSendAsAsync(user, mailbox, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    saOutcome = PermissionOutcome.NotAttempted;
                    saMsg = null;
                }

                rows.Add(new BulkAddResult(
                    UserUpn: user,
                    SharedMailboxAddress: mailbox,
                    FullAccessOutcome: faOutcome,
                    SendAsOutcome: saOutcome,
                    AccessStatusMessage: faMsg,
                    SendAsStatusMessage: saMsg));
            }
        }

        return rows;
    }

    private async Task<(PermissionOutcome Outcome, string Message)> GrantFullAccessAsync(
        string user, string mailbox, CancellationToken cancellationToken)
    {
        // Check first, mirroring the script's `if (-not $hasFullAccess)` guard.
        var existing = await _host.InvokeAsync(@"
            Get-MailboxPermission -Identity $Identity -ErrorAction SilentlyContinue |
                Where-Object {
                    $_.User -eq $User -and -not $_.IsInherited -and -not $_.Deny -and
                    ($_.AccessRights -contains 'FullAccess')
                }",
            new Dictionary<string, object?> { ["Identity"] = mailbox, ["User"] = user },
            streams: null, cancellationToken: cancellationToken).ConfigureAwait(false);

        if (existing.Count > 0)
        {
            return (PermissionOutcome.AlreadyPresent, "FullAccess already present");
        }

        try
        {
            await _host.InvokeAsync(
                "Add-MailboxPermission -Identity $Identity -User $User -AccessRights FullAccess -InheritanceType All -Confirm:$false -WarningAction SilentlyContinue -ErrorAction Stop | Out-Null",
                new Dictionary<string, object?> { ["Identity"] = mailbox, ["User"] = user },
                streams: null, cancellationToken: cancellationToken).ConfigureAwait(false);

            _logger.LogInformation("Granted FullAccess on {Mailbox} to {User}", mailbox, user);
            return (PermissionOutcome.Granted, "FullAccess granted");
        }
        catch (PowerShellInvocationException ex)
        {
            _logger.LogWarning("Failed to grant FullAccess on {Mailbox} to {User}: {Error}",
                mailbox, user, ex.Message);
            return (PermissionOutcome.Failed, $"Failed to grant FullAccess: {ex.Message}");
        }
    }

    private async Task<(PermissionOutcome Outcome, string Message)> GrantSendAsAsync(
        string user, string mailbox, CancellationToken cancellationToken)
    {
        var existing = await _host.InvokeAsync(@"
            Get-RecipientPermission -Identity $Identity -ErrorAction SilentlyContinue |
                Where-Object {
                    $_.Trustee -eq $User -and -not $_.IsInherited -and -not $_.Deny -and
                    ($_.AccessRights -contains 'SendAs')
                }",
            new Dictionary<string, object?> { ["Identity"] = mailbox, ["User"] = user },
            streams: null, cancellationToken: cancellationToken).ConfigureAwait(false);

        if (existing.Count > 0)
        {
            return (PermissionOutcome.AlreadyPresent, "SendAs already present");
        }

        try
        {
            await _host.InvokeAsync(
                "Add-RecipientPermission -Identity $Identity -Trustee $User -AccessRights SendAs -Confirm:$false -ErrorAction Stop | Out-Null",
                new Dictionary<string, object?> { ["Identity"] = mailbox, ["User"] = user },
                streams: null, cancellationToken: cancellationToken).ConfigureAwait(false);

            _logger.LogInformation("Granted SendAs on {Mailbox} to {User}", mailbox, user);
            return (PermissionOutcome.Granted, "SendAs granted");
        }
        catch (PowerShellInvocationException ex)
        {
            _logger.LogWarning("Failed to grant SendAs on {Mailbox} to {User}: {Error}",
                mailbox, user, ex.Message);
            return (PermissionOutcome.Failed, $"Failed to grant SendAs: {ex.Message}");
        }
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// Resolve an arbitrary trustee identifier (could be a UPN, DN, NetBIOS\name, SID, or
    /// PrimarySmtpAddress) to a UPN. Mirrors Try-ResolveUpn in the script: try
    /// Get-EXORecipient first, fall back to Get-Recipient, return the input unchanged
    /// if both fail.
    /// </summary>
    private async Task<string> ResolveUpnAsync(string identity, CancellationToken cancellationToken)
    {
        identity = identity.Trim();
        if (identity.Length == 0) return identity;

        // Fast path: already looks like a UPN.
        if (identity.Contains('@') && !identity.Contains('\\') && !identity.StartsWith("S-1-"))
            return identity;

        try
        {
            var output = await _host.InvokeAsync(
                "Get-EXORecipient -Identity $Identity -ErrorAction Stop",
                new Dictionary<string, object?> { ["Identity"] = identity },
                streams: null, cancellationToken: cancellationToken).ConfigureAwait(false);

            if (output.Count > 0)
            {
                var upn = output[0].Properties["UserPrincipalName"]?.Value?.ToString();
                if (!string.IsNullOrWhiteSpace(upn)) return upn;
                var smtp = output[0].Properties["PrimarySmtpAddress"]?.Value?.ToString();
                if (!string.IsNullOrWhiteSpace(smtp)) return smtp;
            }
        }
        catch (PowerShellInvocationException)
        {
            // Fall through to Get-Recipient.
        }

        try
        {
            var output = await _host.InvokeAsync(
                "Get-Recipient -Identity $Identity -ErrorAction Stop",
                new Dictionary<string, object?> { ["Identity"] = identity },
                streams: null, cancellationToken: cancellationToken).ConfigureAwait(false);

            if (output.Count > 0)
            {
                var smtp = output[0].Properties["PrimarySmtpAddress"]?.Value?.ToString();
                if (!string.IsNullOrWhiteSpace(smtp)) return smtp;
            }
        }
        catch (PowerShellInvocationException)
        {
            // Last resort: return input unchanged (the script does the same).
        }

        return identity;
    }

    private static RecipientTypeDetails ParseRecipientType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return RecipientTypeDetails.Unknown;
        return Enum.TryParse<RecipientTypeDetails>(value, ignoreCase: true, out var parsed)
            ? parsed
            : RecipientTypeDetails.Unknown;
    }
}

using Microsoft.Extensions.Logging;
using SharedMailbox.Core.Services;
using SharedMailbox.PowerShell.Hosting;

namespace SharedMailbox.PowerShell.Adapters;

/// <summary>
/// Default <see cref="IUserGroupMembershipProvider"/>. Wraps <c>Get-MgUserMemberOf</c>
/// through the hosted PowerShell runspace. Returns just the object IDs — other shape
/// (display name, type, etc.) is ignored by the authorization resolver and would only
/// bloat the wire payload.
///
/// Same pattern as <see cref="PowerShellSharedMailboxService"/>'s Graph calls: the
/// script runs via <see cref="IPowerShellHost.InvokeAsync"/>, results come back as
/// <c>PSObject</c>, we read <c>.Properties["Id"]</c> and parse to <see cref="Guid"/>.
/// </summary>
public sealed class PowerShellUserGroupMembershipProvider : IUserGroupMembershipProvider
{
    private readonly IPowerShellHost _host;
    private readonly ILogger<PowerShellUserGroupMembershipProvider> _logger;

    public PowerShellUserGroupMembershipProvider(
        IPowerShellHost host,
        ILogger<PowerShellUserGroupMembershipProvider> logger)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<IReadOnlyList<Guid>> GetMembershipsAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        var output = await _host.InvokeAsync(
            "Get-MgUserMemberOf -UserId $UserId -All -ErrorAction Stop",
            new Dictionary<string, object?> { ["UserId"] = userId },
            streams: null,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var ids = new List<Guid>(output.Count);
        foreach (var item in output)
        {
            var idStr = item.Properties["Id"]?.Value?.ToString();
            if (Guid.TryParse(idStr, out var id))
            {
                ids.Add(id);
            }
            // Silently skip items without a parseable Id — Get-MgUserMemberOf can return
            // DirectoryObject subtypes (e.g., administrativeUnits) whose Id we don't care
            // about for role-membership purposes. The role config GUIDs only match
            // security-group IDs anyway.
        }

        _logger.LogDebug("Fetched {Count} group membership(s) for {User}", ids.Count, userId);
        return ids;
    }
}

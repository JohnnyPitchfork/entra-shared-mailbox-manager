namespace SharedMailbox.Core.Domain;

/// <summary>
/// A SharedMail- prefixed Entra security group used to scope which mailboxes
/// the tool operates on (e.g., SharedMail-Permits, SharedMail-Utilities,
/// SharedMail-PermAndUtil, SharedMail-ProjectProgression, SharedMail-Temp).
///
/// In the original PowerShell, these were defined in the hard-coded $groupOptions array
/// at the bottom of shared-mailbox-manager.ps1. We move them to configuration so they
/// can be edited without rebuilding (see SharedMailGroupConfig).
/// </summary>
/// <param name="GroupId">The Azure AD object ID of the security group.</param>
/// <param name="Name">Human-friendly display name shown in the UI.</param>
public sealed record SharedMailGroup(Guid GroupId, string Name)
{
    public override string ToString() => Name;
}

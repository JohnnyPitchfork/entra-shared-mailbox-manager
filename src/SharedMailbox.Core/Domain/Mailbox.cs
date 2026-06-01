namespace SharedMailbox.Core.Domain;

/// <summary>
/// A single Exchange Online mailbox. The Id is the Azure AD object ID when the mailbox
/// was discovered via Get-MgGroupMember; for mailboxes resolved only via EXO it may be
/// the ExchangeObjectId. UPN is the source of truth for identity in the UI.
/// </summary>
public sealed record Mailbox(
    Guid Id,
    string UserPrincipalName,
    RecipientTypeDetails RecipientType)
{
    public bool IsSharedMailbox => RecipientType == RecipientTypeDetails.SharedMailbox;

    public override string ToString() => UserPrincipalName;
}

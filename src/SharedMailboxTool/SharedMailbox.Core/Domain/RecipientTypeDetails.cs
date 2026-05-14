namespace SharedMailbox.Core.Domain;

/// <summary>
/// Mirrors Exchange Online's RecipientTypeDetails values that this tool cares about.
/// We only operate on <see cref="SharedMailbox"/> entries; the script's
/// Get-SharedMailboxesOnly function exists specifically to filter to this type.
/// </summary>
public enum RecipientTypeDetails
{
    Unknown = 0,
    UserMailbox,
    SharedMailbox,
    RoomMailbox,
    EquipmentMailbox,
    DiscoveryMailbox,
    MailUser,
    GuestMailUser,
    GroupMailbox,
}

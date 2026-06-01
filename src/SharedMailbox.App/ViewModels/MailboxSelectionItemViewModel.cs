using CommunityToolkit.Mvvm.ComponentModel;
using SharedMailbox.Core.Domain;

namespace SharedMailbox.App.ViewModels;

/// <summary>
/// One row in the Bulk Grant tab's multi-select mailbox ListBox. Wraps a
/// <see cref="Mailbox"/> from the underlying group and adds an
/// <see cref="IsSelected"/> flag the checkbox binds to.
///
/// Same shape and intent as <see cref="CleanupRowViewModel"/> — a thin wrapper
/// around an immutable domain object that exposes a single observable
/// selection flag for the view to bind against.
/// </summary>
public sealed partial class MailboxSelectionItemViewModel : ObservableObject
{
    public MailboxSelectionItemViewModel(Mailbox mailbox)
    {
        Mailbox = mailbox ?? throw new ArgumentNullException(nameof(mailbox));
    }

    public Mailbox Mailbox { get; }

    public string UserPrincipalName => Mailbox.UserPrincipalName;

    [ObservableProperty]
    private bool _isSelected;
}

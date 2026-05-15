using CommunityToolkit.Mvvm.ComponentModel;
using SharedMailbox.Core.Domain;

namespace SharedMailbox.App.ViewModels;

/// <summary>
/// One row in the cleanup tab's DataGrid. Wraps a <see cref="DelegateReport"/> from the
/// underlying audit and adds an <see cref="IsSelected"/> flag the checkbox column binds to.
///
/// We wrap rather than mutating the domain record because the audit result is meant to be
/// immutable; the row's selection state is purely a view-layer concern.
/// </summary>
public sealed partial class CleanupRowViewModel : ObservableObject
{
    public CleanupRowViewModel(DelegateReport report)
    {
        Report = report ?? throw new ArgumentNullException(nameof(report));
    }

    public DelegateReport Report { get; }

    /// <summary>Two-way bound to the DataGrid's checkbox column.</summary>
    [ObservableProperty]
    private bool _isSelected;
}

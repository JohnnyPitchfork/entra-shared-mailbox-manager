using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SharedMailbox.Core.Configuration;
using SharedMailbox.Core.Domain;

namespace SharedMailbox.App.ViewModels;

/// <summary>
/// Drives the sidebar's group-picker UI. Initialized from <see cref="AppConfig.KnownGroups"/>
/// so the deploying admin's curated list of SharedMail- groups shows up by default; users can
/// also paste a custom group object ID for one-off operations (mirrors the PS script's
/// "Enter a Group Object ID manually" option).
///
/// Lifetime: singleton, shared by every tab's view model so a group selected once in the
/// sidebar drives the audit, cleanup, and bulk-grant flows simultaneously.
/// </summary>
public sealed partial class GroupPickerViewModel : ObservableObject
{
    public GroupPickerViewModel(AppConfig appConfig)
    {
        ArgumentNullException.ThrowIfNull(appConfig);

        Groups = new ObservableCollection<SharedMailGroup>(
            appConfig.KnownGroups.Select(g => g.ToDomain()));

        SelectedGroup = Groups.Count > 0 ? Groups[0] : null;
    }

    /// <summary>The set of groups offered in the sidebar list.</summary>
    public ObservableCollection<SharedMailGroup> Groups { get; }

    [ObservableProperty]
    private SharedMailGroup? _selectedGroup;

    [ObservableProperty]
    private string _customGroupIdText = string.Empty;

    [ObservableProperty]
    private string? _customGroupError;

    /// <summary>
    /// Parses <see cref="CustomGroupIdText"/> as a GUID and appends it to <see cref="Groups"/>
    /// under the name "Custom Group". Selects the new entry. Validates that the GUID parses
    /// and isn't already present.
    /// </summary>
    [RelayCommand]
    private void AddCustomGroup()
    {
        CustomGroupError = null;

        var raw = CustomGroupIdText?.Trim() ?? string.Empty;
        if (raw.Length == 0)
        {
            CustomGroupError = "Enter a group object ID.";
            return;
        }

        if (!Guid.TryParse(raw, out var id))
        {
            CustomGroupError = "Not a valid GUID.";
            return;
        }

        if (id == Guid.Empty)
        {
            CustomGroupError = "Group ID cannot be all zeros.";
            return;
        }

        if (Groups.Any(g => g.GroupId == id))
        {
            CustomGroupError = "That group is already in the list.";
            return;
        }

        var group = new SharedMailGroup(id, "Custom Group");
        Groups.Add(group);
        SelectedGroup = group;
        CustomGroupIdText = string.Empty;
    }
}

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using SharedMailbox.Core.Configuration;
using SharedMailbox.Core.Domain;
using SharedMailbox.Core.Services;

namespace SharedMailbox.App.ViewModels;

/// <summary>
/// Drives the sidebar's group-picker UI. Initialized from <see cref="AppConfig.KnownGroups"/>,
/// then filtered at sign-in based on <see cref="IUserAuthorizationService"/> output so the
/// user only sees the SharedMail- groups their roles permit.
///
/// State model:
///   * <see cref="_allGroups"/> is the immutable full list from configuration plus any
///     custom-GUID entries the user has added in the sidebar.
///   * <see cref="Groups"/> is the *visible* list — equal to <c>_allGroups</c> when not
///     signed in, or when Roles aren't configured; a filtered subset otherwise.
///   * <see cref="AccessMessage"/> is shown when the visible list is empty for an
///     actionable reason (no role mapping, Graph lookup failed) so the user gets a
///     human-readable explanation instead of staring at an empty list.
///
/// Lifetime: singleton, shared by every tab's view model so a group selected once in
/// the sidebar drives the audit, cleanup, and bulk-grant flows simultaneously.
/// </summary>
public sealed partial class GroupPickerViewModel : ObservableObject
{
    private readonly IUserAuthorizationService _authorizationService;
    private readonly IConnectionService _connectionService;
    private readonly ILogger<GroupPickerViewModel> _logger;
    private readonly List<SharedMailGroup> _allGroups;

    public GroupPickerViewModel(
        AppConfig appConfig,
        IUserAuthorizationService authorizationService,
        IConnectionService connectionService,
        ILogger<GroupPickerViewModel> logger)
    {
        ArgumentNullException.ThrowIfNull(appConfig);
        _authorizationService = authorizationService ?? throw new ArgumentNullException(nameof(authorizationService));
        _connectionService = connectionService ?? throw new ArgumentNullException(nameof(connectionService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _allGroups = appConfig.KnownGroups.Select(g => g.ToDomain()).ToList();

        Groups = new ObservableCollection<SharedMailGroup>(_allGroups);
        SelectedGroup = Groups.Count > 0 ? Groups[0] : null;

        _connectionService.StatusChanged += OnConnectionStatusChanged;
    }

    /// <summary>The visible set of groups bound to the sidebar ListBox.</summary>
    public ObservableCollection<SharedMailGroup> Groups { get; }

    [ObservableProperty]
    private SharedMailGroup? _selectedGroup;

    [ObservableProperty]
    private string _customGroupIdText = string.Empty;

    [ObservableProperty]
    private string? _customGroupError;

    /// <summary>
    /// Informational message shown in the sidebar when the visible group list is empty
    /// for an actionable reason — "no roles mapped," "couldn't determine access," etc.
    /// Null when no message should be shown (most cases).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAccessMessage))]
    private string? _accessMessage;

    public bool HasAccessMessage => !string.IsNullOrEmpty(AccessMessage);

    // -----------------------------------------------------------------------
    // Custom group entry (unchanged)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Parses <see cref="CustomGroupIdText"/> as a GUID, adds it to both the full list
    /// and the visible list, and selects it. Validates that the GUID parses, isn't
    /// all-zeros, and isn't already present in the visible list.
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

        if (_allGroups.Any(g => g.GroupId == id))
        {
            CustomGroupError = "That group is already in the list.";
            return;
        }

        var group = new SharedMailGroup(id, "Custom Group");
        _allGroups.Add(group);
        Groups.Add(group);
        SelectedGroup = group;
        CustomGroupIdText = string.Empty;
    }

    // -----------------------------------------------------------------------
    // Authorization-driven filtering
    // -----------------------------------------------------------------------

    private void OnConnectionStatusChanged(object? sender, ConnectionStatus status)
    {
        // Fire-and-forget; we don't await event handlers. Exceptions are caught inside.
        _ = UpdateGroupsForAuthorizationAsync(status);
    }

    private async Task UpdateGroupsForAuthorizationAsync(ConnectionStatus status)
    {
        try
        {
            UserAuthorization auth;

            if (!status.IsFullyConnected)
            {
                // Not signed in — show everything so the user can see the catalog,
                // but action commands stay disabled (their VMs gate on IsSignedIn).
                ApplyOnDispatcher(_allGroups, accessMessage: null);
                return;
            }

            auth = await _authorizationService.ResolveAsync().ConfigureAwait(false);

            switch (auth.Status)
            {
                case UserAuthorizationStatus.NotConfigured:
                    ApplyOnDispatcher(_allGroups, accessMessage: null);
                    break;

                case UserAuthorizationStatus.Authorized:
                    var allowed = _allGroups
                        .Where(g => auth.AllowedGroupIds.Contains(g.GroupId))
                        .ToList();
                    ApplyOnDispatcher(
                        allowed,
                        accessMessage: allowed.Count == 0
                            ? "Your roles grant access, but none of the allowed group IDs match the KnownGroups list. Contact your administrator."
                            : null);
                    break;

                case UserAuthorizationStatus.NotAuthorized:
                    ApplyOnDispatcher(
                        Array.Empty<SharedMailGroup>(),
                        "No mailbox groups are mapped to your roles. Contact your administrator.");
                    break;

                case UserAuthorizationStatus.LookupFailed:
                    ApplyOnDispatcher(
                        Array.Empty<SharedMailGroup>(),
                        $"Could not determine your access: {auth.ErrorMessage}");
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update group picker for new authorization state");
            ApplyOnDispatcher(
                Array.Empty<SharedMailGroup>(),
                $"Could not load groups: {ex.Message}");
        }
    }

    private void ApplyOnDispatcher(IReadOnlyList<SharedMailGroup> visible, string? accessMessage)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            Apply(visible, accessMessage);
        }
        else
        {
            dispatcher.Invoke(() => Apply(visible, accessMessage));
        }
    }

    private void Apply(IReadOnlyList<SharedMailGroup> visible, string? accessMessage)
    {
        // Preserve current selection if still visible; otherwise default to first or null.
        var currentSelection = SelectedGroup;

        Groups.Clear();
        foreach (var g in visible) Groups.Add(g);

        SelectedGroup =
            currentSelection is not null && visible.Any(g => g.GroupId == currentSelection.GroupId)
                ? visible.First(g => g.GroupId == currentSelection.GroupId)
                : visible.Count > 0
                    ? visible[0]
                    : null;

        AccessMessage = accessMessage;
    }
}

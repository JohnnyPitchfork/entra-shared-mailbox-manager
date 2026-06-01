using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using SharedMailbox.Core.Domain;
using SharedMailbox.Core.Services;

namespace SharedMailbox.App.ViewModels;

/// <summary>
/// Drives the Audit tab. Mirrors Path 2 of the original PowerShell script:
/// pick scope (single mailbox or all in the selected group), optionally include
/// SendAs scanning, run, see results in a DataGrid, and auto-export to CSV.
///
/// Wiring:
///   * Subscribes to <see cref="GroupPickerViewModel.SelectedGroup"/> changes; when
///     the user picks a different group, results / selected mailbox / export path
///     are cleared.
///   * Subscribes to <see cref="IConnectionService.StatusChanged"/> so the Run button
///     enables only when fully signed in.
///   * Progress callbacks from <see cref="ISharedMailboxService"/> arrive via
///     <see cref="Progress{T}"/>, which captures the SynchronizationContext at
///     construction — so update of the bound properties happens on the UI thread
///     without explicit dispatcher hops.
/// </summary>
public sealed partial class AuditViewModel : ObservableObject
{
    private readonly ISharedMailboxService _mailboxService;
    private readonly IAuditLogWriter _logWriter;
    private readonly IConnectionService _connectionService;
    private readonly GroupPickerViewModel _groupPicker;
    private readonly ILogger<AuditViewModel> _logger;

    public AuditViewModel(
        ISharedMailboxService mailboxService,
        IAuditLogWriter logWriter,
        IConnectionService connectionService,
        GroupPickerViewModel groupPicker,
        ILogger<AuditViewModel> logger)
    {
        _mailboxService = mailboxService ?? throw new ArgumentNullException(nameof(mailboxService));
        _logWriter = logWriter ?? throw new ArgumentNullException(nameof(logWriter));
        _connectionService = connectionService ?? throw new ArgumentNullException(nameof(connectionService));
        _groupPicker = groupPicker ?? throw new ArgumentNullException(nameof(groupPicker));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _connectionService.StatusChanged += OnConnectionStatusChanged;
        _groupPicker.PropertyChanged += OnGroupPickerPropertyChanged;

        IsSignedIn = _connectionService.Status.IsFullyConnected;
    }

    /// <summary>UPNs available in the selected group, used to populate the "single mailbox" dropdown.</summary>
    public ObservableCollection<Mailbox> AvailableMailboxes { get; } = new();

    /// <summary>Audit result rows shown in the DataGrid.</summary>
    public ObservableCollection<DelegateReport> Results { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunAuditCommand))]
    private bool _isSignedIn;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunAuditCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenExportFolderCommand))]
    private bool _isRunning;

    /// <summary>"Audit every shared mailbox in the selected group" — default scope.</summary>
    [ObservableProperty]
    private bool _isAllInGroupScope = true;

    /// <summary>"Audit one mailbox picked from the dropdown".</summary>
    [ObservableProperty]
    private bool _isSingleMailboxScope;

    [ObservableProperty]
    private Mailbox? _selectedMailbox;

    /// <summary>Mirrors the IncludeSendAs switch in the original script. Default on.</summary>
    [ObservableProperty]
    private bool _includeSendAs = true;

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private string? _progressStatus;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResults))]
    private int _resultCount;

    public bool HasResults => ResultCount > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasExportPath))]
    [NotifyCanExecuteChangedFor(nameof(OpenExportFolderCommand))]
    private string? _lastExportPath;

    public bool HasExportPath => !string.IsNullOrEmpty(LastExportPath);

    // -----------------------------------------------------------------------
    // Commands
    // -----------------------------------------------------------------------

    [RelayCommand(CanExecute = nameof(CanRunAudit), IncludeCancelCommand = true)]
    private async Task RunAuditAsync(CancellationToken cancellationToken)
    {
        IsRunning = true;
        Progress = 0;
        ProgressStatus = "Preparing…";
        Results.Clear();
        ResultCount = 0;
        LastExportPath = null;

        var progress = new Progress<MailboxOperationProgress>(p =>
        {
            Progress = p.Fraction;
            ProgressStatus = string.IsNullOrEmpty(p.Detail)
                ? $"{p.Status} ({p.Completed}/{p.Total})"
                : $"{p.Status}: {p.Detail} ({p.Completed}/{p.Total})";
        });

        try
        {
            var upns = await ResolveMailboxUpnsAsync(progress, cancellationToken).ConfigureAwait(true);
            if (upns.Count == 0)
            {
                ProgressStatus = "No shared mailboxes resolved for the selected scope.";
                return;
            }

            ProgressStatus = "Auditing delegated permissions…";
            var results = await _mailboxService
                .AuditAsync(upns, IncludeSendAs, progress, cancellationToken)
                .ConfigureAwait(true);

            foreach (var row in results)
            {
                Results.Add(row);
            }
            ResultCount = Results.Count;

            // Auto-export to CSV — matches the original script's behavior of dropping a
            // mailbox-audit-{ts}.csv every run. The path is rendered in the footer.
            if (Results.Count > 0)
            {
                LastExportPath = await _logWriter
                    .WriteAuditAsync(results, cancellationToken)
                    .ConfigureAwait(true);
                ProgressStatus = $"Done. {Results.Count} row(s).";
            }
            else
            {
                ProgressStatus = "Done. No delegates found.";
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Audit cancelled");
            ProgressStatus = "Cancelled.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Audit failed");
            ProgressStatus = $"Failed: {ex.Message}";
        }
        finally
        {
            IsRunning = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanOpenExportFolder))]
    private void OpenExportFolder()
    {
        if (string.IsNullOrEmpty(LastExportPath)) return;

        try
        {
            // explorer.exe /select,<path> opens the folder and highlights the file.
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{LastExportPath}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Open export folder failed for {Path}", LastExportPath);
        }
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private async Task<IReadOnlyList<string>> ResolveMailboxUpnsAsync(
        IProgress<MailboxOperationProgress> progress,
        CancellationToken cancellationToken)
    {
        if (IsSingleMailboxScope)
        {
            return SelectedMailbox is null
                ? Array.Empty<string>()
                : new[] { SelectedMailbox.UserPrincipalName };
        }

        // All-in-group: pull the group members, then filter to RecipientTypeDetails = SharedMailbox.
        // This mirrors Get-SharedMailboxesOnly in the script.
        var group = _groupPicker.SelectedGroup;
        if (group is null) return Array.Empty<string>();

        var members = await _mailboxService
            .GetGroupMembersAsync(group.GroupId, cancellationToken)
            .ConfigureAwait(true);

        var shared = await _mailboxService
            .FilterSharedMailboxesAsync(
                members.Select(m => m.UserPrincipalName).ToList(),
                progress,
                cancellationToken)
            .ConfigureAwait(true);

        return shared.Select(m => m.UserPrincipalName).ToList();
    }

    private bool CanRunAudit() =>
        IsSignedIn
        && !IsRunning
        && _groupPicker.SelectedGroup is not null
        && (IsAllInGroupScope || (IsSingleMailboxScope && SelectedMailbox is not null));

    private bool CanOpenExportFolder() => HasExportPath && !IsRunning;

    // -----------------------------------------------------------------------
    // Reactive plumbing
    // -----------------------------------------------------------------------

    partial void OnIsAllInGroupScopeChanged(bool value)
    {
        if (value)
        {
            IsSingleMailboxScope = false;
        }
        RunAuditCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsSingleMailboxScopeChanged(bool value)
    {
        if (value)
        {
            IsAllInGroupScope = false;
            if (AvailableMailboxes.Count == 0 && _groupPicker.SelectedGroup is not null)
            {
                _ = LoadAvailableMailboxesAsync();
            }
        }
        RunAuditCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedMailboxChanged(Mailbox? value)
    {
        RunAuditCommand.NotifyCanExecuteChanged();
    }

    private void OnConnectionStatusChanged(object? sender, ConnectionStatus status)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            IsSignedIn = status.IsFullyConnected;
        }
        else
        {
            dispatcher.Invoke(() => IsSignedIn = status.IsFullyConnected);
        }
    }

    private void OnGroupPickerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(GroupPickerViewModel.SelectedGroup)) return;

        // A new group means previous results / mailbox list / export path are stale.
        AvailableMailboxes.Clear();
        SelectedMailbox = null;
        Results.Clear();
        ResultCount = 0;
        LastExportPath = null;
        Progress = 0;
        ProgressStatus = null;
        RunAuditCommand.NotifyCanExecuteChanged();

        // Pre-load the dropdown if the user is currently in single-mailbox mode so they
        // don't see an empty dropdown while waiting for Graph.
        if (IsSingleMailboxScope && _groupPicker.SelectedGroup is not null)
        {
            _ = LoadAvailableMailboxesAsync();
        }
    }

    private async Task LoadAvailableMailboxesAsync()
    {
        var group = _groupPicker.SelectedGroup;
        if (group is null) return;

        try
        {
            var members = await _mailboxService
                .GetGroupMembersAsync(group.GroupId)
                .ConfigureAwait(true);

            AvailableMailboxes.Clear();
            foreach (var m in members)
            {
                AvailableMailboxes.Add(m);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load mailboxes for group {Group}", group.GroupId);
            ProgressStatus = $"Could not load mailboxes: {ex.Message}";
        }
    }
}

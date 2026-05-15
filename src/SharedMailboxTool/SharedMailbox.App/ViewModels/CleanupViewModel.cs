using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using SharedMailbox.App.Services;
using SharedMailbox.Core.Domain;
using SharedMailbox.Core.Services;

namespace SharedMailbox.App.ViewModels;

/// <summary>
/// Drives the Cleanup tab. Mirrors Path 3 of the original PowerShell script
/// (Remove-BlockedDelegates), but replaces the Y/N/A/Q console prompt loop with:
///
///   1. Run audit → DataGrid of <see cref="CleanupRowViewModel"/> entries.
///   2. User checks rows individually, or clicks "Select all blocked".
///   3. "Remove selected" opens a modal listing the exact permissions about to be
///      revoked. User confirms once for the whole batch (or cancels).
///   4. On confirm, RemoveDelegatesAsync runs; CSV log is written; successfully
///      processed rows disappear from the grid.
///
/// Each tab runs its own audit independently so a stale audit can never drive a
/// destructive action — switching groups in the sidebar clears the grid.
/// </summary>
public sealed partial class CleanupViewModel : ObservableObject
{
    private readonly ISharedMailboxService _mailboxService;
    private readonly IAuditLogWriter _logWriter;
    private readonly IConnectionService _connectionService;
    private readonly GroupPickerViewModel _groupPicker;
    private readonly ICleanupConfirmationService _confirmation;
    private readonly ILogger<CleanupViewModel> _logger;

    public CleanupViewModel(
        ISharedMailboxService mailboxService,
        IAuditLogWriter logWriter,
        IConnectionService connectionService,
        GroupPickerViewModel groupPicker,
        ICleanupConfirmationService confirmation,
        ILogger<CleanupViewModel> logger)
    {
        _mailboxService = mailboxService ?? throw new ArgumentNullException(nameof(mailboxService));
        _logWriter = logWriter ?? throw new ArgumentNullException(nameof(logWriter));
        _connectionService = connectionService ?? throw new ArgumentNullException(nameof(connectionService));
        _groupPicker = groupPicker ?? throw new ArgumentNullException(nameof(groupPicker));
        _confirmation = confirmation ?? throw new ArgumentNullException(nameof(confirmation));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _connectionService.StatusChanged += OnConnectionStatusChanged;
        _groupPicker.PropertyChanged += OnGroupPickerPropertyChanged;

        IsSignedIn = _connectionService.Status.IsFullyConnected;
    }

    public ObservableCollection<Mailbox> AvailableMailboxes { get; } = new();
    public ObservableCollection<CleanupRowViewModel> Rows { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunAuditCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveSelectedCommand))]
    private bool _isSignedIn;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunAuditCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(SelectAllBlockedCommand))]
    [NotifyCanExecuteChangedFor(nameof(ClearSelectionCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenExportFolderCommand))]
    private bool _isRunning;

    [ObservableProperty] private bool _isAllInGroupScope = true;
    [ObservableProperty] private bool _isSingleMailboxScope;
    [ObservableProperty] private Mailbox? _selectedMailbox;
    [ObservableProperty] private bool _includeSendAs = true;

    [ObservableProperty] private double _progress;
    [ObservableProperty] private string? _progressStatus;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(SelectAllBlockedCommand))]
    [NotifyCanExecuteChangedFor(nameof(ClearSelectionCommand))]
    private int _selectedCount;

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
        ClearRows();
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

            foreach (var report in results)
            {
                AddRow(new CleanupRowViewModel(report));
            }

            ProgressStatus = Rows.Count == 0
                ? "Done. No delegates found."
                : $"Done. {Rows.Count} row(s). Select rows to remove, or click 'Select all blocked'.";
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Cleanup audit cancelled");
            ProgressStatus = "Cancelled.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Cleanup audit failed");
            ProgressStatus = $"Failed: {ex.Message}";
        }
        finally
        {
            IsRunning = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanSelectAllBlocked))]
    private void SelectAllBlocked()
    {
        foreach (var row in Rows)
        {
            row.IsSelected = row.Report.SignInBlocked == true;
        }
    }

    [RelayCommand(CanExecute = nameof(CanClearSelection))]
    private void ClearSelection()
    {
        foreach (var row in Rows)
        {
            row.IsSelected = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRemoveSelected))]
    private async Task RemoveSelectedAsync(CancellationToken cancellationToken)
    {
        var selectedReports = Rows
            .Where(r => r.IsSelected)
            .Select(r => r.Report)
            .ToList();

        if (selectedReports.Count == 0) return;

        // Show the confirmation modal first — runs synchronously on the UI thread.
        // No EXO call happens unless the user clicks "Remove permissions".
        if (!_confirmation.Confirm(selectedReports))
        {
            _logger.LogInformation("Cleanup cancelled at confirmation modal");
            return;
        }

        IsRunning = true;
        Progress = 0;
        ProgressStatus = "Removing delegated permissions…";

        var progress = new Progress<MailboxOperationProgress>(p =>
        {
            Progress = p.Fraction;
            ProgressStatus = string.IsNullOrEmpty(p.Detail)
                ? $"{p.Status} ({p.Completed}/{p.Total})"
                : $"{p.Status}: {p.Detail} ({p.Completed}/{p.Total})";
        });

        try
        {
            var actions = await _mailboxService
                .RemoveDelegatesAsync(selectedReports, IncludeSendAs, progress, cancellationToken)
                .ConfigureAwait(true);

            if (actions.Count > 0)
            {
                LastExportPath = await _logWriter
                    .WriteCleanupAsync(actions, cancellationToken)
                    .ConfigureAwait(true);
            }

            // Tally outcomes per (mailbox, trustee) and drop the rows whose every action succeeded.
            // Rows with any failure stay in the grid so the user can retry; the CSV log captures
            // exactly which right(s) failed and why.
            var groupedOutcomes = actions
                .GroupBy(a => (a.Mailbox, a.Trustee))
                .ToDictionary(g => g.Key, g => g.All(a => a.Result == ActionResult.Success));

            var fullySucceeded = 0;
            var anyFailed = 0;
            foreach (var report in selectedReports)
            {
                if (groupedOutcomes.TryGetValue((report.Mailbox, report.Trustee), out var allOk) && allOk)
                {
                    RemoveRow(report);
                    fullySucceeded++;
                }
                else
                {
                    anyFailed++;
                }
            }

            ProgressStatus = anyFailed == 0
                ? $"Done. Removed permissions for {fullySucceeded} trustee(s)."
                : $"Done. {fullySucceeded} succeeded, {anyFailed} had at least one failure (see cleanup CSV).";
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Cleanup cancelled mid-run");
            ProgressStatus = "Cancelled.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Cleanup failed");
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

    private void AddRow(CleanupRowViewModel row)
    {
        row.PropertyChanged += OnRowPropertyChanged;
        Rows.Add(row);
    }

    private void RemoveRow(DelegateReport report)
    {
        var row = Rows.FirstOrDefault(r =>
            r.Report.Mailbox == report.Mailbox &&
            r.Report.Trustee == report.Trustee);
        if (row is null) return;

        row.PropertyChanged -= OnRowPropertyChanged;
        Rows.Remove(row);
        SelectedCount = Rows.Count(r => r.IsSelected);
    }

    private void ClearRows()
    {
        foreach (var r in Rows) r.PropertyChanged -= OnRowPropertyChanged;
        Rows.Clear();
        SelectedCount = 0;
    }

    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CleanupRowViewModel.IsSelected))
        {
            SelectedCount = Rows.Count(r => r.IsSelected);
        }
    }

    // -----------------------------------------------------------------------
    // CanExecute predicates
    // -----------------------------------------------------------------------

    private bool CanRunAudit() =>
        IsSignedIn
        && !IsRunning
        && _groupPicker.SelectedGroup is not null
        && (IsAllInGroupScope || (IsSingleMailboxScope && SelectedMailbox is not null));

    private bool CanRemoveSelected() => IsSignedIn && !IsRunning && SelectedCount > 0;
    private bool CanSelectAllBlocked() => !IsRunning && Rows.Count > 0;
    private bool CanClearSelection() => !IsRunning && SelectedCount > 0;
    private bool CanOpenExportFolder() => HasExportPath && !IsRunning;

    // -----------------------------------------------------------------------
    // Reactive plumbing
    // -----------------------------------------------------------------------

    partial void OnIsAllInGroupScopeChanged(bool value)
    {
        if (value) IsSingleMailboxScope = false;
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

        AvailableMailboxes.Clear();
        SelectedMailbox = null;
        ClearRows();
        LastExportPath = null;
        Progress = 0;
        ProgressStatus = null;
        RunAuditCommand.NotifyCanExecuteChanged();

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

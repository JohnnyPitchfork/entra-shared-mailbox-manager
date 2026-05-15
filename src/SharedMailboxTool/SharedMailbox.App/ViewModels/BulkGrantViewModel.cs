using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using SharedMailbox.App.Services;
using SharedMailbox.Core.Domain;
using SharedMailbox.Core.Services;

namespace SharedMailbox.App.ViewModels;

/// <summary>
/// Drives the Bulk Grant tab. Mirrors Path 1 of the original PowerShell script
/// (Invoke-AddUsersToAllMailboxesInGroup), translated to a GUI:
///
///   * UPN list built by typing one at a time OR importing a CSV with a 'UPN' header.
///   * Mailbox scope picker — same pattern as Audit/Cleanup (all in group / single).
///   * GrantSendAs toggle (default on, matches the script).
///   * "Grant access" opens a confirmation modal showing exactly the user × mailbox
///     fan-out about to run; only on confirm does any Add-* cmdlet fire.
///   * Per-attempt BulkAddResult rows fill a DataGrid and auto-export to
///     SharedMail-BulkAction-{ts}.csv.
/// </summary>
public sealed partial class BulkGrantViewModel : ObservableObject
{
    private readonly ISharedMailboxService _mailboxService;
    private readonly IAuditLogWriter _logWriter;
    private readonly IUpnImportReader _upnReader;
    private readonly IConnectionService _connectionService;
    private readonly GroupPickerViewModel _groupPicker;
    private readonly IBulkGrantConfirmationService _confirmation;
    private readonly ILogger<BulkGrantViewModel> _logger;

    public BulkGrantViewModel(
        ISharedMailboxService mailboxService,
        IAuditLogWriter logWriter,
        IUpnImportReader upnReader,
        IConnectionService connectionService,
        GroupPickerViewModel groupPicker,
        IBulkGrantConfirmationService confirmation,
        ILogger<BulkGrantViewModel> logger)
    {
        _mailboxService = mailboxService ?? throw new ArgumentNullException(nameof(mailboxService));
        _logWriter = logWriter ?? throw new ArgumentNullException(nameof(logWriter));
        _upnReader = upnReader ?? throw new ArgumentNullException(nameof(upnReader));
        _connectionService = connectionService ?? throw new ArgumentNullException(nameof(connectionService));
        _groupPicker = groupPicker ?? throw new ArgumentNullException(nameof(groupPicker));
        _confirmation = confirmation ?? throw new ArgumentNullException(nameof(confirmation));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _connectionService.StatusChanged += OnConnectionStatusChanged;
        _groupPicker.PropertyChanged += OnGroupPickerPropertyChanged;
        UserUpns.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(UserCount));
            RunCommand.NotifyCanExecuteChanged();
            ClearUsersCommand.NotifyCanExecuteChanged();
        };

        IsSignedIn = _connectionService.Status.IsFullyConnected;
    }

    // -----------------------------------------------------------------------
    // Users list (left input)
    // -----------------------------------------------------------------------

    /// <summary>UPNs that will be granted access.</summary>
    public ObservableCollection<string> UserUpns { get; } = new();

    public int UserCount => UserUpns.Count;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddUpnCommand))]
    private string _newUpnText = string.Empty;

    [ObservableProperty]
    private string? _importStatus;

    [ObservableProperty]
    private string? _importError;

    // -----------------------------------------------------------------------
    // Mailbox scope (right input)
    // -----------------------------------------------------------------------

    public ObservableCollection<Mailbox> AvailableMailboxes { get; } = new();

    [ObservableProperty] private bool _isAllInGroupScope = true;
    [ObservableProperty] private bool _isSingleMailboxScope;
    [ObservableProperty] private Mailbox? _selectedMailbox;

    // -----------------------------------------------------------------------
    // Options + run state
    // -----------------------------------------------------------------------

    /// <summary>Grant SendAs in addition to FullAccess. Default on, matches the script.</summary>
    [ObservableProperty] private bool _grantSendAs = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    private bool _isSignedIn;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddUpnCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveUpnCommand))]
    [NotifyCanExecuteChangedFor(nameof(ImportCsvCommand))]
    [NotifyCanExecuteChangedFor(nameof(ClearUsersCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenExportFolderCommand))]
    private bool _isRunning;

    [ObservableProperty] private double _progress;
    [ObservableProperty] private string? _progressStatus;

    /// <summary>Per-(user × mailbox) attempt results shown in the DataGrid.</summary>
    public ObservableCollection<BulkAddResult> Results { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasExportPath))]
    [NotifyCanExecuteChangedFor(nameof(OpenExportFolderCommand))]
    private string? _lastExportPath;

    public bool HasExportPath => !string.IsNullOrEmpty(LastExportPath);

    // -----------------------------------------------------------------------
    // User-list commands
    // -----------------------------------------------------------------------

    [RelayCommand(CanExecute = nameof(CanAddUpn))]
    private void AddUpn()
    {
        var upn = NewUpnText.Trim();
        if (upn.Length == 0 || !upn.Contains('@')) return;

        if (!UserUpns.Contains(upn, StringComparer.OrdinalIgnoreCase))
        {
            UserUpns.Add(upn);
        }
        NewUpnText = string.Empty;
    }

    [RelayCommand(CanExecute = nameof(CanRemoveUpn))]
    private void RemoveUpn(string? upn)
    {
        if (string.IsNullOrEmpty(upn)) return;
        UserUpns.Remove(upn);
    }

    [RelayCommand(CanExecute = nameof(CanImportCsv))]
    private async Task ImportCsvAsync(CancellationToken cancellationToken)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select a CSV with a 'UPN' header column",
            Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog() != true) return;

        ImportError = null;
        ImportStatus = $"Reading {Path.GetFileName(dialog.FileName)}…";

        try
        {
            var imported = await _upnReader.ReadAsync(dialog.FileName, cancellationToken).ConfigureAwait(true);

            var added = 0;
            foreach (var upn in imported)
            {
                if (!UserUpns.Contains(upn, StringComparer.OrdinalIgnoreCase))
                {
                    UserUpns.Add(upn);
                    added++;
                }
            }

            ImportStatus = $"Imported {added} new UPN(s) from {Path.GetFileName(dialog.FileName)}.";
        }
        catch (UpnImportException ex)
        {
            _logger.LogWarning(ex, "UPN CSV import rejected");
            ImportError = ex.Message;
            ImportStatus = null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "UPN CSV import failed unexpectedly");
            ImportError = $"Unexpected error: {ex.Message}";
            ImportStatus = null;
        }
    }

    [RelayCommand(CanExecute = nameof(CanClearUsers))]
    private void ClearUsers()
    {
        UserUpns.Clear();
        ImportStatus = null;
        ImportError = null;
    }

    // -----------------------------------------------------------------------
    // Run / cancel / open-folder
    // -----------------------------------------------------------------------

    [RelayCommand(CanExecute = nameof(CanRun), IncludeCancelCommand = true)]
    private async Task RunAsync(CancellationToken cancellationToken)
    {
        IsRunning = true;
        Progress = 0;
        ProgressStatus = "Resolving mailboxes…";
        Results.Clear();
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
            var mailboxUpns = await ResolveMailboxUpnsAsync(progress, cancellationToken).ConfigureAwait(true);
            if (mailboxUpns.Count == 0)
            {
                ProgressStatus = "No shared mailboxes resolved for the selected scope.";
                return;
            }

            var users = UserUpns.ToList();

            // Confirmation gate — runs on UI thread (sync ShowDialog).
            if (!_confirmation.Confirm(users, mailboxUpns, GrantSendAs))
            {
                _logger.LogInformation("Bulk grant cancelled at confirmation modal");
                ProgressStatus = "Cancelled.";
                return;
            }

            ProgressStatus = "Granting permissions…";
            var results = await _mailboxService
                .AddUsersToMailboxesAsync(users, mailboxUpns, GrantSendAs, progress, cancellationToken)
                .ConfigureAwait(true);

            foreach (var r in results)
            {
                Results.Add(r);
            }

            if (results.Count > 0)
            {
                LastExportPath = await _logWriter
                    .WriteBulkAddAsync(results, cancellationToken)
                    .ConfigureAwait(true);
            }

            var failed = results.Count(r => r.AnyFailure);
            var succeeded = results.Count - failed;
            ProgressStatus = failed == 0
                ? $"Done. {succeeded} operation(s) succeeded."
                : $"Done. {succeeded} succeeded, {failed} had a failure (see bulk-action CSV).";
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Bulk grant cancelled mid-run");
            ProgressStatus = "Cancelled.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Bulk grant failed");
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

    // -----------------------------------------------------------------------
    // CanExecute predicates
    // -----------------------------------------------------------------------

    private bool CanAddUpn() =>
        !IsRunning
        && !string.IsNullOrWhiteSpace(NewUpnText)
        && NewUpnText.Trim().Contains('@')
        && !UserUpns.Contains(NewUpnText.Trim(), StringComparer.OrdinalIgnoreCase);

    private bool CanRemoveUpn(string? upn) => !IsRunning && !string.IsNullOrEmpty(upn);
    private bool CanImportCsv() => !IsRunning;
    private bool CanClearUsers() => !IsRunning && UserUpns.Count > 0;
    private bool CanOpenExportFolder() => HasExportPath && !IsRunning;

    private bool CanRun() =>
        IsSignedIn
        && !IsRunning
        && UserUpns.Count > 0
        && _groupPicker.SelectedGroup is not null
        && (IsAllInGroupScope || (IsSingleMailboxScope && SelectedMailbox is not null));

    // -----------------------------------------------------------------------
    // Reactive plumbing
    // -----------------------------------------------------------------------

    partial void OnIsAllInGroupScopeChanged(bool value)
    {
        if (value) IsSingleMailboxScope = false;
        RunCommand.NotifyCanExecuteChanged();
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
        RunCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedMailboxChanged(Mailbox? value)
    {
        RunCommand.NotifyCanExecuteChanged();
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

        // New group => previous mailbox list / results / export path are stale.
        AvailableMailboxes.Clear();
        SelectedMailbox = null;
        Results.Clear();
        LastExportPath = null;
        Progress = 0;
        ProgressStatus = null;
        RunCommand.NotifyCanExecuteChanged();

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

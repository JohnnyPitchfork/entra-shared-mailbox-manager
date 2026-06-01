using System.Management.Automation;
using System.Management.Automation.Runspaces;
using Microsoft.Extensions.Logging;

namespace SharedMailbox.PowerShell.Hosting;

/// <summary>
/// Default <see cref="IPowerShellHost"/>. Owns one Runspace for the life of the process,
/// serializes invocations behind a SemaphoreSlim, and converts cancellation tokens into
/// <see cref="PowerShell.Stop()"/> calls so long-running cmdlets (audit loops, EXO calls
/// over a slow link) can be cancelled cleanly from the UI.
/// </summary>
public sealed class PowerShellHost : IPowerShellHost
{
    /// <summary>
    /// Modules the host must import before any adapter can run. These are the same modules
    /// the original script's bootstrapping block checks for at the top of shared-mailbox-manager.ps1.
    /// </summary>
    private static readonly string[] RequiredModules =
    {
        "ExchangeOnlineManagement",
        "Microsoft.Graph.Authentication",
        "Microsoft.Graph.Users",
        "Microsoft.Graph.Groups",
    };

    private readonly ILogger<PowerShellHost> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private Runspace? _runspace;
    private bool _disposed;

    public PowerShellHost(ILogger<PowerShellHost> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public bool IsInitialized =>
        _runspace?.RunspaceStateInfo.State == RunspaceState.Opened;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsInitialized) return;

            _logger.LogInformation("Initializing PowerShell runspace");

            var iss = InitialSessionState.CreateDefault2();
            // Bypass execution policy for this in-process runspace only. We never load
            // arbitrary user scripts — only the literal strings in our adapter classes.
            iss.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;
            iss.ImportPSModule(RequiredModules);

            var rs = RunspaceFactory.CreateRunspace(iss);
            rs.Open();
            _runspace = rs;

            await VerifyModulesLoadedAsync(cancellationToken).ConfigureAwait(false);

            _logger.LogInformation("PowerShell runspace initialized");
        }
        catch
        {
            // Failed init — make sure we don't leak the runspace.
            if (_runspace is { } rs)
            {
                try { rs.Dispose(); } catch { /* swallow */ }
                _runspace = null;
            }
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<PSObject>> InvokeAsync(
        string script,
        IReadOnlyDictionary<string, object?>? parameters = null,
        IProgress<PowerShellStreamEvent>? streams = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(script);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!IsInitialized)
            await InitializeAsync(cancellationToken).ConfigureAwait(false);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var ps = System.Management.Automation.PowerShell.Create();
            ps.Runspace = _runspace;

            ps.AddScript(BuildScript(script, parameters));
            if (parameters is { Count: > 0 })
            {
                foreach (var (key, value) in parameters)
                {
                    ps.AddParameter(key, value);
                }
            }

            if (streams is not null) WireStreamEvents(ps, streams);

            await using var ctr = cancellationToken.Register(() =>
            {
                try { ps.Stop(); }
                catch (Exception ex) { _logger.LogDebug(ex, "Stopping PowerShell pipeline failed"); }
            });

            PSDataCollection<PSObject> output;
            try
            {
                output = await Task.Factory.FromAsync(
                    ps.BeginInvoke(),
                    ps.EndInvoke).ConfigureAwait(false);
            }
            catch (PipelineStoppedException) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var errors = ps.Streams.Error.Select(e => e.ToString()).ToList();
                throw new PowerShellInvocationException(
                    $"PowerShell invocation failed: {ex.Message}", errors, ex);
            }

            if (ps.HadErrors)
            {
                var errors = ps.Streams.Error.Select(e => e.ToString()).ToList();
                throw new PowerShellInvocationException(
                    "PowerShell invocation produced one or more errors: " + string.Join("; ", errors),
                    errors);
            }

            return output.ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    // -----------------------------------------------------------------------
    // Internals
    // -----------------------------------------------------------------------

    /// <summary>
    /// Auto-generates a <c>param(...)</c> block from the parameter dictionary keys.
    /// This is the safe alternative to string-interpolating values into the script body
    /// (which would be an injection risk and a binding-coercion nightmare).
    /// </summary>
    private static string BuildScript(string body, IReadOnlyDictionary<string, object?>? parameters)
    {
        if (parameters is null || parameters.Count == 0) return body;

        var paramDecl = string.Join(", ", parameters.Keys.Select(k => "$" + k));
        return $"param({paramDecl})\n{body}";
    }

    private static void WireStreamEvents(
        System.Management.Automation.PowerShell ps,
        IProgress<PowerShellStreamEvent> sink)
    {
        ps.Streams.Verbose.DataAdded += (s, e) =>
        {
            var item = ((PSDataCollection<VerboseRecord>)s!)[e.Index];
            sink.Report(new PowerShellStreamEvent(PowerShellStreamKind.Verbose, item.Message));
        };
        ps.Streams.Warning.DataAdded += (s, e) =>
        {
            var item = ((PSDataCollection<WarningRecord>)s!)[e.Index];
            sink.Report(new PowerShellStreamEvent(PowerShellStreamKind.Warning, item.Message));
        };
        ps.Streams.Information.DataAdded += (s, e) =>
        {
            var item = ((PSDataCollection<InformationRecord>)s!)[e.Index];
            sink.Report(new PowerShellStreamEvent(
                PowerShellStreamKind.Information,
                item.MessageData?.ToString() ?? string.Empty));
        };
        ps.Streams.Error.DataAdded += (s, e) =>
        {
            var item = ((PSDataCollection<ErrorRecord>)s!)[e.Index];
            sink.Report(new PowerShellStreamEvent(PowerShellStreamKind.Error, item.ToString()));
        };
        ps.Streams.Debug.DataAdded += (s, e) =>
        {
            var item = ((PSDataCollection<DebugRecord>)s!)[e.Index];
            sink.Report(new PowerShellStreamEvent(PowerShellStreamKind.Debug, item.Message));
        };
        ps.Streams.Progress.DataAdded += (s, e) =>
        {
            var item = ((PSDataCollection<ProgressRecord>)s!)[e.Index];
            sink.Report(new PowerShellStreamEvent(
                PowerShellStreamKind.Progress,
                $"{item.Activity} - {item.StatusDescription} ({item.PercentComplete}%)"));
        };
    }

    private async Task VerifyModulesLoadedAsync(CancellationToken cancellationToken)
    {
        using var ps = System.Management.Automation.PowerShell.Create();
        ps.Runspace = _runspace;
        ps.AddScript(@"
            param($Modules)
            $missing = @()
            foreach ($m in $Modules) {
                if (-not (Get-Module -ListAvailable -Name $m)) { $missing += $m }
            }
            ,$missing
        ");
        ps.AddParameter("Modules", RequiredModules);

        await using var ctr = cancellationToken.Register(() =>
        {
            try { ps.Stop(); } catch { /* swallow */ }
        });

        PSDataCollection<PSObject> output;
        try
        {
            output = await Task.Factory.FromAsync(ps.BeginInvoke(), ps.EndInvoke).ConfigureAwait(false);
        }
        catch (PipelineStoppedException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        // Output is a single PSObject wrapping the missing[] array.
        var missing = new List<string>();
        foreach (var obj in output)
        {
            if (obj?.BaseObject is System.Collections.IEnumerable list)
            {
                foreach (var name in list)
                {
                    if (name is not null) missing.Add(name.ToString()!);
                }
            }
            else if (obj?.BaseObject is string s)
            {
                missing.Add(s);
            }
        }

        if (missing.Count > 0)
        {
            throw new PowerShellInvocationException(
                "Required PowerShell modules are not installed: " +
                string.Join(", ", missing) + ". From an elevated PowerShell prompt, run: " +
                "Install-Module ExchangeOnlineManagement, Microsoft.Graph -Scope CurrentUser -Force",
                missing);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_runspace is not null)
            {
                try
                {
                    _runspace.Close();
                    _runspace.Dispose();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error disposing PowerShell runspace");
                }
                _runspace = null;
            }
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }
}

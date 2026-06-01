using System.Management.Automation;

namespace SharedMailbox.PowerShell.Hosting;

/// <summary>
/// Owns a long-lived PowerShell Runspace pre-loaded with ExchangeOnlineManagement and
/// Microsoft.Graph.* modules. Adapters in this assembly invoke cmdlets through this
/// interface so they don't have to manage runspace lifecycle themselves.
///
/// Thread-safety: invocations are serialized internally via a SemaphoreSlim. A caller
/// may issue concurrent <see cref="InvokeAsync"/> calls and they will queue, not interleave.
/// A Runspace can only run one pipeline at a time, so serialization is required, not optional.
/// </summary>
public interface IPowerShellHost : IAsyncDisposable
{
    /// <summary>True once the runspace has been opened and required modules imported.</summary>
    bool IsInitialized { get; }

    /// <summary>
    /// Opens the runspace and imports ExchangeOnlineManagement + Microsoft.Graph.*. Idempotent:
    /// subsequent calls return immediately. Throws <see cref="PowerShellInvocationException"/>
    /// if a required module is not installed on the host machine.
    /// </summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Invoke a PowerShell script and return its pipeline output.
    ///
    /// The script must NOT include its own <c>param(...)</c> block — the host generates one
    /// from the keys of <paramref name="parameters"/>. Inside the script, reference each parameter
    /// by its dictionary key prefixed with <c>$</c> (e.g., dictionary key <c>"Identity"</c> →
    /// reference <c>$Identity</c>).
    ///
    /// Non-terminating errors written to the error stream cause this method to throw
    /// <see cref="PowerShellInvocationException"/>. Warning / verbose / information messages
    /// are forwarded to <paramref name="streams"/> if supplied.
    /// </summary>
    Task<IReadOnlyList<PSObject>> InvokeAsync(
        string script,
        IReadOnlyDictionary<string, object?>? parameters = null,
        IProgress<PowerShellStreamEvent>? streams = null,
        CancellationToken cancellationToken = default);
}

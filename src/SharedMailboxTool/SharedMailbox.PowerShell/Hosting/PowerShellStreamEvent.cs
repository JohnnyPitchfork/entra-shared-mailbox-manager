namespace SharedMailbox.PowerShell.Hosting;

/// <summary>
/// One message from a PowerShell pipeline stream (verbose / warning / error / ...)
/// emitted while a script is running. Subscribed to via
/// <see cref="IProgress{PowerShellStreamEvent}"/> on <c>InvokeAsync</c>.
///
/// The original PowerShell wrote these to the console with Write-Verbose / Write-Host;
/// the GUI version surfaces them in a "Log" pane so users can see the same diagnostics.
/// </summary>
public sealed record PowerShellStreamEvent(PowerShellStreamKind Kind, string Message)
{
    public override string ToString() => $"[{Kind}] {Message}";
}

public enum PowerShellStreamKind
{
    Verbose,
    Information,
    Warning,
    Error,
    Debug,
    Progress,
}

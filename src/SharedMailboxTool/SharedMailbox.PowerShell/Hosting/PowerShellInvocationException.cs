namespace SharedMailbox.PowerShell.Hosting;

/// <summary>
/// Thrown by <see cref="IPowerShellHost"/> when a script invocation either threw a
/// terminating exception or produced one or more non-terminating ErrorRecord entries.
///
/// <see cref="ErrorRecords"/> contains the stringified ErrorRecord values from the
/// pipeline's error stream, exactly as PowerShell would have displayed them.
/// </summary>
public sealed class PowerShellInvocationException : Exception
{
    public IReadOnlyList<string> ErrorRecords { get; }

    public PowerShellInvocationException(string message, IReadOnlyList<string> errorRecords)
        : base(message)
    {
        ErrorRecords = errorRecords;
    }

    public PowerShellInvocationException(string message, IReadOnlyList<string> errorRecords, Exception inner)
        : base(message, inner)
    {
        ErrorRecords = errorRecords;
    }
}

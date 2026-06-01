namespace SharedMailbox.Core.Domain;

/// <summary>
/// One row per attempted permission removal during the cleanup workflow.
/// Mirrors the PSCustomObject appended to $actionsTaken in Remove-BlockedDelegates.
/// </summary>
public sealed record CleanupAction(
    string Mailbox,
    string Trustee,
    AccessRight Right,
    ActionResult Result,
    string? Notes);

public enum ActionResult
{
    Success,
    Failed,
    Skipped,
}

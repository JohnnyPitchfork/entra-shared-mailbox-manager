# Legacy — `shared-mailbox-manager.ps1`

> This folder preserves the **v1 PowerShell script** that the modern WPF application is replacing. It is kept in the repository as a historical reference and as the design source-of-truth for the v2 feature set.

> [!NOTE]
> **Redacted for public release.** The hardcoded `$groupOptions` array near the bottom of the script originally contained five real Microsoft 365 security group display names and object IDs. Those values have been replaced with neutral placeholders (`SharedMail-GroupOne` … `SharedMail-GroupFive` and zero-padded GUIDs). If you want to run this legacy script in your own tenant, swap the placeholders for your real group names and IDs — or just choose the "Enter a Group Object ID manually" option at the prompt.

## Why this file exists in the repo

I built `shared-mailbox-manager.ps1` and maintained it for roughly a year as an internal utility for managing Microsoft 365 shared mailbox delegations at scale. It started life as a single-purpose script — *"add this user to every mailbox in this SharedMail- security group"* — and grew, one operation at a time, into a three-mode console tool covering bulk grants, delegate audits, and blocked-user cleanup.

The v2 product in the parent repository (`entra-shared-mailbox-manager`) is a ground-up reimplementation in C# / WPF with a real security model, real RBAC, and a real deployment story. But the *behaviour* of v2 is anchored to what v1 did in production, so this script is the canonical reference for the operations the new tool must support.

I've also kept it because looking at the gap between v1 and v2 is, frankly, the point — it shows the arc from *"script that solves my problem today"* to *"product that other administrators can deploy in their tenant."* That arc is the story this repository tells.

## What the script does

Three operations, all gated by a SharedMail- security group selected at launch:

1. **Bulk grant.** Add a single user, or a CSV of UPNs (`UPN` header column), to every shared mailbox that is a member of the selected group. Grants both `FullAccess` and `SendAs`. Skips permissions already present. Writes a per-user-per-mailbox result log to `./Logs/SharedMail-BulkAction-<timestamp>.csv`.

2. **Audit.** Enumerate every non-inherited delegate (`FullAccess`, `SendAs`, `SendOnBehalf`) across one mailbox or all mailboxes in the group. Cross-references each delegate against Microsoft Graph to determine `accountEnabled`, flagging sign-in-blocked accounts (a common cause of orphaned permissions after offboarding). `SendAs` scanning is optional per run. Exports to `./Logs/mailbox-audit-<timestamp>.csv`.

3. **Cleanup.** Same audit pass, but interactively prompts the operator (`Y` / `N` / `A` / `Q`) to remove permissions for blocked users — one permission type at a time, with structured success/failure logging to `./Logs/mailbox-cleanup-<timestamp>.csv`.

Across all three modes the script caches Graph user lookups in-process to keep API calls reasonable on tenants with hundreds of shared mailboxes.

## Dependencies

- PowerShell 7.x
- [`ExchangeOnlineManagement`](https://learn.microsoft.com/en-us/powershell/exchange/exchange-online-powershell-v3) (modern REST-based connection — no WinRM, no Basic auth)
- [`Microsoft.Graph`](https://learn.microsoft.com/en-us/powershell/microsoftgraph/installation) — specifically the modules backing `Connect-MgGraph`, `Get-MgGroupMember`, `Get-MgUser`

Delegated Graph scopes requested at connect time: `Group.Read.All`, `User.Read.All`. Exchange Online connects via `Connect-ExchangeOnline` (interactive, in the operator's own context).

## How to run

```powershell
# From a directory that contains the script
.\shared-mailbox-manager.ps1

# Or with verbose output
.\shared-mailbox-manager.ps1 -Verbose
```

The script prompts for menu choices interactively. The bulk-grant CSV path can be entered as either a filename (relative to the current directory) or a full path. The required CSV format is a single column with the header `UPN`; a one-line template is included alongside this README as `UserUPN.csv`.

## Known limitations (carried over from script comments)

These are the issues I had open against v1 at the time the rewrite began. Both are explicitly resolved in v2:

1. **CSV logs misreport when a UPN does not exist in the tenant.** The bulk-grant log currently records "FullAccess granted" / "SendAs granted" rows even when the upstream Exchange call failed because the trustee UPN could not be resolved. The console output correctly shows the warning, but the exported log does not. Cause: log rows are written before the cmdlet's return value is interrogated. The fix is structural — pre-validate every UPN in the CSV against Graph before any mutation happens.

2. **No pre-flight validation of bulk import.** Related to (1): if a CSV of 50 users contains one bad UPN, the script processes the first 49 successfully and then partially fails on the 50th across many mailboxes, leaving a half-applied state. A proper pre-flight pass (resolve every UPN, surface unknowns, ask the operator to confirm) was on the v1 backlog but never landed.

Additional limitations not in the script's bug list but worth naming:

- The list of known SharedMail- security groups is **hardcoded** in `$groupOptions` as an array of `@{Name; Id}` hashtables. This is the most explicitly org-coupled part of the script and one of the central motivations for the v2 configuration model (per-deployment JSON, with a centrally-hosted option for live updates across all installs).
- All operations run in the operator's own delegated Exchange context. The script does not enforce any authorization itself — it relies on whatever permissions the running user happens to have at the tenant level. v2 introduces real Exchange RBAC management scopes so that team managers can be granted least-privilege control over only their own team's mailboxes.
- No structured logging beyond CSV export. v2 adds Serilog with a rolling file sink and JSON output alongside the existing per-action CSVs.

## What comes next

The successor product lives in the parent of this folder. The design rationale, security model, configuration architecture, and deployment patterns are documented in [`../docs/Architecture.md`](../docs/Architecture.md). The top-level [`../README.md`](../README.md) covers what the tool is, who it's for, and how to deploy it.

If you've found this folder before the v2 product is ready, the script above is fully functional and self-contained — it does what it does, with the caveats above.

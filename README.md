# entra-shared-mailbox-manager

> A least-privilege delegation toolkit for Microsoft 365 shared mailboxes. Replaces Admin Center workflows with role-mapped self-service: team managers get scoped Exchange RBAC over their team's mailboxes via Entra group membership, with bulk operations, delegate audits, and role-to-scope filtering. WPF / .NET 8, Intune-deployable.

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-8.0-512BD4.svg)](https://dotnet.microsoft.com/en-us/download/dotnet/8.0)
[![Platform: Windows](https://img.shields.io/badge/Platform-Windows-0078D6.svg)]()
[![Status: v1.0 pre-release](https://img.shields.io/badge/Status-v1.0%20pre--release-orange.svg)]()

> [!IMPORTANT]
> **The v1.0 codebase is in place; the first release artifact is in preparation.** Source code, tests, configuration loader, MSAL authentication, the three core flows (audit / cleanup / bulk grant), and role-to-scope filtering are all implemented. MSIX packaging and the first GitHub Release are the remaining work between here and a publish-able v1.0. See [`docs/Roadmap.md`](docs/Roadmap.md) for the version-by-version delivery plan and [`docs/Setup.md`](docs/Setup.md) for the v1.0 deployment runbook. The v1 PowerShell script that this tool replaces is functional and lives in [`legacy/`](legacy/).

---

## What this is

Microsoft 365 shared mailboxes are easy to create and miserable to administer at scale. The Exchange admin center supports only one-mailbox-at-a-time changes, offers no way to safely delegate mailbox-permission management to non-admin team leads, and leaves orphaned permissions behind whenever a user is offboarded. In a tenant with dozens of shared mailboxes and hundreds of trustees, this turns into a real operational burden.

`entra-shared-mailbox-manager` is a Windows desktop tool that solves all three problems by combining a **bulk-operations UI** with a **real least-privilege delegation model**. A Project Manager can be authorized to administer only the shared mailboxes that belong to their team — enforced by Exchange Online's own RBAC, not by the tool — and can add users, audit delegates, and clean up blocked accounts without ever touching Admin Center and without holding any tenant-wide admin role.

The tool is **org-agnostic**. The same compiled MSIX works in any Microsoft 365 tenant given a configuration drop. Tenant data, group IDs, and role mappings are loaded from a configuration file the deploying admin produces — never baked into the code.

## Key features

- **Bulk grant** of `FullAccess` and `SendAs` to one or more users across one, several, or all shared mailboxes in a target Entra security group, with a mandatory confirmation modal listing every (user × mailbox) operation before any cmdlet fires.
- **Delegate auditing** that resolves every trustee against Microsoft Graph and flags accounts whose Entra sign-in is blocked — the most common source of orphaned mailbox permissions.
- **Cleanup of blocked delegates** through a DataGrid with row-level checkboxes, a "Select all blocked" shortcut, and a confirmation modal listing the exact permissions about to be revoked. Cancelling at the modal performs no writes.
- **Role-to-scope filtering.** Each operator's sidebar shows only the SharedMail- groups their roles permit. A Project Manager sees their team's mailboxes, an Ops lead sees theirs, neither sees the other's. Mappings live in `appsettings.json` and follow the dual-layer security model described in [`docs/Architecture.md`](docs/Architecture.md).
- **No service principal with standing privileges.** Every operation runs in the operator's own delegated context via MSAL. A bug in the tool cannot give a user more access than they would have manually.
- **CSV audit trail** matching the legacy script byte-for-byte — `mailbox-audit-{ts}.csv`, `mailbox-cleanup-{ts}.csv`, and `SharedMail-BulkAction-{ts}.csv` — plus a daily-rolling Serilog text file. The tool emits no telemetry.
- **Centrally-managed configuration** and **structured per-action JSON receipts** are roadmapped enhancements (v2.0 and v1.1 respectively); see [`docs/Roadmap.md`](docs/Roadmap.md).

## Screenshots

> *(Coming once the WPF UI is built. The first screenshot will be the main mailbox-picker view with role-filtered scope; the second will be the bulk-operation preview pane.)*

## Security model — at a glance

Authorization is enforced in two layers:

1. **Platform-enforced (Exchange Online RBAC).** Each delegated role (`ROLE-ProjectManager`, `ROLE-Helpdesk`, etc.) is an Entra security group whose members hold a custom Exchange role group bound to a management scope. The scope restricts the role to a defined subset of shared mailboxes — typically by `CustomAttribute1` value. Exchange Online itself refuses any operation outside that scope.

2. **Tool-side UX filtering.** The app reads the same role-to-scope mapping from its configuration and uses it to drive what the user sees. A team manager opening the tool sees only their team's mailboxes — not an empty list with permission errors, and not every mailbox in the tenant.

**Layer 1 is the security boundary. Layer 2 is the user experience.** If the tool's authorization filtering were bypassed, the platform would still refuse the operation. This is the design property that distinguishes a productivity tool from a security tool, and it is the design property the architecture has been built around.

The complete reasoning, the Exchange RBAC setup, the authentication flow, and the configuration model are all in [`docs/Architecture.md`](docs/Architecture.md).

## Deployment patterns

| Pattern | Who it's for | Setup time | Config updates | v1.0 status |
|---|---|---|---|---|
| **C — Solo / manual install** | Individual admins, evaluation, contributors | ~10 min | Local JSON edit | **Supported** |
| **B — Intune, static config** | Enterprise, fleet rollout | ~30 min, one-time | Redeploy via Intune | **Supported** |
| **A — Intune + Central SharePoint** | Enterprise with live-edit role mapping | ~30 min, one-time | Edit JSON in SharePoint, no redeployment | v2.0 (planned) |

v1.0 supports Patterns B and C. Pattern A — and the **Config Builder** companion app that automates the tenant-side setup for all three patterns — is planned for v2.0. Full step-by-step instructions for v1.0 are in [`docs/Setup.md`](docs/Setup.md).

## Quickstart for evaluators

> *(MSIX artifact will be attached to the first GitHub Release once packaging is finalized — see [`docs/Roadmap.md`](docs/Roadmap.md). Until then, build from source per [`docs/Setup.md`](docs/Setup.md) §9. The [`legacy/`](legacy/) folder contains the v1 PowerShell script for comparison.)*

The flow:

```text
1. Download the latest SharedMailbox.Package_*.msix from the GitHub Releases
   page, or build from source.
2. Install the MSIX (double-click, or Add-AppxPackage on Windows 10/11).
3. Drop a tenant-specific appsettings.json into
   %LOCALAPPDATA%\entra-shared-mailbox-manager\ — minimum required values
   are TenantId, ClientId, and at least one entry in KnownGroups.
4. Run scripts/Install-Prerequisites.ps1 to install the ExchangeOnlineManagement
   and Microsoft.Graph PowerShell modules.
5. Launch the app. Sign in with your Entra account.
```

## Quickstart for administrators (deploy to a team)

> *(Full procedure in [`docs/Setup.md`](docs/Setup.md).)*

Pattern B high-level steps:

```text
1. Create an Entra app registration (Setup.md §3) and grant admin consent for
   Group.Read.All, User.Read.All, and Exchange.Manage delegated scopes.
2. Assign Exchange Recipient Administrator to your operators (Setup.md §4).
3. (Optional but recommended) Configure Exchange RBAC management scopes for
   Layer 1 platform-enforced security (Setup.md §5).
4. Define role-to-scope mapping in appsettings.json for Layer 2 UX-side
   filtering (Setup.md §7.2).
5. Deploy the MSIX as a line-of-business app via Intune.
6. Deploy a small PowerShell script via Intune that drops the tenant
   appsettings.json onto each device.
7. Deploy scripts/Install-Prerequisites.ps1 via Intune to install the
   required PowerShell modules.
```

After step 7, ongoing role-mapping changes in v1.0 require updating the deployment script and reassigning in Intune. The live-edit-in-SharePoint workflow is a v2.0 enhancement.

## Repository structure

```text
entra-shared-mailbox-manager/
├── docs/
│   ├── Roadmap.md            Version-by-version delivery plan. The
│   │                         canonical reference for "what's in v1.0"
│   │                         versus "what's deferred."
│   ├── Architecture.md       Design source-of-truth: security model,
│   │                         config architecture, deployment patterns,
│   │                         component layout, references.
│   └── Setup.md              v1.0 admin deployment runbook (Patterns B
│                             and C, RBAC setup, troubleshooting, uninstall).
├── legacy/
│   ├── shared-mailbox-manager.ps1   The v1 PowerShell script this product
│   │                                replaces (preserved for historical
│   │                                reference; tenant data redacted).
│   ├── UserUPN.csv                  CSV template for the v1 bulk-grant flow.
│   └── README.md                    Origin story and v1 capability summary.
├── src/                      Visual Studio solution (SharedMailboxTool.sln):
│   ├── SharedMailbox.Core           Domain types, service interfaces, CSV
│   │                                writer, UPN reader. No external deps.
│   ├── SharedMailbox.PowerShell     EXO + Graph adapter (hosted PS runspace).
│   ├── SharedMailbox.App            WPF main application (MSAL + WPF-UI +
│   │                                CommunityToolkit.Mvvm).
│   ├── SharedMailbox.Tests          xUnit test suite (~75 tests).
│   ├── SharedMailbox.Package        MSIX packaging project (.wapproj) —
│   │                                produces the signed Intune-deployable .msix.
│   └── SharedMailbox.ConfigBuilder  (v2.0) WPF companion app for tenant
│                                    setup automation.
├── scripts/
│   └── Install-Prerequisites.ps1    PowerShell module installer. Idempotent.
│                                    Intune-deployable.
├── deployment/               (v1.0 in progress) MSIX packaging and Intune
│                             assets — added in the MSIX packaging batch.
├── .gitignore
├── LICENSE
└── README.md                 (this file)
```

## From PowerShell script to product

The [`legacy/`](legacy/) folder preserves the v1 PowerShell script that I built and maintained for roughly a year before starting this rewrite. It is functional and documents — by way of its own structure — every operation the v2 product must support. The gap between v1 and v2 is, by intent, the story this repository tells: from a single-purpose script that solved one team's problem to a productized tool that any administrator can deploy in their tenant. The script is preserved for historical reference and for any administrator who would prefer to run the v1 flow today, with their own group IDs swapped in.

The two structural v1 bugs (CSV log misreporting and lack of pre-flight bulk validation) and the three structural v1 limits (hardcoded tenant data, no tool-side authorization, no structured logging) are all explicitly addressed in the v2 design. See section 12 of [`docs/Architecture.md`](docs/Architecture.md) for the fix mechanisms.

## Roadmap

See [`docs/Roadmap.md`](docs/Roadmap.md) for the canonical version-by-version delivery plan. At a glance:

- **v1.0** — legacy parity (audit / cleanup / bulk grant), MSAL authentication, role-to-scope filtering (Layer 2 of the security model), MSIX packaging, Patterns B and C. *In progress; this is the next release.*
- **v1.1** — first-run wizard, operation receipts directory, GitHub Actions CI, system-theme detection, app-layer tests.
- **v1.2** — tenant setup automation (`Setup-EntraApp.ps1`, `Setup-ExchangeRBAC.ps1`), in-app logs viewer, CustomAttribute1 tag-editing UI.
- **v2.0** — the full Architecture.md design: SharePoint central configuration, Config Builder companion app, live config reload (Pattern A becomes available).
- **v2.x** — calendar permissions, distribution / M365 group permissions, scheduled audits, additional config backends, signed-by-default MSIX.

Items deliberately out of scope are listed in [`docs/Architecture.md`](docs/Architecture.md#13-out-of-scope-and-future-work) and [`docs/Roadmap.md`](docs/Roadmap.md).

## Contributing

The v1.0 codebase is in place and the test suite passes. Useful contributions:

- Reviewing [`docs/Architecture.md`](docs/Architecture.md), [`docs/Setup.md`](docs/Setup.md), or [`docs/Roadmap.md`](docs/Roadmap.md) and opening issues for design questions, gaps, alternative approaches, or doc bugs.
- Building from source per [`docs/Setup.md`](docs/Setup.md) §9, testing in your own tenant, and reporting issues against the v1.0 implementation.
- Proposing concrete improvements to the security model, configuration model, deployment patterns, or per-version scope in the Roadmap.

A `CONTRIBUTING.md` with developer-setup specifics is on the v1.0 hygiene-pass list.

## License

MIT — see [LICENSE](LICENSE).

This is a personal project, built and maintained by Jon Campbell ([@JohnnyPitchfork](https://github.com/JohnnyPitchfork)). It is not affiliated with Microsoft, with any prior or current employer, or with any organization whose mailboxes it is intended to administer.

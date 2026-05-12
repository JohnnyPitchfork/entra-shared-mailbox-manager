# entra-shared-mailbox-manager

> A least-privilege delegation toolkit for Microsoft 365 shared mailboxes. Replaces Admin Center workflows with role-mapped self-service: team managers get scoped Exchange RBAC over their team's mailboxes via Entra group membership, with bulk operations, delegate audits, and SharePoint-hosted config. WPF / .NET 8, Intune-deployable.

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-8.0-512BD4.svg)](https://dotnet.microsoft.com/en-us/download/dotnet/8.0)
[![Platform: Windows](https://img.shields.io/badge/Platform-Windows-0078D6.svg)]()
[![Status: design phase](https://img.shields.io/badge/Status-design%20phase-orange.svg)]()

> [!IMPORTANT]
> **This repository is in the design phase.** The architecture and security model are documented and stable (see [`docs/Architecture.md`](docs/Architecture.md)). The v1 PowerShell script that this tool is replacing is functional and lives in [`legacy/`](legacy/). The v2 WPF application is under active development — no compiled binaries are available yet. If you are evaluating this for production use, watch the repo and check back when a release is published.

---

## What this is

Microsoft 365 shared mailboxes are easy to create and miserable to administer at scale. The Exchange admin center supports only one-mailbox-at-a-time changes, offers no way to safely delegate mailbox-permission management to non-admin team leads, and leaves orphaned permissions behind whenever a user is offboarded. In a tenant with dozens of shared mailboxes and hundreds of trustees, this turns into a real operational burden.

`entra-shared-mailbox-manager` is a Windows desktop tool that solves all three problems by combining a **bulk-operations UI** with a **real least-privilege delegation model**. A Project Manager can be authorized to administer only the shared mailboxes that belong to their team — enforced by Exchange Online's own RBAC, not by the tool — and can add users, audit delegates, and clean up blocked accounts without ever touching Admin Center and without holding any tenant-wide admin role.

The tool is **org-agnostic**. The same compiled MSIX works in any Microsoft 365 tenant given a configuration drop. Tenant data, group IDs, and role mappings are loaded from a configuration file the deploying admin produces — never baked into the code.

## Key features

- **Bulk grant and bulk revoke** of `FullAccess`, `SendAs`, and `SendOnBehalf` across all shared mailboxes in a target Entra security group, with a mandatory preview pane before any mutation.
- **Delegate auditing** that resolves every trustee against Microsoft Graph and flags accounts whose Entra sign-in is blocked — the most common source of orphaned mailbox permissions.
- **Interactive cleanup** of blocked delegates with `Y` / `N` / `Approve-all` / `Quit` semantics per trustee, structured per-action logging, and dry-run support.
- **Delegated administration** for non-admin team leads, enforced by Exchange Online RBAC management scopes. A Project Manager can administer PM mailboxes; an Operations lead can administer Ops mailboxes; neither can touch the other's, and neither holds a tenant-wide admin role.
- **Centrally-managed configuration** via a JSON file hosted in SharePoint Online. Admins edit role mappings in one place; every running instance picks up changes on next launch — no Intune redeployment for config changes.
- **Three deployment patterns from one binary.** Intune-managed with central config, Intune-managed with static config, or solo install with a first-run wizard. Same MSIX. Same code path.
- **No service principal with standing privileges.** Every operation runs in the operator's own delegated context. A bug in the tool cannot give a user more access than they would have manually.
- **Local audit trail.** CSV exports for human inspection, structured Serilog output for SIEM ingestion, and per-action JSON receipts retained for 90 days. The tool emits no telemetry.

## Screenshots

> *(Coming once the WPF UI is built. The first screenshot will be the main mailbox-picker view with role-filtered scope; the second will be the bulk-operation preview pane.)*

## Security model — at a glance

Authorization is enforced in two layers:

1. **Platform-enforced (Exchange Online RBAC).** Each delegated role (`ROLE-ProjectManager`, `ROLE-Helpdesk`, etc.) is an Entra security group whose members hold a custom Exchange role group bound to a management scope. The scope restricts the role to a defined subset of shared mailboxes — typically by `CustomAttribute1` value. Exchange Online itself refuses any operation outside that scope.

2. **Tool-side UX filtering.** The app reads the same role-to-scope mapping from its configuration and uses it to drive what the user sees. A team manager opening the tool sees only their team's mailboxes — not an empty list with permission errors, and not every mailbox in the tenant.

**Layer 1 is the security boundary. Layer 2 is the user experience.** If the tool's authorization filtering were bypassed, the platform would still refuse the operation. This is the design property that distinguishes a productivity tool from a security tool, and it is the design property the architecture has been built around.

The complete reasoning, the Exchange RBAC setup, the authentication flow, and the configuration model are all in [`docs/Architecture.md`](docs/Architecture.md).

## Deployment patterns

| Pattern | Who it's for | Setup time | Config updates |
|---|---|---|---|
| **A — Intune + Central SharePoint** | Enterprise, hundreds–thousands of users | ~30 min, one-time | Edit JSON in SharePoint, no redeployment |
| **B — Intune, static config** | Enterprise without SharePoint hosting | ~20 min, one-time | Redeploy via Intune |
| **C — Solo / manual install** | Individual admins, evaluation, contributors | ~5 min | First-run wizard or local JSON edit |

All three patterns are produced from the same artifacts. The deploying administrator runs the **Config Builder** companion app once per tenant, picks a pattern, and receives a tenant-specific deployment kit (PowerShell scripts, JSON configs, and a Markdown walkthrough). Full step-by-step instructions are in [`docs/Setup.md`](docs/Setup.md).

## Quickstart for evaluators

> *(Available once the first MSIX is published to GitHub Releases. Until then, the [`legacy/`](legacy/) folder contains the v1 PowerShell script, which is functional in any M365 tenant after replacing the placeholder group IDs with real ones.)*

The intended flow once releases are available:

```text
1. Download the latest SharedMailboxTool.msix and SharedMailboxTool.ConfigBuilder.exe
   from the GitHub Releases page.
2. Run ConfigBuilder.exe. The wizard walks you through Entra app registration,
   role-to-scope mapping, and emits a deployment kit.
3. Install the MSIX (Add-AppxPackage on Windows 10/11) and run the deployment
   kit's Deploy-AppConfig.ps1 to drop the bootstrap config.
4. Launch the app. Sign in with your Entra account. The mailbox picker will
   be scoped to the roles you hold.
```

## Quickstart for administrators (deploy to a team)

> *(Available once the first release is published. The full step-by-step procedure is in [`docs/Setup.md`](docs/Setup.md).)*

Anticipated high-level steps for Pattern A:

```text
1. Create an Entra app registration (the Config Builder generates a helper script).
2. Create an Entra security group per delegated role (e.g., ROLE-ProjectManager).
3. Create Exchange RBAC management scopes and role groups per delegated role
   (the Config Builder generates Setup-ExchangeRBAC.ps1 from a JSON definition).
4. Tag shared mailboxes with CustomAttribute1 to match their scope.
5. Upload the central config JSON to a SharePoint document library.
6. Deploy the MSIX to target devices via Intune.
7. Deploy the bootstrap Deploy-AppConfig.ps1 via Intune device scripts.
```

After step 7, ongoing role-mapping changes are made by editing the SharePoint JSON. Every installed instance picks up the changes on next launch.

## Repository structure

```text
entra-shared-mailbox-manager/
├── docs/
│   ├── Architecture.md       Design source-of-truth: security model,
│   │                         config architecture, deployment patterns,
│   │                         component layout, references.
│   └── Setup.md              Admin deployment runbook (Patterns A/B/C,
│                             RBAC setup, troubleshooting, uninstall).
├── legacy/
│   ├── shared-mailbox-manager.ps1   The v1 PowerShell script this product
│   │                                replaces (preserved for historical
│   │                                reference; tenant data redacted).
│   ├── UserUPN.csv                  CSV template for the v1 bulk-grant flow.
│   └── README.md                    Origin story and v1 capability summary.
├── src/                      (Forthcoming) Visual Studio solution:
│   ├── SharedMailbox.Core           Domain types and service interfaces.
│   ├── SharedMailbox.PowerShell     EXO + Graph adapter (hosted PS).
│   ├── SharedMailbox.App            WPF main application.
│   ├── SharedMailbox.ConfigBuilder  WPF companion for deployment kits.
│   └── SharedMailbox.Tests          xUnit test project.
├── scripts/                  (Forthcoming)
│   ├── Setup-EntraApp.ps1           App registration helper.
│   └── Setup-ExchangeRBAC.ps1       Role group + management scope helper.
├── deployment/               (Forthcoming) Packaging and Intune assets.
├── .gitignore
├── LICENSE
└── README.md                 (this file)
```

## From PowerShell script to product

The [`legacy/`](legacy/) folder preserves the v1 PowerShell script that I built and maintained for roughly a year before starting this rewrite. It is functional and documents — by way of its own structure — every operation the v2 product must support. The gap between v1 and v2 is, by intent, the story this repository tells: from a single-purpose script that solved one team's problem to a productized tool that any administrator can deploy in their tenant. The script is preserved for historical reference and for any administrator who would prefer to run the v1 flow today, with their own group IDs swapped in.

The two structural v1 bugs (CSV log misreporting and lack of pre-flight bulk validation) and the three structural v1 limits (hardcoded tenant data, no tool-side authorization, no structured logging) are all explicitly addressed in the v2 design. See section 12 of [`docs/Architecture.md`](docs/Architecture.md) for the fix mechanisms.

## Roadmap

**v1.0 (target):** the three legacy operations (bulk grant, audit, blocked-delegate cleanup) under the dual-layer security model, with Patterns A / B / C all supported, MSIX packaging, and a complete admin deployment runbook.

**v1.x:** Outlook calendar permission management on shared mailboxes; mailbox `CustomAttribute` tagging UI for admins; scheduled audits with optional email delivery.

**v2:** Distribution group and Microsoft 365 group permission management; additional central-config backends (Azure Blob with SAS, tenant-internal HTTPS); signed-by-default MSIX for verified-publisher status.

Items deliberately out of scope are listed in [`docs/Architecture.md`](docs/Architecture.md#13-out-of-scope-and-future-work).

## Contributing

Contributions are welcome once the v1 codebase is in place. Until then, useful contributions include:

- Reviewing [`docs/Architecture.md`](docs/Architecture.md) and opening issues for design questions, gaps, or alternative approaches.
- Testing the [legacy script](legacy/shared-mailbox-manager.ps1) in your own tenant (with placeholder group IDs replaced) and reporting behaviour that the v2 design should preserve.
- Proposing concrete improvements to the security model, configuration model, or deployment patterns.

A `CONTRIBUTING.md` with full developer setup instructions will land alongside the first code commits.

## License

MIT — see [LICENSE](LICENSE).

This is a personal project, built and maintained by Jon Campbell ([@JohnnyPitchfork](https://github.com/JohnnyPitchfork)). It is not affiliated with Microsoft, with any prior or current employer, or with any organization whose mailboxes it is intended to administer.

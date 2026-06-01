# Roadmap — `entra-shared-mailbox-manager`

> The canonical version-by-version delivery plan for the tool. This document is the single source of truth for "what's already in," "what's coming next," and "what's deliberately deferred." [`README.md`](../README.md), [`Architecture.md`](Architecture.md), and [`Setup.md`](Setup.md) reference this roadmap rather than duplicating the scope-per-version breakdown.

The original design in [`Architecture.md`](Architecture.md) describes the fully-realized tool. The v1.0 release is a deliberate subset of that design — the legacy script's three flows under a modern authentication model, with the most important security feature (tool-side role-to-scope filtering) but without the enterprise scaffolding (SharePoint central config, Config Builder companion, full RBAC automation). Subsequent versions fill in the rest.

This is **sequence-based, not date-based**. Versions ship when they're ready. Order may shift if real-world feedback shows a later-version feature is more urgent than a polish item planned earlier.

---

## Milestone summary

| Version | Theme | Status |
|---------|-------|--------|
| v1.0 | First release: legacy parity + role-to-scope filtering + MSIX | In development |
| v1.1 | Polish and operator experience | Planned |
| v1.2 | Tenant setup automation | Planned |
| v2.0 | Enterprise scale-out (the full original design) | Planned |
| v2.x | Feature expansion (adjacent permission types) | Future |
| — | Permanently out of scope | See below |

---

## v1.0 — First release

The minimum viable product. Covers everything the legacy PowerShell script did, under MSAL authentication, with role-based filtering of what each user sees. Deployable to a fleet via Intune as an MSIX.

**Shipped (already in the codebase):**

- Three core flows — audit delegates, cleanup blocked delegates, bulk grant — matching the legacy script's behaviour 1:1.
- MSAL public-client authentication with DPAPI-cached token (Graph interactive once, Exchange silent thereafter).
- Hosted PowerShell runspace for Exchange / Graph cmdlet invocation behind a clean `Core` interface.
- Per-user `appsettings.json` configuration with a three-layer override system (bundled / dev-local / per-user).
- Multi-select mailbox scope in Bulk Grant (added after the initial three-flow batch).
- CSV audit / cleanup / bulk-grant logs matching the legacy script's column format byte-for-byte.
- Unit tests covering the Core domain types, the CSV writer, the UPN-import reader, and the PowerShell adapter.

**In progress for v1.0:**

- **Role-to-scope filtering** — the tool-side half of Architecture.md's dual-layer security model. Each role is an Entra security group of users; each role is mapped to one or more SharedMail- groups it can administer. At sign-in, the sidebar filters to only the groups the user holds a role for. Users in no mapped role see an empty state with a clear message instead of every group in the tenant.
- **MSIX packaging** — the path from "runs on dev box" to "deployable via Intune." Adds a Windows Application Packaging Project to the solution and configures signing.
- **Documentation reconciliation** — `Setup.md`, `Architecture.md`, and `README.md` updated to honestly reflect what ships in v1.0 versus what's deferred.
- **Release hygiene** — `LICENSE` verification, redundant `<Folder Include>` csproj entries cleaned up. (The internal `src/SharedMailboxTool/` folder was collapsed to just `src/` during MSIX packaging to keep paths under Windows' MAX_PATH limit; `entra-shared-mailbox-manager` is now the single canonical name across repo, install dir, and `%LOCALAPPDATA%`.)

**Deliberately deferred from v1.0:**

These were in the original Architecture.md v1.0 scope. They're real features, but each is large enough on its own that holding v1.0 to ship them would mean shipping much later. See the v1.1 / v1.2 / v2.0 sections for where they land.

- SharePoint-hosted central configuration (Pattern A)
- The Config Builder companion app
- First-run wizard (currently the app shows a `ConfigurationException` MessageBox and exits if `appsettings.json` is missing)
- Per-action JSON receipts in a `Receipts/` directory
- `Setup-EntraApp.ps1` and `Setup-ExchangeRBAC.ps1` helper scripts
- CustomAttribute1-based scope matching (v1.0 uses explicit SharedMail- group ID lists in the role config, which is simpler and matches how existing tenants already organize their mailboxes)

---

## v1.1 — Polish and operator experience

Quality-of-life improvements that make the tool more pleasant to deploy and use day-to-day. No architectural changes — same Core, PowerShell adapter, and security model as v1.0.

- **First-run wizard** — replaces the bare-bones ConfigurationException MessageBox. Walks a new user through TenantId, ClientId, and at least one SharedMail- group entry, then writes the per-user `appsettings.json`. The wizard is for Pattern C solo installs; Intune deployments still use the scripted config drop.
- **Operation receipts** — per-mutation JSON files in a `Receipts/` directory under `%LOCALAPPDATA%`. Each receipt captures the operator, the operation, the before-state, the after-state, the result, and any failure detail. 90-day retention, per Architecture.md §11. The canonical record for "what did I do on Tuesday?" questions.
- **GitHub Actions CI** — runs `dotnet build` + `dotnet test` on every push and pull request. Failing tests block merge.
- **System theme detection** — WPF-UI exposes `ApplicationThemeManager`; light / dark / high-contrast follow the OS preference instead of hardcoded Light.
- **App icon and branding** — replace the default WPF icon with a proper 256x256 PNG/ICO set. Affects taskbar, MSIX tile, About dialog.
- **App-layer unit tests** — `ViewModels`, `JsonConfigProvider` validation, the in-app role resolver. Requires the `Tests` project to reference `App` (and consequently target `net8.0-windows`).
- **Window-state persistence** — remember window size, position, last-selected SharedMail- group across launches.

---

## v1.2 — Tenant setup automation

Helpers for the deploying administrator that reduce the click-heavy parts of `Setup.md`. Ships alongside the MSIX as a separate `scripts/` bundle.

- **`Setup-EntraApp.ps1`** — automates the Entra app registration steps from Setup.md §3.1. Idempotent: re-running on an existing registration is a no-op. Adds permissions and triggers admin consent. Output is the TenantId + ClientId to drop into the bootstrap config.
- **`Setup-ExchangeRBAC.ps1`** — automates the Layer 1 platform-enforced security setup from Setup.md §3.4. Takes a JSON definition of roles + scopes and creates the `New-ManagementScope` / `New-RoleGroup` / `Add-RoleGroupMember` primitives. With this and Layer 2 from v1.0, the full dual-layer security model is in place.
- **In-app logs viewer** — a fourth tab (or docked pane) that tails the Serilog rolling file with severity filtering. Removes the need to open a separate text editor when investigating an error.
- **CustomAttribute1 tag-editing UI** — for administrators who hold Exchange admin rights. Lets them tag shared mailboxes with the `CustomAttribute1` value referenced by an Exchange management scope. Adds Architecture.md's preferred Layer 1 scope-match mechanism as an option alongside the explicit-group-ID list from v1.0.

---

## v2.0 — Enterprise scale-out

The features needed for fleet-scale deployment with centrally-managed configuration. The Architecture.md design is fully realized at this milestone.

- **SharePoint Online central configuration (Pattern A)** — the role-to-scope mapping, the list of SharedMail- groups, and the audit-policy settings all live in a SharePoint document library. The app reads the JSON via Graph at launch and merges it on top of the local bootstrap config. Per Architecture.md §7 and §8.1.
- **Config Builder companion app** — a separate WPF executable used once per tenant by the deploying administrator. Walks them through app-reg creation, role-to-scope mapping, and SharePoint setup, then emits a complete deployment kit (`appsettings.json`, `Deploy-AppConfig.ps1`, a Markdown walkthrough). Per Architecture.md §4.1 and §9.4. This is the heaviest single piece of v2.0 work — effectively a second application.
- **Config cache with TTL for offline resilience** — central-config fetches are cached locally with a configurable TTL (default 24h). If the SharePoint fetch fails on a subsequent launch and the cache is within TTL, the cached config is used. Past TTL, the user sees a clear "config refresh required" message.
- **Live config reload** — the app polls or watches for central-config changes and refreshes role mappings without an app restart. Edit JSON in SharePoint, save, every running instance picks it up.

---

## v2.x — Feature expansion

Adjacent operational capabilities once the core shared-mailbox flow is mature. These are real features but each adds a meaningful surface to test and document, so they're staged after v2.0 has settled.

- **Calendar permission management on shared mailboxes** — uses different EXO cmdlets (`Get-MailboxFolderPermission`, etc.) and different Graph endpoints, but operationally adjacent to delegate permissions.
- **Distribution group and Microsoft 365 group permission management** — extends the bulk-grant and audit patterns to non-shared-mailbox recipient types.
- **Scheduled audits with optional email delivery** — "run the audit every Sunday and email the report." Currently audits are on-demand only.
- **Additional central-config backends** — Azure Blob Storage with SAS auth, tenant-internal HTTPS URL. Per Architecture.md §7.4's list of considered-and-rejected-for-v2.0 alternatives.
- **Code-signed MSIX** — the initial public release is unsigned with documented sideload steps. Verified-publisher signing is a future activity once distribution patterns stabilize.

---

## Permanently out of scope

These will not be addressed in any version. Captured here for completeness; the rationale lives in Architecture.md §3.2 and §13.

- On-premises or hybrid Exchange (Exchange Online only)
- Cross-tenant administration (one running instance = one tenant)
- EWS-based operations (EWS is deprecating for mail/calendar/contacts data access in late 2026)
- Automated SharePoint Online site or library provisioning (would require app permissions the tool deliberately does not request)
- Tenant-wide compliance reporting (use Purview / SIEM ingestion of the Serilog output)
- Mobile, web, or cross-platform UI

---

## How the security model evolves across versions

The dual-layer security model from Architecture.md §4.2 is built incrementally. Both layers cooperate when both are in place; either alone is meaningful but weaker.

| Version | Layer 1 (Exchange RBAC, platform-enforced) | Layer 2 (tool-side UX filtering) |
|---------|--------------------------------------------|----------------------------------|
| v1.0    | Manual setup per Setup.md                  | **Implemented** (role-to-scope mapping) |
| v1.1    | Manual setup per Setup.md                  | Implemented                      |
| v1.2    | Automated by `Setup-ExchangeRBAC.ps1`      | Implemented                      |
| v2.0    | Automated through Config Builder           | Implemented; centrally configured|

A deployment with only Layer 2 (v1.0 default) is a productivity layer. A savvy user could bypass the UI filtering with direct PowerShell. Adding Layer 1 (manual in v1.0/v1.1, automated from v1.2) closes that bypass at the platform level. The recommendation across all versions is to deploy both.

---

## How deployment patterns mature across versions

| Version | Pattern C (Solo install) | Pattern B (Intune, static config) | Pattern A (Intune + SharePoint central) |
|---------|--------------------------|------------------------------------|------------------------------------------|
| v1.0    | Manual `appsettings.json` drop | MSIX + scripted config drop  | Not available                            |
| v1.1    | First-run wizard         | MSIX + scripted config drop        | Not available                            |
| v2.0    | First-run wizard         | MSIX + scripted config drop        | **Available** (live SharePoint central) |

---

## Versioning notes

This roadmap uses **semantic-ish versioning** — major versions (v1, v2) represent architectural shifts; minor versions (v1.1, v1.2) add features without breaking existing deployments; patch versions (not enumerated here, e.g., v1.0.1) are bug-fix releases.

Existing `appsettings.json` files remain compatible across minor versions. A v1.0 config will load and run correctly under v1.1 and v1.2. Major-version transitions (v1 → v2) may require config-schema migration; a migration tool would ship with the v2.0 release if so.

---

*This roadmap evolves alongside the codebase. Any divergence between this document and the planned versions in commits or release notes is a documentation bug — please open an issue.*

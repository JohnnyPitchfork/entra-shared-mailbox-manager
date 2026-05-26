# Setup guide — v1.0

> Admin deployment runbook for `entra-shared-mailbox-manager`. Walks an IT or Exchange administrator through registering the application in Entra, configuring it for a single tenant, and rolling it out either manually on one machine or to a fleet via Intune.
>
> **This guide is scoped to the v1.0 release.** Features that are deferred to later versions (SharePoint-hosted central configuration, the Config Builder companion app, first-run wizard, fully-automated RBAC setup) are not described here — see [`Roadmap.md`](Roadmap.md) for what lands when. The v1.0-supported deployment patterns are **Pattern B** (Intune + scripted config drop) and **Pattern C** (solo manual install). Pattern A (SharePoint central config) is a v2.0 feature.

---

## 1. At a glance

The end-to-end path is six steps. Steps 1, 2, and 3 are one-time tenant work the administrator does once. Steps 4, 5, and 6 are per-machine and can be automated via Intune.

| # | What | Where it runs | Repeat per |
|---|------|---------------|------------|
| 1 | Create an Entra app registration and grant admin consent | Entra admin center | Tenant (once) |
| 2 | Assign Exchange admin roles to your operators | Entra / Exchange admin center | Operator |
| 3 | (Optional) Configure Exchange RBAC management scopes for Layer 1 security | Exchange Online PowerShell | Role |
| 4 | Install the prerequisite PowerShell modules | `Install-Prerequisites.ps1` | Machine |
| 5 | Drop the tenant `appsettings.json` onto each machine | Manual or Intune device script | Machine |
| 6 | Install the application | Manual or Intune MSIX assignment | Machine |

Steps 4, 5, and 6 are independent and can be done in any order; the app refuses to start cleanly if any are missing, with an actionable error in each case.

---

## 2. Prerequisites

### 2.1 Tenant

- A Microsoft 365 / Entra tenant where the operator accounts live.
- Permission to create an Entra app registration and grant admin consent (**Global Administrator** or **Privileged Role Administrator**).
- Existing `SharedMail-` prefixed Entra security groups whose members are the shared mailboxes the tool will operate on, **or** willingness to use the manual group-ID entry fallback in the sidebar.

### 2.2 Operator (end user)

The user who runs the tool needs an Exchange admin role at the tenant level. Minimum is **Exchange Recipient Administrator**, which is sufficient for every cmdlet the tool calls (`Add-MailboxPermission`, `Remove-MailboxPermission`, `Add-RecipientPermission`, `Remove-RecipientPermission`, `Set-Mailbox -GrantSendOnBehalfTo`). For a less-scoped role, **Exchange Administrator** also works.

### 2.3 Machine

- Windows 10 (build 1809 or later) or Windows 11.
- .NET 8 Desktop Runtime (bundled by the MSIX deployment; downloadable from `https://dotnet.microsoft.com/download/dotnet/8.0` for manual installs).
- PowerShell 7.x recommended for running `Install-Prerequisites.ps1`. The app bundles `Microsoft.PowerShell.SDK` for its in-process runspace, so PS 7 is not strictly required at runtime.
- Internet access to `login.microsoftonline.com`, `graph.microsoft.com`, and `outlook.office365.com`.

---

## 3. Step 1 — Create the Entra app registration

The app is a **public-client (mobile and desktop) registration**. It does not need a client secret or certificate because every operation runs in the operator's own delegated context via MSAL.

1. In the Entra admin center, navigate to **Identity → Applications → App registrations → New registration**.
2. **Name:** `SharedMail Tool` (or any name your operators will recognize on the consent prompt).
3. **Supported account types:** *Accounts in this organizational directory only* (single-tenant). The tool is single-tenant by design in v1.0.
4. Leave **Redirect URI** blank on the create page. Click **Register**.
5. On the Overview page, record the **Application (client) ID** and the **Directory (tenant) ID** — both go into `appsettings.json` later.

### 3.1 Configure the platform

1. **Authentication → Add a platform → Mobile and desktop applications**.
2. Tick `http://localhost` from the suggested redirect URIs (or add it manually). This matches the `RedirectUri` default in `appsettings.json` and the MSAL system-browser flow the app uses.
3. Scroll to **Advanced settings → Allow public client flows** and set it to **Yes**. Without this, MSAL public-client flows fail with an obscure AADSTS error.
4. Save.

### 3.2 Add and grant API permissions

Add each from **API permissions → Add a permission**:

| API | Permission | Purpose |
|-----|------------|---------|
| Microsoft Graph | `Group.Read.All` (delegated) | `Get-MgGroupMember` for SharedMail- groups; `Get-MgUserMemberOf` for the role filter |
| Microsoft Graph | `User.Read.All` (delegated) | `Get-MgUser` for the audit's sign-in-status lookup |
| Office 365 Exchange Online | `Exchange.Manage` (delegated) | All EXO cmdlets the tool runs |

After adding all three, click **Grant admin consent for {tenant}**. This requires Global Administrator or Privileged Role Administrator. Without admin consent, end users would see a consent prompt on every sign-in for the `.All`-scope Graph permissions, which the user-consent flow does not support.

The API-permissions blade should show all three permissions with a **Granted for {tenant}** badge after consent.

> **Do not** add a client secret, a certificate, or any application-permission scopes. v1.0 is delegated-only by design — the security model depends on the app having no standing privileges.

---

## 4. Step 2 — Assign operator roles

Each user who runs the tool needs an Exchange admin role at the tenant level. Two practical approaches:

- **Direct assignment.** In the Entra admin center: **Identity → Roles & admins → Roles → Exchange Recipient Administrator → Add assignments**. Fast for a handful of operators.
- **Group-based assignment.** Create an Entra security group named e.g. `ROLE-MailboxAdmins`, mark it **role-assignable**, and assign Exchange Recipient Administrator to the group. Then add operators to the group. Scales better and pairs naturally with the role-to-scope mapping in Step 5.

Role changes in Entra propagate within minutes but can take up to an hour. If sign-in works but operations fail with "Access is denied" or AADSTS errors, the role isn't applied yet.

---

## 5. Step 3 (optional) — Configure Exchange RBAC for Layer 1 security

This step is **optional for v1.0 but recommended for production**. Skip if you're doing a quick first-time evaluation.

The v1.0 tool implements Layer 2 of the dual-layer security model from [`Architecture.md`](Architecture.md) §4.2 — the sidebar filters to show each operator only the SharedMail- groups their roles permit. Layer 1 — the platform-enforced Exchange RBAC management scope — has to be configured manually until the v1.2 release adds an automation helper (see [`Roadmap.md`](Roadmap.md)).

Without Layer 1, the v1.0 tool's filtering is a **UX layer, not a security boundary** — a sufficiently determined operator could bypass it by running PowerShell directly against their tenant. For a small internal evaluation that's fine; for production, you'll want both layers.

### 5.1 What Layer 1 actually enforces

Exchange Online's RBAC system can scope a role group to a subset of recipients (e.g., "Project Managers can administer only mailboxes tagged `CustomAttribute1 = PM`"). When configured, Exchange Online itself refuses any operation outside that scope — regardless of whether it came from the tool, from PowerShell, or from the Exchange admin center.

### 5.2 Recipe (one role)

For each role you want to delegate, run the following from an elevated Exchange Online PowerShell session. Substitute names and CustomAttribute1 values as appropriate.

```powershell
Connect-ExchangeOnline

# 1. Tag the shared mailboxes that this role can administer.
$mailboxes = @("pm-team@contoso.com", "project-alpha@contoso.com", "project-beta@contoso.com")
foreach ($mbx in $mailboxes) {
    Set-Mailbox -Identity $mbx -CustomAttribute1 "PM"
}

# 2. Create a management scope that filters to those mailboxes.
New-ManagementScope `
    -Name "Scope-SharedMail-PM" `
    -RecipientRestrictionFilter "RecipientTypeDetails -eq 'SharedMailbox' -and CustomAttribute1 -eq 'PM'"

# 3. Create a role group bound to that scope, holding the minimum permission-management role.
New-RoleGroup `
    -Name "RoleGroup-SharedMail-PM-Managers" `
    -Roles "Mail Recipients" `
    -CustomRecipientWriteScope "Scope-SharedMail-PM"

# 4. Add the Entra security group of users as a member of the role group.
Add-RoleGroupMember `
    -Identity "RoleGroup-SharedMail-PM-Managers" `
    -Member "ROLE-ProjectManagers"
```

Repeat per role. After running for every role, validate by signing in as a member of one of the Entra groups and trying to run `Get-Mailbox` against a mailbox outside the scope — Exchange should refuse.

The CustomAttribute1 tag (`PM` in the example above) is the link between the Exchange-side scope and the role mapping you'll define in Step 6.2. Use a short uppercase tag per role.

---

## 6. Step 4 — Install PowerShell modules

The tool hosts a PowerShell runspace internally and imports two module families on first sign-in: `ExchangeOnlineManagement` and `Microsoft.Graph`. Combined install footprint: ~250 MB. Install time: 2–5 minutes on a typical connection.

### 6.1 Manual install

For evaluation or single-machine setups, run `scripts/Install-Prerequisites.ps1` from a PowerShell prompt:

```powershell
.\Install-Prerequisites.ps1                  # installs for the current user
.\Install-Prerequisites.ps1 -Scope AllUsers  # all users (elevated)
.\Install-Prerequisites.ps1 -Force           # reinstall at latest versions
```

The script is idempotent. Re-running when the modules are already present is a no-op unless `-Force` is supplied.

### 6.2 Intune deployment

For fleet rollouts, upload `Install-Prerequisites.ps1` as a Windows platform script under **Devices → Scripts and remediations → Add → Windows platform script**.

Recommended settings:
- **Run this script using the logged on credentials:** No (run as SYSTEM).
- **Enforce script signature check:** No (script is unsigned by default; sign it with your own code-signing cert if your tenant requires signed scripts).
- **Run script in 64 bit PowerShell:** Yes.

Target the same device group that gets the application package. Intune retries failed runs on its own schedule, so transient install failures usually self-heal within a few hours.

### 6.3 What happens if modules are missing

If an operator launches the app on a machine where the modules aren't installed, the first sign-in attempt throws a `PowerShellInvocationException` whose message includes the install command verbatim. Surfaces in the in-app error pane at the bottom of the window — copy-paste-runnable.

---

## 7. Step 5 — Configure the application

The application reads configuration from a JSON file with a three-layer override system. From lowest to highest priority:

1. **`appsettings.json` next to the executable** — bundled default. Always ships with placeholder values; never has real tenant data.
2. **`appsettings.local.json` next to the executable** — optional developer override. Git-ignored. Useful when running `dotnet run` from a clone.
3. **`%LOCALAPPDATA%\entra-shared-mailbox-manager\appsettings.json`** — the per-user override. This is what Intune drops onto end-user machines, and where individual operators put real values for local evaluation.

Values from higher layers replace values from lower layers key-by-key. Arrays (like `KnownGroups` and `Roles`) are replaced as a whole, not merged element-by-element.

### 7.1 Minimum required content

The smallest viable override file:

```json
{
  "AzureAd": {
    "TenantId": "your-tenant-guid",
    "ClientId": "your-app-reg-client-guid"
  },
  "KnownGroups": [
    { "Name": "SharedMail-Permits",   "GroupId": "9aea063a-0af2-440b-9c60-65f4c6e9431d" },
    { "Name": "SharedMail-Utilities", "GroupId": "01defb2f-bb5f-46b9-9a73-e6f6a7121f1c" }
  ]
}
```

Replace the GUIDs with the **Application (client) ID** and **Directory (tenant) ID** from Step 1, and your tenant's SharedMail- groups. `Name` is the display label shown in the sidebar — make it whatever your operators will recognize.

### 7.2 Adding role-to-scope filtering (optional)

If you want the sidebar to show each operator only the SharedMail- groups their roles permit (the v1.0 Layer 2 security feature), add a `Roles` section:

```json
{
  "AzureAd":     { "TenantId": "...", "ClientId": "..." },
  "KnownGroups": [ ... ],
  "Roles": [
    {
      "Name": "Project Managers",
      "EntraGroupId": "<ROLE-ProjectManagers-group-guid>",
      "AllowedGroupIds": [
        "9aea063a-0af2-440b-9c60-65f4c6e9431d",
        "01defb2f-bb5f-46b9-9a73-e6f6a7121f1c"
      ]
    },
    {
      "Name": "Operations Leads",
      "EntraGroupId": "<ROLE-OpsLeads-group-guid>",
      "AllowedGroupIds": [
        "01defb2f-bb5f-46b9-9a73-e6f6a7121f1c"
      ]
    }
  ]
}
```

Each role's `EntraGroupId` is the object ID of the Entra security group whose members hold that role. `AllowedGroupIds` are the object IDs of the SharedMail- groups that role can administer. They must appear in `KnownGroups` above; entries that don't are silently invisible.

If `Roles` is empty or missing, no filtering is applied — every operator sees every `KnownGroup`. This is the legacy v1.0-pre behavior and a sensible starting point for a small evaluation.

### 7.3 Optional fields

Everything not in the override inherits the bundled default:

| Field | Bundled default | Override when |
|-------|-----------------|---------------|
| `AzureAd.GraphScopes` | `["Group.Read.All", "User.Read.All"]` | You need additional Graph scopes (rare) |
| `AzureAd.ExchangeResource` | `"https://outlook.office365.com"` | Sovereign cloud (GCC High, DoD, China) |
| `AzureAd.RedirectUri` | `"http://localhost"` | Must match what's configured in the app registration |
| `Logging.LogDirectory` | `"Logs"` (resolved relative to `%LOCALAPPDATA%\entra-shared-mailbox-manager\`) | You want logs in a specific shared location |

### 7.4 Validation

If any required value is missing, the app refuses to start with a "Configuration validation failed" dialog listing every problem found. The dialog names the file path to edit, so end users get actionable guidance even if they didn't write the config themselves.

---

## 8. Step 6 — Install the application

### 8.1 Pattern C — Solo install (manual)

For evaluation, a single admin's own machine, or environments without Intune:

1. Build from source (see Section 9) or download the signed MSIX from the latest GitHub release.
2. Install: double-click the `.msix` file (or `Add-AppxPackage -Path .\SharedMailboxTool.msix` from PowerShell). Windows installs the application.
3. Create `%LOCALAPPDATA%\entra-shared-mailbox-manager\` (auto-created on first MSAL sign-in, but you can pre-create it).
4. Drop your tenant `appsettings.json` (from Step 5) into that folder.
5. Run `Install-Prerequisites.ps1` if you haven't already.
6. Launch the app from the Start menu. Sign in.

### 8.2 Pattern B — Intune deployment

For rolling out to multiple operators on managed devices:

1. **Sign and package the MSIX.** See Section 9.2 for the signing options.
2. **Upload the MSIX as a line-of-business app.** Intune admin center → **Apps → Windows → Add → Line-of-business app** → select the `.msix` or `.msixbundle` → assign to the target device group as **Required**.
3. **Deploy the configuration script.** Author a small PowerShell script that writes the tenant `appsettings.json` to `%LOCALAPPDATA%\entra-shared-mailbox-manager\` for the logged-on user. A reference template is forthcoming as `scripts/Deploy-AppConfig.ps1`. Upload it as a Windows platform script targeted at the same device group, configured to **run using the logged-on credentials**.
4. **Deploy `Install-Prerequisites.ps1`** as a separate Windows platform script if you haven't already.
5. Wait for Intune to push everything to target devices (minutes to a few hours depending on tenant size).
6. Operators sign in on first launch. MSAL caches the token; subsequent launches are silent.

### 8.3 Pattern A — SharePoint central configuration

Deferred to v2.0. Tracking in [`Roadmap.md`](Roadmap.md). In v2.0, central role mapping and group definitions will live in a SharePoint document library and be edited in-browser without redeploying through Intune. For v1.0, role/group changes require a script redeployment.

---

## 9. Build from source

For contributors or admins who prefer their own binaries instead of a published MSIX.

### 9.1 Build and run

```powershell
git clone https://github.com/JohnnyPitchfork/entra-shared-mailbox-manager.git
cd entra-shared-mailbox-manager
dotnet restore src/SharedMailboxTool/SharedMailboxTool.sln
dotnet build src/SharedMailboxTool/SharedMailboxTool.sln -c Release
dotnet test  src/SharedMailboxTool/SharedMailboxTool.sln -c Release
```

The release build output is at `src/SharedMailboxTool/SharedMailbox.App/bin/Release/net8.0-windows/`. Run `SharedMailbox.App.exe` directly from there, or package as MSIX (Section 9.2).

### 9.2 Package and sign the MSIX

MSIX packaging is added by the Windows Application Packaging Project in the solution. Sign with one of three options:

- **Self-signed cert** — fastest path for internal testing. Generate via PowerShell, install the cert into "Trusted People" on each test machine (Intune can push this), sign the MSIX with it. Not production-grade but unblocks first end-to-end test.
- **Real code-signing cert** — ~$200–500/year from a CA (DigiCert, Sectigo, etc.). Required for "verified publisher" status; recommended before rolling out to production operators.
- **Unsigned + sideload override** — works on a developer machine in Developer Mode; not viable for Intune-managed devices.

The choice is a deployment decision, not a code decision. The MSIX project is identical regardless of which cert you sign with.

---

## 10. Troubleshooting

### "Configuration validation failed: AzureAd.TenantId is not a valid Guid"

Launched the app before placing a real `appsettings.json` in `%LOCALAPPDATA%\entra-shared-mailbox-manager\`. Create the file per Section 7 and relaunch.

### "Required PowerShell modules are not installed: ExchangeOnlineManagement, Microsoft.Graph"

The runspace failed to import the modules. Re-run `Install-Prerequisites.ps1`. If install itself fails, check internet access to `www.powershellgallery.com`.

### "AADSTS65001: The user or administrator has not consented to use the application"

Admin consent wasn't granted for one of the `.All`-scope Graph permissions. Return to the app registration → API permissions blade → **Grant admin consent for {tenant}**.

### "AADSTS50011: The redirect URI specified in the request does not match the redirect URIs configured for the application"

`AzureAd.RedirectUri` in `appsettings.json` doesn't match what's configured under **Authentication → Mobile and desktop applications** in the app registration. The default is `http://localhost` on both sides — verify neither has drifted.

### Sign-in completes but every action fails with "Access is denied"

The signed-in user doesn't hold the Exchange admin role required for the operation. Verify role assignment per Step 2. Entra role changes can take up to an hour to propagate.

### Sidebar shows "No mailbox groups are mapped to your roles"

You configured `Roles` in `appsettings.json` but the signed-in user is in none of the `EntraGroupId` values listed there. Either add the user to one of those groups, or remove/empty the `Roles` array if you don't want filtering.

### Sidebar shows "Could not determine your access: ..."

`Get-MgUserMemberOf` failed during the role-resolution step. Most common cause: the user has revoked Graph consent or the Graph session expired. Sign out and back in.

### App freezes during sign-in

The MSAL system-browser flow is waiting on user interaction in the default browser. If the browser opened to a blank page, sign out of any conflicting tenants in the browser and retry. Last resort: delete `%LOCALAPPDATA%\entra-shared-mailbox-manager\msal_cache.bin` to force a clean re-auth.

### CSV logs are missing

CSV logs default to `%LOCALAPPDATA%\entra-shared-mailbox-manager\Logs\`. If the directory doesn't exist after a successful run, check `appsettings.json`'s `Logging.LogDirectory` — a rooted path overrides the default.

---

## 11. Updating

For MSIX-deployed installations, Windows handles updates automatically once a new version is pushed to Intune. For manual installations, replace the package; configuration in `%LOCALAPPDATA%` is preserved across updates.

The MSAL token cache (`msal_cache.bin`) and the CSV log directory survive uninstall by default.

---

## 12. Uninstalling

### MSIX installations

`Settings → Apps → Installed apps → SharedMail Tool → Uninstall`, or change the Intune assignment to **Uninstall** for the device group.

### Manual installations

`Get-AppxPackage *SharedMailboxTool* | Remove-AppxPackage`

### Full clean uninstall (including config and cache)

```powershell
Remove-Item -Path "$env:LOCALAPPDATA\entra-shared-mailbox-manager\" -Recurse -Force -ErrorAction SilentlyContinue
```

### Revoking the Entra app registration

Removing the app from machines does not revoke the app registration. To disable the application tenant-wide: Entra admin center → **Identity → Applications → Enterprise applications** → find the registration → **Properties → Enabled for users to sign in** → Off. Existing access tokens continue to work until they expire (up to one hour); cached refresh tokens become unusable immediately.

---

## 13. See also

- [`Roadmap.md`](Roadmap.md) — version-by-version delivery plan. Use this to find which version a feature lands in.
- [`Architecture.md`](Architecture.md) — design source-of-truth covering the security model, component layout, and the full-vision configuration architecture.
- [`README.md`](../README.md) — project overview and feature summary.
- [`Install-Prerequisites.ps1`](../scripts/Install-Prerequisites.ps1) — the module-install script.

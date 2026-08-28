# Shared Mailbox Manager: Graph / Kiota / PowerShell Compatibility Issue

## Summary

The Shared Mailbox Manager began failing while invoking embedded PowerShell with an exception similar to:

```text
PowerShell invocation failed because $ErrorActionPreference
or common parameter is set to Stop:

Method not found:
'Void Microsoft.Graph.Authentication.AzureIdentityAccessTokenProvider..ctor(
    Azure.Core.TokenCredential,
    System.String[],
    Microsoft.Kiota.Authentication.Azure.ObservabilityOptions,
    Boolean,
    System.String[])'
```

The important part was:

```text
Method not found:
Microsoft.Graph.Authentication.AzureIdentityAccessTokenProvider..ctor(...)
```

This was **not an authentication/token-cache problem**. It was a runtime assembly compatibility problem.

A class was successfully found, but the loaded version of that class did not contain the constructor signature another component expected.

That strongly indicated:

```text
Component A compiled against dependency version X
+
runtime loaded dependency version Y
=
MethodNotFoundException
```

---

# Architecture involved

The application has two different Microsoft Graph dependency worlds living inside the same process:

```text
SharedMailbox.App
│
├── C# / NuGet Graph SDK
│   ├── Microsoft.Graph
│   ├── Microsoft.Graph.Core
│   ├── Microsoft.Kiota.*
│   └── Azure.Core
│
└── Embedded PowerShell
    ├── Microsoft.PowerShell.SDK
    └── machine-installed Microsoft.Graph PowerShell modules
```

That distinction is critical.

The application's C# dependencies are controlled by NuGet.

The Graph PowerShell modules are installed **on the computer itself** under the user's PowerShell module directories.

Because the application hosts PowerShell in-process, both dependency trees can wind up loading related Graph/Kiota assemblies into the **same .NET process**.

That makes version compatibility unusually important.

---

# Initial red herring: Graph NuGet 6.5.0 upgrade

During troubleshooting, `SharedMailbox.App` had been upgraded from:

```text
Microsoft.Graph 5.105.0
```

to:

```text
Microsoft.Graph 6.5.0
```

The upgraded dependency tree was:

```text
Microsoft.Graph                         6.5.0
Microsoft.Graph.Core                    4.0.1
Microsoft.Kiota.Abstractions            2.0.0
Microsoft.Kiota.Authentication.Azure    2.0.0
Microsoft.Kiota.Http.HttpClientLibrary  2.0.0
Azure.Core                              1.50.0
Microsoft.PowerShell.SDK                7.4.15
```

This looked extremely suspicious because it represented:

```text
Graph 5 → 6
Kiota 1.x → 2.x
```

and the exception specifically involved a Kiota authentication API.

We therefore rolled the repository completely back to the known GitHub state.

---

# Restoring the known-good Git state

Commands used from anywhere inside the repository:

```powershell
git fetch origin
git reset --hard origin/main
git clean -fd
```

Verification:

```powershell
git branch --show-current
git rev-parse --show-toplevel
```

Result:

```text
Branch: main
Repo root:
G:/Git-Repos/entra-shared-mailbox-manager
```

Known-good commit at the time:

```text
df8c8f8 Get MSIX publish working end-to-end
```

Important: `git clean -fd` removes untracked files/directories too.

---

# Known-good NuGet dependency stack

After restoring GitHub `main`, this command was run:

```powershell
dotnet list .\SharedMailbox.App\SharedMailbox.App.csproj package --include-transitive |
    Select-String "Graph|Kiota|Azure.Core|PowerShell"
```

The known-good application dependency stack is:

```text
Microsoft.Graph                         5.105.0
Microsoft.Graph.Core                    3.2.5
Microsoft.Kiota.Abstractions            1.21.1
Microsoft.Kiota.Authentication.Azure    1.21.1
Microsoft.Kiota.Http.HttpClientLibrary  1.21.1
Microsoft.Kiota.Serialization.Form      1.21.1
Microsoft.Kiota.Serialization.Json      1.21.1
Microsoft.Kiota.Serialization.Multipart 1.21.1
Microsoft.Kiota.Serialization.Text      1.21.1
Azure.Core                              1.50.0
Microsoft.PowerShell.SDK                7.4.15
```

The `SharedMailbox.PowerShell` project itself does **not** reference Graph/Kiota packages directly. Its relevant explicit dependency was:

```text
Microsoft.PowerShell.SDK 7.4.15
```

That became important because it showed that the Graph cmdlets used through PowerShell were coming from the machine's installed PowerShell modules, rather than from NuGet dependencies inside `SharedMailbox.PowerShell`.

---

# Saving the known-good package baseline

From:

```text
...\entra-shared-mailbox-manager\src
```

the complete dependency snapshot can be written to the repository root with:

```powershell
dotnet list .\SharedMailbox.App\SharedMailbox.App.csproj package --include-transitive > ..\known-good-packages.txt
```

Keep that file around. It gives an exact dependency reference if future package upgrades break the application.

---

# Comparing against the machine where the app already worked

This was the key troubleshooting step.

The same NuGet dependency query was run on the known-working work desktop.

It returned the **exact same application dependency stack**:

```text
Microsoft.Graph                         5.105.0
Microsoft.Graph.Core                    3.2.5
Microsoft.Kiota.*                       1.21.1
Azure.Core                              1.50.0
Microsoft.PowerShell.SDK                7.4.15
```

Therefore:

> The C# application dependencies were not different between the working and failing machines.

That shifted attention to the **machine environment**.

---

# The actual difference: Microsoft Graph PowerShell SDK

We checked machine-installed Graph modules with:

```powershell
Get-Module Microsoft.Graph* -ListAvailable | Select-Object Name,Version,ModuleBase | Sort-Object Name,Version
```

## Working machine

The working computer had Graph PowerShell `2.25.0`, plus some older `2.19.0` modules.

Most importantly:

```text
Microsoft.Graph.Authentication 2.25.0
```

was installed.

## Failing machine

The failing computer had:

```text
Microsoft.Graph.* 2.36.1
```

throughout, including:

```text
Microsoft.Graph.Authentication 2.36.1
```

There were roughly 38 Graph submodules at `2.36.1`.

This was the first significant environmental difference between the two systems.

---

# Working theory that was confirmed

The known-good application loads:

```text
Microsoft.Graph 5.105.0
Microsoft.Kiota.Authentication.Azure 1.21.1
```

into its .NET process.

The embedded PowerShell environment then imports the machine-installed Graph PowerShell modules.

On the working computer:

```text
C#:
Graph 5.105
Kiota 1.21.1

PowerShell:
Microsoft.Graph.Authentication 2.25.0

= compatible
```

On the failing computer:

```text
C#:
Graph 5.105
Kiota 1.21.1

PowerShell:
Microsoft.Graph.Authentication 2.36.1

= runtime assembly/API collision
```

Graph PowerShell `2.36.1` attempted to call an `AzureIdentityAccessTokenProvider` constructor that did not exist in the Kiota assembly loaded by the application.

Hence:

```text
MethodNotFoundException
```

This also explains why Graph PowerShell commands could potentially work perfectly well when launched in a normal standalone PowerShell process while failing inside this application.

The application is hosting PowerShell **inside a process that has already loaded its own Graph/Kiota assemblies**.

---

# Installing the known-working Graph PowerShell version

We installed the same Graph PowerShell version used on the working desktop:

```powershell
Install-Module Microsoft.Graph -RequiredVersion 2.25.0 -Scope CurrentUser -AllowClobber
```

`-AllowClobber` was required because Graph `2.36.1` already exposed many of the same `Get-Mg*`, `Set-Mg*`, etc. commands.

In this context, `-AllowClobber` means:

> Permit the module installation even though command names overlap with commands exported by another installed module.

It does **not** delete the existing module version.

Afterward:

```powershell
Get-Module Microsoft.Graph.Authentication -ListAvailable |
    Select-Object Name,Version,ModuleBase |
    Sort-Object Version
```

showed:

```text
Microsoft.Graph.Authentication 2.25.0
Microsoft.Graph.Authentication 2.36.1
```

---

# Important discovery about uninstalling Microsoft.Graph

Removing only:

```powershell
Uninstall-Module Microsoft.Graph -RequiredVersion 2.36.1 -Force
```

was **not sufficient**.

`Microsoft.Graph` is essentially a meta-module. Its individual submodules remained installed, including:

```text
Microsoft.Graph.Authentication 2.36.1
Microsoft.Graph.Users 2.36.1
Microsoft.Graph.Groups 2.36.1
...
```

Therefore the application continued throwing the exact same exception.

This command exposed the remaining modules:

```powershell
Get-InstalledModule |
    Where-Object {
        $_.Name -like 'Microsoft.Graph*' -and
        $_.Version -eq '2.36.1'
    } |
    Select-Object Name,Version
```

---

# Graph module cleanup command

To remove **every Graph submodule belonging specifically to version 2.36.1**, while preserving 2.25.0:

```powershell
Get-InstalledModule |
    Where-Object {
        $_.Name -like 'Microsoft.Graph*' -and
        $_.Version -eq '2.36.1'
    } |
    ForEach-Object {
        Uninstall-Module -Name $_.Name -RequiredVersion $_.Version -Force
    }
```

This is a useful reusable module-family cleanup pattern:

```text
enumerate modules
→ filter by family
→ filter by exact version
→ uninstall every exact match
```

Because the filter explicitly targeted:

```text
Version == 2.36.1
```

the installed `2.25.0` modules were left intact.

---

# Result

After removing **all** Microsoft Graph PowerShell `2.36.1` submodules and leaving `2.25.0` installed:

**THE APPLICATION WORKED.**

That effectively confirmed the root cause.

## Confirmed compatible environment

At this point, the known-working configuration is:

```text
APPLICATION / NUGET
────────────────────────────────────────
Microsoft.Graph                         5.105.0
Microsoft.Graph.Core                    3.2.5
Microsoft.Kiota.Abstractions            1.21.1
Microsoft.Kiota.Authentication.Azure    1.21.1
Microsoft.Kiota.Http.HttpClientLibrary  1.21.1
Azure.Core                              1.50.0
Microsoft.PowerShell.SDK                7.4.15


MACHINE POWERSHELL MODULES
────────────────────────────────────────
Microsoft.Graph.*                       2.25.0
Microsoft.Graph.Authentication          2.25.0
```

Graph PowerShell `2.36.1` is **not compatible with the current in-process dependency environment**.

---

# MSAL cache was NOT the problem

We considered deleting:

```text
msal_cache.bin
```

from the application's data directory.

We deliberately did **not** do so during the test.

That was the correct choice because the error was:

```text
MethodNotFoundException
```

rather than an authentication/token exception.

Keeping the MSAL cache untouched also preserved a clean A/B test. The only meaningful environmental change was the Graph PowerShell version.

Since changing Graph PowerShell from `2.36.1` to `2.25.0` fixed the application, token-cache corruption can essentially be ruled out for this incident.

---

# PowerShell version note

The failing machine reported:

```text
PowerShell 7.6.5
```

The application itself references:

```text
Microsoft.PowerShell.SDK 7.4.15
```

This did **not** prove relevant to this specific failure.

An installed standalone PowerShell version is not necessarily the same runtime used by the embedded PowerShell host.

Do not chase this unless another issue gives reason to.

---

# What NOT to do casually

## Do not casually run

```powershell
Update-Module Microsoft.Graph
```

on a machine expected to run this build.

A Graph PowerShell update can break the application even if **none of the application's source code or NuGet dependencies change**.

## Do not casually upgrade

```text
Microsoft.Graph 5.x → 6.x
```

in NuGet either.

That upgrade caused:

```text
Microsoft.Graph.Core 3.x → 4.x
Kiota 1.x → 2.x
```

which is a major dependency change and should be treated as an intentional compatibility project, not a routine package update.

We rolled that change back during this investigation, so Graph 6 compatibility is currently **unknown**, not proven broken or supported.

---

# Improvements to make when development resumes

The biggest architectural weakness is that the app currently depends on a machine-installed PowerShell module whose version can drift independently of the application.

## 1. Add a startup/runtime prerequisite check

Detect `Microsoft.Graph.Authentication` and verify that a supported version is available.

Instead of eventually showing:

```text
Method not found...
```

show something useful:

```text
Unsupported Microsoft Graph PowerShell version detected.

Installed: 2.36.1
Supported: 2.25.0
```

## 2. Explicitly load the supported Graph PowerShell version

If practical, do not allow ordinary PowerShell module resolution to silently pick the highest installed Graph version.

Ideally the embedded runspace should explicitly request the version the application was tested against.

## 3. Consider bundling or isolating PowerShell dependencies

Longer term, the application should ideally not depend on whichever Graph modules happen to exist in:

```text
Documents\PowerShell\Modules
```

That makes application behavior dependent on workstation state.

## 4. Investigate whether both Graph SDK access methods are necessary

The app currently combines:

```text
C# Graph SDK
+
Graph PowerShell SDK
```

in one process.

If portions of the PowerShell functionality could instead use the C# Graph client, that would reduce assembly-conflict risk.

Exchange Online PowerShell may still be necessary for Exchange-specific operations, so this does **not** necessarily mean eliminating embedded PowerShell altogether.

## 5. Treat Graph 6 as its own upgrade task

If or when upgrading from:

```text
Microsoft.Graph 5.105.0
```

to 6.x, test the entire matrix:

```text
Graph SDK
Graph.Core
Kiota
Graph PowerShell
embedded PowerShell SDK
```

together.

---

# Quick recovery procedure if this exact error returns

If this appears again:

```text
Method not found:
Microsoft.Graph.Authentication.AzureIdentityAccessTokenProvider..ctor(...)
```

start here.

## Check application packages

```powershell
dotnet list .\SharedMailbox.App\SharedMailbox.App.csproj package --include-transitive |
    Select-String "Graph|Kiota|Azure.Core|PowerShell"
```

Expected baseline:

```text
Microsoft.Graph 5.105.0
Microsoft.Graph.Core 3.2.5
Microsoft.Kiota.* 1.21.1
Microsoft.PowerShell.SDK 7.4.15
```

## Check machine Graph modules

```powershell
Get-Module Microsoft.Graph* -ListAvailable | Select-Object Name,Version,ModuleBase | Sort-Object Name,Version
```

If a newer Graph PowerShell release has appeared, especially in place of `2.25.0`, suspect that first.

## Check specifically

```powershell
Get-Module Microsoft.Graph.Authentication -ListAvailable | Select-Object Name,Version,ModuleBase
```

Current known-working version:

```text
2.25.0
```

## Find an unwanted version, for example 2.36.1

```powershell
Get-InstalledModule | Where-Object { $_.Name -like 'Microsoft.Graph*' -and $_.Version -eq '2.36.1' } | Select-Object Name,Version
```

## Remove that exact Graph version family

```powershell
Get-InstalledModule | Where-Object { $_.Name -like 'Microsoft.Graph*' -and $_.Version -eq '2.36.1' } | ForEach-Object { Uninstall-Module -Name $_.Name -RequiredVersion $_.Version -Force }
```

## Reinstall the known-compatible version

```powershell
Install-Module Microsoft.Graph -RequiredVersion 2.25.0 -Scope CurrentUser -AllowClobber
```

Then completely restart Visual Studio and the app before retesting so an old Graph/Kiota assembly is not still loaded in the existing process.

---

# TL;DR for Future Jon

The Shared Mailbox Manager currently has a hard compatibility relationship between its C# Graph/Kiota packages and the machine-installed Microsoft Graph PowerShell SDK.

Known good:

```text
C# Microsoft.Graph         5.105.0
C# Kiota                   1.21.1
Graph PowerShell           2.25.0
PowerShell SDK             7.4.15
```

Known bad:

```text
same C# application
+
Graph PowerShell 2.36.1
=
AzureIdentityAccessTokenProvider MethodNotFoundException
```

Removing **all** Graph PowerShell `2.36.1` submodules and restoring `2.25.0` immediately fixed the application.

So if this happens again:

> **Check the machine's Graph PowerShell version before tearing apart the code.**

using System.Collections;
using System.Management.Automation;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using SharedMailbox.Core.Domain;
using SharedMailbox.Core.Services;
using SharedMailbox.PowerShell.Adapters;
using SharedMailbox.PowerShell.Hosting;

namespace SharedMailbox.Tests.PowerShell.Adapters;

/// <summary>
/// Tests for <see cref="PowerShellSharedMailboxService"/>. NSubstitute mocks the
/// <see cref="IPowerShellHost"/> dependency so we can:
///   1. Stub PS pipeline results with hand-rolled PSObjects (no runspace required).
///   2. Verify the adapter sent the right cmdlet + parameters by inspecting the
///      strings/dicts it passed to InvokeAsync.
///
/// These tests intentionally don't exercise the PowerShell scripts themselves — they
/// guarantee the adapter does the bookkeeping correctly. A live integration test
/// against EXO/Graph would be needed to verify the scripts themselves still work
/// with whatever module version is deployed.
/// </summary>
public class PowerShellSharedMailboxServiceTests
{
    private readonly IPowerShellHost _host = Substitute.For<IPowerShellHost>();
    private readonly IGraphUserLookup _graphLookup = Substitute.For<IGraphUserLookup>();
    private readonly PowerShellSharedMailboxService _service;

    public PowerShellSharedMailboxServiceTests()
    {
        _service = new PowerShellSharedMailboxService(
            _host,
            _graphLookup,
            NullLogger<PowerShellSharedMailboxService>.Instance);

        // Default graph lookup behavior: every UPN resolves as a healthy, enabled user.
        // Tests that need a blocked account override this for specific UPNs.
        _graphLookup.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(new UserSignInStatus(
                UserLookupStatus.Ok,
                DisplayName: "Display Name",
                UserPrincipalName: call.Arg<string>(),
                AccountEnabled: true)));

        // Default host behavior: empty PSObject list. Tests override per-script with the
        // ConfigureScriptResponse helper below.
        _host.InvokeAsync(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, object?>>(),
                Arg.Any<IProgress<PowerShellStreamEvent>?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<PSObject>>(Array.Empty<PSObject>()));
    }

    // =======================================================================
    // GetGroupMembersAsync
    // =======================================================================

    [Fact]
    public async Task GetGroupMembersAsync_PassesGroupIdAsScriptParameter()
    {
        var groupId = Guid.NewGuid();
        ConfigureScriptResponse("Get-MgGroupMember", Array.Empty<PSObject>());

        await _service.GetGroupMembersAsync(groupId);

        await _host.Received(1).InvokeAsync(
            Arg.Is<string>(s => s.Contains("Get-MgGroupMember")),
            Arg.Is<IReadOnlyDictionary<string, object?>>(d =>
                d.ContainsKey("GroupId") && d["GroupId"]!.ToString() == groupId.ToString()),
            Arg.Any<IProgress<PowerShellStreamEvent>?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetGroupMembersAsync_FiltersOutMembersWithNoUpn()
    {
        ConfigureScriptResponse("Get-MgGroupMember", new[]
        {
            GroupMember("id-1", upn: "alice@tenant.com"),
            GroupMember("id-2", upn: null), // no UPN in AdditionalProperties → filtered out
            GroupMember("id-3", upn: "bob@tenant.com"),
        });

        var result = await _service.GetGroupMembersAsync(Guid.NewGuid());

        result.Should().HaveCount(2);
        result.Select(m => m.UserPrincipalName).Should().Equal("alice@tenant.com", "bob@tenant.com");
    }

    [Fact]
    public async Task GetGroupMembersAsync_FiltersOutMembersWhereUpnLacksAtSign()
    {
        // Mirrors the script's `-like '*@*'` predicate. Pseudo-entries like SIDs or DNs
        // sometimes show up under AdditionalProperties.userPrincipalName.
        ConfigureScriptResponse("Get-MgGroupMember", new[]
        {
            GroupMember("id-1", upn: "alice@tenant.com"),
            GroupMember("id-2", upn: "S-1-5-21-not-an-email"),
            GroupMember("id-3", upn: "bob@tenant.com"),
        });

        var result = await _service.GetGroupMembersAsync(Guid.NewGuid());

        result.Should().HaveCount(2);
        result.Select(m => m.UserPrincipalName).Should().Equal("alice@tenant.com", "bob@tenant.com");
    }

    [Fact]
    public async Task GetGroupMembersAsync_ProducesMailboxWithUnknownRecipientType()
    {
        // Get-MgGroupMember doesn't return RecipientTypeDetails — that's an Exchange
        // concept. Adapter should leave it as Unknown until FilterSharedMailboxesAsync
        // resolves the real type.
        ConfigureScriptResponse("Get-MgGroupMember", new[]
        {
            GroupMember(Guid.NewGuid().ToString(), upn: "alice@tenant.com"),
        });

        var result = await _service.GetGroupMembersAsync(Guid.NewGuid());

        result.Single().RecipientType.Should().Be(RecipientTypeDetails.Unknown);
    }

    // =======================================================================
    // AuditAsync — IncludeSendAs branching
    // =======================================================================

    [Fact]
    public async Task AuditAsync_IncludeSendAsFalse_DoesNotCallGetRecipientPermission()
    {
        await _service.AuditAsync(
            mailboxUpns: new[] { "shared@tenant.com" },
            includeSendAs: false);

        await _host.DidNotReceive().InvokeAsync(
            Arg.Is<string>(s => s.Contains("Get-RecipientPermission")),
            Arg.Any<IReadOnlyDictionary<string, object?>>(),
            Arg.Any<IProgress<PowerShellStreamEvent>?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AuditAsync_IncludeSendAsTrue_CallsGetRecipientPermission()
    {
        await _service.AuditAsync(
            mailboxUpns: new[] { "shared@tenant.com" },
            includeSendAs: true);

        await _host.Received().InvokeAsync(
            Arg.Is<string>(s => s.Contains("Get-RecipientPermission")),
            Arg.Any<IReadOnlyDictionary<string, object?>>(),
            Arg.Any<IProgress<PowerShellStreamEvent>?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AuditAsync_ProducesOneRowPerTrusteeWithCorrectRights()
    {
        // FullAccess: alice + bob. SendAs scanning OFF. GrantSendOnBehalfTo: carol.
        // Expected rows (sorted): alice (FA), bob (FA), carol (SOB).
        _host.InvokeAsync(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, object?>>(),
                Arg.Any<IProgress<PowerShellStreamEvent>?>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var script = (string)call[0];
                if (script.Contains("Get-MailboxPermission"))
                {
                    return Task.FromResult<IReadOnlyList<PSObject>>(new[]
                    {
                        Ps(("User", "alice@tenant.com")),
                        Ps(("User", "bob@tenant.com")),
                    });
                }
                if (script.Contains("Get-Mailbox"))
                {
                    return Task.FromResult<IReadOnlyList<PSObject>>(new[]
                    {
                        Ps(("GrantSendOnBehalfTo", new[] { "carol@tenant.com" })),
                    });
                }
                return Task.FromResult<IReadOnlyList<PSObject>>(Array.Empty<PSObject>());
            });

        var result = await _service.AuditAsync(
            mailboxUpns: new[] { "shared@tenant.com" },
            includeSendAs: false);

        result.Should().HaveCount(3);
        result.Should().ContainSingle(r => r.Trustee == "alice@tenant.com" && r.FullAccess && !r.SendOnBehalf);
        result.Should().ContainSingle(r => r.Trustee == "bob@tenant.com" && r.FullAccess && !r.SendOnBehalf);
        result.Should().ContainSingle(r => r.Trustee == "carol@tenant.com" && !r.FullAccess && r.SendOnBehalf);
        result.Should().AllSatisfy(r => r.SendAsScanned.Should().BeFalse());
    }

    // =======================================================================
    // RemoveDelegatesAsync — cmdlet dispatch + failure capture
    // =======================================================================

    [Fact]
    public async Task RemoveDelegatesAsync_FullAccessRow_CallsRemoveMailboxPermission()
    {
        var row = Row(fullAccess: true);

        await _service.RemoveDelegatesAsync(new[] { row }, includeSendAs: false);

        await _host.Received(1).InvokeAsync(
            Arg.Is<string>(s => s.Contains("Remove-MailboxPermission")),
            Arg.Any<IReadOnlyDictionary<string, object?>>(),
            Arg.Any<IProgress<PowerShellStreamEvent>?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RemoveDelegatesAsync_SendAsRow_IncludeSendAsFalse_DoesNotCallRemoveRecipientPermission()
    {
        // The cleanup row says the trustee has SendAs, but the user chose not to scan/clean
        // SendAs this run. The adapter must respect the includeSendAs flag and skip it.
        var row = Row(fullAccess: false, sendAs: true);

        await _service.RemoveDelegatesAsync(new[] { row }, includeSendAs: false);

        await _host.DidNotReceive().InvokeAsync(
            Arg.Is<string>(s => s.Contains("Remove-RecipientPermission")),
            Arg.Any<IReadOnlyDictionary<string, object?>>(),
            Arg.Any<IProgress<PowerShellStreamEvent>?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RemoveDelegatesAsync_SendOnBehalfRow_CallsSetMailboxRemove()
    {
        var row = Row(sendOnBehalf: true);

        await _service.RemoveDelegatesAsync(new[] { row }, includeSendAs: false);

        await _host.Received(1).InvokeAsync(
            Arg.Is<string>(s => s.Contains("Set-Mailbox") && s.Contains("GrantSendOnBehalfTo")),
            Arg.Any<IReadOnlyDictionary<string, object?>>(),
            Arg.Any<IProgress<PowerShellStreamEvent>?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RemoveDelegatesAsync_WhenHostThrows_RecordsFailureWithErrorMessage()
    {
        // When Remove-MailboxPermission throws, the action result should be Failed with
        // the exception message captured in Notes for the cleanup CSV.
        _host.InvokeAsync(
                Arg.Is<string>(s => s.Contains("Remove-MailboxPermission")),
                Arg.Any<IReadOnlyDictionary<string, object?>>(),
                Arg.Any<IProgress<PowerShellStreamEvent>?>(),
                Arg.Any<CancellationToken>())
            .Throws(new PowerShellInvocationException(
                "Insufficient access rights to perform the operation.",
                new[] { "ACL error" }));

        var row = Row(fullAccess: true);
        var actions = await _service.RemoveDelegatesAsync(new[] { row }, includeSendAs: false);

        actions.Should().ContainSingle(a =>
            a.Right == AccessRight.FullAccess &&
            a.Result == ActionResult.Failed &&
            a.Notes != null &&
            a.Notes.Contains("Insufficient access rights"));
    }

    // =======================================================================
    // AddUsersToMailboxesAsync — grant logic
    // =======================================================================

    [Fact]
    public async Task AddUsersToMailboxesAsync_WhenFullAccessAlreadyExists_DoesNotCallAdd()
    {
        // First call (existence check) returns a non-empty result → adapter must NOT
        // call Add-MailboxPermission. This is the "already present" optimization in
        // the script that prevents redundant grants.
        _host.InvokeAsync(
                Arg.Is<string>(s => s.Contains("Get-MailboxPermission")),
                Arg.Any<IReadOnlyDictionary<string, object?>>(),
                Arg.Any<IProgress<PowerShellStreamEvent>?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<PSObject>>(new[]
            {
                Ps(("User", "user@tenant.com")),
            }));

        var results = await _service.AddUsersToMailboxesAsync(
            userUpns: new[] { "user@tenant.com" },
            mailboxUpns: new[] { "shared@tenant.com" },
            grantSendAs: false);

        await _host.DidNotReceive().InvokeAsync(
            Arg.Is<string>(s => s.Contains("Add-MailboxPermission")),
            Arg.Any<IReadOnlyDictionary<string, object?>>(),
            Arg.Any<IProgress<PowerShellStreamEvent>?>(),
            Arg.Any<CancellationToken>());

        results.Should().ContainSingle(r => r.FullAccessOutcome == PermissionOutcome.AlreadyPresent);
    }

    [Fact]
    public async Task AddUsersToMailboxesAsync_WhenFullAccessAbsent_CallsAddAndReportsGranted()
    {
        // Default host setup returns empty for Get-MailboxPermission, so existence check
        // sees "not present" and the adapter proceeds to call Add-MailboxPermission.
        var results = await _service.AddUsersToMailboxesAsync(
            userUpns: new[] { "user@tenant.com" },
            mailboxUpns: new[] { "shared@tenant.com" },
            grantSendAs: false);

        await _host.Received(1).InvokeAsync(
            Arg.Is<string>(s => s.Contains("Add-MailboxPermission")),
            Arg.Any<IReadOnlyDictionary<string, object?>>(),
            Arg.Any<IProgress<PowerShellStreamEvent>?>(),
            Arg.Any<CancellationToken>());

        results.Should().ContainSingle(r => r.FullAccessOutcome == PermissionOutcome.Granted);
    }

    [Fact]
    public async Task AddUsersToMailboxesAsync_GrantSendAsFalse_RecordsSendAsAsNotAttempted()
    {
        var results = await _service.AddUsersToMailboxesAsync(
            userUpns: new[] { "user@tenant.com" },
            mailboxUpns: new[] { "shared@tenant.com" },
            grantSendAs: false);

        // No Add-RecipientPermission call, no Get-RecipientPermission existence check.
        await _host.DidNotReceive().InvokeAsync(
            Arg.Is<string>(s => s.Contains("Add-RecipientPermission") || s.Contains("Get-RecipientPermission")),
            Arg.Any<IReadOnlyDictionary<string, object?>>(),
            Arg.Any<IProgress<PowerShellStreamEvent>?>(),
            Arg.Any<CancellationToken>());

        results.Single().SendAsOutcome.Should().Be(PermissionOutcome.NotAttempted);
    }

    [Fact]
    public async Task AddUsersToMailboxesAsync_FansOutOverEveryUserMailboxPair()
    {
        // 2 users × 3 mailboxes = 6 result rows, one per pair.
        var users = new[] { "u1@t.com", "u2@t.com" };
        var mailboxes = new[] { "m1@t.com", "m2@t.com", "m3@t.com" };

        var results = await _service.AddUsersToMailboxesAsync(users, mailboxes, grantSendAs: false);

        results.Should().HaveCount(6);
        results.Select(r => (r.UserUpn, r.SharedMailboxAddress)).Should().BeEquivalentTo(new[]
        {
            ("u1@t.com", "m1@t.com"), ("u1@t.com", "m2@t.com"), ("u1@t.com", "m3@t.com"),
            ("u2@t.com", "m1@t.com"), ("u2@t.com", "m2@t.com"), ("u2@t.com", "m3@t.com"),
        });
    }

    // =======================================================================
    // Helpers
    // =======================================================================

    /// <summary>Configures the mock host to return <paramref name="response"/> for any
    /// script that contains <paramref name="scriptFragment"/>.</summary>
    private void ConfigureScriptResponse(string scriptFragment, IReadOnlyList<PSObject> response)
    {
        _host.InvokeAsync(
                Arg.Is<string>(s => s.Contains(scriptFragment)),
                Arg.Any<IReadOnlyDictionary<string, object?>>(),
                Arg.Any<IProgress<PowerShellStreamEvent>?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(response));
    }

    /// <summary>
    /// Build a PSObject with the given properties. Mirrors what
    /// <c>System.Management.Automation</c> produces when a real cmdlet result is wrapped.
    /// </summary>
    private static PSObject Ps(params (string Name, object? Value)[] properties)
    {
        var obj = new PSObject();
        foreach (var (name, value) in properties)
        {
            obj.Properties.Add(new PSNoteProperty(name, value));
        }
        return obj;
    }

    /// <summary>
    /// Build a fake <c>Get-MgGroupMember</c> result. The real cmdlet returns a
    /// DirectoryObject with an <c>AdditionalProperties</c> IDictionary containing
    /// <c>userPrincipalName</c>; we replicate that shape here.
    /// </summary>
    private static PSObject GroupMember(string id, string? upn)
    {
        var additional = new Hashtable();
        if (upn is not null) additional["userPrincipalName"] = upn;
        return Ps(
            ("Id", id),
            ("AdditionalProperties", additional));
    }

    /// <summary>Build a DelegateReport row for cleanup tests.</summary>
    private static DelegateReport Row(
        bool fullAccess = false,
        bool sendAs = false,
        bool sendOnBehalf = false) =>
        new()
        {
            Mailbox = "shared@tenant.com",
            Trustee = "trustee@tenant.com",
            FullAccess = fullAccess,
            SendAs = sendAs,
            SendOnBehalf = sendOnBehalf,
            LookupStatus = UserLookupStatus.Ok,
            AccountEnabled = false,
            SignInBlocked = true,
            SendAsScanned = true,
        };
}

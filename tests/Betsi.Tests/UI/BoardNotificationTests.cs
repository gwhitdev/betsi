namespace Betsi.Tests.UI;

using Betsi.Application.Commands;
using Betsi.Infrastructure.Tenancy;
using Betsi.UI;

/// <summary>
/// What tells a board it is out of date (MVP-023: under ten seconds' staleness).
/// </summary>
/// <remarks>
/// The push is in the command pipeline rather than off the outbox, because the outbox drains on
/// a timer and a waiting-time escalation should reach a screen in the second it is raised. These
/// tests hold the two properties that follow from that: a command that did not happen must not
/// nudge anything, and a failure to reach a websocket must not fail a clinical action.
/// </remarks>
public class BoardNotificationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class RecordingNotifier : IBoardNotifier
    {
        public List<(Guid TenantId, string Reason)> Sent { get; } = [];

        public Task ChangedAsync(Guid tenantId, string reason, CancellationToken cancellationToken)
        {
            Sent.Add((tenantId, reason));
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingNotifier : IBoardNotifier
    {
        public Task ChangedAsync(Guid tenantId, string reason, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The hub is unreachable.");
    }

    private static TenantContext ResolvedContext(Guid tenantId)
    {
        var context = new TenantContext();
        context.Resolve(tenantId, Guid.NewGuid(), "Nurse");
        return context;
    }

    private static BoardNotificationBehaviour<RegisterPatientCommand, CommandResult> Behaviour(
        IBoardNotifier notifier, ITenantContext tenantContext) => new(notifier, tenantContext);

    [Fact]
    public async Task A_successful_command_nudges_its_own_tenants_boards()
    {
        var tenantId = Guid.NewGuid();
        var notifier = new RecordingNotifier();

        await Behaviour(notifier, ResolvedContext(tenantId)).Handle(
            new RegisterPatientCommand(),
            _ => Task.FromResult(new CommandResult(Guid.NewGuid(), 1)),
            Ct);

        var sent = notifier.Sent.ShouldHaveSingleItem();
        sent.TenantId.ShouldBe(tenantId);
        // The reason names the command, not its contents: this string reaches every connected
        // browser in the department.
        sent.Reason.ShouldBe(nameof(RegisterPatientCommand));
    }

    [Fact]
    public async Task A_command_that_threw_nudges_nothing()
    {
        // Otherwise a refused or invalid command would have every board in the department
        // refetch, and a caller retrying a failing command would be a denial of service.
        var notifier = new RecordingNotifier();

        await Should.ThrowAsync<InvalidOperationException>(() =>
            Behaviour(notifier, ResolvedContext(Guid.NewGuid())).Handle(
                new RegisterPatientCommand(),
                _ => throw new InvalidOperationException("Rejected."),
                Ct));

        notifier.Sent.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_command_with_no_resolved_tenant_nudges_nothing()
    {
        var notifier = new RecordingNotifier();

        await Behaviour(notifier, new TenantContext()).Handle(
            new RegisterPatientCommand(),
            _ => Task.FromResult(new CommandResult(Guid.NewGuid(), 1)),
            Ct);

        notifier.Sent.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_notifier_that_throws_still_fails_the_request()
    {
        // The behaviour does not swallow: BoardNotifier itself catches, because that is where
        // "the hub is unreachable" is known to be a display problem. A different notifier
        // throwing is a bug, and a bug that is swallowed here would be invisible.
        await Should.ThrowAsync<InvalidOperationException>(() =>
            Behaviour(new ThrowingNotifier(), ResolvedContext(Guid.NewGuid())).Handle(
                new RegisterPatientCommand(),
                _ => Task.FromResult(new CommandResult(Guid.NewGuid(), 1)),
                Ct));
    }

    [Fact]
    public void Each_tenant_has_its_own_group()
    {
        var one = Guid.NewGuid();
        var other = Guid.NewGuid();

        BoardHub.GroupFor(one).ShouldNotBe(BoardHub.GroupFor(other));
        BoardHub.GroupFor(one).ShouldBe(BoardHub.GroupFor(one));
        BoardHub.GroupFor(one).ShouldContain(one.ToString());
    }
}

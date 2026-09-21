namespace Betsi.UI;

using Betsi.Application.Commands;
using Betsi.Infrastructure.Tenancy;
using Betsi.Security;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

/// <summary>
/// Pushes "something changed" to the boards of one tenant (MVP-023: under ten seconds' staleness).
/// </summary>
/// <remarks>
/// <para>
/// A signal, not a payload. The hub says a department's state has changed and each board
/// refetches what it is entitled to see; it never pushes patient data down a connection whose
/// permissions it would have to re-derive. That keeps one authorisation path — the queries — and
/// means a board showing less than another cannot be tricked into rendering more.
/// </para>
/// <para>
/// Groups are per tenant, joined from the connection's own verified tenant claim, so a
/// subscriber cannot name a department it does not belong to.
/// </para>
/// </remarks>
[Authorize]
public sealed class BoardHub : Hub
{
    public const string ChangedMethod = "Changed";

    private readonly BetsiClaimOptions _claims;
    private readonly ILogger<BoardHub> _logger;

    public BoardHub(BetsiClaimOptions claims, ILogger<BoardHub> logger)
    {
        _claims = claims;
        _logger = logger;
    }

    public static string GroupFor(Guid tenantId) => $"tenant:{tenantId}";

    public override async Task OnConnectedAsync()
    {
        if (!Guid.TryParse(Context.User?.FindFirst(_claims.Tenant)?.Value, out var tenantId))
        {
            // Nothing to subscribe to. Aborting rather than leaving a connection that silently
            // receives nothing, which would look like a department where nothing ever happens.
            _logger.LogWarning("Refused a board subscription with no tenant claim");
            Context.Abort();
            return;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, GroupFor(tenantId));
        await base.OnConnectedAsync();
    }
}

/// <summary>Tells a tenant's connected boards that something changed.</summary>
public interface IBoardNotifier
{
    Task ChangedAsync(Guid tenantId, string reason, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IBoardNotifier"/>
public sealed class BoardNotifier : IBoardNotifier
{
    private readonly IHubContext<BoardHub> _hub;
    private readonly ILogger<BoardNotifier> _logger;

    public BoardNotifier(IHubContext<BoardHub> hub, ILogger<BoardNotifier> logger)
    {
        _hub = hub;
        _logger = logger;
    }

    public async Task ChangedAsync(Guid tenantId, string reason, CancellationToken cancellationToken)
    {
        try
        {
            // A display nudge must never hold a committed clinical or configuration command
            // open indefinitely. Reconciliation on reconnect remains the source of truth.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(1));
            await _hub.Clients.Group(BoardHub.GroupFor(tenantId))
                .SendAsync(BoardHub.ChangedMethod, reason, timeout.Token);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A board that misses a nudge is stale until its next one; a command that fails
            // because a websocket did is a clinical action lost to a display concern.
            _logger.LogWarning(exception, "Could not notify boards for tenant {TenantId}", tenantId);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(exception, "Timed out notifying boards for tenant {TenantId}", tenantId);
        }
    }
}

/// <summary>
/// Notifies the tenant's boards after every command that succeeds.
/// </summary>
/// <remarks>
/// In the pipeline rather than off the outbox, because the outbox drains on a timer and a
/// waiting-time escalation should reach a screen in the second it is raised, not at the next
/// poll. Constrained to commands, so a query never nudges anything. Automatic escalations go
/// through the same commands as human ones, so the background monitor is covered too.
/// </remarks>
public sealed class BoardNotificationBehaviour<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : ICommand
{
    private readonly IBoardNotifier _notifier;
    private readonly ITenantContext _tenantContext;

    public BoardNotificationBehaviour(IBoardNotifier notifier, ITenantContext tenantContext)
    {
        _notifier = notifier;
        _tenantContext = tenantContext;
    }

    public async Task<TResponse> Handle(
        TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        var response = await next();

        if (_tenantContext.IsResolved)
            await _notifier.ChangedAsync(_tenantContext.TenantId, typeof(TRequest).Name, cancellationToken);

        return response;
    }
}

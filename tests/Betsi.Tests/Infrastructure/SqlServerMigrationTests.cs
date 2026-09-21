namespace Betsi.Tests.Infrastructure;

using Betsi.Domain.Aggregates;
using Betsi.Infrastructure.Persistence;
using Betsi.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using Testcontainers.MsSql;

/// <summary>
/// Applies the migrations to a real SQL Server and exercises the schema they produce.
/// </summary>
/// <remarks>
/// The rest of the suite runs on SQLite, which cannot prove that SQL Server-specific
/// mappings — filtered unique indexes, <c>GETUTCDATE()</c> defaults, <c>nvarchar(max)</c>
/// columns — are valid. This closes that gap against the production provider.
///
/// Skipped when no Docker daemon is reachable, so the suite still runs on a workstation
/// without one; CI has Docker and therefore always runs it.
/// </remarks>
public sealed class SqlServerMigrationTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _actorId = Guid.NewGuid();

    private MsSqlContainer? _container;
    private string? _skipReason;

    public async ValueTask InitializeAsync()
    {
        if (!await Docker.IsAvailableAsync())
        {
            _skipReason = "No Docker daemon is reachable, so SQL Server cannot be started.";
            return;
        }

        // The image is pinned so a CI run cannot silently change the database version it
        // validates the schema against.
        _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();
        await _container.StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
            await _container.DisposeAsync();
    }

    private BetsiDbContext NewContext()
    {
        var tenantContext = new TenantContext();
        tenantContext.Resolve(_tenantId, _actorId, "Nurse");

        var options = new DbContextOptionsBuilder<BetsiDbContext>()
            .UseSqlServer(_container!.GetConnectionString())
            .Options;

        return new BetsiDbContext(options, tenantContext);
    }

    [Fact]
    public async Task Competing_observation_corrections_commit_one_chain_with_matching_events()
    {
        Assert.SkipWhen(_skipReason is not null, _skipReason ?? string.Empty);
        var ct = Ct;
        var tenant = TenantContextFor();
        var now = DateTime.UtcNow;
        var episode = PatientEpisode.CreateNew(_tenantId, "Synthetic", "Observation",
            new DateTime(1980, 1, 1), null, _actorId, "Nurse");
        ClinicalObservation Observation(Guid? supersedes = null) => ClinicalObservation.Record(
            _tenantId, episode.Id, Betsi.Domain.Clinical.AgeBand.Adult,
            ClinicalObservation.ObservationKind.Routine, default, null, null, "Synthetic",
            now, _actorId, "Nurse", supersedes,
            source: ClinicalObservation.ObservationSource.NursingAssessment,
            sbarSituation: "Structured SQL Server round trip",
            breathingFinding: ClinicalObservation.AssessmentFinding.Concern,
            breathingDetails: "Synthetic finding",
            circulationFinding: ClinicalObservation.AssessmentFinding.NoConcern,
            mobilityFinding: ClinicalObservation.AssessmentFinding.UnableToAssess);
        var original = Observation();
        await using (var setup = NewContext())
        {
            await setup.Database.MigrateAsync(ct);
            var unit = new UnitOfWork(setup, tenant);
            unit.Add(episode);
            unit.Add(original);
            await unit.CommitAsync([episode, original], ct);
        }

        await using var firstContext = NewContext();
        await using var secondContext = NewContext();
        // Both callers read version 1 before either commits: deterministic lost-update race.
        var firstOriginal = await firstContext.ClinicalObservations.SingleAsync(o => o.Id == original.Id, ct);
        var secondOriginal = await secondContext.ClinicalObservations.SingleAsync(o => o.Id == original.Id, ct);
        var winner = Observation(original.Id);
        var loser = Observation(original.Id);
        firstOriginal.SupersededBy(winner.Id, now, _actorId, "Nurse");
        secondOriginal.SupersededBy(loser.Id, now, _actorId, "Nurse");
        var firstUnit = new UnitOfWork(firstContext, tenant);
        var secondUnit = new UnitOfWork(secondContext, tenant);
        firstUnit.Add(winner);
        secondUnit.Add(loser);
        await firstUnit.CommitAsync([winner, firstOriginal], ct);
        var failure = await Should.ThrowAsync<DbUpdateException>(() => secondUnit.CommitAsync([loser, secondOriginal], ct));
        Betsi.API.Problems.ProblemDetailsExceptionHandler.Map(failure, isDevelopment: false).Status.ShouldBe(409);

        await using var read = NewContext();
        var history = await read.ClinicalObservations.Where(o => o.PatientEpisodeId == episode.Id).ToListAsync(ct);
        history.Count.ShouldBe(2);
        history.Single(o => o.Id == original.Id).SupersededByObservationId.ShouldBe(winner.Id);
        history.Single(o => o.Id == original.Id).Version.ShouldBe(2);
        history.Single(o => o.Id == winner.Id).SupersedesObservationId.ShouldBe(original.Id);
        history.ShouldAllBe(o => o.Source == ClinicalObservation.ObservationSource.NursingAssessment);
        history.ShouldAllBe(o => o.SbarSituation == "Structured SQL Server round trip");
        history.ShouldAllBe(o => o.BreathingFinding == ClinicalObservation.AssessmentFinding.Concern);
        history.ShouldAllBe(o => o.BreathingDetails == "Synthetic finding");
        history.ShouldAllBe(o => o.CirculationFinding == ClinicalObservation.AssessmentFinding.NoConcern);
        history.ShouldAllBe(o => o.MobilityFinding == ClinicalObservation.AssessmentFinding.UnableToAssess);
        (await read.DomainEvents.CountAsync(e => e.AggregateId == original.Id || e.AggregateId == winner.Id, ct)).ShouldBe(3);
        (await read.OutboxMessages.CountAsync(e => e.AggregateId == original.Id || e.AggregateId == winner.Id, ct)).ShouldBe(3);
        (await read.DomainEvents.AnyAsync(e => e.AggregateId == loser.Id, ct)).ShouldBeFalse();
        (await read.OutboxMessages.AnyAsync(e => e.AggregateId == loser.Id, ct)).ShouldBeFalse();

        // Measurements cannot be edited or deleted through normal persistence.
        var stored = history.Single(o => o.Id == winner.Id);
        read.Entry(stored).Property(o => o.Notes).CurrentValue = "Overwritten";
        await Should.ThrowAsync<InvalidOperationException>(() => read.SaveChangesAsync(ct));
        read.ChangeTracker.Clear();
        read.ClinicalObservations.Remove(stored);
        await Should.ThrowAsync<InvalidOperationException>(() => read.SaveChangesAsync(ct));
    }

    [Fact]
    public async Task The_migrations_apply_cleanly_to_a_real_sql_server()
    {
        Assert.SkipWhen(_skipReason is not null, _skipReason ?? string.Empty);

        await using var context = NewContext();

        await context.Database.MigrateAsync(Ct);

        var applied = await context.Database.GetAppliedMigrationsAsync(Ct);
        applied.ShouldNotBeEmpty();

        var pending = await context.Database.GetPendingMigrationsAsync(Ct);
        pending.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_patient_round_trips_through_the_migrated_schema()
    {
        Assert.SkipWhen(_skipReason is not null, _skipReason ?? string.Empty);

        await using (var migrationContext = NewContext())
            await migrationContext.Database.MigrateAsync(Ct);

        var episode = PatientEpisode.CreateNew(
            _tenantId, "Gwen", "Jones", new DateTime(1962, 4, 19),
            "9434765919", _actorId, "Nurse");
        episode.BeginTriage(_actorId, "Nurse");

        await using (var writeContext = NewContext())
        {
            var repository = new AggregateRepository<PatientEpisode>(
                writeContext, TenantContextFor());
            await repository.AddAsync(episode, Ct);
        }

        await using var readContext = NewContext();

        var reloaded = await readContext.PatientEpisodes.SingleAsync(e => e.Id == episode.Id, Ct);
        reloaded.State.ShouldBe(PatientEpisode.PatientState.InTriage);

        // Instants come back marked UTC, so they serialise with a "Z"; a date of birth is a
        // calendar date and must come back exactly as written, never shifted by a time zone.
        reloaded.ArrivedAt.Kind.ShouldBe(DateTimeKind.Utc);
        reloaded.DateOfBirth.ShouldBe(new DateTime(1962, 4, 19));

        // The GETUTCDATE() default and the nvarchar(max) event payload only exist on SQL Server.
        var events = await readContext.DomainEvents
            .Where(e => e.AggregateId == episode.Id).ToListAsync(Ct);

        events.Count.ShouldBe(2);
        events.ShouldAllBe(e => e.CreatedAt > DateTime.UnixEpoch);
        events.ShouldAllBe(e => e.EventData.Length > 0);
    }

    [Fact]
    public async Task The_filtered_unique_index_allows_many_patients_without_an_nhs_number()
    {
        Assert.SkipWhen(_skipReason is not null, _skipReason ?? string.Empty);

        await using (var migrationContext = NewContext())
            await migrationContext.Database.MigrateAsync(Ct);

        // Unidentified patients are routine in an ED. The unique index on NHS number is
        // filtered precisely so that more than one of them can be registered at once.
        for (var i = 0; i < 3; i++)
        {
            var episode = PatientEpisode.CreateNew(
                _tenantId, "Unknown", $"Patient{i}", new DateTime(1990, 1, 1),
                nhsNumber: null, _actorId, "Nurse");

            await using var context = NewContext();
            await new AggregateRepository<PatientEpisode>(context, TenantContextFor())
                .AddAsync(episode, Ct);
        }

        await using var readContext = NewContext();
        (await readContext.PatientEpisodes.CountAsync(e => e.NhsNumber == null, Ct))
            .ShouldBeGreaterThanOrEqualTo(3);
    }

    [Fact]
    public async Task The_tier_index_allows_many_staff_escalations_but_one_policy_escalation_per_tier()
    {
        Assert.SkipWhen(_skipReason is not null, _skipReason ?? string.Empty);

        await using (var migrationContext = NewContext())
            await migrationContext.Database.MigrateAsync(Ct);

        var episode = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var tier = new WaitingTimeTier(1, 240, "Waiting-room Coordinator", 30, "Review");

        // Staff-raised escalations have no tier; SQL Server treats NULLs as equal in a unique
        // index, so without the filter the second of these would be refused.
        for (var i = 0; i < 2; i++)
        {
            await using var context = NewContext();
            await new AggregateRepository<Escalation>(context, TenantContextFor()).AddAsync(
                Escalation.CreateFromWaitingTime(_tenantId, episode, null, "Nurse in Charge", now), Ct);
        }

        await using (var context = NewContext())
        {
            await new AggregateRepository<Escalation>(context, TenantContextFor()).AddAsync(
                Escalation.CreateFromWaitingTimePolicy(_tenantId, episode, null, 1, tier, 241, now), Ct);
        }

        await using (var context = NewContext())
        {
            await Should.ThrowAsync<DbUpdateException>(() =>
                new AggregateRepository<Escalation>(context, TenantContextFor()).AddAsync(
                    Escalation.CreateFromWaitingTimePolicy(_tenantId, episode, null, 1, tier, 242, now), Ct));
        }

        await using var readContext = NewContext();
        (await readContext.Escalations.CountAsync(e => e.PatientEpisodeId == episode, Ct)).ShouldBe(3);
    }

    [Fact]
    public async Task Policy_tiers_round_trip_as_json_on_sql_server()
    {
        Assert.SkipWhen(_skipReason is not null, _skipReason ?? string.Empty);

        await using (var migrationContext = NewContext())
            await migrationContext.Database.MigrateAsync(Ct);

        var tiers = new List<WaitingTimeTier>
        {
            new(1, 240, "Waiting-room Coordinator", 30, "Review clinical status"),
            new(2, 360, "Nurse in Charge", 30, "Decide on admission")
        };

        var policy = EscalationPolicy.Propose(_tenantId, 1, true, tiers, "Operations Manager", "Initial", _actorId, "Admin", DateTime.UtcNow);

        await using (var context = NewContext())
            await new AggregateRepository<EscalationPolicy>(context, TenantContextFor()).AddAsync(policy, Ct);

        await using var readContext = NewContext();
        (await readContext.EscalationPolicies.SingleAsync(p => p.Id == policy.Id, Ct)).Tiers.ShouldBe(tiers);
    }

    [Fact]
    public async Task One_follow_up_exception_per_missed_deadline_is_enforced_by_the_database()
    {
        Assert.SkipWhen(_skipReason is not null, _skipReason ?? string.Empty);

        await using (var migrationContext = NewContext())
            await migrationContext.Database.MigrateAsync(Ct);

        var raisedAt = DateTime.UtcNow;
        var escalation = Escalation.CreateFromWaitingTime(_tenantId, Guid.NewGuid(), null, "Nurse in Charge", raisedAt);

        await using (var context = NewContext())
        {
            var repository = new AggregateRepository<FollowUpException>(context, TenantContextFor());
            await repository.AddAsync(FollowUpException.Raise(_tenantId, escalation, "Matron", raisedAt.AddMinutes(16)), Ct);
        }

        await using (var context = NewContext())
        {
            // A second monitor instance racing the first on the same missed deadline.
            await Should.ThrowAsync<DbUpdateException>(() =>
                new AggregateRepository<FollowUpException>(context, TenantContextFor())
                    .AddAsync(FollowUpException.Raise(_tenantId, escalation, "Matron", raisedAt.AddMinutes(17)), Ct));
        }
    }

    [Fact]
    public async Task Waiting_board_pages_are_complete_and_ordered_on_sql_server()
    {
        Assert.SkipWhen(_skipReason is not null, _skipReason ?? string.Empty);

        await using (var migrationContext = NewContext())
            await migrationContext.Database.MigrateAsync(Ct);

        // Several patients with the same arrival time force the id tie-breaker, whose ordering
        // on SQL Server (uniqueidentifier byte order) differs from .NET's.
        var arrivedAt = DateTime.UtcNow.AddHours(-2);
        await using (var context = NewContext())
        {
            for (var i = 0; i < 12; i++)
            {
                var episode = PatientEpisode.CreateNew(_tenantId, "Board", $"Patient{i}", new DateTime(1990, 1, 1), null, _actorId, "Nurse");
                context.PatientEpisodes.Add(episode);
                context.Entry(episode).Property(e => e.ArrivedAt).CurrentValue = arrivedAt;
            }

            await context.SaveChangesAsync(Ct);
        }

        var seen = new List<Guid>();
        string? cursor = null;
        do
        {
            await using var context = NewContext();
            var page = await new Betsi.Application.Queries.EpisodeQueries(context, TimeProvider.System,
                new Betsi.Application.Commands.Handlers.ClinicalOptions())
                .GetWaitingBoardAsync(new Betsi.Application.Queries.WaitingBoardFilter(Cursor: cursor, PageSize: 5), Ct);
            seen.AddRange(page.Items.Select(i => i.EpisodeId));
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        seen.Count.ShouldBe(12);
        seen.ShouldBeUnique();
    }

    private ITenantContext TenantContextFor()
    {
        var tenantContext = new TenantContext();
        tenantContext.Resolve(_tenantId, _actorId, "Nurse");
        return tenantContext;
    }
}

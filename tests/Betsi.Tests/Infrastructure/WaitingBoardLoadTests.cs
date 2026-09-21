namespace Betsi.Tests.Infrastructure;

using Betsi.Application.Queries;
using Betsi.Application.Commands.Handlers;
using Betsi.Domain.Aggregates;
using Betsi.Infrastructure.Persistence;
using Betsi.Infrastructure.Tenancy;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using System.Diagnostics;
using Testcontainers.MsSql;

/// <summary>
/// The waiting board's latency at a department's volume, against a real SQL Server (MVP-111).
/// </summary>
/// <remarks>
/// <para>
/// The budget in the backlog is median under 150ms, p95 under 500ms, p99 under 1000ms. Until
/// now it was stated and unmeasured. This measures the read that a whole department stares at,
/// under concurrent load, against a real database with real indexes — the one place the SQLite
/// suites cannot speak for.
/// </para>
/// <para>
/// What this is not: a load test of a deployed system. It runs on one developer's machine
/// against a container, so the absolute numbers are not a site's numbers. What it does catch is
/// the change that turns an index-backed keyset query into a scan, which is the realistic way
/// this budget gets broken — and it fails the build rather than waiting for a pilot to notice.
/// </para>
/// </remarks>
public sealed class WaitingBoardLoadTests : IAsyncLifetime
{
    /// <summary>A busy emergency department, not a quiet one: a hundred and fifty waiting.</summary>
    private const int PatientsWaiting = 150;

    private const int Concurrency = 8;
    private const int RequestsPerWorker = 25;

    private static readonly TimeSpan MedianBudget = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan P95Budget = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan P99Budget = TimeSpan.FromMilliseconds(1000);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly Guid _tenantId = Guid.NewGuid();

    private MsSqlContainer? _container;
    private string? _skipReason;

    public async ValueTask InitializeAsync()
    {
        if (!await Docker.IsAvailableAsync())
        {
            _skipReason = Docker.UnavailableReason;
            return;
        }

        _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();
        await _container.StartAsync();

        await using var context = NewContext();
        await context.Database.MigrateAsync(Ct);

        var arrived = DateTime.UtcNow;
        for (var i = 0; i < PatientsWaiting; i++)
        {
            // Spread across twelve hours, so the keyset paging has a realistic spread of
            // arrival times to order and seek by rather than a single instant.
            var episode = PatientEpisode.CreateNew(
                _tenantId, $"Patient{i}", $"Surname{i}", new DateTime(1950 + (i % 60), 1 + (i % 12), 1 + (i % 28)));

            context.PatientEpisodes.Add(episode);
        }

        await context.SaveChangesAsync(Ct);

        // Arrival times are set by the aggregate; spread them afterwards in one statement rather
        // than fighting the domain model for a test fixture.
        await context.Database.ExecuteSqlRawAsync(
            "UPDATE dbo.PatientEpisodes SET ArrivedAt = DATEADD(minute, -(ABS(CHECKSUM(NEWID())) % 720), {0})",
            [arrived], Ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
            await _container.DisposeAsync();
    }

    private BetsiDbContext NewContext()
    {
        var tenantContext = new TenantContext();
        tenantContext.ResolveSystem(_tenantId);

        var options = new DbContextOptionsBuilder<BetsiDbContext>()
            .UseSqlServer(new SqlConnectionStringBuilder(_container!.GetConnectionString())
            {
                InitialCatalog = "betsi_load"
            }.ConnectionString)
            .Options;

        return new BetsiDbContext(options, tenantContext);
    }

    [Fact]
    public async Task The_waiting_board_meets_its_latency_budget_under_concurrent_load()
    {
        Assert.SkipWhen(_skipReason is not null, _skipReason ?? string.Empty);

        // One warm pass first: the first query of a process pays for EF's model and query
        // compilation, and measuring that would be measuring start-up, not the board.
        await MeasureAsync();

        var timings = new List<TimeSpan>[Concurrency];

        await Task.WhenAll(Enumerable.Range(0, Concurrency).Select(async worker =>
        {
            var measurements = new List<TimeSpan>(RequestsPerWorker);
            for (var i = 0; i < RequestsPerWorker; i++)
                measurements.Add(await MeasureAsync());

            timings[worker] = measurements;
        }));

        var all = timings.SelectMany(t => t).OrderBy(t => t).ToList();
        all.Count.ShouldBe(Concurrency * RequestsPerWorker);

        var median = Percentile(all, 0.50);
        var p95 = Percentile(all, 0.95);
        var p99 = Percentile(all, 0.99);

        // Printed whether or not it passes, and to the console so it reaches a CI log: a number
        // nobody can see is not a measurement, and the evidence pack wants the figure rather
        // than the verdict.
        var summary =
            $"Waiting board: {PatientsWaiting} waiting, {Concurrency} concurrent readers, {all.Count} queries — " +
            $"median {median.TotalMilliseconds:F0}ms, p95 {p95.TotalMilliseconds:F0}ms, " +
            $"p99 {p99.TotalMilliseconds:F0}ms, max {all[^1].TotalMilliseconds:F0}ms";

        TestContext.Current.TestOutputHelper?.WriteLine(summary);
        await RecordAsync(summary);

        median.ShouldBeLessThan(MedianBudget, $"median was {median.TotalMilliseconds:F0}ms");
        p95.ShouldBeLessThan(P95Budget, $"p95 was {p95.TotalMilliseconds:F0}ms");
        p99.ShouldBeLessThan(P99Budget, $"p99 was {p99.TotalMilliseconds:F0}ms");
    }

    [Fact]
    public async Task Paging_through_the_whole_board_stays_within_budget_on_every_page()
    {
        Assert.SkipWhen(_skipReason is not null, _skipReason ?? string.Empty);

        await using var context = NewContext();
        var queries = new EpisodeQueries(context, TimeProvider.System, new ClinicalOptions());

        string? cursor = null;
        var pages = 0;
        var seen = 0;
        var slowest = TimeSpan.Zero;

        do
        {
            var stopwatch = Stopwatch.StartNew();
            var page = await queries.GetWaitingBoardAsync(new WaitingBoardFilter(Cursor: cursor, PageSize: 25), Ct);
            stopwatch.Stop();

            // Keyset paging, not OFFSET: the last page must cost what the first page cost. An
            // OFFSET-paged board gets slower the further down a long night someone scrolls.
            if (stopwatch.Elapsed > slowest)
                slowest = stopwatch.Elapsed;

            seen += page.Items.Count;
            cursor = page.NextCursor;
            pages++;
        }
        while (cursor is not null && pages < 20);

        seen.ShouldBe(PatientsWaiting);
        slowest.ShouldBeLessThan(P95Budget, $"the slowest page took {slowest.TotalMilliseconds:F0}ms");
    }

    private async Task<TimeSpan> MeasureAsync()
    {
        // A context per query, as a request would have: reusing one would measure a warm change
        // tracker rather than what a clinician's browser asks for.
        await using var context = NewContext();
        var queries = new EpisodeQueries(context, TimeProvider.System, new ClinicalOptions());

        var stopwatch = Stopwatch.StartNew();
        var page = await queries.GetWaitingBoardAsync(new WaitingBoardFilter(), Ct);
        stopwatch.Stop();

        page.Items.ShouldNotBeEmpty();
        return stopwatch.Elapsed;
    }

    /// <summary>
    /// Appends the measurement to <c>TestResults/performance.txt</c>.
    /// </summary>
    /// <remarks>
    /// A test runner reports pass or fail; the evidence pack needs the figure. Writing it to a
    /// file means a CI run can keep it as an artefact and a person can paste it into
    /// docs/DSPT-EVIDENCE.md, rather than the number existing only in a console nobody read.
    /// </remarks>
    private static async Task RecordAsync(string summary)
    {
        try
        {
            var directory = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "TestResults");
            Directory.CreateDirectory(directory);

            await File.AppendAllTextAsync(
                Path.Combine(directory, "performance.txt"),
                $"{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}  {summary}{Environment.NewLine}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Recording a number must never fail a build. The assertions below are the gate.
        }
    }

    private static TimeSpan Percentile(IReadOnlyList<TimeSpan> sorted, double percentile)
    {
        var index = (int)Math.Ceiling(percentile * sorted.Count) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Count - 1)];
    }
}

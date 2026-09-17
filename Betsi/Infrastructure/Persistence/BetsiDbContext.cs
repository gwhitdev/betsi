namespace Betsi.Infrastructure.Persistence;

using Betsi.Domain;
using Betsi.Domain.Aggregates;
using Betsi.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using System.Text.Json;

/// <summary>
/// Entity Framework Core DbContext for Betsi Patient Flow.
/// Persists aggregates, domain events, outbox messages, and audit logs.
/// Configured for multi-tenant isolation with tenant_id on all tables.
/// </summary>
public class BetsiDbContext : DbContext
{
    private readonly ITenantContext _tenantContext;

    public BetsiDbContext(DbContextOptions<BetsiDbContext> options, ITenantContext tenantContext)
        : base(options)
    {
        _tenantContext = tenantContext;
    }

    // ============= Aggregates =============
    public DbSet<PatientEpisode> PatientEpisodes { get; set; } = null!;
    public DbSet<Location> Locations { get; set; } = null!;
    public DbSet<Queue> Queues { get; set; } = null!;
    public DbSet<Escalation> Escalations { get; set; } = null!;
    public DbSet<EscalationPolicy> EscalationPolicies { get; set; } = null!;
    public DbSet<FollowUpException> FollowUpExceptions { get; set; } = null!;

    // ============= Event Store & Outbox =============
    public DbSet<DomainEventRecord> DomainEvents { get; set; } = null!;
    public DbSet<OutboxMessage> OutboxMessages { get; set; } = null!;
    public DbSet<AuditLogRecord> AuditLogs { get; set; } = null!;

    /// <summary>
    /// Whether the configured provider is SQL Server.
    /// </summary>
    /// <remarks>
    /// A handful of mappings are provider-specific: filtered-index predicates and
    /// default-value SQL are written in the provider's own dialect. They are applied
    /// conditionally so the same model can be built against SQLite in tests, which is what
    /// lets the repository and handler suites exercise real relational behaviour — concurrency
    /// tokens, transactions, value converters — without requiring a SQL Server container.
    /// </remarks>
    private bool IsSqlServer =>
        Database.ProviderName == "Microsoft.EntityFrameworkCore.SqlServer";

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // No global query filters. ADR-001 specifies database-per-tenant, so isolation is
        // enforced by which database the connection points at (see ITenantConnectionResolver).
        // A filter closing over an instance field would also be silently wrong: EF caches the
        // built model per DbContextOptions, so the first tenant seen in the process would have
        // its id baked into the compiled model for every subsequent tenant.
        //
        // TenantId is still stored on every row and asserted on write by SaveChangesAsync
        // below, as defence in depth against a mis-resolved connection.

        // Configure aggregate mappings
        ConfigurePatientEpisode(modelBuilder);
        ConfigureLocation(modelBuilder);
        ConfigureQueue(modelBuilder);
        ConfigureEscalation(modelBuilder);
        ConfigureEscalationPolicy(modelBuilder);
        ConfigureFollowUpException(modelBuilder);
        ConfigureDomainEventRecord(modelBuilder);
        ConfigureOutboxMessage(modelBuilder);
        ConfigureAuditLogRecord(modelBuilder);

        // Every timestamp is an instant in UTC and is read back marked as such (see
        // UtcDateTimeConverter). Date of birth is a calendar date, not an instant, and must
        // never be shifted by a time-zone conversion.
        var utc = new Betsi.ControlPlane.UtcDateTimeConverter();
        foreach (var property in modelBuilder.Model.GetEntityTypes().SelectMany(t => t.GetProperties()))
        {
            if (property.ClrType.UnderlyingSystemType is var type &&
                (type == typeof(DateTime) || type == typeof(DateTime?)) &&
                property.Name != nameof(PatientEpisode.DateOfBirth))
            {
                property.SetValueConverter(utc);
            }
        }
    }

    private void ConfigurePatientEpisode(ModelBuilder modelBuilder)
    {
        var builder = modelBuilder.Entity<PatientEpisode>();

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();

        builder.Property(e => e.TenantId).IsRequired();
        builder.Property(e => e.Version).IsRequired().IsConcurrencyToken();
        builder.Property(e => e.State).IsRequired();
        builder.Property(e => e.NhsNumber).HasMaxLength(10);
        builder.Property(e => e.FirstName).IsRequired().HasMaxLength(100);
        builder.Property(e => e.LastName).IsRequired().HasMaxLength(100);
        builder.Property(e => e.DateOfBirth).IsRequired();
        builder.Property(e => e.LocationId);
        builder.Property(e => e.QueuePosition);
        builder.Property(e => e.ArrivedAt).IsRequired();
        builder.Property(e => e.TriageStartedAt);
        builder.Property(e => e.TreatmentStartedAt);
        builder.Property(e => e.EndedAt);

        var nhsNumberIndex = builder.HasIndex(e => new { e.TenantId, e.NhsNumber }).IsUnique();
        if (IsSqlServer)
            nhsNumberIndex.HasFilter("[NhsNumber] IS NOT NULL");
        builder.HasIndex(e => new { e.TenantId, e.State });
        builder.HasIndex(e => new { e.TenantId, e.ArrivedAt });
        builder.HasIndex(e => new { e.TenantId, e.LocationId });

        builder.ToTable("PatientEpisodes", "dbo");
    }

    private void ConfigureLocation(ModelBuilder modelBuilder)
    {
        var builder = modelBuilder.Entity<Location>();

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();

        builder.Property(e => e.TenantId).IsRequired();
        builder.Property(e => e.Version).IsRequired().IsConcurrencyToken();
        builder.Property(e => e.Name).IsRequired().HasMaxLength(200);
        builder.Property(e => e.Description).HasMaxLength(500);
        builder.Property(e => e.State).IsRequired();
        builder.Property(e => e.Capacity).IsRequired();
        builder.Property(e => e.CurrentOccupancy).IsRequired();
        builder.Property(e => e.QueueId);
        builder.Property(e => e.EscalationEnabled).IsRequired();

        builder.HasIndex(e => new { e.TenantId, e.Name }).IsUnique();
        builder.HasIndex(e => new { e.TenantId, e.State });

        builder.ToTable("Locations", "dbo");
    }

    private void ConfigureQueue(ModelBuilder modelBuilder)
    {
        var builder = modelBuilder.Entity<Queue>();

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();

        builder.Property(e => e.TenantId).IsRequired();
        builder.Property(e => e.Version).IsRequired().IsConcurrencyToken();
        builder.Property(e => e.LocationId).IsRequired();
        builder.Property(e => e.Name).IsRequired().HasMaxLength(200);
        builder.Property(e => e.HasExceededEscalationThreshold).IsRequired();
        builder.Property(e => e.ExceededThresholdAt);

        // The ordered patient list is a value of the Queue, not a separate table: it is only
        // ever read and written as a whole, so it is stored as a JSON array. The mapping goes
        // via the backing field because Patients is an IReadOnlyList projection.
        var patientsProperty = builder.Property<List<Guid>>("_patients")
            .HasColumnName("PatientIds")
            .IsRequired()
            .HasConversion(
                v => JsonSerializer.Serialize(v, JsonOptions),
                v => JsonSerializer.Deserialize<List<Guid>>(v, JsonOptions) ?? new List<Guid>(),
                new ValueComparer<List<Guid>>(
                    (a, b) => a != null && b != null && a.SequenceEqual(b),
                    v => v.Aggregate(0, (hash, id) => HashCode.Combine(hash, id.GetHashCode())),
                    v => v.ToList()));

        if (IsSqlServer)
            patientsProperty.HasColumnType("nvarchar(max)");

        builder.Ignore(e => e.Patients);

        builder.HasIndex(e => new { e.TenantId, e.LocationId }).IsUnique();
        builder.HasIndex(e => new { e.TenantId, e.HasExceededEscalationThreshold });

        builder.ToTable("Queues", "dbo");
    }

    private void ConfigureEscalation(ModelBuilder modelBuilder)
    {
        var builder = modelBuilder.Entity<Escalation>();

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();

        builder.Property(e => e.TenantId).IsRequired();
        builder.Property(e => e.Version).IsRequired().IsConcurrencyToken();
        builder.Property(e => e.PatientEpisodeId).IsRequired();
        builder.Property(e => e.LocationId);
        builder.Property(e => e.QueueId);
        builder.Property(e => e.State).IsRequired();
        builder.Property(e => e.Trigger).IsRequired();
        builder.Property(e => e.ResponsibleRole).IsRequired().HasMaxLength(100);
        builder.Property(e => e.PolicyRevision);
        builder.Property(e => e.TierLevel);
        builder.Property(e => e.ThresholdMinutes);
        builder.Property(e => e.WaitedMinutes);
        builder.Property(e => e.RecommendedAction).HasMaxLength(500);
        builder.Property(e => e.AcknowledgementDeadlineMinutes).IsRequired();
        builder.Property(e => e.AcknowledgedByActorId);
        builder.Property(e => e.AcknowledgedByRole).HasMaxLength(100);
        builder.Property(e => e.ResolvedByActorId);
        builder.Property(e => e.ResolvedByRole).HasMaxLength(100);
        builder.Property(e => e.CreatedAt).IsRequired();
        builder.Property(e => e.AcknowledgementDueAt);
        builder.Property(e => e.AcknowledgedAt);
        builder.Property(e => e.ResolvedAt);
        builder.Property(e => e.ClosedAt);
        builder.Property(e => e.Notes).HasMaxLength(2000);
        builder.Ignore(e => e.IsOpen);

        // At most one policy escalation per patient per tier, enforced by the database so two
        // monitor instances racing on the same patient cannot both raise it. Staff-raised
        // escalations have no tier and are unrestricted. SQLite treats NULLs as distinct in a
        // unique index, so the filter is only needed (and only valid) on SQL Server.
        var tierIndex = builder.HasIndex(e => new { e.TenantId, e.PatientEpisodeId, e.TierLevel }).IsUnique();
        if (IsSqlServer)
            tierIndex.HasFilter("[TierLevel] IS NOT NULL");

        builder.HasIndex(e => new { e.TenantId, e.PatientEpisodeId });
        builder.HasIndex(e => new { e.TenantId, e.LocationId });
        builder.HasIndex(e => new { e.TenantId, e.State });
        builder.HasIndex(e => new { e.TenantId, e.CreatedAt });
        var dueIndex = builder.HasIndex(e => new { e.TenantId, e.AcknowledgementDueAt });
        if (IsSqlServer)
            dueIndex.HasFilter("[AcknowledgementDueAt] IS NOT NULL");

        builder.ToTable("Escalations", "dbo");
    }

    private void ConfigureEscalationPolicy(ModelBuilder modelBuilder)
    {
        var builder = modelBuilder.Entity<EscalationPolicy>();

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();
        builder.Property(e => e.TenantId).IsRequired();
        builder.Property(e => e.Version).IsRequired().IsConcurrencyToken();
        builder.Property(e => e.Revision).IsRequired();
        builder.Property(e => e.State).IsRequired();
        builder.Property(e => e.Enabled).IsRequired();
        builder.Property(e => e.FollowUpOwnerRole).IsRequired().HasMaxLength(100);
        builder.Property(e => e.ProposalReason).IsRequired().HasMaxLength(1000);
        builder.Property(e => e.ProposedByActorId).IsRequired();
        builder.Property(e => e.ProposedByRole).IsRequired().HasMaxLength(100);
        builder.Property(e => e.ProposedAt).IsRequired();
        builder.Property(e => e.RestoresRevision);
        builder.Property(e => e.DecidedByActorId);
        builder.Property(e => e.DecidedByRole).HasMaxLength(100);
        builder.Property(e => e.DecidedAt);
        builder.Property(e => e.DecisionReason).HasMaxLength(1000);
        builder.Property(e => e.EffectiveFrom);

        // A revision's tiers are only ever read and written together, so they are one JSON
        // value, mapped through the backing field as Queue.Patients is.
        var tiers = builder.Property<List<WaitingTimeTier>>("_tiers")
            .HasColumnName("Tiers")
            .IsRequired()
            .HasConversion(
                v => JsonSerializer.Serialize(v, JsonOptions),
                v => JsonSerializer.Deserialize<List<WaitingTimeTier>>(v, JsonOptions) ?? new List<WaitingTimeTier>(),
                new ValueComparer<List<WaitingTimeTier>>(
                    (a, b) => a != null && b != null && a.SequenceEqual(b),
                    v => v.Aggregate(0, (hash, t) => HashCode.Combine(hash, t.GetHashCode())),
                    v => v.ToList()));
        if (IsSqlServer)
            tiers.HasColumnType("nvarchar(max)");
        builder.Ignore(e => e.Tiers);

        // Two proposals racing for the same revision number: one wins, the other gets a conflict.
        builder.HasIndex(e => new { e.TenantId, e.Revision }).IsUnique();
        builder.HasIndex(e => new { e.TenantId, e.State, e.EffectiveFrom });

        builder.ToTable("EscalationPolicies", "dbo");
    }

    private void ConfigureFollowUpException(ModelBuilder modelBuilder)
    {
        var builder = modelBuilder.Entity<FollowUpException>();

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();
        builder.Property(e => e.TenantId).IsRequired();
        builder.Property(e => e.Version).IsRequired().IsConcurrencyToken();
        builder.Property(e => e.EscalationId).IsRequired();
        builder.Property(e => e.PatientEpisodeId).IsRequired();
        builder.Property(e => e.EscalationResponsibleRole).IsRequired().HasMaxLength(100);
        builder.Property(e => e.MissedDeadline).IsRequired();
        builder.Property(e => e.RaisedAt).IsRequired();
        builder.Property(e => e.OwnerRole).IsRequired().HasMaxLength(100);
        builder.Property(e => e.State).IsRequired();
        builder.Property(e => e.Outcome);
        builder.Property(e => e.ReviewNotes).HasMaxLength(2000);
        builder.Property(e => e.ClosedByActorId);
        builder.Property(e => e.ClosedByRole).HasMaxLength(100);
        builder.Property(e => e.ClosedAt);

        // One exception per missed deadline (MVP-022 idempotency), enforced by the database.
        builder.HasIndex(e => new { e.TenantId, e.EscalationId, e.MissedDeadline }).IsUnique();
        builder.HasIndex(e => new { e.TenantId, e.State });

        builder.ToTable("FollowUpExceptions", "dbo");
    }

    private void ConfigureDomainEventRecord(ModelBuilder modelBuilder)
    {
        var builder = modelBuilder.Entity<DomainEventRecord>();

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedOnAdd();

        builder.Property(e => e.TenantId).IsRequired();
        builder.Property(e => e.EventId).IsRequired();
        builder.Property(e => e.AggregateId).IsRequired();
        builder.Property(e => e.AggregateType).IsRequired().HasMaxLength(100);
        builder.Property(e => e.EventType).IsRequired().HasMaxLength(100);
        builder.Property(e => e.EventData).IsRequired();
        if (IsSqlServer)
            builder.Property(e => e.EventData).HasColumnType("nvarchar(max)");
        builder.Property(e => e.Version).IsRequired().IsConcurrencyToken();
        builder.Property(e => e.ActorId).IsRequired();
        builder.Property(e => e.ActorRole).IsRequired().HasMaxLength(100);
        builder.Property(e => e.OccurredAt).IsRequired();
        builder.Property(e => e.CreatedAt).IsRequired();
        if (IsSqlServer)
            builder.Property(e => e.CreatedAt).HasDefaultValueSql("GETUTCDATE()");

        builder.HasIndex(e => new { e.TenantId, e.EventId }).IsUnique();
        builder.HasIndex(e => new { e.TenantId, e.AggregateId, e.Version });
        builder.HasIndex(e => new { e.TenantId, e.AggregateType });
        builder.HasIndex(e => new { e.TenantId, e.EventType });
        builder.HasIndex(e => new { e.TenantId, e.OccurredAt });

        builder.ToTable("DomainEvents", "dbo");
    }

    private void ConfigureOutboxMessage(ModelBuilder modelBuilder)
    {
        var builder = modelBuilder.Entity<OutboxMessage>();

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedOnAdd();

        builder.Property(e => e.TenantId).IsRequired();
        builder.Property(e => e.EventId).IsRequired();
        builder.Property(e => e.AggregateId).IsRequired();
        builder.Property(e => e.AggregateType).IsRequired().HasMaxLength(100);
        builder.Property(e => e.EventType).IsRequired().HasMaxLength(100);
        builder.Property(e => e.EventData).IsRequired();
        if (IsSqlServer)
            builder.Property(e => e.EventData).HasColumnType("nvarchar(max)");
        builder.Property(e => e.CreatedAt).IsRequired();
        if (IsSqlServer)
            builder.Property(e => e.CreatedAt).HasDefaultValueSql("GETUTCDATE()");
        builder.Property(e => e.ProcessedAt);
        builder.Property(e => e.ProcessedBy).HasMaxLength(100);
        builder.Property(e => e.Attempts).IsRequired();
        builder.Property(e => e.LastError).HasMaxLength(500);

        builder.HasIndex(e => new { e.TenantId, e.EventId }).IsUnique();
        var unprocessedIndex = builder.HasIndex(e => new { e.TenantId, e.ProcessedAt });
        if (IsSqlServer)
            unprocessedIndex.HasFilter("[ProcessedAt] IS NULL");

        builder.ToTable("OutboxMessages", "dbo");
    }

    private void ConfigureAuditLogRecord(ModelBuilder modelBuilder)
    {
        var builder = modelBuilder.Entity<AuditLogRecord>();

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedOnAdd();

        builder.Property(e => e.TenantId).IsRequired();
        builder.Property(e => e.Action).IsRequired().HasMaxLength(100);
        builder.Property(e => e.ActorId).IsRequired();
        builder.Property(e => e.ActorRole).IsRequired().HasMaxLength(100);
        builder.Property(e => e.AffectedAggregateId).IsRequired();
        builder.Property(e => e.AffectedAggregateType).IsRequired().HasMaxLength(100);
        builder.Property(e => e.Outcome).IsRequired().HasMaxLength(20); // Success, Failure
        builder.Property(e => e.ErrorMessage).HasMaxLength(500);
        builder.Property(e => e.Context);
        if (IsSqlServer)
            builder.Property(e => e.Context).HasColumnType("nvarchar(max)");
        builder.Property(e => e.CreatedAt).IsRequired();
        if (IsSqlServer)
            builder.Property(e => e.CreatedAt).HasDefaultValueSql("GETUTCDATE()");

        builder.HasIndex(e => new { e.TenantId, e.Action });
        builder.HasIndex(e => new { e.TenantId, e.ActorId });
        builder.HasIndex(e => new { e.TenantId, e.AffectedAggregateId });
        builder.HasIndex(e => new { e.TenantId, e.Outcome });
        builder.HasIndex(e => new { e.TenantId, e.CreatedAt });

        builder.ToTable("AuditLogs", "dbo");
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General);

    /// <summary>
    /// Stamps the resolved tenant onto new rows and refuses to persist any row belonging to
    /// a different tenant.
    /// </summary>
    /// <remarks>
    /// Under database-per-tenant this should never fire: a mismatch means the connection was
    /// resolved for one tenant while the aggregate was built for another. That is a bug worth
    /// failing loudly on rather than writing a patient record into the wrong organisation's
    /// database.
    /// </remarks>
    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        var tenantId = _tenantContext.TenantId;

        foreach (var entry in ChangeTracker.Entries())
        {
            // The event log and the audit log are the evidence trail (MVP-024: immutable). A
            // code path that updates or deletes a row in either is a bug, whatever its intent.
            if (entry.Entity is DomainEventRecord or AuditLogRecord &&
                entry.State is EntityState.Modified or EntityState.Deleted)
            {
                throw new InvalidOperationException(
                    $"{entry.Metadata.ClrType.Name} rows are append-only and cannot be {entry.State.ToString().ToLowerInvariant()}.");
            }

            if (entry.Metadata.FindProperty(nameof(AggregateRoot.TenantId)) is null)
                continue;

            var property = entry.Property(nameof(AggregateRoot.TenantId));

            if (entry.State == EntityState.Added && (Guid)property.CurrentValue! == Guid.Empty)
            {
                property.CurrentValue = tenantId;
                continue;
            }

            if (entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted
                && (Guid)property.CurrentValue! != tenantId)
            {
                throw new TenantIsolationViolationException(
                    entry.Metadata.ClrType.Name, (Guid)property.CurrentValue!, tenantId);
            }
        }

        return base.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>
/// Thrown when a write would cross a tenant boundary.
/// </summary>
public sealed class TenantIsolationViolationException : Exception
{
    public TenantIsolationViolationException(string entityType, Guid rowTenantId, Guid contextTenantId)
        : base($"Refusing to persist a '{entityType}' belonging to tenant '{rowTenantId}' " +
               $"while the current scope is resolved to tenant '{contextTenantId}'.")
    {
    }
}


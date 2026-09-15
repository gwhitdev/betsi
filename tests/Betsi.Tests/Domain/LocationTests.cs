namespace Betsi.Tests.Domain;

using Betsi.Domain;
using Betsi.Domain.Aggregates;

public class LocationTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid ActorId = Guid.NewGuid();
    private const string ActorRole = "Charge Nurse";

    private static Location ABayWithCapacity(int capacity) =>
        Location.CreateNew(TenantId, "Majors Bay 3", capacity, "Adult majors", true, ActorId, ActorRole);

    [Fact]
    public void A_new_location_is_empty_and_available()
    {
        var location = ABayWithCapacity(4);

        location.State.ShouldBe(Location.LocationState.Available);
        location.CurrentOccupancy.ShouldBe(0);
        location.Capacity.ShouldBe(4);
    }

    [Fact]
    public void A_location_must_have_capacity()
    {
        Should.Throw<ArgumentException>(
            () => Location.CreateNew(TenantId, "Nowhere", 0, actorId: ActorId));
    }

    [Fact]
    public void Occupying_a_space_moves_a_location_from_available_to_occupied()
    {
        var location = ABayWithCapacity(2);

        location.OccupySpace(Guid.NewGuid(), ActorId, ActorRole);

        location.CurrentOccupancy.ShouldBe(1);
        location.State.ShouldBe(Location.LocationState.Occupied);
    }

    [Fact]
    public void Filling_every_space_marks_the_location_full()
    {
        var location = ABayWithCapacity(2);

        location.OccupySpace(Guid.NewGuid(), ActorId, ActorRole);
        location.OccupySpace(Guid.NewGuid(), ActorId, ActorRole);

        location.State.ShouldBe(Location.LocationState.Full);
    }

    [Fact]
    public void A_full_location_refuses_another_patient()
    {
        var location = ABayWithCapacity(1);
        location.OccupySpace(Guid.NewGuid(), ActorId, ActorRole);

        Should.Throw<DomainRuleViolationException>(
            () => location.OccupySpace(Guid.NewGuid(), ActorId, ActorRole));
    }

    [Fact]
    public void Releasing_a_space_returns_the_location_towards_available()
    {
        var location = ABayWithCapacity(2);
        var patientId = Guid.NewGuid();
        location.OccupySpace(patientId, ActorId, ActorRole);

        location.ReleaseSpace(patientId, ActorId, ActorRole);

        location.CurrentOccupancy.ShouldBe(0);
        location.State.ShouldBe(Location.LocationState.Available);
    }

    [Fact]
    public void Occupancy_never_goes_negative()
    {
        var location = ABayWithCapacity(2);

        location.ReleaseSpace(Guid.NewGuid(), ActorId, ActorRole);

        location.CurrentOccupancy.ShouldBe(0);
    }

    [Fact]
    public void Maintenance_takes_a_location_out_of_service_and_returns_it_empty()
    {
        var location = ABayWithCapacity(2);
        location.OccupySpace(Guid.NewGuid(), ActorId, ActorRole);

        location.BeginMaintenance(ActorId, ActorRole);
        location.State.ShouldBe(Location.LocationState.Maintenance);

        location.EndMaintenance(ActorId, ActorRole);
        location.State.ShouldBe(Location.LocationState.Available);
        location.CurrentOccupancy.ShouldBe(0);
    }

    [Fact]
    public void Every_occupancy_change_is_recorded_as_an_event()
    {
        var location = ABayWithCapacity(2);
        var patientId = Guid.NewGuid();

        location.OccupySpace(patientId, ActorId, ActorRole);
        location.ReleaseSpace(patientId, ActorId, ActorRole);

        location.GetUncommittedEvents().Select(e => e.GetType()).ShouldBe([
            typeof(LocationCreated),
            typeof(LocationOccupied),
            typeof(LocationSpaceReleased)
        ]);
    }
}

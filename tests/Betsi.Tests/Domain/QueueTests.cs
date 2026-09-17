namespace Betsi.Tests.Domain;

using Betsi.Domain;
using Betsi.Domain.Aggregates;

public class QueueTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid ActorId = Guid.NewGuid();
    private const string ActorRole = "Receptionist";

    private static Queue AWaitingRoomQueue() =>
        Queue.CreateNew(TenantId, Guid.NewGuid(), "Main Waiting Room", ActorId, ActorRole);

    [Fact]
    public void A_new_queue_is_empty_and_below_threshold()
    {
        var queue = AWaitingRoomQueue();

        queue.Patients.ShouldBeEmpty();
        queue.HasExceededEscalationThreshold.ShouldBeFalse();
        queue.ExceededThresholdAt.ShouldBeNull();
    }

    [Fact]
    public void Patients_keep_the_order_they_joined_in()
    {
        var queue = AWaitingRoomQueue();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var third = Guid.NewGuid();

        queue.EnqueuePatient(first, ActorId, ActorRole);
        queue.EnqueuePatient(second, ActorId, ActorRole);
        queue.EnqueuePatient(third, ActorId, ActorRole);

        queue.Patients.ShouldBe([first, second, third]);
    }

    [Fact]
    public void Queue_position_is_one_based()
    {
        var queue = AWaitingRoomQueue();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        queue.EnqueuePatient(first, ActorId, ActorRole);
        queue.EnqueuePatient(second, ActorId, ActorRole);

        queue.GetPatientQueuePosition(first).ShouldBe(1);
        queue.GetPatientQueuePosition(second).ShouldBe(2);
    }

    [Fact]
    public void A_patient_not_in_the_queue_has_no_position()
    {
        var queue = AWaitingRoomQueue();

        queue.GetPatientQueuePosition(Guid.NewGuid()).ShouldBe(0);
    }

    [Fact]
    public void The_same_patient_cannot_be_queued_twice()
    {
        var queue = AWaitingRoomQueue();
        var patientId = Guid.NewGuid();
        queue.EnqueuePatient(patientId, ActorId, ActorRole);

        // A duplicate would show the same person waiting in two places on the board and
        // double-count them against the waiting-time thresholds.
        Should.Throw<DomainRuleViolationException>(
            () => queue.EnqueuePatient(patientId, ActorId, ActorRole));
    }

    [Fact]
    public void Removing_a_patient_closes_the_gap_behind_them()
    {
        var queue = AWaitingRoomQueue();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var third = Guid.NewGuid();
        queue.EnqueuePatient(first, ActorId, ActorRole);
        queue.EnqueuePatient(second, ActorId, ActorRole);
        queue.EnqueuePatient(third, ActorId, ActorRole);

        queue.DequeuePatient(first, ActorId, ActorRole);

        queue.Patients.ShouldBe([second, third]);
        queue.GetPatientQueuePosition(second).ShouldBe(1);
    }

    [Fact]
    public void Removing_a_patient_who_is_not_queued_is_rejected()
    {
        var queue = AWaitingRoomQueue();

        Should.Throw<DomainRuleViolationException>(
            () => queue.DequeuePatient(Guid.NewGuid(), ActorId, ActorRole));
    }

    [Fact]
    public void Crossing_the_escalation_threshold_is_recorded_once()
    {
        var queue = AWaitingRoomQueue();
        queue.GetUncommittedEvents();

        queue.MarkEscalationThresholdExceeded(Guid.NewGuid(), ActorId, ActorRole);
        queue.MarkEscalationThresholdExceeded(Guid.NewGuid(), ActorId, ActorRole);

        queue.HasExceededEscalationThreshold.ShouldBeTrue();
        queue.ExceededThresholdAt.ShouldNotBeNull();

        // Re-marking must not raise a second event, or the escalation dashboard would show
        // one queue breach as many.
        queue.GetUncommittedEvents().Count.ShouldBe(1);
    }

    [Fact]
    public void Resetting_the_threshold_clears_the_breach()
    {
        var queue = AWaitingRoomQueue();
        queue.MarkEscalationThresholdExceeded(Guid.NewGuid(), ActorId, ActorRole);

        queue.ResetEscalationThreshold(ActorId, ActorRole);

        queue.HasExceededEscalationThreshold.ShouldBeFalse();
        queue.ExceededThresholdAt.ShouldBeNull();
    }

    [Fact]
    public void Resetting_a_queue_that_never_breached_does_nothing()
    {
        var queue = AWaitingRoomQueue();
        queue.GetUncommittedEvents();

        queue.ResetEscalationThreshold(ActorId, ActorRole);

        queue.GetUncommittedEvents().ShouldBeEmpty();
    }

    [Fact]
    public void Enqueuing_records_the_position_the_patient_was_given()
    {
        var queue = AWaitingRoomQueue();
        queue.GetUncommittedEvents();
        queue.EnqueuePatient(Guid.NewGuid(), ActorId, ActorRole);
        queue.EnqueuePatient(Guid.NewGuid(), ActorId, ActorRole);

        queue.GetUncommittedEvents()
            .OfType<PatientEnqueued>()
            .Select(e => e.QueuePosition)
            .ShouldBe([1, 2]);
    }
}

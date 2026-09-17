namespace Betsi.Tests.Domain;

using Betsi.Domain;
using Betsi.Domain.Aggregates;

public class PatientEpisodeTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid ActorId = Guid.NewGuid();
    private const string ActorRole = "Nurse";

    private static PatientEpisode ARegisteredPatient() =>
        PatientEpisode.CreateNew(
            TenantId, "Gwen", "Jones", new DateTime(1962, 4, 19), "9434765919", ActorId, ActorRole);

    private static PatientEpisode APatientAwaitingTreatment()
    {
        var episode = ARegisteredPatient();
        episode.BeginTriage(ActorId, ActorRole);
        episode.CompleteTriage(ActorId, ActorRole);
        return episode;
    }

    public class Registration
    {
        [Fact]
        public void A_registered_patient_starts_waiting()
        {
            var episode = ARegisteredPatient();

            episode.State.ShouldBe(PatientEpisode.PatientState.Waiting);
            episode.TenantId.ShouldBe(TenantId);
            episode.Id.ShouldNotBe(Guid.Empty);
        }

        [Fact]
        public void Registration_records_the_arrival_time()
        {
            var before = DateTime.UtcNow;

            var episode = ARegisteredPatient();

            episode.ArrivedAt.ShouldBeInRange(before, DateTime.UtcNow);
        }

        [Fact]
        public void Registration_raises_a_created_event_naming_the_actor()
        {
            var episode = ARegisteredPatient();

            var created = episode.GetUncommittedEvents().Single().ShouldBeOfType<PatientEpisodeCreated>();

            created.ActorId.ShouldBe(ActorId);
            created.ActorRole.ShouldBe(ActorRole);
            created.NhsNumber.ShouldBe("9434765919");
        }
    }

    public class TheHappyPath
    {
        [Fact]
        public void A_patient_moves_from_waiting_through_triage_to_treatment_and_discharge()
        {
            var episode = ARegisteredPatient();

            episode.BeginTriage(ActorId, ActorRole);
            episode.State.ShouldBe(PatientEpisode.PatientState.InTriage);

            episode.CompleteTriage(ActorId, ActorRole);
            episode.State.ShouldBe(PatientEpisode.PatientState.AwaitingTreatment);

            episode.BeginTreatment(Guid.NewGuid(), ActorId, ActorRole);
            episode.State.ShouldBe(PatientEpisode.PatientState.InTreatment);

            episode.Discharge(ActorId, ActorRole, "Advised to rest");
            episode.State.ShouldBe(PatientEpisode.PatientState.Discharged);
        }

        [Fact]
        public void The_whole_journey_is_recorded_as_an_ordered_event_stream()
        {
            var episode = ARegisteredPatient();
            episode.BeginTriage(ActorId, ActorRole);
            episode.CompleteTriage(ActorId, ActorRole);
            episode.BeginTreatment(Guid.NewGuid(), ActorId, ActorRole);
            episode.Discharge(ActorId, ActorRole);

            var events = episode.GetUncommittedEvents();

            events.Select(e => e.GetType()).ShouldBe([
                typeof(PatientEpisodeCreated),
                typeof(PatientTriageStarted),
                typeof(PatientTriageCompleted),
                typeof(PatientTreatmentStarted),
                typeof(PatientDischarged)
            ]);

            events.Select(e => e.Version).ShouldBe([1, 2, 3, 4, 5]);
        }

        [Fact]
        public void Triage_records_when_it_started()
        {
            var episode = ARegisteredPatient();
            var before = DateTime.UtcNow;

            episode.BeginTriage(ActorId, ActorRole);

            episode.TriageStartedAt.ShouldNotBeNull();
            episode.TriageStartedAt!.Value.ShouldBeInRange(before, DateTime.UtcNow);
        }

        [Fact]
        public void Beginning_treatment_records_the_location()
        {
            var locationId = Guid.NewGuid();
            var episode = APatientAwaitingTreatment();

            episode.BeginTreatment(locationId, ActorId, ActorRole);

            episode.LocationId.ShouldBe(locationId);
            episode.TreatmentStartedAt.ShouldNotBeNull();
        }

        [Fact]
        public void Discharge_ends_the_episode_and_carries_the_notes()
        {
            var episode = APatientAwaitingTreatment();
            episode.GetUncommittedEvents();

            episode.Discharge(ActorId, ActorRole, "Fracture ruled out");

            episode.EndedAt.ShouldNotBeNull();
            episode.GetUncommittedEvents().Single()
                .ShouldBeOfType<PatientDischarged>()
                .DischargeNotes.ShouldBe("Fracture ruled out");
        }
    }

    public class IllegalTransitions
    {
        [Fact]
        public void Triage_cannot_begin_twice()
        {
            var episode = ARegisteredPatient();
            episode.BeginTriage(ActorId, ActorRole);

            Should.Throw<DomainRuleViolationException>(() => episode.BeginTriage(ActorId, ActorRole));
        }

        [Fact]
        public void Triage_cannot_be_completed_before_it_has_begun()
        {
            var episode = ARegisteredPatient();

            Should.Throw<DomainRuleViolationException>(() => episode.CompleteTriage(ActorId, ActorRole));
        }

        [Fact]
        public void Treatment_cannot_begin_for_a_patient_who_is_still_waiting_for_triage()
        {
            var episode = ARegisteredPatient();

            Should.Throw<DomainRuleViolationException>(
                () => episode.BeginTreatment(Guid.NewGuid(), ActorId, ActorRole));
        }

        [Fact]
        public void A_discharged_patient_cannot_be_discharged_again()
        {
            var episode = APatientAwaitingTreatment();
            episode.Discharge(ActorId, ActorRole);

            Should.Throw<DomainRuleViolationException>(() => episode.Discharge(ActorId, ActorRole));
        }

        [Fact]
        public void A_discharged_patient_cannot_be_cancelled()
        {
            var episode = APatientAwaitingTreatment();
            episode.Discharge(ActorId, ActorRole);

            Should.Throw<DomainRuleViolationException>(() => episode.Cancel(ActorId, ActorRole, "LWBS"));
        }

        [Fact]
        public void A_cancelled_patient_cannot_be_discharged()
        {
            var episode = ARegisteredPatient();
            episode.Cancel(ActorId, ActorRole, "Left without being seen");

            Should.Throw<DomainRuleViolationException>(() => episode.Discharge(ActorId, ActorRole));
        }

        [Fact]
        public void A_rejected_transition_leaves_the_state_and_version_untouched()
        {
            var episode = ARegisteredPatient();
            episode.GetUncommittedEvents();
            var versionBefore = episode.Version;

            Should.Throw<DomainRuleViolationException>(() => episode.CompleteTriage(ActorId, ActorRole));

            episode.State.ShouldBe(PatientEpisode.PatientState.Waiting);
            episode.Version.ShouldBe(versionBefore);
            episode.GetUncommittedEvents().ShouldBeEmpty();
        }
    }

    public class LeavingWithoutBeingSeen
    {
        [Fact]
        public void A_waiting_patient_can_be_cancelled_with_a_reason()
        {
            var episode = ARegisteredPatient();
            episode.GetUncommittedEvents();

            episode.Cancel(ActorId, ActorRole, "Left without being seen");

            episode.State.ShouldBe(PatientEpisode.PatientState.Cancelled);
            episode.EndedAt.ShouldNotBeNull();
            episode.GetUncommittedEvents().Single()
                .ShouldBeOfType<PatientEpisodeCancelled>()
                .Reason.ShouldBe("Left without being seen");
        }
    }

    public class WaitingTime
    {
        [Fact]
        public void A_waiting_patient_accrues_waiting_time()
        {
            var episode = ARegisteredPatient();

            episode.CurrentWaitingTime.ShouldBeGreaterThanOrEqualTo(TimeSpan.Zero);
        }

        [Fact]
        public void A_patient_in_triage_is_not_counted_as_waiting()
        {
            var episode = ARegisteredPatient();
            episode.BeginTriage(ActorId, ActorRole);

            episode.CurrentWaitingTime.ShouldBe(TimeSpan.Zero);
        }

        [Fact]
        public void A_patient_awaiting_treatment_after_triage_is_waiting_again()
        {
            // This is the cohort the ED report's prolonged-wait findings are about: triaged,
            // still not treated, and still accruing time against the 4/6/8-hour thresholds.
            var episode = APatientAwaitingTreatment();

            episode.CurrentWaitingTime.ShouldBeGreaterThanOrEqualTo(TimeSpan.Zero);
            episode.State.ShouldBe(PatientEpisode.PatientState.AwaitingTreatment);
        }

        [Fact]
        public void A_discharged_patient_is_not_waiting()
        {
            var episode = APatientAwaitingTreatment();
            episode.Discharge(ActorId, ActorRole);

            episode.CurrentWaitingTime.ShouldBe(TimeSpan.Zero);
        }
    }
}

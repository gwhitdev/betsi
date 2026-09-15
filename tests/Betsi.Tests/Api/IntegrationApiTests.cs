namespace Betsi.Tests.Api;

using Betsi.Application.Commands;
using Betsi.Domain.Aggregates;
using Betsi.Infrastructure.Outbox;
using Betsi.Infrastructure.Persistence;
using Betsi.Infrastructure.Tenancy;
using Betsi.Integrations;
using Betsi.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

/// <summary>Outbound webhooks (MVP-064) and signed inbound HL7 v2 / FHIR messages (MVP-063, MVP-066).</summary>
[Collection(ApiCollection.Name)]
public class IntegrationApiTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly Guid Tenant = BetsiApiFactory.IntegrationTenant;

    private readonly BetsiApiFactory _factory;

    public IntegrationApiTests(BetsiApiFixture fixture)
    {
        _factory = fixture.Factory;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private HttpClient Administrator => _factory.ClientFor(Tenant, Guid.NewGuid(), "Site Administrator");
    private HttpClient Nurse => _factory.ClientFor(Tenant, Guid.NewGuid(), "Nurse");

    private async Task<WebhookSecretIssued> SubscribeAsync(params string[] eventTypes)
    {
        var response = await Administrator.PostAsJsonAsync("/api/v1/webhooks/register",
            new { url = $"https://hooks.example.test/{Guid.NewGuid():N}", eventTypes, description = "Pager bridge" }, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<WebhookSecretIssued>(Json, Ct))!;
    }

    private async Task DrainOutboxAndDeliverAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().ResolveSystem(Tenant);
        await scope.ServiceProvider.GetRequiredService<IOutboxProcessor>().DrainAsync(Tenant, Ct);

        await using var deliveryScope = _factory.Services.CreateAsyncScope();
        deliveryScope.ServiceProvider.GetRequiredService<TenantContext>().ResolveSystem(Tenant);
        await deliveryScope.ServiceProvider.GetRequiredService<WebhookDeliverer>().DeliverDueAsync(Ct);
    }

    [Collection(ApiCollection.Name)]
    public class OutboundWebhooks(BetsiApiFixture fixture) : IntegrationApiTests(fixture)
    {
        [Fact]
        public async Task A_signed_minimal_payload_is_delivered_for_a_subscribed_event()
        {
            var subscription = await SubscribeAsync("escalation.raised", "patient.arrived");
            subscription.Secret.ShouldStartWith("whsec_");

            const string distinctiveName = "Zebedee-Quartermaine";
            var patient = await (await Nurse.PostAsJsonAsync("/api/v1/patients/register",
                new RegisterPatientCommand { FirstName = "Ynyr", LastName = distinctiveName, DateOfBirth = new DateTime(1931, 2, 3), NhsNumber = "9434765919" }, Ct))
                .ReadCommandResultAsync(Ct);
            await Nurse.PostAsJsonAsync("/api/v1/escalations/waiting-time", new TriggerWaitingTimeEscalationCommand
            {
                PatientEpisodeId = patient.AggregateId, LocationId = Guid.NewGuid(), ResponsibleRole = "Nurse in Charge"
            }, Ct);

            var before = _factory.WebhookReceiver.Received.Count;
            await DrainOutboxAndDeliverAsync();

            var sent = _factory.WebhookReceiver.Received.Skip(before)
                .Where(r => r.Request.RequestUri!.AbsolutePath.EndsWith(subscription.Url.Split('/').Last()))
                .ToList();

            sent.Select(r => r.Request.Headers.GetValues("Betsi-Event-Type").Single()).ShouldBe(["patient.arrived", "escalation.raised"], ignoreOrder: true);

            foreach (var (request, body) in sent)
            {
                WebhookSignature.Verify(subscription.Secret, request.Headers.GetValues(WebhookSignature.SignatureHeader).Single(), body, DateTimeOffset.UtcNow, out var failure)
                    .ShouldBeTrue(failure);

                // Minimum necessary data: no names, date of birth or NHS number leave the system.
                body.ShouldNotContain(distinctiveName);
                body.ShouldNotContain("Ynyr");
                body.ShouldNotContain("9434765919");
                body.ShouldNotContain("1931");
            }

            using var raised = JsonDocument.Parse(sent.Single(r => r.Body.Contains("escalation.raised")).Body);
            raised.RootElement.GetProperty("data").GetProperty("patientEpisodeId").GetGuid().ShouldBe(patient.AggregateId);
            raised.RootElement.GetProperty("data").GetProperty("responsibleRole").GetString().ShouldBe("Nurse in Charge");
            raised.RootElement.GetProperty("schemaVersion").GetInt32().ShouldBe(1);
        }

        [Fact]
        public async Task A_failed_delivery_backs_off_then_dead_letters_and_can_be_retried()
        {
            var subscription = await SubscribeAsync("patient.arrived");
            _factory.WebhookReceiver.Respond = HttpStatusCode.ServiceUnavailable;

            try
            {
                var patient = await (await Nurse.PostAsJsonAsync("/api/v1/patients/register",
                    new RegisterPatientCommand { FirstName = "Retry", LastName = "Case", DateOfBirth = new DateTime(1970, 1, 1) }, Ct))
                    .ReadCommandResultAsync(Ct);
                await DrainOutboxAndDeliverAsync();

                WebhookDelivery delivery;
                await using (var db = _factory.DatabaseFor(Tenant))
                {
                    // Other tests' events may also be pending for this subscription; follow this patient's.
                    delivery = await db.WebhookDeliveries.SingleAsync(d => d.SubscriptionId == subscription.Id && d.Payload.Contains(patient.AggregateId.ToString()), Ct);
                    delivery.Status.ShouldBe(WebhookDeliveryStatus.Pending);
                    delivery.Attempts.ShouldBe(1);
                    delivery.LastStatusCode.ShouldBe(503);
                    delivery.NextAttemptAt.ShouldBeGreaterThan(DateTime.UtcNow.AddSeconds(20));

                    // Fast-forward to the final attempt.
                    delivery.Attempts = 7;
                    delivery.NextAttemptAt = DateTime.UtcNow.AddMinutes(-1);
                    await db.SaveChangesAsync(Ct);
                }

                await DrainOutboxAndDeliverAsync();

                await using (var db = _factory.DatabaseFor(Tenant))
                    (await db.WebhookDeliveries.SingleAsync(d => d.Id == delivery.Id, Ct)).Status.ShouldBe(WebhookDeliveryStatus.DeadLettered);

                var deadLettered = await Administrator.GetFromJsonAsync<List<WebhookDeliveryView>>(
                    $"/api/v1/webhooks/{subscription.Id}/deliveries?status=DeadLettered", Json, Ct);
                deadLettered!.ShouldContain(d => d.Id == delivery.Id);

                (await Administrator.PostAsync($"/api/v1/webhooks/deliveries/{delivery.Id}/retry", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

                _factory.WebhookReceiver.Respond = HttpStatusCode.OK;
                await DrainOutboxAndDeliverAsync();

                await using (var db = _factory.DatabaseFor(Tenant))
                    (await db.WebhookDeliveries.SingleAsync(d => d.Id == delivery.Id, Ct)).Status.ShouldBe(WebhookDeliveryStatus.Delivered);
            }
            finally
            {
                _factory.WebhookReceiver.Respond = HttpStatusCode.OK;
            }
        }

        [Fact]
        public async Task A_deactivated_subscription_receives_nothing_further()
        {
            var subscription = await SubscribeAsync("patient.arrived");
            (await Administrator.PostAsync($"/api/v1/webhooks/{subscription.Id}/deactivate", null, Ct)).EnsureSuccessStatusCode();

            await Nurse.PostAsJsonAsync("/api/v1/patients/register",
                new RegisterPatientCommand { FirstName = "After", LastName = "Deactivation", DateOfBirth = new DateTime(1970, 1, 1) }, Ct);
            await DrainOutboxAndDeliverAsync();

            await using var db = _factory.DatabaseFor(Tenant);
            (await db.WebhookDeliveries.AnyAsync(d => d.SubscriptionId == subscription.Id, Ct)).ShouldBeFalse();
        }

        [Theory]
        [InlineData("http://hooks.example.test/x", "patient.arrived")]
        [InlineData("https://127.0.0.1/x", "patient.arrived")]
        [InlineData("https://169.254.169.254/latest/meta-data", "patient.arrived")]
        [InlineData("https://user:pass@hooks.example.test/x", "patient.arrived")]
        [InlineData("https://hooks.example.test/x", "patient.name_changed")]
        public async Task Unsafe_urls_and_unknown_events_are_refused(string url, string eventType)
        {
            var response = await Administrator.PostAsJsonAsync("/api/v1/webhooks/register", new { url, eventTypes = new[] { eventType } }, Ct);

            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        [Fact]
        public async Task Only_site_administrators_manage_webhooks_and_secrets_are_never_listed()
        {
            (await Nurse.GetAsync("/api/v1/webhooks", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

            var subscription = await SubscribeAsync("patient.discharged");
            var listing = await Administrator.GetStringAsync("/api/v1/webhooks", Ct);

            listing.ShouldContain(subscription.Id.ToString());
            listing.ShouldNotContain(subscription.Secret);

            await using var db = _factory.DatabaseFor(Tenant);
            var stored = await db.WebhookSubscriptions.SingleAsync(s => s.Id == subscription.Id, Ct);
            stored.ProtectedSecret.ShouldNotContain(subscription.Secret);
        }

        [Fact]
        public async Task Processing_an_outbox_message_twice_does_not_duplicate_deliveries()
        {
            var subscription = await SubscribeAsync("patient.cancelled");
            var message = new OutboxMessage
            {
                TenantId = Tenant, EventId = Guid.NewGuid(), AggregateId = Guid.NewGuid(), AggregateType = "PatientEpisode",
                EventType = "PatientEpisodeCancelled", EventData = "{\"Reason\":\"LWBS\",\"Version\":3}", CreatedAt = DateTime.UtcNow
            };

            for (var i = 0; i < 2; i++)
            {
                await using var scope = _factory.Services.CreateAsyncScope();
                scope.ServiceProvider.GetRequiredService<TenantContext>().ResolveSystem(Tenant);
                await scope.ServiceProvider.GetRequiredService<WebhookFanOutPublisher>().PublishAsync(message, Ct);
                await scope.ServiceProvider.GetRequiredService<BetsiDbContext>().SaveChangesAsync(Ct);
            }

            await using var db = _factory.DatabaseFor(Tenant);
            var deliveries = await db.WebhookDeliveries.Where(d => d.EventId == message.EventId && d.SubscriptionId == subscription.Id).ToListAsync(Ct);
            deliveries.ShouldHaveSingleItem().Payload.ShouldNotContain("LWBS");
        }
    }

    [Collection(ApiCollection.Name)]
    public class InboundMessages(BetsiApiFixture fixture) : IntegrationApiTests(fixture)
    {
        private async Task<InboundSourceSecretIssued> SourceAsync(string format)
        {
            var response = await Administrator.PostAsJsonAsync("/api/v1/integrations/sources",
                new { name = $"EPR {format} {Guid.NewGuid():N}", format }, Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
            return (await response.Content.ReadFromJsonAsync<InboundSourceSecretIssued>(Json, Ct))!;
        }

        private async Task<HttpResponseMessage> SendAsync(
            InboundSourceSecretIssued source, string body, string messageId, string contentType,
            string? secret = null, DateTimeOffset? signedAt = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, source.InboundPath)
            {
                Content = new StringContent(body, Encoding.UTF8)
            };
            request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
            request.Headers.Add(WebhookSignature.SignatureHeader, WebhookSignature.Create(secret ?? source.Secret, signedAt ?? DateTimeOffset.UtcNow, body));
            request.Headers.Add(InboundSignatureAuthenticationHandler.MessageIdHeader, messageId);

            return await _factory.CreateClient().SendAsync(request, Ct);
        }

        private static string FhirEncounter(string visitId, string status, string family = "Pritchard", string nhsNumber = "9434765870") => JsonSerializer.Serialize(new
        {
            resourceType = "Bundle",
            type = "message",
            entry = new object[]
            {
                new { resource = new { resourceType = "Patient", identifier = new[] { new { system = FhirR4Reader.NhsNumberSystem, value = nhsNumber } },
                    name = new[] { new { family, given = new[] { "Eirlys" } } }, birthDate = "1948-11-02" } },
                new { resource = new { resourceType = "Encounter", status, identifier = new[] { new { system = "urn:epr:visit", value = visitId } } } }
            }
        });

        private static string Hl7(string trigger, string controlId, string visitId, string nhsNumber = "9434765919") =>
            $"MSH|^~\\&|EPR|YGC|BETSI|YGC|20260915120000||ADT^{trigger}|{controlId}|P|2.5\r" +
            $"EVN|{trigger}|20260915120000\r" +
            $"PID|1||{nhsNumber}^^^NHS^NH~H123456^^^YGC^MR||Hughes^Dafydd^^^Mr||19550607|M\r" +
            $"PV1|1|E|ED^^^YGC||||||||||||||||{visitId}\r";

        [Fact]
        public async Task A_signed_fhir_arrival_registers_a_patient_as_the_integration_and_a_resend_is_harmless()
        {
            var source = await SourceAsync("FhirR4");
            var visit = $"V{Guid.NewGuid():N}"[..12];
            var body = FhirEncounter(visit, "arrived", nhsNumber: "4010232137");

            var first = await SendAsync(source, body, "msg-1", "application/fhir+json");
            first.StatusCode.ShouldBe(HttpStatusCode.Accepted, await first.Content.ReadAsStringAsync(Ct));
            var outcome = (await first.Content.ReadFromJsonAsync<InboundOutcome>(Json, Ct))!;
            outcome.Status.ShouldBe("Accepted");

            var again = (await (await SendAsync(source, body, "msg-1", "application/fhir+json")).Content.ReadFromJsonAsync<InboundOutcome>(Json, Ct))!;
            again.Duplicate.ShouldBeTrue();
            again.EpisodeId.ShouldBe(outcome.EpisodeId);

            await using var db = _factory.DatabaseFor(Tenant);
            var episode = await db.PatientEpisodes.SingleAsync(e => e.Id == outcome.EpisodeId, Ct);
            episode.NhsNumber.ShouldBe("4010232137");
            episode.LastName.ShouldBe("Pritchard");

            var audit = await db.AuditLogs.SingleAsync(a => a.AffectedAggregateId == episode.Id && a.Action == nameof(RegisterPatientCommand), Ct);
            audit.ActorRole.ShouldBe(RoleMatrix.IntegrationRole);
            audit.ActorId.ShouldBe(source.Id);

            // Accepted messages are not retained.
            (await db.InboundMessages.SingleAsync(m => m.SourceId == source.Id && m.MessageId == "msg-1", Ct)).Body.ShouldBeNull();
        }

        [Fact]
        public async Task A_fhir_finished_encounter_discharges_the_linked_episode()
        {
            var source = await SourceAsync("FhirR4");
            var visit = $"V{Guid.NewGuid():N}"[..12];

            var arrived = (await (await SendAsync(source, FhirEncounter(visit, "arrived", nhsNumber: "6012345674"), "a", "application/fhir+json"))
                .Content.ReadFromJsonAsync<InboundOutcome>(Json, Ct))!;
            var finished = (await (await SendAsync(source, FhirEncounter(visit, "finished"), "b", "application/fhir+json"))
                .Content.ReadFromJsonAsync<InboundOutcome>(Json, Ct))!;

            finished.Status.ShouldBe("Accepted");
            finished.EpisodeId.ShouldBe(arrived.EpisodeId);

            await using var db = _factory.DatabaseFor(Tenant);
            (await db.PatientEpisodes.SingleAsync(e => e.Id == arrived.EpisodeId, Ct)).State.ShouldBe(PatientEpisode.PatientState.Discharged);
        }

        [Fact]
        public async Task Hl7_a01_and_a03_register_and_discharge_with_hl7_acknowledgements()
        {
            var source = await SourceAsync("Hl7v2");
            var visit = $"E{Guid.NewGuid():N}"[..10];

            var admit = await SendAsync(source, Hl7("A01", "CTRL001", visit, nhsNumber: "9876543210"), "hl7-1", "application/hl7-v2");
            admit.StatusCode.ShouldBe(HttpStatusCode.OK);
            var ack = await admit.Content.ReadAsStringAsync(Ct);
            ack.ShouldContain("MSA|AA|CTRL001");

            var discharge = await SendAsync(source, Hl7("A03", "CTRL002", visit), "hl7-2", "application/hl7-v2");
            (await discharge.Content.ReadAsStringAsync(Ct)).ShouldContain("MSA|AA|CTRL002");

            await using var db = _factory.DatabaseFor(Tenant);
            var episode = await db.PatientEpisodes.SingleAsync(e => e.NhsNumber == "9876543210", Ct);
            episode.FirstName.ShouldBe("Dafydd");
            episode.LastName.ShouldBe("Hughes");
            episode.DateOfBirth.ShouldBe(new DateTime(1955, 6, 7));
            episode.State.ShouldBe(PatientEpisode.PatientState.Discharged);
        }

        [Fact]
        public async Task A_message_that_cannot_be_processed_is_quarantined_for_review_not_discarded()
        {
            var source = await SourceAsync("Hl7v2");
            var body = Hl7("A08", "CTRL-Q", "E-UNSUPPORTED");

            var response = await SendAsync(source, body, "hl7-q", "application/hl7-v2");

            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await response.Content.ReadAsStringAsync(Ct)).ShouldContain("MSA|AE|CTRL-Q");

            var quarantined = await Administrator.GetFromJsonAsync<List<InboundMessageSummary>>(
                $"/api/v1/integrations/sources/{source.Id}/messages?status=Quarantined", Json, Ct);
            var summary = quarantined!.ShouldHaveSingleItem();
            summary.Error.ShouldNotBeNull().ShouldContain("ADT^A08");

            // The body has patient data: administrators see the summary, clinical staff review the content.
            (await Administrator.GetAsync($"/api/v1/integrations/messages/{summary.Id}", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            var detail = await Nurse.GetFromJsonAsync<InboundMessageDetail>($"/api/v1/integrations/messages/{summary.Id}", Json, Ct);
            detail!.Body.ShouldBe(body);
        }

        [Fact]
        public async Task A_discharge_for_an_unknown_visit_is_quarantined()
        {
            var source = await SourceAsync("FhirR4");

            var outcome = (await (await SendAsync(source, FhirEncounter("NEVER-ARRIVED", "finished"), "orphan", "application/fhir+json"))
                .Content.ReadFromJsonAsync<InboundOutcome>(Json, Ct))!;

            outcome.Status.ShouldBe("Quarantined");
            outcome.Error.ShouldNotBeNull().ShouldContain("No arrival");
        }

        [Fact]
        public async Task Reusing_a_message_id_for_different_content_is_a_conflict()
        {
            var source = await SourceAsync("FhirR4");
            var visit = $"V{Guid.NewGuid():N}"[..12];
            await SendAsync(source, FhirEncounter(visit, "arrived", nhsNumber: "4857773457"), "same-id", "application/fhir+json");

            var reused = await SendAsync(source, FhirEncounter(visit, "finished"), "same-id", "application/fhir+json");

            reused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        }

        [Fact]
        public async Task Unsigned_mis_signed_stale_and_unknown_source_messages_are_401()
        {
            var source = await SourceAsync("FhirR4");
            var body = FhirEncounter("V-SEC", "arrived");

            (await SendAsync(source, body, "s1", "application/fhir+json", secret: "insec_wrong")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            (await SendAsync(source, body, "s2", "application/fhir+json", signedAt: DateTimeOffset.UtcNow.AddMinutes(-6))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

            var unknown = source with { InboundPath = source.InboundPath.Replace(source.Id.ToString(), Guid.NewGuid().ToString()) };
            var response = await SendAsync(unknown, body, "s3", "application/fhir+json");
            response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            (await response.Content.ReadAsStringAsync(Ct)).ShouldContain("INVALID_SIGNATURE");

            using var noSignature = new HttpRequestMessage(HttpMethod.Post, source.InboundPath) { Content = new StringContent(body, Encoding.UTF8, "application/fhir+json") };
            noSignature.Headers.Add(InboundSignatureAuthenticationHandler.MessageIdHeader, "s4");
            (await _factory.CreateClient().SendAsync(noSignature, Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

            // A bearer token for the tenant, however privileged, does not open the inbound endpoint.
            var withToken = _factory.ClientWithToken(TestTokens.For(Tenant, ["Clinical Lead"]));
            (await withToken.PostAsync(source.InboundPath, new StringContent(body, Encoding.UTF8, "application/fhir+json"), Ct))
                .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        [Fact]
        public async Task A_deactivated_source_is_refused()
        {
            var source = await SourceAsync("FhirR4");
            (await Administrator.PostAsync($"/api/v1/integrations/sources/{source.Id}/deactivate", null, Ct)).EnsureSuccessStatusCode();

            (await SendAsync(source, FhirEncounter("V-OFF", "arrived"), "off", "application/fhir+json")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }
    }

    public class Primitives
    {
        [Fact]
        public void A_signature_verifies_and_any_change_breaks_it()
        {
            var now = DateTimeOffset.UtcNow;
            var header = WebhookSignature.Create("whsec_secret", now, "{\"a\":1}");

            WebhookSignature.Verify("whsec_secret", header, "{\"a\":1}", now, out _).ShouldBeTrue();
            WebhookSignature.Verify("whsec_secret", header, "{\"a\":2}", now, out _).ShouldBeFalse();
            WebhookSignature.Verify("whsec_other", header, "{\"a\":1}", now, out _).ShouldBeFalse();
            WebhookSignature.Verify("whsec_secret", header, "{\"a\":1}", now.AddMinutes(6), out _).ShouldBeFalse();
        }

        [Fact]
        public void During_rotation_either_secret_verifies()
        {
            var now = DateTimeOffset.UtcNow;
            var oldSignature = WebhookSignature.Create("whsec_old", now, "body");
            var header = oldSignature + "," + WebhookSignature.Create("whsec_new", now, "body").Split(',')[1];

            WebhookSignature.Verify("whsec_new", header, "body", now, out _).ShouldBeTrue();
            WebhookSignature.Verify("whsec_old", header, "body", now, out _).ShouldBeTrue();
        }

        [Theory]
        [InlineData("127.0.0.1", true)]
        [InlineData("10.2.3.4", true)]
        [InlineData("172.20.0.1", true)]
        [InlineData("192.168.1.1", true)]
        [InlineData("169.254.169.254", true)]
        [InlineData("100.64.0.1", true)]
        [InlineData("::1", true)]
        [InlineData("fd00::1", true)]
        [InlineData("::ffff:10.0.0.1", true)]
        [InlineData("51.140.1.1", false)]
        [InlineData("2a00:1450:4009::1", false)]
        public void Private_and_link_local_addresses_are_recognised(string address, bool isPrivate)
        {
            NetworkTargets.IsPrivate(IPAddress.Parse(address)).ShouldBe(isPrivate);
        }

        [Fact]
        public async Task The_delivery_handler_refuses_to_connect_to_a_private_address()
        {
            using var client = new HttpClient(NetworkTargets.CreateHandler(allowPrivate: false));

            await Should.ThrowAsync<HttpRequestException>(() => client.GetAsync("http://127.0.0.1:9/", TestContext.Current.CancellationToken));
        }

        [Fact]
        public void The_hl7_reader_rejects_what_it_does_not_understand()
        {
            Should.Throw<UnprocessableMessageException>(() => Hl7v2Reader.Read("not hl7"));
            Should.Throw<UnprocessableMessageException>(() => Hl7v2Reader.Read("MSH|^~\\&|A|B|C|D|20260101||ORU^R01|1|P|2.5\rPID|1\rPV1|1"));
            Should.Throw<UnprocessableMessageException>(() => Hl7v2Reader.Read("MSH|^~\\&|A|B|C|D|20260101||ADT^A01|1|P|2.5\rPID|1||||Hughes^Dafydd||19550607\rPV1|1"));
        }

        [Fact]
        public void Integrations_may_only_register_discharge_and_ingest()
        {
            RoleMatrix.PermissionsOf(RoleMatrix.IntegrationRole).ShouldBe(
                [Permissions.IntegrationIngest, Permissions.PatientsRegister, Permissions.PatientsDischarge], ignoreOrder: true);
        }
    }
}

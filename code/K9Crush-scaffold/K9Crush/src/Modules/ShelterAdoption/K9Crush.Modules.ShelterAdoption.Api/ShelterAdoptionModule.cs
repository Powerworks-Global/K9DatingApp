using JasperFx.Events.Projections;
using Marten;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using K9Crush.BuildingBlocks.Persistence;
using K9Crush.BuildingBlocks.Web;
using K9Crush.Modules.ShelterAdoption.Domain;

namespace K9Crush.Modules.ShelterAdoption.Api;

/// <summary>
/// Composition root for the Shelter &amp; Adoption module. Api.Host
/// discovers this via assembly scanning (see Program.cs) - nothing else
/// references this type.
/// </summary>
public sealed class ShelterAdoptionModule : IModule
{
    public string Name => "ShelterAdoption";

    public IMartenModuleConfiguration MartenConfiguration { get; } = new ShelterAdoptionMartenConfiguration();

    // First set on this module (ADR-028) - CancelApplicationsForRemovedListingHandler
    // and NotifyApplicantsOfListingChangeHandler react to this module's OWN
    // published events (DogListingRemovedV1/DogListingSignificantlyEditedV1),
    // which still route through the shared k9crush.events exchange same as
    // any cross-module event - there's no separate "local-only" pub/sub in
    // this codebase, so a same-module cascade needs a queue too, same as
    // Discovery/Identity/Notifications. Confirmed safe against Wolverine's
    // actual RabbitMQ transport source: the shared exchange is fanout (every
    // bound queue gets every message), and a message type with no local
    // handler is a graceful no-op (NoHandlerContinuation just acks it), not
    // an error - so this queue also quietly absorbing every other module's
    // events it doesn't handle is expected, not a problem.
    public string? IntegrationEventQueueName => "shelteradoption.integration-events";

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        // Nothing beyond Wolverine's auto-discovered handlers for this
        // module yet.
    }

    private sealed class ShelterAdoptionMartenConfiguration : IMartenModuleConfiguration
    {
        public string SchemaName => "shelteradoption";

        public void Configure(StoreOptions options)
        {
            // ADR-031 event-sourcing retrofit, Phase 5/5 - every entity in
            // this module is now an event stream, self-aggregating via
            // Create/Apply and registered as its own Inline snapshot (dual
            // use for the read models that genuinely query current state -
            // GetShelterDogListings/GetDogListingDetails/GetAdoptionListings/
            // GetPendingApplicationsQueue/GetApplicationStatus/
            // GetDraftApplications/GetSurrenderReviewQueue/
            // GetFosterApplicationsQueue/GetVolunteerApplicationsQueue, plus
            // every ownership-check LoadAsync<ShelterAccount>).
            //
            // Event store schema is configured once, centrally, in
            // Program.cs - see its comment for why. Each Inline snapshot
            // below is still a normal Marten document (mt_doc_*) and needs
            // its own explicit DatabaseSchemaName() call - without it,
            // Marten defaults the document schema to "public" regardless
            // of SchemaName, which is what was actually happening here
            // until this fix (see marten_schema_isolation_bug memory).
            options.Projections.Snapshot<ShelterAccount>(SnapshotLifecycle.Inline);
            options.Projections.Snapshot<DogListing>(SnapshotLifecycle.Inline);
            options.Projections.Snapshot<Application>(SnapshotLifecycle.Inline);
            options.Projections.Snapshot<DogSurrenderRequest>(SnapshotLifecycle.Inline);
            options.Projections.Snapshot<FosterApplication>(SnapshotLifecycle.Inline);
            options.Projections.Snapshot<VolunteerApplication>(SnapshotLifecycle.Inline);

            options.Schema.For<ShelterAccount>().DatabaseSchemaName(SchemaName);
            options.Schema.For<DogListing>().DatabaseSchemaName(SchemaName);
            options.Schema.For<Application>().DatabaseSchemaName(SchemaName);
            options.Schema.For<DogSurrenderRequest>().DatabaseSchemaName(SchemaName);
            options.Schema.For<FosterApplication>().DatabaseSchemaName(SchemaName);
            options.Schema.For<VolunteerApplication>().DatabaseSchemaName(SchemaName);

            // PHASE 0 POC — DCB tag registration for
            // ApproveApplicationHandler's modernized write boundary (see
            // that file's own doc comment, and
            // poc/DcbRewrite/NOTES.md in internal-modernization-accelerator
            // for the full before/after story). Registered module-side,
            // not centrally in Program.cs, since these tag types are
            // ShelterAdoption-specific with no cross-module naming
            // collision risk (unlike the event-store DatabaseSchemaName
            // bug this file's own comment above already documents).
            //
            // KNOWN UNRESOLVED GAP (empirically confirmed against real
            // Postgres, 2026-09-17): FetchForWritingByTags<ApproveApplicationDcbBoundary>
            // throws at RUNTIME ("No source-generated dispatcher found ...
            // there is no runtime fallback") even though this type has real
            // Apply methods - Marten's compile-time source generator isn't
            // picking it up as a self-aggregating type via ForAggregate<T>
            // alone. Registering it via Projections.Snapshot<T> instead
            // (the mechanism Application/DogListing use successfully)
            // throws a DIFFERENT error at DocumentStore.For time
            // (ArgumentNullException inside SingleStreamProjection<T>'s
            // MakeGenericType call) - almost certainly because this type
            // has no Id/identity property, which Snapshot<T> requires but
            // a pure DCB boundary type conceptually shouldn't need. This
            // is real, unresolved Marten 9.20.1 DCB setup friction (DCB
            // shipped ~4 months before this session), not a mistake in
            // this handler's business logic - see poc/DcbRewrite/NOTES.md
            // for the full investigation trail before attempting a fix.
            options.Events.RegisterTagType<Commands.ApproveApplication.ApplicationTag>("application")
                .ForAggregate<Commands.ApproveApplication.ApproveApplicationDcbBoundary>();
            options.Events.RegisterTagType<Commands.ApproveApplication.DogListingTag>("doglisting")
                .ForAggregate<Commands.ApproveApplication.ApproveApplicationDcbBoundary>();
        }
    }
}

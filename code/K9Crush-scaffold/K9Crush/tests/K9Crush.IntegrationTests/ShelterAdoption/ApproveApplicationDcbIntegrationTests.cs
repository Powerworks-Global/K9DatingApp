using System.Security.Claims;
using FluentAssertions;
using K9Crush.Modules.ShelterAdoption.Api.Commands.ApproveApplication;
using K9Crush.Modules.ShelterAdoption.Domain;
using Xunit;

namespace K9Crush.IntegrationTests.ShelterAdoption;

/// <summary>
/// Layer 3 (TestingApproach.md) - PHASE 0 POC: the one thing Layer 2's
/// mocked-session tests for ApproveApplicationHandler CANNOT prove is
/// whether the DCB rewrite's tag-based writes actually land in the SAME
/// physical event streams as Application/DogListing's existing Inline
/// snapshots (both registered via options.Projections.Snapshot(...) keyed
/// by their own aggregate id, per ShelterAdoptionModule.cs). This is the
/// single most load-bearing, previously-unverified assumption in
/// poc/DcbRewrite/NOTES.md (internal-modernization-accelerator repo) -
/// answered here empirically against a real Postgres instance, not
/// asserted from documentation alone.
/// </summary>
[Collection(ShelterAdoptionPostgresCollection.Name)]
public class ApproveApplicationDcbIntegrationTests(ShelterAdoptionPostgresFixture fixture)
{
    private static ClaimsPrincipal BuildUser(Guid ownerId) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, ownerId.ToString())]));

    [Fact]
    public async Task Handle_RealPostgres_DcbAppendsAreVisibleThroughTheExistingInlineSnapshots()
    {
        var shelterOwnerId = Guid.NewGuid();
        var applicantOwnerId = Guid.NewGuid();

        var (shelterAccount, requestedEvent) = ShelterAccount.RequestNew(
            shelterOwnerId, "Sunny Paws Rescue, EIN 12-3456789", Guid.NewGuid());

        var (dogListing, dogAddedEvent) = DogListing.AddNew(
            shelterAccount.Id, "Biscuit", "Beagle mix", 24, "Friendly");

        var (application, submittedEvent) = Application.SubmitNew(
            applicantOwnerId, dogListing.Id, shelterAccount.Id, TestIntake.Default);
        var reviewedEvent = application.Review();

        await using (var setupSession = fixture.Store.LightweightSession())
        {
            setupSession.Events.StartStream<ShelterAccount>(shelterAccount.Id, requestedEvent);
            setupSession.Events.StartStream<DogListing>(dogListing.Id, dogAddedEvent);
            setupSession.Events.StartStream<Application>(application.Id, submittedEvent, reviewedEvent);
            await setupSession.SaveChangesAsync();
        }

        // Sanity check BEFORE the DCB handler runs: the Inline snapshots
        // see the real, freshly-created pre-approval state.
        await using (var preCheckSession = fixture.Store.LightweightSession())
        {
            var preApplication = await preCheckSession.LoadAsync<Application>(application.Id);
            preApplication!.Status.Should().Be(ApplicationStatus.UnderReview);
        }

        await using (var handlerSession = fixture.Store.LightweightSession())
        {
            var (result, integrationEvent) = await ApproveApplicationHandler.Handle(
                application.Id, BuildUser(shelterOwnerId), handlerSession, CancellationToken.None);

            result.Result.Should().BeOfType<Microsoft.AspNetCore.Http.HttpResults.Ok<ApproveApplicationResponse>>(
                "a real Postgres-backed run of the DCB handler against real, freshly-created streams should succeed exactly like the mocked Layer 2 tests predict");
            integrationEvent.Should().NotBeNull();
            integrationEvent!.DogName.Should().Be("Biscuit");
        }

        // THE REAL QUESTION: does a FRESH session's LoadAsync (i.e. the
        // existing Inline snapshot machinery, completely independent of
        // anything the handler above did in-memory) see the DCB-appended
        // events as belonging to Application's/DogListing's own streams?
        await using var verifySession = fixture.Store.LightweightSession();
        var postApplication = await verifySession.LoadAsync<Application>(application.Id);
        var postDogListing = await verifySession.LoadAsync<DogListing>(dogListing.Id);

        postApplication.Should().NotBeNull("the Application document must still exist under its own id after a DCB-tagged append");
        postApplication!.Status.Should().Be(
            ApplicationStatus.Approved,
            "if this fails, DCB tag-routing does NOT reuse the same physical stream as the classic FetchForWriting/Inline-snapshot API for an existing aggregate - a real, load-bearing finding for whether this migration pattern is safe to use incrementally alongside not-yet-converted handlers");
        postDogListing.Should().NotBeNull();
        postDogListing!.Status.Should().Be(DogListingStatus.Adopted);
    }
}

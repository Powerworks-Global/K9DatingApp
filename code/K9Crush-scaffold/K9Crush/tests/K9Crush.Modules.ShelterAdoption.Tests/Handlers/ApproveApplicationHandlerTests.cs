using System.Security.Claims;
using FluentAssertions;
using Marten;
using JasperFx.Events;
using JasperFx.Events.Tags;
using Microsoft.AspNetCore.Http.HttpResults;
using NSubstitute;
using K9Crush.Modules.ShelterAdoption.Api.Commands.ApproveApplication;
using K9Crush.Modules.ShelterAdoption.Domain;
using Xunit;

namespace K9Crush.Modules.ShelterAdoption.Tests.Handlers;

/// <summary>
/// Layer 2 (TestingApproach.md) - PHASE 0 POC UPDATE: ApproveApplicationHandler
/// was rewritten to use a DCB (Dynamic Consistency Boundary) write boundary
/// (session.Events.FetchForWritingByTags) instead of two independent
/// FetchForWriting calls - see the handler's own doc comment and
/// poc/DcbRewrite/NOTES.md (internal-modernization-accelerator repo).
/// Business-decision reads now go through session.LoadAsync against the
/// Inline snapshots directly (Application, DogListing, ShelterAccount are
/// all already registered as such), so these tests mock LoadAsync for
/// reads and mock the DCB boundary object for the write/AppendOne
/// assertions - same business intent as the original tests, adapted to
/// the new call shape, not gutted.
/// </summary>
public class ApproveApplicationHandlerTests
{
    private static readonly Guid ApplicantOwnerId = Guid.NewGuid();
    private static readonly Guid DogListingId = Guid.NewGuid();
    private static readonly Guid ShelterOwnerId = Guid.NewGuid();

    private static IDocumentSession BuildSession(
        ShelterAccount shelterAccount, Application? application, DogListing? dogListing,
        out IEventBoundary<ApproveApplicationDcbBoundary> boundary)
    {
        var session = Substitute.For<IDocumentSession>();
        var eventStore = Substitute.For<Marten.Events.IEventStoreOperations>();
        session.Events.Returns(eventStore);

        session.LoadAsync<Application>(application?.Id ?? Guid.NewGuid(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(application));
        session.LoadAsync<DogListing>(DogListingId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(dogListing));
        session.LoadAsync<ShelterAccount>(shelterAccount.Id, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ShelterAccount?>(shelterAccount));

        boundary = Substitute.For<IEventBoundary<ApproveApplicationDcbBoundary>>();
        eventStore.FetchForWritingByTags<ApproveApplicationDcbBoundary>(Arg.Any<EventTagQuery>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(boundary));

        // BuildEvent must return a real, working IEvent for WithTag/Data to
        // operate on - a bare NSubstitute mock's Data/Tags default to null,
        // which is fine here since these tests assert on the boundary's
        // AppendOne calls (by wrapped event type), not on tag contents,
        // which is Marten's own plumbing, not this handler's business logic.
        eventStore.BuildEvent(Arg.Any<object>()).Returns(callInfo =>
        {
            var wrapped = Substitute.For<IEvent>();
            wrapped.Data.Returns(callInfo.Arg<object>());
            return wrapped;
        });

        return session;
    }

    private static ClaimsPrincipal BuildUser(Guid ownerId) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, ownerId.ToString())]));

    [Fact]
    public async Task Handle_WhenCallerDoesNotOwnTheShelter_ReturnsForbidAndNoIntegrationEvent()
    {
        var shelterAccount = ShelterAccount.RequestNew(ShelterOwnerId, "Sunny Paws Rescue, EIN 12-3456789", Guid.NewGuid()).ShelterAccount;
        var application = Application.SubmitNew(ApplicantOwnerId, DogListingId, shelterAccount.Id, TestIntake.Default).Application;
        application.Review();

        var session = BuildSession(shelterAccount, application, null, out _);

        var (result, integrationEvent) = await ApproveApplicationHandler.Handle(
            application.Id, BuildUser(Guid.NewGuid()), session, CancellationToken.None);

        result.Result.Should().BeOfType<ForbidHttpResult>();
        integrationEvent.Should().BeNull();
    }

    [Fact]
    public async Task Handle_WhenUnderReview_ApprovesAndCascadesApplicationApprovedWithDogName()
    {
        var shelterAccount = ShelterAccount.RequestNew(ShelterOwnerId, "Sunny Paws Rescue, EIN 12-3456789", Guid.NewGuid()).ShelterAccount;
        var application = Application.SubmitNew(ApplicantOwnerId, DogListingId, shelterAccount.Id, TestIntake.Default).Application;
        application.Review();
        var dogListing = DogListing.AddNew(shelterAccount.Id, "Biscuit", "Beagle mix", 24, "Friendly").DogListing;

        var session = BuildSession(shelterAccount, application, dogListing, out var boundary);

        var (result, integrationEvent) = await ApproveApplicationHandler.Handle(
            application.Id, BuildUser(ShelterOwnerId), session, CancellationToken.None);

        result.Result.Should().BeOfType<Ok<ApproveApplicationResponse>>();
        application.Status.Should().Be(ApplicationStatus.Approved);
        dogListing.Status.Should().Be(DogListingStatus.Adopted, "v3 ENRICHMENT: approval cascades the listing's status");

        // ONE combined boundary now gets both appends, instead of two
        // independent per-stream streams (DCB modernization's whole point).
        boundary.Received(1).AppendOne(Arg.Is<IEvent>(e => e != null && e.Data is K9Crush.Modules.ShelterAdoption.Domain.Events.ApplicationApprovalV1));
        boundary.Received(1).AppendOne(Arg.Is<IEvent>(e => e != null && e.Data is K9Crush.Modules.ShelterAdoption.Domain.Events.DogListingStatusUpdatedV1));
        await session.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());

        integrationEvent.Should().NotBeNull();
        integrationEvent!.ApplicationId.Should().Be(application.Id);
        integrationEvent.ApplicantOwnerId.Should().Be(ApplicantOwnerId);
        integrationEvent.DogName.Should().Be("Biscuit");
    }

    [Fact]
    public async Task Handle_WhenTheDogListingNoLongerExists_StillApprovesAndCascadesWithBlankDogName()
    {
        var shelterAccount = ShelterAccount.RequestNew(ShelterOwnerId, "Sunny Paws Rescue, EIN 12-3456789", Guid.NewGuid()).ShelterAccount;
        var application = Application.SubmitNew(ApplicantOwnerId, DogListingId, shelterAccount.Id, TestIntake.Default).Application;
        application.Review();

        var session = BuildSession(shelterAccount, application, null, out var boundary);

        var (result, integrationEvent) = await ApproveApplicationHandler.Handle(
            application.Id, BuildUser(ShelterOwnerId), session, CancellationToken.None);

        result.Result.Should().BeOfType<Ok<ApproveApplicationResponse>>();
        integrationEvent!.DogName.Should().BeEmpty();
        boundary.DidNotReceive().AppendOne(Arg.Is<IEvent>(e => e != null && e.Data is K9Crush.Modules.ShelterAdoption.Domain.Events.DogListingStatusUpdatedV1));
    }
}

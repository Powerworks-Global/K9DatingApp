using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http.HttpResults;
using K9Crush.Modules.ShelterAdoption.Api.Commands.AddToWaitingList;
using K9Crush.Modules.ShelterAdoption.Domain;
using Xunit;

namespace K9Crush.IntegrationTests.ShelterAdoption;

/// <summary>
/// Layer 3 (TestingApproach.md) - AddToWaitingListHandler calls
/// session.Query&lt;DogSurrenderRequest&gt;().Where(...).CountAsync() to
/// compute waitlistPosition (a per-shelter count), which Layer 2's
/// IDocumentSession mocks can't reach. Covers the yaml's
/// SurrenderingYourDogFullIntake chapter's "AddedToWaitingList" spec
/// (given Dog Surrender Accepted, when Add To Waiting List, then Added To
/// Waiting List) plus the position-increments-per-shelter behavior that
/// spec implies but doesn't spell out. Query is scoped by ShelterAccountId
/// (unlike GetSurrenderReviewQueueHandler's platform-wide query), so this
/// safely shares the collection-wide Postgres container.
/// </summary>
[Collection(ShelterAdoptionPostgresCollection.Name)]
public class AddToWaitingListIntegrationTests(ShelterAdoptionPostgresFixture fixture)
{
    private static ClaimsPrincipal BuildUser(Guid ownerId) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, ownerId.ToString())]));

    private async Task<Guid> CreateFullIntakeShelterAsync(Guid ownerId)
    {
        var (shelterAccount, requestedEvent) = ShelterAccount.RequestNew(ownerId, "Sunny Paws Rescue, EIN 12-3456789", Guid.NewGuid());
        var modeConfiguredEvent = shelterAccount.ConfigureSurrenderIntakeMode(SurrenderIntakeMode.FullIntake);

        await using var session = fixture.Store.LightweightSession();
        session.Events.StartStream<ShelterAccount>(shelterAccount.Id, requestedEvent, modeConfiguredEvent);
        await session.SaveChangesAsync();
        return shelterAccount.Id;
    }

    private async Task<Guid> CreateAcceptedSurrenderRequestAsync(Guid shelterAccountId)
    {
        var (surrenderRequest, requestedEvent) = DogSurrenderRequest.RequestNew(
            Guid.NewGuid(), "Cooper", "Terrier mix", 48, "Relocating for work", "Gentle", "Healthy");
        var reviewedEvent = surrenderRequest.Review();
        var acceptedEvent = surrenderRequest.Accept(shelterAccountId);

        await using var session = fixture.Store.LightweightSession();
        session.Events.StartStream<DogSurrenderRequest>(surrenderRequest.Id, requestedEvent, reviewedEvent, acceptedEvent);
        await session.SaveChangesAsync();
        return surrenderRequest.Id;
    }

    [Fact]
    public async Task Handle_WhenAccepted_AddsToWaitingListAtPositionOne()
    {
        var ownerId = Guid.NewGuid();
        var shelterAccountId = await CreateFullIntakeShelterAsync(ownerId);
        var surrenderRequestId = await CreateAcceptedSurrenderRequestAsync(shelterAccountId);

        await using var session = fixture.Store.LightweightSession();
        var result = await AddToWaitingListHandler.Handle(surrenderRequestId, BuildUser(ownerId), session, CancellationToken.None);

        result.Result.Should().BeOfType<Ok<AddToWaitingListResponse>>();
        var ok = (Ok<AddToWaitingListResponse>)result.Result;
        ok.Value!.SurrenderRequestId.Should().Be(surrenderRequestId);
        ok.Value.WaitlistPosition.Should().Be(1);
    }

    [Fact]
    public async Task Handle_WhenOtherRequestsAlreadyOnThisShelterWaitingList_ReturnsNextPosition()
    {
        var ownerId = Guid.NewGuid();
        var shelterAccountId = await CreateFullIntakeShelterAsync(ownerId);
        var firstRequestId = await CreateAcceptedSurrenderRequestAsync(shelterAccountId);
        var secondRequestId = await CreateAcceptedSurrenderRequestAsync(shelterAccountId);

        await using (var session = fixture.Store.LightweightSession())
        {
            var firstResult = await AddToWaitingListHandler.Handle(firstRequestId, BuildUser(ownerId), session, CancellationToken.None);
            ((Ok<AddToWaitingListResponse>)firstResult.Result).Value!.WaitlistPosition.Should().Be(1);
        }

        await using (var session = fixture.Store.LightweightSession())
        {
            var secondResult = await AddToWaitingListHandler.Handle(secondRequestId, BuildUser(ownerId), session, CancellationToken.None);
            ((Ok<AddToWaitingListResponse>)secondResult.Result).Value!.WaitlistPosition.Should().Be(2);
        }
    }

    [Fact]
    public async Task Handle_WhenAnotherShelterHasWaitlistedRequests_DoesNotCountThemTowardsThisShelter()
    {
        var otherOwnerId = Guid.NewGuid();
        var otherShelterAccountId = await CreateFullIntakeShelterAsync(otherOwnerId);
        var otherShelterRequestId = await CreateAcceptedSurrenderRequestAsync(otherShelterAccountId);
        await using (var session = fixture.Store.LightweightSession())
        {
            await AddToWaitingListHandler.Handle(otherShelterRequestId, BuildUser(otherOwnerId), session, CancellationToken.None);
        }

        var ownerId = Guid.NewGuid();
        var shelterAccountId = await CreateFullIntakeShelterAsync(ownerId);
        var surrenderRequestId = await CreateAcceptedSurrenderRequestAsync(shelterAccountId);

        await using var thisSession = fixture.Store.LightweightSession();
        var result = await AddToWaitingListHandler.Handle(surrenderRequestId, BuildUser(ownerId), thisSession, CancellationToken.None);

        ((Ok<AddToWaitingListResponse>)result.Result).Value!.WaitlistPosition.Should().Be(1);
    }
}

using System.Security.Claims;
using FluentAssertions;
using Marten;
using Microsoft.AspNetCore.Http.HttpResults;
using NSubstitute;
using K9Crush.Modules.ShelterAdoption.Api.Commands.PerformBehaviorTest;
using K9Crush.Modules.ShelterAdoption.Domain;
using Xunit;

namespace K9Crush.Modules.ShelterAdoption.Tests.Handlers;

/// <summary>
/// Layer 2 (TestingApproach.md) - PerformBehaviorTestHandler is a
/// FetchForWriting/AppendOne/SaveChangesAsync write against
/// DogSurrenderRequest, plus two plain read-only LoadAsync calls
/// (DogListing, then ShelterAccount) for the ownership check - same shape
/// as UpdateListingStatusHandler's dogListingId -> ShelterAccountId
/// resolution, but with DogSurrenderRequest (not DogListing) as the
/// actual write target.
/// </summary>
public class PerformBehaviorTestHandlerTests
{
    private static readonly Guid ShelterOwnerId = Guid.NewGuid();

    private static ClaimsPrincipal BuildUser(Guid ownerId) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, ownerId.ToString())]));

    private static DogSurrenderRequest BuildAccepted()
    {
        var request = DogSurrenderRequest.RequestNew(
            Guid.NewGuid(), "Cooper", "Terrier mix", 48, "Relocating for work", "Gentle, a little shy", "Healthy").DogSurrenderRequest;
        request.Review();
        request.Accept();
        return request;
    }

    private static (ShelterAccount shelterAccount, DogListing dogListing) SeedListing()
    {
        var shelterAccount = ShelterAccount.RequestNew(ShelterOwnerId, "Sunny Paws Rescue, EIN 12-3456789", Guid.NewGuid()).ShelterAccount;
        var dogListing = DogListing.AddNew(shelterAccount.Id, "Cooper", "Terrier mix", 48, "Gentle, a little shy").DogListing;
        return (shelterAccount, dogListing);
    }

    private static IDocumentSession BuildSession(DogListing? dogListing, ShelterAccount? shelterAccount, DogSurrenderRequest? surrenderRequest, out JasperFx.Events.IEventStream<DogSurrenderRequest> stream)
    {
        var session = MartenEventStoreTestHelpers.BuildSessionWithFetchForWriting(surrenderRequest?.Id ?? Guid.NewGuid(), surrenderRequest, out stream);
        session.LoadAsync<DogListing>(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(dogListing);
        session.LoadAsync<ShelterAccount>(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(shelterAccount);
        return session;
    }

    [Fact]
    public async Task Handle_WhenAcceptedAndCallerOwnsTheListing_CompletesBehaviorTestAndPersists()
    {
        var surrenderRequest = BuildAccepted();
        var (shelterAccount, dogListing) = SeedListing();
        var session = BuildSession(dogListing, shelterAccount, surrenderRequest, out var stream);
        var performedBy = Guid.NewGuid();

        var result = await PerformBehaviorTestHandler.Handle(
            surrenderRequest.Id, new PerformBehaviorTestRequest(dogListing.Id, performedBy, true, "Friendly, no aggression observed"),
            BuildUser(ShelterOwnerId), session, CancellationToken.None);

        result.Result.Should().BeOfType<Ok<PerformBehaviorTestResponse>>();
        var response = ((Ok<PerformBehaviorTestResponse>)result.Result).Value!;
        response.SurrenderRequestId.Should().Be(surrenderRequest.Id);
        response.SuitableForRehoming.Should().BeTrue();
        response.BehaviorNotes.Should().Be("Friendly, no aggression observed");

        surrenderRequest.BehaviorTestPerformedBy.Should().Be(performedBy);
        surrenderRequest.BehaviorTestSuitableForRehoming.Should().BeTrue();
        stream.Received(1).AppendOne(Arg.Is<object>(o => o != null && o.GetType() == typeof(K9Crush.Modules.ShelterAdoption.Domain.Events.BehaviorTestCompletedV1)));
        await session.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenDogListingDoesNotExist_ReturnsNotFound()
    {
        var session = BuildSession(null, null, null, out _);

        var result = await PerformBehaviorTestHandler.Handle(
            Guid.NewGuid(), new PerformBehaviorTestRequest(Guid.NewGuid(), Guid.NewGuid(), true, "Friendly"),
            BuildUser(ShelterOwnerId), session, CancellationToken.None);

        result.Result.Should().BeOfType<NotFound>();
    }

    [Fact]
    public async Task Handle_WhenCallerDoesNotOwnTheListingsShelter_ReturnsForbid()
    {
        var surrenderRequest = BuildAccepted();
        var (shelterAccount, dogListing) = SeedListing();
        var session = BuildSession(dogListing, shelterAccount, surrenderRequest, out _);

        var result = await PerformBehaviorTestHandler.Handle(
            surrenderRequest.Id, new PerformBehaviorTestRequest(dogListing.Id, Guid.NewGuid(), true, "Friendly"),
            BuildUser(Guid.NewGuid()), session, CancellationToken.None);

        result.Result.Should().BeOfType<ForbidHttpResult>();
    }

    [Fact]
    public async Task Handle_WhenSurrenderRequestDoesNotExist_ReturnsNotFound()
    {
        var (shelterAccount, dogListing) = SeedListing();
        var session = BuildSession(dogListing, shelterAccount, null, out _);

        var result = await PerformBehaviorTestHandler.Handle(
            Guid.NewGuid(), new PerformBehaviorTestRequest(dogListing.Id, Guid.NewGuid(), true, "Friendly"),
            BuildUser(ShelterOwnerId), session, CancellationToken.None);

        result.Result.Should().BeOfType<NotFound>();
    }

    [Fact]
    public async Task Handle_WhenSurrenderRequestIsNotAccepted_ReturnsConflictAndDoesNotPersist()
    {
        var surrenderRequest = DogSurrenderRequest.RequestNew(
            Guid.NewGuid(), "Cooper", "Terrier mix", 48, "Relocating for work", "Gentle", "Healthy").DogSurrenderRequest; // Requested, not Accepted
        var (shelterAccount, dogListing) = SeedListing();
        var session = BuildSession(dogListing, shelterAccount, surrenderRequest, out var stream);

        var result = await PerformBehaviorTestHandler.Handle(
            surrenderRequest.Id, new PerformBehaviorTestRequest(dogListing.Id, Guid.NewGuid(), true, "Friendly"),
            BuildUser(ShelterOwnerId), session, CancellationToken.None);

        result.Result.Should().BeOfType<Conflict<string>>();
        stream.DidNotReceiveWithAnyArgs().AppendOne(default!);
    }
}

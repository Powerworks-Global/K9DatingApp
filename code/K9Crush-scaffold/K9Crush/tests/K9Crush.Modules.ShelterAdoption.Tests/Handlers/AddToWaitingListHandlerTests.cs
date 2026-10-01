using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http.HttpResults;
using NSubstitute;
using K9Crush.Modules.ShelterAdoption.Api.Commands.AddToWaitingList;
using K9Crush.Modules.ShelterAdoption.Domain;
using Xunit;

namespace K9Crush.Modules.ShelterAdoption.Tests.Handlers;

/// <summary>
/// Layer 2 (TestingApproach.md) - only the early-exit branches (NotFound,
/// Forbid, and the two Conflict guards) are covered here; they all return
/// before AddToWaitingListHandler reaches session.Query&lt;DogSurrenderRequest&gt;(),
/// which NSubstitute cannot meaningfully mock (build-state-change skill's
/// Step 5). The success path (waitlistPosition computed via that query) is
/// covered by AddToWaitingListIntegrationTests instead.
/// </summary>
public class AddToWaitingListHandlerTests
{
    private static ClaimsPrincipal BuildUser(Guid ownerId) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, ownerId.ToString())]));

    private static DogSurrenderRequest BuildAccepted(Guid shelterAccountId)
    {
        var (surrenderRequest, _) = DogSurrenderRequest.RequestNew(
            Guid.NewGuid(), "Cooper", "Terrier mix", 48, "Relocating for work", "Gentle", "Healthy");
        surrenderRequest.Review();
        surrenderRequest.Accept(shelterAccountId);
        return surrenderRequest;
    }

    [Fact]
    public async Task Handle_WhenSurrenderRequestDoesNotExist_ReturnsNotFound()
    {
        var surrenderRequestId = Guid.NewGuid();
        var session = MartenEventStoreTestHelpers.BuildSessionWithFetchForWriting<DogSurrenderRequest>(surrenderRequestId, null, out _);

        var result = await AddToWaitingListHandler.Handle(surrenderRequestId, BuildUser(Guid.NewGuid()), session, CancellationToken.None);

        result.Result.Should().BeOfType<NotFound>();
    }

    [Fact]
    public async Task Handle_WhenShelterAccountDoesNotExist_ReturnsNotFound()
    {
        var shelterAccountId = Guid.NewGuid();
        var surrenderRequest = BuildAccepted(shelterAccountId);
        var session = MartenEventStoreTestHelpers.BuildSessionWithFetchForWriting(surrenderRequest.Id, surrenderRequest, out _);
        session.LoadAsync<ShelterAccount>(shelterAccountId, Arg.Any<CancellationToken>()).Returns((ShelterAccount?)null);

        var result = await AddToWaitingListHandler.Handle(surrenderRequest.Id, BuildUser(Guid.NewGuid()), session, CancellationToken.None);

        result.Result.Should().BeOfType<NotFound>();
    }

    [Fact]
    public async Task Handle_WhenCallerDoesNotOwnTheShelterAccount_ReturnsForbid()
    {
        var shelterAccount = ShelterAccount.RequestNew(Guid.NewGuid(), "Sunny Paws Rescue, EIN 12-3456789", Guid.NewGuid()).ShelterAccount;
        var surrenderRequest = BuildAccepted(shelterAccount.Id);
        var session = MartenEventStoreTestHelpers.BuildSessionWithFetchForWriting(surrenderRequest.Id, surrenderRequest, out var stream);
        session.LoadAsync<ShelterAccount>(shelterAccount.Id, Arg.Any<CancellationToken>()).Returns(shelterAccount);

        var result = await AddToWaitingListHandler.Handle(surrenderRequest.Id, BuildUser(Guid.NewGuid()), session, CancellationToken.None);

        result.Result.Should().BeOfType<ForbidHttpResult>();
        stream.DidNotReceiveWithAnyArgs().AppendOne(default!);
    }

    [Fact]
    public async Task Handle_WhenShelterIsNotInFullIntakeMode_ReturnsConflict()
    {
        var ownerId = Guid.NewGuid();
        var shelterAccount = ShelterAccount.RequestNew(ownerId, "Sunny Paws Rescue, EIN 12-3456789", Guid.NewGuid()).ShelterAccount;
        var surrenderRequest = BuildAccepted(shelterAccount.Id);
        var session = MartenEventStoreTestHelpers.BuildSessionWithFetchForWriting(surrenderRequest.Id, surrenderRequest, out var stream);
        session.LoadAsync<ShelterAccount>(shelterAccount.Id, Arg.Any<CancellationToken>()).Returns(shelterAccount);

        var result = await AddToWaitingListHandler.Handle(surrenderRequest.Id, BuildUser(ownerId), session, CancellationToken.None);

        result.Result.Should().BeOfType<Conflict<string>>();
        stream.DidNotReceiveWithAnyArgs().AppendOne(default!);
    }

    [Fact]
    public async Task Handle_WhenSurrenderRequestIsNotAccepted_ReturnsConflict()
    {
        var (surrenderRequest, _) = DogSurrenderRequest.RequestNew(
            Guid.NewGuid(), "Cooper", "Terrier mix", 48, "Relocating for work", "Gentle", "Healthy");
        var session = MartenEventStoreTestHelpers.BuildSessionWithFetchForWriting(surrenderRequest.Id, surrenderRequest, out var stream);

        var result = await AddToWaitingListHandler.Handle(surrenderRequest.Id, BuildUser(Guid.NewGuid()), session, CancellationToken.None);

        result.Result.Should().BeOfType<Conflict<string>>();
        stream.DidNotReceiveWithAnyArgs().AppendOne(default!);
    }
}

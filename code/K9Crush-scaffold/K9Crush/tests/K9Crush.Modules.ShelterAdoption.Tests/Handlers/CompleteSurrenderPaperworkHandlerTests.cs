using System.Security.Claims;
using FluentAssertions;
using Marten;
using Microsoft.AspNetCore.Http.HttpResults;
using NSubstitute;
using K9Crush.Modules.ShelterAdoption.Api.Commands.CompleteSurrenderPaperwork;
using K9Crush.Modules.ShelterAdoption.Domain;
using K9Crush.Modules.ShelterAdoption.Domain.Events;
using Xunit;

namespace K9Crush.Modules.ShelterAdoption.Tests.Handlers;

/// <summary>
/// Layer 2 (TestingApproach.md) - CompleteSurrenderPaperworkHandler is a
/// genuine two-load write: FetchForWriting/AppendOne against
/// DogSurrenderRequest plus a plain LoadAsync against ShelterAccount for
/// the ownership/FullIntake-mode checks (ADR-031), same shape as
/// AcceptDogSurrenderHandlerTests.
/// </summary>
public class CompleteSurrenderPaperworkHandlerTests
{
    private static ClaimsPrincipal BuildUser(Guid ownerId) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, ownerId.ToString())]));

    private static DogSurrenderRequest BuildAccepted(Guid shelterAccountId)
    {
        var request = DogSurrenderRequest.RequestNew(
            Guid.NewGuid(), "Cooper", "Terrier mix", 48, "Relocating for work", "Gentle", "Healthy").DogSurrenderRequest;
        request.Review();
        request.Accept(shelterAccountId);
        return request;
    }

    private static ShelterAccount BuildFullIntakeShelterAccount(Guid ownerId)
    {
        var shelterAccount = ShelterAccount.RequestNew(ownerId, "Sunny Paws Rescue, EIN 12-3456789", Guid.NewGuid()).ShelterAccount;
        shelterAccount.Verify();
        shelterAccount.Activate();
        shelterAccount.ConfigureSurrenderIntakeMode(SurrenderIntakeMode.FullIntake);
        return shelterAccount;
    }

    [Fact]
    public async Task Handle_WhenSurrenderRequestDoesNotExist_ReturnsNotFound()
    {
        var surrenderRequestId = Guid.NewGuid();
        var session = MartenEventStoreTestHelpers.BuildSessionWithFetchForWriting<DogSurrenderRequest>(surrenderRequestId, null, out _);

        var result = await CompleteSurrenderPaperworkHandler.Handle(
            surrenderRequestId, new CompleteSurrenderPaperworkRequest(true, "dog_licence"),
            BuildUser(Guid.NewGuid()), session, CancellationToken.None);

        result.Result.Should().BeOfType<NotFound>();
    }

    [Fact]
    public async Task Handle_WhenShelterAccountDoesNotExist_ReturnsForbid()
    {
        var surrenderRequest = BuildAccepted(Guid.NewGuid());
        var session = MartenEventStoreTestHelpers.BuildSessionWithFetchForWriting(surrenderRequest.Id, surrenderRequest, out var stream);
        session.LoadAsync<ShelterAccount>(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((ShelterAccount?)null);

        var result = await CompleteSurrenderPaperworkHandler.Handle(
            surrenderRequest.Id, new CompleteSurrenderPaperworkRequest(true, "dog_licence"),
            BuildUser(Guid.NewGuid()), session, CancellationToken.None);

        result.Result.Should().BeOfType<ForbidHttpResult>();
        stream.DidNotReceiveWithAnyArgs().AppendOne(default!);
    }

    [Fact]
    public async Task Handle_WhenCallerDoesNotOwnTheShelterAccount_ReturnsForbid()
    {
        var shelterAccount = BuildFullIntakeShelterAccount(Guid.NewGuid());
        var surrenderRequest = BuildAccepted(shelterAccount.Id);
        var session = MartenEventStoreTestHelpers.BuildSessionWithFetchForWriting(surrenderRequest.Id, surrenderRequest, out var stream);
        session.LoadAsync<ShelterAccount>(shelterAccount.Id, Arg.Any<CancellationToken>()).Returns(shelterAccount);

        var result = await CompleteSurrenderPaperworkHandler.Handle(
            surrenderRequest.Id, new CompleteSurrenderPaperworkRequest(true, "dog_licence"),
            BuildUser(Guid.NewGuid()), session, CancellationToken.None);

        result.Result.Should().BeOfType<ForbidHttpResult>();
        stream.DidNotReceiveWithAnyArgs().AppendOne(default!);
    }

    [Fact]
    public async Task Handle_WhenNotAccepted_ReturnsConflict()
    {
        var ownerId = Guid.NewGuid();
        var shelterAccount = BuildFullIntakeShelterAccount(ownerId);
        var surrenderRequest = DogSurrenderRequest.RequestNew(
            Guid.NewGuid(), "Cooper", "Terrier mix", 48, "Relocating for work", "Gentle", "Healthy").DogSurrenderRequest; // Requested, not Accepted
        var session = MartenEventStoreTestHelpers.BuildSessionWithFetchForWriting(surrenderRequest.Id, surrenderRequest, out var stream);
        session.LoadAsync<ShelterAccount>(shelterAccount.Id, Arg.Any<CancellationToken>()).Returns(shelterAccount);

        var result = await CompleteSurrenderPaperworkHandler.Handle(
            surrenderRequest.Id, new CompleteSurrenderPaperworkRequest(true, "dog_licence"),
            BuildUser(ownerId), session, CancellationToken.None);

        result.Result.Should().BeOfType<Conflict<string>>();
        stream.DidNotReceiveWithAnyArgs().AppendOne(default!);
    }

    [Fact]
    public async Task Handle_WhenShelterIsNotFullIntakeMode_ReturnsConflict()
    {
        var ownerId = Guid.NewGuid();
        var shelterAccount = ShelterAccount.RequestNew(ownerId, "Sunny Paws Rescue, EIN 12-3456789", Guid.NewGuid()).ShelterAccount;
        shelterAccount.Verify();
        shelterAccount.Activate(); // Simple mode (default) - no ConfigureSurrenderIntakeMode call
        var surrenderRequest = BuildAccepted(shelterAccount.Id);
        var session = MartenEventStoreTestHelpers.BuildSessionWithFetchForWriting(surrenderRequest.Id, surrenderRequest, out var stream);
        session.LoadAsync<ShelterAccount>(shelterAccount.Id, Arg.Any<CancellationToken>()).Returns(shelterAccount);

        var result = await CompleteSurrenderPaperworkHandler.Handle(
            surrenderRequest.Id, new CompleteSurrenderPaperworkRequest(true, "dog_licence"),
            BuildUser(ownerId), session, CancellationToken.None);

        result.Result.Should().BeOfType<Conflict<string>>();
        stream.DidNotReceiveWithAnyArgs().AppendOne(default!);
    }

    [Fact]
    public async Task Handle_WhenAcceptedAndFullIntakeAndOwned_CompletesPaperworkAndPersists()
    {
        var ownerId = Guid.NewGuid();
        var shelterAccount = BuildFullIntakeShelterAccount(ownerId);
        var surrenderRequest = BuildAccepted(shelterAccount.Id);
        var session = MartenEventStoreTestHelpers.BuildSessionWithFetchForWriting(surrenderRequest.Id, surrenderRequest, out var stream);
        session.LoadAsync<ShelterAccount>(shelterAccount.Id, Arg.Any<CancellationToken>()).Returns(shelterAccount);

        var result = await CompleteSurrenderPaperworkHandler.Handle(
            surrenderRequest.Id, new CompleteSurrenderPaperworkRequest(true, "dog_licence"),
            BuildUser(ownerId), session, CancellationToken.None);

        result.Result.Should().BeOfType<Ok<CompleteSurrenderPaperworkResponse>>();
        var response = ((Ok<CompleteSurrenderPaperworkResponse>)result.Result).Value!;
        response.SurrenderRequestId.Should().Be(surrenderRequest.Id);
        response.LegalTransferSigned.Should().BeTrue();
        response.OwnershipProofType.Should().Be("dog_licence");

        surrenderRequest.Status.Should().Be(SurrenderRequestStatus.Accepted);
        stream.Received(1).AppendOne(Arg.Is<object>(o => o != null && o.GetType() == typeof(SurrenderPaperworkCompletedV1)));
        await session.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}

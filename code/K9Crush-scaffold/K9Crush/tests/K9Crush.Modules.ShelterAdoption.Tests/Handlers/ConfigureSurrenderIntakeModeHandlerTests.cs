using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http.HttpResults;
using NSubstitute;
using K9Crush.Modules.ShelterAdoption.Api.Commands.ConfigureSurrenderIntakeMode;
using K9Crush.Modules.ShelterAdoption.Domain;
using K9Crush.Modules.ShelterAdoption.Domain.Events;
using Xunit;

namespace K9Crush.Modules.ShelterAdoption.Tests.Handlers;

/// <summary>
/// Layer 2 (TestingApproach.md) - ConfigureSurrenderIntakeModeHandler only
/// calls FetchForWriting/AppendOne/SaveChangesAsync against ShelterAccount
/// itself, so IDocumentSession mocks cleanly here (ADR-031).
/// </summary>
public class ConfigureSurrenderIntakeModeHandlerTests
{
    private static ClaimsPrincipal BuildUser(Guid ownerId) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, ownerId.ToString())]));

    [Fact]
    public async Task Handle_WhenShelterAccountDoesNotExist_ReturnsNotFound()
    {
        var shelterAccountId = Guid.NewGuid();
        var session = MartenEventStoreTestHelpers.BuildSessionWithFetchForWriting<ShelterAccount>(shelterAccountId, null, out _);

        var result = await ConfigureSurrenderIntakeModeHandler.Handle(
            shelterAccountId, new ConfigureSurrenderIntakeModeRequest(SurrenderIntakeMode.FullIntake),
            BuildUser(Guid.NewGuid()), session, CancellationToken.None);

        result.Result.Should().BeOfType<NotFound>();
    }

    [Fact]
    public async Task Handle_WhenCallerDoesNotOwnTheShelterAccount_ReturnsForbid()
    {
        var shelterAccount = ShelterAccount.RequestNew(Guid.NewGuid(), "Sunny Paws Rescue, EIN 12-3456789", Guid.NewGuid()).ShelterAccount;
        var session = MartenEventStoreTestHelpers.BuildSessionWithFetchForWriting(shelterAccount.Id, shelterAccount, out var stream);

        var result = await ConfigureSurrenderIntakeModeHandler.Handle(
            shelterAccount.Id, new ConfigureSurrenderIntakeModeRequest(SurrenderIntakeMode.FullIntake),
            BuildUser(Guid.NewGuid()), session, CancellationToken.None);

        result.Result.Should().BeOfType<ForbidHttpResult>();
        stream.DidNotReceiveWithAnyArgs().AppendOne(default!);
    }

    [Fact]
    public async Task Handle_WhenCallerOwnsTheShelterAccount_ConfiguresModeAndPersists()
    {
        var ownerId = Guid.NewGuid();
        var shelterAccount = ShelterAccount.RequestNew(ownerId, "Sunny Paws Rescue, EIN 12-3456789", Guid.NewGuid()).ShelterAccount;
        var session = MartenEventStoreTestHelpers.BuildSessionWithFetchForWriting(shelterAccount.Id, shelterAccount, out var stream);

        var result = await ConfigureSurrenderIntakeModeHandler.Handle(
            shelterAccount.Id, new ConfigureSurrenderIntakeModeRequest(SurrenderIntakeMode.FullIntake),
            BuildUser(ownerId), session, CancellationToken.None);

        result.Result.Should().BeOfType<Ok<ConfigureSurrenderIntakeModeResponse>>();
        ((Ok<ConfigureSurrenderIntakeModeResponse>)result.Result).Value!.Mode.Should().Be(SurrenderIntakeMode.FullIntake);
        shelterAccount.SurrenderIntakeMode.Should().Be(SurrenderIntakeMode.FullIntake);

        stream.Received(1).AppendOne(Arg.Is<object>(o => o != null && o.GetType() == typeof(SurrenderIntakeModeConfiguredV1)));
        await session.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}

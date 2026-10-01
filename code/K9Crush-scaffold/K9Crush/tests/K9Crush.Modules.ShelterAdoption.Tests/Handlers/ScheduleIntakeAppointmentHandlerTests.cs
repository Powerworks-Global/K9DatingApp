using System.Security.Claims;
using FluentAssertions;
using Marten;
using Microsoft.AspNetCore.Http.HttpResults;
using NSubstitute;
using K9Crush.Modules.ShelterAdoption.Api.Commands.ScheduleIntakeAppointment;
using K9Crush.Modules.ShelterAdoption.Domain;
using K9Crush.Modules.ShelterAdoption.Domain.Events;
using Xunit;

namespace K9Crush.Modules.ShelterAdoption.Tests.Handlers;

/// <summary>
/// Layer 2 (TestingApproach.md) - ScheduleIntakeAppointmentHandler calls
/// FetchForWriting/AppendOne/SaveChangesAsync against DogSurrenderRequest
/// plus a plain LoadAsync against ShelterAccount for the ownership +
/// FullIntake-mode checks (read-only, not a self-load - ADR-031).
/// </summary>
public class ScheduleIntakeAppointmentHandlerTests
{
    private static readonly Guid ShelterOwnerId = Guid.NewGuid();
    private static readonly DateOnly AppointmentDate = new(2026, 8, 5);

    private static ClaimsPrincipal BuildUser(Guid ownerId) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, ownerId.ToString())]));

    private static ShelterAccount BuildShelterAccount(SurrenderIntakeMode mode)
    {
        var shelterAccount = ShelterAccount.RequestNew(ShelterOwnerId, "Sunny Paws Rescue, EIN 12-3456789", Guid.NewGuid()).ShelterAccount;
        if (mode == SurrenderIntakeMode.FullIntake)
            shelterAccount.ConfigureSurrenderIntakeMode(SurrenderIntakeMode.FullIntake);
        return shelterAccount;
    }

    private static DogSurrenderRequest BuildAcceptedRequest(Guid shelterAccountId)
    {
        var request = DogSurrenderRequest.RequestNew(
            Guid.NewGuid(), "Cooper", "Terrier mix", 48,
            "Relocating for work", "Gentle, a little shy", "Up to date on vaccinations").DogSurrenderRequest;
        request.Review();
        request.Accept(shelterAccountId);
        return request;
    }

    private static IDocumentSession BuildSession(ShelterAccount shelterAccount, DogSurrenderRequest? surrenderRequest, out JasperFx.Events.IEventStream<DogSurrenderRequest> stream)
    {
        var session = MartenEventStoreTestHelpers.BuildSessionWithFetchForWriting(surrenderRequest?.Id ?? Guid.NewGuid(), surrenderRequest, out stream);
        session.LoadAsync<ShelterAccount>(shelterAccount.Id, Arg.Any<CancellationToken>()).Returns(shelterAccount);
        return session;
    }

    [Fact]
    public async Task Handle_WhenSurrenderRequestDoesNotExist_ReturnsNotFound()
    {
        var surrenderRequestId = Guid.NewGuid();
        var session = MartenEventStoreTestHelpers.BuildSessionWithFetchForWriting<DogSurrenderRequest>(surrenderRequestId, null, out _);

        var result = await ScheduleIntakeAppointmentHandler.Handle(
            surrenderRequestId, new ScheduleIntakeAppointmentRequest(AppointmentDate), BuildUser(ShelterOwnerId), session, CancellationToken.None);

        result.Result.Should().BeOfType<NotFound>();
    }

    [Fact]
    public async Task Handle_WhenCallerDoesNotOwnTheShelter_ReturnsForbid()
    {
        var shelterAccount = BuildShelterAccount(SurrenderIntakeMode.FullIntake);
        var surrenderRequest = BuildAcceptedRequest(shelterAccount.Id);
        var session = BuildSession(shelterAccount, surrenderRequest, out var stream);

        var result = await ScheduleIntakeAppointmentHandler.Handle(
            surrenderRequest.Id, new ScheduleIntakeAppointmentRequest(AppointmentDate), BuildUser(Guid.NewGuid()), session, CancellationToken.None);

        result.Result.Should().BeOfType<ForbidHttpResult>();
        stream.DidNotReceiveWithAnyArgs().AppendOne(default!);
    }

    [Fact]
    public async Task Handle_WhenShelterIsNotFullIntakeMode_ReturnsConflictAndDoesNotPersist()
    {
        var shelterAccount = BuildShelterAccount(SurrenderIntakeMode.Simple);
        var surrenderRequest = BuildAcceptedRequest(shelterAccount.Id);
        var session = BuildSession(shelterAccount, surrenderRequest, out var stream);

        var result = await ScheduleIntakeAppointmentHandler.Handle(
            surrenderRequest.Id, new ScheduleIntakeAppointmentRequest(AppointmentDate), BuildUser(ShelterOwnerId), session, CancellationToken.None);

        result.Result.Should().BeOfType<Conflict<string>>();
        stream.DidNotReceiveWithAnyArgs().AppendOne(default!);
    }

    [Fact]
    public async Task Handle_WhenSurrenderRequestIsNotAccepted_ReturnsConflictAndDoesNotPersist()
    {
        var shelterAccount = BuildShelterAccount(SurrenderIntakeMode.FullIntake);
        var surrenderRequest = DogSurrenderRequest.RequestNew(
            Guid.NewGuid(), "Cooper", "Terrier mix", 48,
            "Relocating for work", "Gentle, a little shy", "Up to date on vaccinations").DogSurrenderRequest;
        var session = BuildSession(shelterAccount, surrenderRequest, out var stream);

        var result = await ScheduleIntakeAppointmentHandler.Handle(
            surrenderRequest.Id, new ScheduleIntakeAppointmentRequest(AppointmentDate), BuildUser(ShelterOwnerId), session, CancellationToken.None);

        result.Result.Should().BeOfType<Conflict<string>>();
        stream.DidNotReceiveWithAnyArgs().AppendOne(default!);
    }

    [Fact]
    public async Task Handle_WhenCallerOwnsTheShelterAndFullIntakeAndAccepted_SchedulesAppointmentAndPersists()
    {
        var shelterAccount = BuildShelterAccount(SurrenderIntakeMode.FullIntake);
        var surrenderRequest = BuildAcceptedRequest(shelterAccount.Id);
        var session = BuildSession(shelterAccount, surrenderRequest, out var stream);

        var result = await ScheduleIntakeAppointmentHandler.Handle(
            surrenderRequest.Id, new ScheduleIntakeAppointmentRequest(AppointmentDate), BuildUser(ShelterOwnerId), session, CancellationToken.None);

        result.Result.Should().BeOfType<Ok<ScheduleIntakeAppointmentResponse>>();
        ((Ok<ScheduleIntakeAppointmentResponse>)result.Result).Value!.AppointmentDate.Should().Be(AppointmentDate);
        surrenderRequest.IntakeAppointmentDate.Should().Be(AppointmentDate);

        stream.Received(1).AppendOne(Arg.Is<object>(o => o != null && o.GetType() == typeof(IntakeAppointmentScheduledV1)));
        await session.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}

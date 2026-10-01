using System.Security.Claims;
using Marten;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using K9Crush.Modules.ShelterAdoption.Domain;
using Wolverine.Http;

namespace K9Crush.Modules.ShelterAdoption.Api.Commands.ScheduleIntakeAppointment;

/// <summary>
/// State-change slice: the SurrenderingYourDogFullIntake chapter's
/// "Schedule Intake Appointment" -> "Intake Appointment Scheduled".
/// FullIntake-only step (see ShelterAccount.SurrenderIntakeMode's doc
/// comment) - Simple-mode shelters never see this endpoint's step in
/// their workflow, but the guard still lives here rather than being
/// silently unreachable, same as the rest of this chapter's steps.
///
/// Ownership resolved via DogSurrenderRequest.ShelterAccountId, set at
/// Accept time (see AcceptDogSurrenderHandler / DogSurrenderAcceptedV1's
/// own doc comments for the disclosed gap-fill that made this field
/// available) - same "keyed by aggregate id alone, ownership resolved via
/// a stored ShelterAccountId" shape as UpdateListingStatusHandler.
/// </summary>
public static class ScheduleIntakeAppointmentHandler
{
    [WolverinePost("/api/v1/shelter-adoption/surrender-requests/{surrenderRequestId:guid}/intake-appointment")]
    [Authorize(Policy = "Shelter")]
    public static async Task<Results<Ok<ScheduleIntakeAppointmentResponse>, NotFound, ForbidHttpResult, Conflict<string>>> Handle(
        Guid surrenderRequestId,
        ScheduleIntakeAppointmentRequest request,
        ClaimsPrincipal user,
        IDocumentSession session,
        CancellationToken cancellationToken)
    {
        var callerOwnerId = Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);

        var stream = await session.Events.FetchForWriting<DogSurrenderRequest>(surrenderRequestId, cancellationToken);
        var surrenderRequest = stream.Aggregate;
        if (surrenderRequest is null)
            return TypedResults.NotFound();

        if (surrenderRequest.Status != SurrenderRequestStatus.Accepted)
            return TypedResults.Conflict($"Cannot schedule an intake appointment for a surrender request in status {surrenderRequest.Status}.");

        var shelterAccount = await session.LoadAsync<ShelterAccount>(surrenderRequest.ShelterAccountId, cancellationToken);
        if (shelterAccount is null || shelterAccount.RequestedByOwnerId != callerOwnerId)
            return TypedResults.Forbid();

        if (shelterAccount.SurrenderIntakeMode != SurrenderIntakeMode.FullIntake)
            return TypedResults.Conflict("This shelter is not configured for FullIntake surrender processing.");

        var @event = surrenderRequest.ScheduleIntakeAppointment(request.AppointmentDate);
        stream.AppendOne(@event);
        await session.SaveChangesAsync(cancellationToken);

        return TypedResults.Ok(new ScheduleIntakeAppointmentResponse(surrenderRequest.Id, surrenderRequest.IntakeAppointmentDate!.Value));
    }
}

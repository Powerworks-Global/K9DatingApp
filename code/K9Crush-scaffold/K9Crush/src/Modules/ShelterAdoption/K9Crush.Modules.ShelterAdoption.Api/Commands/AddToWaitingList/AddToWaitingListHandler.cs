using System.Security.Claims;
using Marten;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using K9Crush.Modules.ShelterAdoption.Domain;
using Wolverine.Http;

namespace K9Crush.Modules.ShelterAdoption.Api.Commands.AddToWaitingList;

/// <summary>
/// State-change slice: the SurrenderingYourDogFullIntake chapter's "Add To
/// Waiting List" -> "Added To Waiting List" - only valid from Accepted, and
/// only for a shelter that has switched SurrenderIntakeMode to FullIntake
/// (see ShelterAccount.SurrenderIntakeMode's doc comment; this is the
/// first of that chapter's 9 gated Shelter Staff steps).
///
/// waitlistPosition (the event's only field) is not caller-supplied - it's
/// computed here as a per-shelter count: how many of this shelter's other
/// DogSurrenderRequests are already on the waiting list, plus one. Scoped
/// by ShelterAccountId (added onto the aggregate by AcceptDogSurrenderHandler)
/// since the "Surrender Intake Pipeline" screen this command comes from is
/// a single shelter's own queue, not a platform-wide one.
///
/// Route/ownership-gate pattern matches ConfigureSurrenderIntakeModeHandler -
/// keyed by surrenderRequestId alone, ownership resolved via the request's
/// own ShelterAccountId.
/// </summary>
public static class AddToWaitingListHandler
{
    [WolverinePost("/api/v1/shelter-adoption/surrender-requests/{surrenderRequestId:guid}/waiting-list")]
    [Authorize(Policy = "Shelter")]
    public static async Task<Results<Ok<AddToWaitingListResponse>, NotFound, ForbidHttpResult, Conflict<string>>> Handle(
        Guid surrenderRequestId,
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
            return TypedResults.Conflict($"Cannot add a surrender request in status {surrenderRequest.Status} to the waiting list.");

        var shelterAccount = await session.LoadAsync<ShelterAccount>(surrenderRequest.ShelterAccountId, cancellationToken);
        if (shelterAccount is null)
            return TypedResults.NotFound();

        if (shelterAccount.RequestedByOwnerId != callerOwnerId)
            return TypedResults.Forbid();

        if (shelterAccount.SurrenderIntakeMode != SurrenderIntakeMode.FullIntake)
            return TypedResults.Conflict("Full intake pipeline steps are only available once this shelter has configured SurrenderIntakeMode to FullIntake.");

        var otherWaitlistedCount = await session.Query<DogSurrenderRequest>()
            .Where(x => x.ShelterAccountId == surrenderRequest.ShelterAccountId && x.WaitlistPosition != null)
            .CountAsync(cancellationToken);
        var waitlistPosition = otherWaitlistedCount + 1;

        var @event = surrenderRequest.AddToWaitingList(waitlistPosition);
        stream.AppendOne(@event);
        await session.SaveChangesAsync(cancellationToken);

        return TypedResults.Ok(new AddToWaitingListResponse(surrenderRequest.Id, waitlistPosition));
    }
}

using System.Security.Claims;
using Marten;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using K9Crush.Modules.ShelterAdoption.Domain;
using Wolverine.Http;

namespace K9Crush.Modules.ShelterAdoption.Api.Commands.PerformBehaviorTest;

/// <summary>
/// State-change slice: the SurrenderingYourDogFullIntake chapter's
/// "Perform Behavior Test" -> "Behavior Test Completed" - only valid once
/// the surrender request is Accepted. Writes to the existing
/// DogSurrenderRequest stream (surrenderRequestId), same as
/// Review/Accept/Decline - this chapter's other FullIntake steps extend
/// that same case-file entity rather than introducing a parallel one.
///
/// DogId is only used for a read-only ownership lookup - DogSurrenderRequest
/// itself has no ShelterAccountId FK (that's only known at Accept time,
/// caller-supplied to AcceptDogSurrenderHandler and never persisted onto
/// the surrender request), so this resolves ownership the same way
/// UpdateListingStatusHandler does: dogId -> DogListing.ShelterAccountId
/// -> ShelterAccount.RequestedByOwnerId.
/// </summary>
public static class PerformBehaviorTestHandler
{
    [WolverinePost("/api/v1/shelter-adoption/surrender-requests/{surrenderRequestId:guid}/behavior-test")]
    [Authorize(Policy = "Shelter")]
    public static async Task<Results<Ok<PerformBehaviorTestResponse>, NotFound, ForbidHttpResult, Conflict<string>>> Handle(
        Guid surrenderRequestId,
        PerformBehaviorTestRequest request,
        ClaimsPrincipal user,
        IDocumentSession session,
        CancellationToken cancellationToken)
    {
        var callerOwnerId = Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);

        var dogListing = await session.LoadAsync<DogListing>(request.DogId, cancellationToken);
        if (dogListing is null)
            return TypedResults.NotFound();

        var shelterAccount = await session.LoadAsync<ShelterAccount>(dogListing.ShelterAccountId, cancellationToken);
        if (shelterAccount is null || shelterAccount.RequestedByOwnerId != callerOwnerId)
            return TypedResults.Forbid();

        var stream = await session.Events.FetchForWriting<DogSurrenderRequest>(surrenderRequestId, cancellationToken);
        var surrenderRequest = stream.Aggregate;
        if (surrenderRequest is null)
            return TypedResults.NotFound();

        if (surrenderRequest.Status != SurrenderRequestStatus.Accepted)
            return TypedResults.Conflict($"Cannot perform a behavior test on a surrender request in status {surrenderRequest.Status}.");

        var @event = surrenderRequest.CompleteBehaviorTest(request.PerformedBy, request.SuitableForRehoming, request.BehaviorNotes);
        stream.AppendOne(@event);
        await session.SaveChangesAsync(cancellationToken);

        return TypedResults.Ok(new PerformBehaviorTestResponse(
            surrenderRequest.Id, surrenderRequest.BehaviorTestSuitableForRehoming!.Value, surrenderRequest.BehaviorTestNotes!));
    }
}

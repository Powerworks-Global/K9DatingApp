using System.Security.Claims;
using Marten;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using K9Crush.Modules.ShelterAdoption.Domain;
using Wolverine.Http;

namespace K9Crush.Modules.ShelterAdoption.Api.Commands.CompleteSurrenderPaperwork;

/// <summary>
/// State-change slice: the SurrenderingYourDogFullIntake chapter's
/// "Complete Surrender Paperwork" -> "Surrender Paperwork Completed". One
/// of the 9 Shelter Staff intake-pipeline steps that only apply to
/// FullIntake-mode shelters (Simple-mode shelters never reach this
/// endpoint's precondition - see ShelterAccount.SurrenderIntakeMode's doc
/// comment). Only valid from DogSurrenderRequest.Status == Accepted, same
/// as the other independent pipeline steps in this chapter (they don't
/// gate on each other, per the yaml's per-step tests).
///
/// Route/ownership-gate pattern matches UpdateListingStatusHandler - keyed
/// by surrenderRequestId alone, ownership resolved via the request's own
/// (now-persisted) ShelterAccountId, gated by the "Shelter" policy rather
/// than Admin, since this is the receiving shelter's own staff action.
/// </summary>
public static class CompleteSurrenderPaperworkHandler
{
    [WolverinePost("/api/v1/shelter-adoption/surrender-requests/{surrenderRequestId:guid}/paperwork")]
    [Authorize(Policy = "Shelter")]
    public static async Task<Results<Ok<CompleteSurrenderPaperworkResponse>, NotFound, ForbidHttpResult, Conflict<string>>> Handle(
        Guid surrenderRequestId,
        CompleteSurrenderPaperworkRequest request,
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
            return TypedResults.Conflict($"Cannot complete surrender paperwork for a request in status {surrenderRequest.Status}.");

        var shelterAccount = await session.LoadAsync<ShelterAccount>(surrenderRequest.ShelterAccountId, cancellationToken);
        if (shelterAccount is null || shelterAccount.RequestedByOwnerId != callerOwnerId)
            return TypedResults.Forbid();

        if (shelterAccount.SurrenderIntakeMode != SurrenderIntakeMode.FullIntake)
            return TypedResults.Conflict("Cannot complete surrender paperwork - this shelter is not configured for FullIntake surrender mode.");

        var @event = surrenderRequest.CompleteSurrenderPaperwork(request.LegalTransferSigned, request.OwnershipProofType);
        stream.AppendOne(@event);
        await session.SaveChangesAsync(cancellationToken);

        return TypedResults.Ok(new CompleteSurrenderPaperworkResponse(
            surrenderRequest.Id, surrenderRequest.LegalTransferSigned!.Value, surrenderRequest.OwnershipProofType!));
    }
}

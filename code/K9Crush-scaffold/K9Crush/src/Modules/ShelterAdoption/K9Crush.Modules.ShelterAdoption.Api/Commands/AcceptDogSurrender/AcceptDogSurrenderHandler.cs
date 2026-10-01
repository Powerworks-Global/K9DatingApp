using Marten;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using K9Crush.Modules.ShelterAdoption.Domain;
using Wolverine.Http;

namespace K9Crush.Modules.ShelterAdoption.Api.Commands.AcceptDogSurrender;

/// <summary>
/// State-change slice: Spec/K9CRUSH.emlang.v3.yaml's SurrenderingYourDog
/// chapter, "Accept Dog Surrender" -> "Dog Surrender Accepted" - only
/// valid from UnderReview. Admin policy, no ownership check needed - same
/// reasoning as ReviewSurrenderRequestHandler.
///
/// Cascades into ShelterManagingListings' "Add Dog Listing" (status
/// NotReadyYet) in the same session/SaveChangesAsync, same-module
/// state-change like ApproveApplicationHandler's DogListing.Status
/// cascade - not a cross-module integration event. The destination
/// ShelterAccount must already be Created/activated, same guard
/// AddDogListingHandler enforces. TemperamentNotes becomes the new
/// listing's Bio (same "Bio doubles as temperament" convention as
/// GetDogListingDetailsHandler) - HealthNotes stays on the
/// DogSurrenderRequest record only, not carried onto the public listing.
///
/// ShelterAccountId is now also persisted onto DogSurrenderRequest itself
/// (DogSurrenderAcceptedV1), not just used transiently to create the
/// DogListing - the SurrenderingYourDogFullIntake chapter's pipeline
/// commands need it to resolve ownership/FullIntake-mode for a given
/// surrender request.
/// </summary>
public static class AcceptDogSurrenderHandler
{
    [WolverinePost("/api/v1/shelter-adoption/surrender-requests/{surrenderRequestId:guid}/accept")]
    [Authorize(Policy = "Admin")]
    public static async Task<Results<Ok<AcceptDogSurrenderResponse>, NotFound, Conflict<string>>> Handle(
        Guid surrenderRequestId,
        AcceptDogSurrenderRequest request,
        IDocumentSession session,
        CancellationToken cancellationToken)
    {
        var surrenderStream = await session.Events.FetchForWriting<DogSurrenderRequest>(surrenderRequestId, cancellationToken);
        var surrenderRequest = surrenderStream.Aggregate;
        if (surrenderRequest is null)
            return TypedResults.NotFound();

        if (surrenderRequest.Status != SurrenderRequestStatus.UnderReview)
            return TypedResults.Conflict($"Cannot accept a surrender request in status {surrenderRequest.Status}.");

        var shelterAccount = await session.LoadAsync<ShelterAccount>(request.ShelterAccountId, cancellationToken);
        if (shelterAccount is null)
            return TypedResults.NotFound();

        if (shelterAccount.Status != ShelterAccountStatus.Created)
            return TypedResults.Conflict($"Cannot add a dog listing to a shelter account in status {shelterAccount.Status}.");

        var acceptedEvent = surrenderRequest.Accept(request.ShelterAccountId);
        surrenderStream.AppendOne(acceptedEvent);

        var (dogListing, dogListingAddedEvent) = DogListing.AddNew(
            request.ShelterAccountId, surrenderRequest.DogName, surrenderRequest.Breed,
            surrenderRequest.AgeInMonths, surrenderRequest.TemperamentNotes);
        session.Events.StartStream<DogListing>(dogListing.Id, dogListingAddedEvent);

        await session.SaveChangesAsync(cancellationToken);

        return TypedResults.Ok(new AcceptDogSurrenderResponse(surrenderRequest.Id, surrenderRequest.Status.ToString(), dogListing.Id));
    }
}

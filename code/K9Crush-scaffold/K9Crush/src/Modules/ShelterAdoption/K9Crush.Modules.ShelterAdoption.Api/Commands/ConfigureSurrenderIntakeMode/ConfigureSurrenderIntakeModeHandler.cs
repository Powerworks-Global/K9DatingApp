using System.Security.Claims;
using Marten;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using K9Crush.Modules.ShelterAdoption.Domain;
using Wolverine.Http;

namespace K9Crush.Modules.ShelterAdoption.Api.Commands.ConfigureSurrenderIntakeMode;

/// <summary>
/// State-change slice: the SurrenderingYourDogFullIntake chapter's
/// "Configure Surrender Intake Mode" -> "Surrender Intake Mode
/// Configured". A single per-shelter switch (Simple/FullIntake), not
/// per-step flags - see ShelterAccount.SurrenderIntakeMode's doc comment.
/// Simple-mode shelters get zero change to the existing
/// DogSurrenderRequest flow; FullIntake unlocks this chapter's other 10
/// shelter-staff commands (separate slices).
///
/// Self-load pattern like ApproveShelterAccountHandler (FetchForWriting
/// on ShelterAccount itself, no separate entity), but gated by ownership
/// rather than Admin - this is the shelter's own setting, same
/// ownership-check shape as UpdateListingStatusHandler.
/// </summary>
public static class ConfigureSurrenderIntakeModeHandler
{
    [WolverinePost("/api/v1/shelter-adoption/shelter-accounts/{shelterAccountId:guid}/surrender-intake-mode")]
    [Authorize(Policy = "Shelter")]
    public static async Task<Results<Ok<ConfigureSurrenderIntakeModeResponse>, NotFound, ForbidHttpResult>> Handle(
        Guid shelterAccountId,
        ConfigureSurrenderIntakeModeRequest request,
        ClaimsPrincipal user,
        IDocumentSession session,
        CancellationToken cancellationToken)
    {
        var callerOwnerId = Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);

        var stream = await session.Events.FetchForWriting<ShelterAccount>(shelterAccountId, cancellationToken);
        var shelterAccount = stream.Aggregate;
        if (shelterAccount is null)
            return TypedResults.NotFound();

        if (shelterAccount.RequestedByOwnerId != callerOwnerId)
            return TypedResults.Forbid();

        var @event = shelterAccount.ConfigureSurrenderIntakeMode(request.Mode);
        stream.AppendOne(@event);
        await session.SaveChangesAsync(cancellationToken);

        return TypedResults.Ok(new ConfigureSurrenderIntakeModeResponse(shelterAccount.Id, shelterAccount.SurrenderIntakeMode));
    }
}

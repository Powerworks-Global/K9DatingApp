using Marten;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using K9Crush.Modules.Admin.Domain;
using Wolverine.Http;

namespace K9Crush.Modules.Admin.Api.Commands.CreateTip;

/// <summary>
/// State-change slice: the emlang yaml's ManagingTipsHealthContent
/// chapter's "Create Tip" -> "Tip Drafted" (its own StaffDraftsANewTip
/// test has no `given`, so there is no prior state to guard against and no
/// NotFound/Conflict branch here - unlike this module's
/// ResolveFeedbackHandler, whose yaml test does have one).
///
/// Scope: this pair only. The chapter's Preview/Publish/Edit/Unpublish Tip
/// steps are separate slices, not yet built.
///
/// ADR-031: starts a brand-new event stream (session.Events.StartStream)
/// rather than session.Store - the same shape as Media's
/// UploadMediaHandler, the codebase's other "this command creates the
/// aggregate" case.
/// </summary>
public static class CreateTipHandler
{
    [WolverinePost("/api/v1/admin/tips")]
    [Authorize(Policy = "Admin")]
    public static async Task<Ok<CreateTipResponse>> Handle(
        IDocumentSession session, CancellationToken cancellationToken)
    {
        var (tip, @event) = Tip.Draft();
        session.Events.StartStream<Tip>(tip.Id, @event);
        await session.SaveChangesAsync(cancellationToken);

        return TypedResults.Ok(new CreateTipResponse(tip.Id, tip.DraftedAt));
    }
}

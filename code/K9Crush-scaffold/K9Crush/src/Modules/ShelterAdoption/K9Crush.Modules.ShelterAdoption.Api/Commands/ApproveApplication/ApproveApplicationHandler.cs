using System.Security.Claims;
using Marten;
using JasperFx.Events;
using JasperFx.Events.Tags;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using K9Crush.Modules.ShelterAdoption.Contracts;
using K9Crush.Modules.ShelterAdoption.Domain;
using Wolverine.Http;

namespace K9Crush.Modules.ShelterAdoption.Api.Commands.ApproveApplication;

/// <summary>What this slice hands back to the caller.</summary>
public sealed record ApproveApplicationResponse(Guid ApplicationId, string Status);

/// <summary>
/// DCB tag identifying the Application side of this handler's consistency
/// boundary. A plain wrapper around the Application's own stream id -
/// per martendb.io/events/dcb.html, "each tag value becomes the stream
/// identity" for events appended carrying it, so this Guid must be the
/// SAME value as the Application's existing single-stream id for its
/// Inline snapshot (Application, in ShelterAdoptionModule.cs) to keep
/// seeing DCB-appended events as part of its own stream.
/// </summary>
public readonly record struct ApplicationTag(Guid Value);

/// <summary>Same role as <see cref="ApplicationTag"/>, for DogListing's stream id.</summary>
public readonly record struct DogListingTag(Guid Value);

/// <summary>
/// DCB boundary aggregate type. Real, empirically-confirmed finding
/// (2026-09-17, against a live Postgres instance): Marten's
/// FetchForWritingByTags&lt;T&gt; throws InvalidProjectionException at
/// runtime if T has no conventional Apply/Create methods - an empty
/// marker type (this class's original, untested shape) is REJECTED, not
/// silently accepted. So this type must be a real, if minimal,
/// self-aggregating projection - even though its resulting Aggregate
/// value is still never read for business decisions here (those come
/// from LoadAsync&lt;Application&gt;/LoadAsync&lt;DogListing&gt; below,
/// both already-registered Inline snapshots, unaffected by whether their
/// historical events carry these new tags). Both ApplicationTag and
/// DogListingTag register against this ONE shared type, per
/// martendb.io/events/dcb.html's
/// RegisterTagType&lt;T&gt;(...).ForAggregate&lt;TState&gt;() pattern
/// (see ShelterAdoptionModule.cs's Marten configuration).
/// </summary>
public sealed class ApproveApplicationDcbBoundary
{
    public void Apply(Domain.Events.ApplicationApprovalV1 _) { }
    public void Apply(Domain.Events.DogListingStatusUpdatedV1 _) { }
}

/// <summary>
/// PHASE 0 POC — DCB (Dynamic Consistency Boundary) modernization of this
/// slice. See poc/DcbRewrite/NOTES.md (internal-modernization-accelerator
/// repo) for the full before/after story. Short version: the original
/// handler did two INDEPENDENT FetchForWriting calls (Application, then
/// DogListing), each with its own optimistic-concurrency check - nothing
/// modeled "these two streams must be considered together" as a single,
/// explicit consistency boundary. This version fetches ONE combined DCB
/// boundary tagged by both streams' ids, so a conflicting change to
/// EITHER stream is caught by ONE combined check on SaveChangesAsync.
///
/// Business-decision reads (application.Status, ownership, dog name) are
/// UNCHANGED in spirit from the original — both Application and DogListing
/// are already Inline snapshots, so LoadAsync gives the real, current
/// projected state regardless of DCB tags. DCB only changes the
/// WRITE/consistency-boundary shape, not how state is read for decisions.
///
/// State-guard (only valid from UnderReview) lives here, same as before.
/// Cascades ApplicationApprovedV1 (ADR-027) and the DogListing's Status to
/// Adopted (v3 ENRICHMENT), same as the original.
/// </summary>
public static class ApproveApplicationHandler
{
    [WolverinePost("/api/v1/shelter-adoption/applications/{applicationId:guid}/approve")]
    [Authorize(Policy = "Shelter")]
    public static async Task<(Results<Ok<ApproveApplicationResponse>, NotFound, ForbidHttpResult, Conflict<string>>, ApplicationApprovedV1?)> Handle(
        Guid applicationId,
        ClaimsPrincipal user,
        IDocumentSession session,
        CancellationToken cancellationToken)
    {
        var callerOwnerId = Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);

        var application = await session.LoadAsync<Application>(applicationId, cancellationToken);
        if (application is null)
            return (TypedResults.NotFound(), null);

        var shelterAccount = await session.LoadAsync<ShelterAccount>(application.ShelterAccountId, cancellationToken);
        if (shelterAccount is null || shelterAccount.RequestedByOwnerId != callerOwnerId)
            return (TypedResults.Forbid(), null);

        if (application.Status != ApplicationStatus.UnderReview)
            return (TypedResults.Conflict($"Cannot approve an application in status {application.Status}."), null);

        var dogListing = await session.LoadAsync<DogListing>(application.DogListingId, cancellationToken);

        // ONE combined consistency boundary across both the Application and
        // DogListing streams, instead of two independent FetchForWriting
        // calls. Real API per martendb.io/events/dcb.html (fetched live
        // this session, DCB shipped in Marten 9.0 / May 2026).
        var query = new EventTagQuery()
            .Or<ApplicationTag>(new ApplicationTag(applicationId))
            .Or<DogListingTag>(new DogListingTag(application.DogListingId));

        var boundary = await session.Events.FetchForWritingByTags<ApproveApplicationDcbBoundary>(query, cancellationToken);

        // Real domain methods, same as the original handler - Approve()/
        // UpdateStatus() both mutate the LOADED aggregate's in-memory state
        // (so callers reading `application`/`dogListing` after this point
        // see the post-approval state, same contract as before) AND return
        // the event to append. Only the FETCH/APPEND boundary changed to
        // DCB - the domain logic itself didn't move.
        var approvedEvent = application.Approve();
        var approvalEvent = session.Events.BuildEvent(approvedEvent);
        approvalEvent.WithTag(new ApplicationTag(applicationId));
        boundary.AppendOne(approvalEvent);

        if (dogListing is not null)
        {
            var dogStatusEvent = dogListing.UpdateStatus(DogListingStatus.Adopted);
            var statusEvent = session.Events.BuildEvent(dogStatusEvent);
            statusEvent.WithTag(new DogListingTag(application.DogListingId));
            boundary.AppendOne(statusEvent);
        }

        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (DcbConcurrencyException)
        {
            // Real, meaningfully different failure mode from the original:
            // this fires on a conflicting change to EITHER stream, where the
            // old two-independent-FetchForWriting version could only ever
            // surface a conflict on the Application stream's own check.
            return (TypedResults.Conflict("A conflicting change to this application or dog listing was detected. Please retry."), null);
        }

        var integrationEvent = new ApplicationApprovedV1(
            EventId: Guid.NewGuid(),
            OccurredAt: DateTimeOffset.UtcNow,
            ApplicationId: application.Id,
            ApplicantOwnerId: application.ApplicantOwnerId,
            DogListingId: application.DogListingId,
            DogName: dogListing?.Name ?? string.Empty);

        return (TypedResults.Ok(new ApproveApplicationResponse(application.Id, ApplicationStatus.Approved.ToString())), integrationEvent);
    }
}

namespace K9Crush.Modules.ShelterAdoption.Domain.Events;

/// <summary>
/// ADR-031 event-sourcing retrofit, Phase 5/5 - one record per Application
/// transition. Two creation events (Submitted/DraftStarted) since
/// Application.Submit and Application.StartDraft are two distinct entry
/// points into the stream, per Application.StartDraft's own comment.
///
/// ApplicationRejectionV1/ApplicationApprovalV1 are deliberately NOT named
/// ApplicationRejectedV1/ApplicationApprovedV1 - those names are already
/// taken by K9Crush.Modules.ShelterAdoption.Contracts' integration events,
/// and RejectApplicationHandler/ApproveApplicationHandler need both
/// namespaces in scope at once (same collision class as every other
/// retrofitted entity this phase).
///
/// ApplicationClosedDogNoLongerAvailableV1 is shared by
/// Application.CloseDraftDogNoLongerAvailable and
/// Application.CancelDogNoLongerAvailable - same technical transition and
/// resulting status regardless of origin state (Draft vs. an open
/// application), same convergence call as ShelterAccount.Activate().
/// </summary>
public sealed record ApplicationSubmittedV1(
    Guid ApplicantOwnerId, Guid DogListingId, Guid ShelterAccountId, ApplicationIntake Intake, DateTimeOffset SubmittedAt);

public sealed record ApplicationDraftStartedV1(
    Guid ApplicantOwnerId, Guid DogListingId, Guid ShelterAccountId, DateTimeOffset StartedAt);

public sealed record ApplicationWithdrawnV1;

public sealed record ApplicationReviewedV1;

public sealed record ApplicationAdditionalDetailsRequestedV1(string Reason);

public sealed record ApplicationAdditionalDetailsSubmittedV1;

public sealed record ApplicationRejectionV1(string Reason);

public sealed record ApplicationApprovalV1;

public sealed record ApplicationDetailsEditedV1(string Details, DateTimeOffset EditedAt);

public sealed record ApplicationDraftSubmittedV1(ApplicationIntake Intake, DateTimeOffset SubmittedAt);

public sealed record ApplicationClosedDogNoLongerAvailableV1;

public sealed record ApplicationMarkedStaleV1;

public sealed record ApplicationClosedV1;

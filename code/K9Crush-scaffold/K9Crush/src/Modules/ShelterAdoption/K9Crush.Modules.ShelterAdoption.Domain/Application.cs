using System.Text.Json.Serialization;
using K9Crush.BuildingBlocks.Domain;
using K9Crush.Modules.ShelterAdoption.Domain.Events;

namespace K9Crush.Modules.ShelterAdoption.Domain;

/// <summary>
/// A member's application to adopt a specific DogListing - from
/// Spec/K9CRUSH.emlang.yaml's TheWouldBeAdopter/CheckingApplicationStatus/
/// ShelterReviewsApplication chapters.
///
/// Self-aggregating event-sourced entity (ADR-031, Phase 5/5). Registered
/// as its own Inline snapshot - GetApplicationStatus/GetDraftApplications/
/// GetPendingApplicationsQueue genuinely query it.
///
/// Stale/Closed (ShelterReviewsApplication's time-based pair) are modeled
/// via ADR-026 Wolverine scheduled messages - see
/// Automations/MarkApplicationStale and Automations/CloseStaleApplication.
///
/// ShelterAccountId is denormalized from DogListing.ShelterAccountId at
/// submission time - a listing can't change which shelter owns it after
/// the fact, so this is safe and saves every shelter-side query
/// (GetPendingApplicationsQueue) a join through DogListing.
/// </summary>
public enum ApplicationStatus
{
    Pending,
    UnderReview,
    ReturnedForAlteration,
    Approved,
    Rejected,
    Withdrawn,

    // Appended, not inserted above - Marten/System.Text.Json serializes
    // this enum as its integer ordinal (confirmed via OwnerRole in the
    // Identity module), so reordering existing members would silently
    // change what any already-persisted Application document's Status
    // means. Add new members here, always at the end.
    Draft,
    ClosedDogNoLongerAvailable,

    // ShelterReviewsApplication's time-based pair - see ADR-026. Stale is
    // reached from ReturnedForAlteration after staleAfterDays (15) of no
    // response; Closed is reached from Stale after a further
    // closesAfterDays (30). Distinct from ClosedDogNoLongerAvailable,
    // which is ResumingADraftApplication's unrelated "the dog is gone"
    // closure and only ever reached from Draft.
    Stale,
    Closed
}

public enum HomeOwnership { Own, Rent }
public enum HomeType { House, Apartment, Other }
public enum GardenSize { Small, Medium, Large }
public enum EnergyLevelPreference { Low, Medium, High, NoPreference }

/// <summary>
/// v3 ENRICHMENT (Spec/K9CRUSH.emlang.v3.yaml's TheWouldBeAdopter chapter) -
/// the household/lifestyle questionnaire answered once, at the point of
/// submission (not the Draft precursor - see Application.Intake's own
/// comment). Plain record value object. GardenSize/GardenEnclosed/
/// ChildrenAgeRange/OtherPetsDetails are only meaningful (and required by
/// SubmitApplicationRequest's validation) when HasGarden/HasChildren/
/// HasOtherPets is true respectively.
/// </summary>
public sealed record ApplicationIntake(
    int HouseholdSize,
    HomeOwnership HomeOwnership,
    HomeType HomeType,
    bool HasGarden,
    GardenSize? GardenSize,
    bool? GardenEnclosed,
    bool HasChildren,
    string? ChildrenAgeRange,
    bool HasOtherPets,
    string? OtherPetsDetails,
    int DailyAloneHours,
    bool HasUpcomingExtendedAbsence,
    EnergyLevelPreference PreferredEnergyLevel,
    string DailyExerciseCommitment,
    bool PastDogOwnershipExperience,
    bool WillingToCareForMedicalNeedsDog,
    bool WillingToCareForNervousDog,
    bool DataProcessingConsent);

public class Application : Entity
{
    [JsonInclude] public Guid ApplicantOwnerId { get; private set; }
    [JsonInclude] public Guid DogListingId { get; private set; }
    [JsonInclude] public Guid ShelterAccountId { get; private set; }
    [JsonInclude] public ApplicationStatus Status { get; private set; }
    [JsonInclude] public string Details { get; private set; } = string.Empty;
    [JsonInclude] public string? AdditionalDetailsRequestReason { get; private set; }
    [JsonInclude] public string? RejectionReason { get; private set; }
    [JsonInclude] public DateTimeOffset StartedAt { get; private set; }
    [JsonInclude] public DateTimeOffset? SubmittedAt { get; private set; }
    [JsonInclude] public DateTimeOffset? LastEditedAt { get; private set; }

    /// <summary>
    /// Null until the application is actually submitted (Submit()/
    /// SubmitDraft()) - a Draft in progress hasn't answered the
    /// questionnaire yet, only the free-text Details field (EditDetails()).
    /// </summary>
    [JsonInclude] public ApplicationIntake? Intake { get; private set; }

    [JsonConstructor]
    private Application() { }

    public static Application Create(ApplicationSubmittedV1 e) => new()
    {
        ApplicantOwnerId = e.ApplicantOwnerId,
        DogListingId = e.DogListingId,
        ShelterAccountId = e.ShelterAccountId,
        Status = ApplicationStatus.Pending,
        StartedAt = e.SubmittedAt,
        SubmittedAt = e.SubmittedAt,
        Intake = e.Intake
    };

    /// <summary>
    /// The emlang yaml's ResumingADraftApplication chapter starting point
    /// (and TheWouldBeAdopter's "Gate Application Start", which carries a
    /// draftApplicationId prop) - every application can start life as a
    /// Draft, editable and resumable, before being submitted. Create(
    /// ApplicationSubmittedV1) above remains the express "submit right
    /// now, skip the draft stage" path - both are legitimate, coexisting
    /// entry points, not a replacement of one by the other.
    /// </summary>
    public static Application Create(ApplicationDraftStartedV1 e) => new()
    {
        ApplicantOwnerId = e.ApplicantOwnerId,
        DogListingId = e.DogListingId,
        ShelterAccountId = e.ShelterAccountId,
        Status = ApplicationStatus.Draft,
        StartedAt = e.StartedAt
    };

    public void Apply(ApplicationWithdrawnV1 e) => Status = ApplicationStatus.Withdrawn;

    public void Apply(ApplicationReviewedV1 e) => Status = ApplicationStatus.UnderReview;

    public void Apply(ApplicationAdditionalDetailsRequestedV1 e)
    {
        AdditionalDetailsRequestReason = e.Reason;
        Status = ApplicationStatus.ReturnedForAlteration;
    }

    public void Apply(ApplicationAdditionalDetailsSubmittedV1 e) => Status = ApplicationStatus.UnderReview;

    public void Apply(ApplicationRejectionV1 e)
    {
        RejectionReason = e.Reason;
        Status = ApplicationStatus.Rejected;
    }

    public void Apply(ApplicationApprovalV1 e) => Status = ApplicationStatus.Approved;

    public void Apply(ApplicationDetailsEditedV1 e)
    {
        Details = e.Details;
        LastEditedAt = e.EditedAt;
    }

    public void Apply(ApplicationDraftSubmittedV1 e)
    {
        Status = ApplicationStatus.Pending;
        SubmittedAt = e.SubmittedAt;
        Intake = e.Intake;
    }

    public void Apply(ApplicationClosedDogNoLongerAvailableV1 e) => Status = ApplicationStatus.ClosedDogNoLongerAvailable;

    public void Apply(ApplicationMarkedStaleV1 e) => Status = ApplicationStatus.Stale;

    public void Apply(ApplicationClosedV1 e) => Status = ApplicationStatus.Closed;

    public static (Application Application, ApplicationSubmittedV1 Event) SubmitNew(
        Guid applicantOwnerId, Guid dogListingId, Guid shelterAccountId, ApplicationIntake intake)
    {
        var @event = new ApplicationSubmittedV1(applicantOwnerId, dogListingId, shelterAccountId, intake, DateTimeOffset.UtcNow);
        return (Create(@event), @event);
    }

    public static (Application Application, ApplicationDraftStartedV1 Event) StartDraftNew(
        Guid applicantOwnerId, Guid dogListingId, Guid shelterAccountId)
    {
        var @event = new ApplicationDraftStartedV1(applicantOwnerId, dogListingId, shelterAccountId, DateTimeOffset.UtcNow);
        return (Create(@event), @event);
    }

    /// <summary>"open" = still occupying one of the applicant's
    /// maxOpenApplications slots and still eligible for shelter review -
    /// used by SubmitApplicationHandler's limit check and the pending
    /// queue's filter. Deliberately excludes Draft - the yaml gives
    /// drafts their own separate maxDraftApplications limit, drafts don't
    /// occupy a real application slot with the shelter yet.</summary>
    public bool IsOpen => Status is ApplicationStatus.Pending or ApplicationStatus.UnderReview or ApplicationStatus.ReturnedForAlteration;

    public ApplicationWithdrawnV1 Withdraw()
    {
        var @event = new ApplicationWithdrawnV1();
        Apply(@event);
        return @event;
    }

    /// <summary>The emlang yaml's "Review Application" -> "Application
    /// Reviewed". State-guard (only valid from Pending) lives in the
    /// handler.</summary>
    public ApplicationReviewedV1 Review()
    {
        var @event = new ApplicationReviewedV1();
        Apply(@event);
        return @event;
    }

    /// <summary>The emlang yaml's "Request Additional Details" ->
    /// "Additional Details Requested". State-guard (only valid from
    /// UnderReview) lives in the handler.</summary>
    public ApplicationAdditionalDetailsRequestedV1 RequestAdditionalDetails(string reason)
    {
        var @event = new ApplicationAdditionalDetailsRequestedV1(reason.Trim());
        Apply(@event);
        return @event;
    }

    /// <summary>The emlang yaml's "Submit Additional Details" ->
    /// "Additional Details Submitted" - the applicant's response to
    /// RequestAdditionalDetails, returning the application to review.
    /// State-guard (only valid from ReturnedForAlteration) lives in the
    /// handler.</summary>
    public ApplicationAdditionalDetailsSubmittedV1 SubmitAdditionalDetails()
    {
        var @event = new ApplicationAdditionalDetailsSubmittedV1();
        Apply(@event);
        return @event;
    }

    /// <summary>The emlang yaml's "Reject Application" -> "Application
    /// Rejected". State-guard (only valid from UnderReview) lives in the
    /// handler.</summary>
    public ApplicationRejectionV1 Reject(string reason)
    {
        var @event = new ApplicationRejectionV1(reason.Trim());
        Apply(@event);
        return @event;
    }

    /// <summary>The emlang yaml's "Approve Application" -> "Application
    /// Approved". State-guard (only valid from UnderReview) lives in the
    /// handler.</summary>
    public ApplicationApprovalV1 Approve()
    {
        var @event = new ApplicationApprovalV1();
        Apply(@event);
        return @event;
    }

    /// <summary>The emlang yaml's "Edit Application Details" ->
    /// "Application Details Edited". State-guard (only valid from Draft)
    /// lives in the handler.</summary>
    public ApplicationDetailsEditedV1 EditDetails(string details)
    {
        var @event = new ApplicationDetailsEditedV1(details.Trim(), DateTimeOffset.UtcNow);
        Apply(@event);
        return @event;
    }

    /// <summary>
    /// Graduates a Draft into a real submitted application - the emlang
    /// yaml's "Submit Application" event, this time carrying a
    /// draftApplicationId prop rather than creating fresh. Shared by
    /// ResumeDraftApplication's eventual submit step and
    /// SubmitApplicationHandler's "I already had a draft going for this
    /// dog" branch - see that handler's comment. State-guard (only valid
    /// from Draft) lives in the handler.
    /// </summary>
    public ApplicationDraftSubmittedV1 SubmitDraft(ApplicationIntake intake)
    {
        var @event = new ApplicationDraftSubmittedV1(intake, DateTimeOffset.UtcNow);
        Apply(@event);
        return @event;
    }

    /// <summary>
    /// The emlang yaml's "Check Dog Availability On Resume" ->
    /// "Draft Application Closed: Dog No Longer Available". "No longer
    /// available" is determined by the handler (the DogListing no longer
    /// existing / IsRemoved under ADR-031), not tracked as a field here.
    /// State-guard (only valid from Draft) lives in the handler.
    /// </summary>
    public ApplicationClosedDogNoLongerAvailableV1 CloseDraftDogNoLongerAvailable()
    {
        var @event = new ApplicationClosedDogNoLongerAvailableV1();
        Apply(@event);
        return @event;
    }

    /// <summary>
    /// The emlang yaml's ShelterManagingListings chapter's "Cancel
    /// Applications For Removed Listing" -> "Applications Cancelled For
    /// Removed Listing" - the open-application counterpart to
    /// CloseDraftDogNoLongerAvailable() above. Reuses the same
    /// ApplicationClosedDogNoLongerAvailableV1 event (identical real-world
    /// meaning: the dog listing is gone), just reached from an open
    /// application (Pending/UnderReview/ReturnedForAlteration) instead of
    /// a Draft. State-guard (only valid while IsOpen) lives in the
    /// handler.
    /// </summary>
    public ApplicationClosedDogNoLongerAvailableV1 CancelDogNoLongerAvailable()
    {
        var @event = new ApplicationClosedDogNoLongerAvailableV1();
        Apply(@event);
        return @event;
    }

    /// <summary>
    /// The emlang yaml's "Mark Application Stale" -> "Application Marked
    /// Stale" (ADR-026). State-guard (only valid from
    /// ReturnedForAlteration - i.e. the applicant never responded to
    /// RequestAdditionalDetails) lives in MarkApplicationStaleHandler,
    /// which re-checks this on every scheduled-message delivery so a
    /// meanwhile-submitted response is never overwritten.
    /// </summary>
    public ApplicationMarkedStaleV1 MarkStale()
    {
        var @event = new ApplicationMarkedStaleV1();
        Apply(@event);
        return @event;
    }

    /// <summary>
    /// The emlang yaml's "Close Stale Application" -> "Application
    /// Closed" (ADR-026). State-guard (only valid from Stale) lives in
    /// CloseStaleApplicationHandler.
    /// </summary>
    public ApplicationClosedV1 Close()
    {
        var @event = new ApplicationClosedV1();
        Apply(@event);
        return @event;
    }
}

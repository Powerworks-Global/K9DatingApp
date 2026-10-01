using System.Text.Json.Serialization;
using K9Crush.BuildingBlocks.Domain;
using K9Crush.Modules.ShelterAdoption.Domain.Events;

namespace K9Crush.Modules.ShelterAdoption.Domain;

/// <summary>
/// Spec/K9CRUSH.emlang.v3.yaml's SurrenderingYourDog chapter - a member
/// surrendering their OWN dog into a shelter's care, distinct from
/// Application (an applicant applying to ADOPT a shelter's existing
/// listing).
///
/// Self-aggregating event-sourced entity (ADR-031, Phase 5/5). Registered
/// as its own Inline snapshot - GetSurrenderReviewQueueHandler genuinely
/// queries it.
/// </summary>
public enum SurrenderRequestStatus
{
    Requested,
    UnderReview,
    AdditionalDetailsRequested,
    Accepted,
    Declined
}

public class DogSurrenderRequest : Entity
{
    [JsonInclude] public Guid RequestedByOwnerId { get; private set; }
    [JsonInclude] public string DogName { get; private set; } = default!;
    [JsonInclude] public string Breed { get; private set; } = default!;
    [JsonInclude] public int AgeInMonths { get; private set; }
    [JsonInclude] public string ReasonForSurrender { get; private set; } = default!;
    [JsonInclude] public string TemperamentNotes { get; private set; } = default!;
    [JsonInclude] public string HealthNotes { get; private set; } = default!;
    [JsonInclude] public SurrenderRequestStatus Status { get; private set; }
    [JsonInclude] public string? AdditionalDetailsRequestReason { get; private set; }
    [JsonInclude] public string? DeclineReason { get; private set; }
    [JsonInclude] public DateTimeOffset RequestedAt { get; private set; }
    [JsonInclude] public Guid? BehaviorTestPerformedBy { get; private set; }
    [JsonInclude] public bool? BehaviorTestSuitableForRehoming { get; private set; }
    [JsonInclude] public string? BehaviorTestNotes { get; private set; }
    [JsonInclude] public Guid ShelterAccountId { get; private set; }
    [JsonInclude] public DateOnly? IntakeAppointmentDate { get; private set; }
    [JsonInclude] public int? WaitlistPosition { get; private set; }
    [JsonInclude] public bool? LegalTransferSigned { get; private set; }
    [JsonInclude] public string? OwnershipProofType { get; private set; }

    [JsonConstructor]
    private DogSurrenderRequest() { }

    public static DogSurrenderRequest Create(DogSurrenderRequestedV1 e) => new()
    {
        RequestedByOwnerId = e.RequestedByOwnerId,
        DogName = e.DogName,
        Breed = e.Breed,
        AgeInMonths = e.AgeInMonths,
        ReasonForSurrender = e.ReasonForSurrender,
        TemperamentNotes = e.TemperamentNotes,
        HealthNotes = e.HealthNotes,
        Status = SurrenderRequestStatus.Requested,
        RequestedAt = e.RequestedAt
    };

    public void Apply(SurrenderRequestReviewedV1 e) => Status = SurrenderRequestStatus.UnderReview;

    public void Apply(AdditionalSurrenderDetailsRequestedV1 e)
    {
        AdditionalDetailsRequestReason = e.Reason;
        Status = SurrenderRequestStatus.AdditionalDetailsRequested;
    }

    public void Apply(AdditionalSurrenderDetailsSubmittedV1 e) => Status = SurrenderRequestStatus.UnderReview;

    public void Apply(DogSurrenderAcceptedV1 e)
    {
        ShelterAccountId = e.ShelterAccountId;
        Status = SurrenderRequestStatus.Accepted;
    }

    public void Apply(IntakeAppointmentScheduledV1 e) => IntakeAppointmentDate = e.AppointmentDate;

    public void Apply(AddedToWaitingListV1 e) => WaitlistPosition = e.WaitlistPosition;

    public void Apply(SurrenderPaperworkCompletedV1 e)
    {
        LegalTransferSigned = e.LegalTransferSigned;
        OwnershipProofType = e.OwnershipProofType;
    }

    public void Apply(DogSurrenderDeclinedV1 e)
    {
        DeclineReason = e.Reason;
        Status = SurrenderRequestStatus.Declined;
    }

    public void Apply(BehaviorTestCompletedV1 e)
    {
        BehaviorTestPerformedBy = e.PerformedBy;
        BehaviorTestSuitableForRehoming = e.SuitableForRehoming;
        BehaviorTestNotes = e.BehaviorNotes;
    }

    /// <summary>The emlang yaml's "Request Dog Surrender" -> "Dog
    /// Surrender Requested".</summary>
    public static (DogSurrenderRequest DogSurrenderRequest, DogSurrenderRequestedV1 Event) RequestNew(
        Guid requestedByOwnerId, string dogName, string breed, int ageInMonths,
        string reasonForSurrender, string temperamentNotes, string healthNotes)
    {
        var @event = new DogSurrenderRequestedV1(
            requestedByOwnerId, dogName.Trim(), breed.Trim(), ageInMonths,
            reasonForSurrender.Trim(), temperamentNotes.Trim(), healthNotes.Trim(), DateTimeOffset.UtcNow);
        return (Create(@event), @event);
    }

    /// <summary>The emlang yaml's "Review Surrender Request" -> "Surrender
    /// Request Reviewed". State-guard (only valid from Requested) lives
    /// in the handler.</summary>
    public SurrenderRequestReviewedV1 Review()
    {
        var @event = new SurrenderRequestReviewedV1();
        Apply(@event);
        return @event;
    }

    /// <summary>The emlang yaml's "Request Additional Surrender Details"
    /// -> "Additional Surrender Details Requested". State-guard (only
    /// valid from UnderReview) lives in the handler.</summary>
    public AdditionalSurrenderDetailsRequestedV1 RequestAdditionalDetails(string reason)
    {
        var @event = new AdditionalSurrenderDetailsRequestedV1(reason.Trim());
        Apply(@event);
        return @event;
    }

    /// <summary>The emlang yaml's "Submit Additional Surrender Details" ->
    /// "Additional Surrender Details Submitted" - the surrendering
    /// member's response, returning the request to review. No captured
    /// content, same as Application.SubmitAdditionalDetails - the yaml
    /// doesn't specify a form field beyond the reason text already
    /// captured on the request side. State-guard (only valid from
    /// AdditionalDetailsRequested) lives in the handler.</summary>
    public AdditionalSurrenderDetailsSubmittedV1 SubmitAdditionalDetails()
    {
        var @event = new AdditionalSurrenderDetailsSubmittedV1();
        Apply(@event);
        return @event;
    }

    /// <summary>The emlang yaml's "Accept Dog Surrender" -> "Dog Surrender
    /// Accepted". State-guard (only valid from UnderReview) lives in the
    /// handler. <paramref name="shelterAccountId"/> defaults to
    /// <see cref="Guid.Empty"/> so existing domain-only tests that predate
    /// the SurrenderingYourDogFullIntake pipeline (which needs this field
    /// for ownership checks) don't have to change - production callers
    /// (AcceptDogSurrenderHandler) always pass a real value.</summary>
    public DogSurrenderAcceptedV1 Accept(Guid shelterAccountId = default)
    {
        var @event = new DogSurrenderAcceptedV1(shelterAccountId);
        Apply(@event);
        return @event;
    }

    /// <summary>The emlang yaml's "Decline Dog Surrender" -> "Dog
    /// Surrender Declined". State-guard (only valid from UnderReview)
    /// lives in the handler.</summary>
    public DogSurrenderDeclinedV1 Decline(string reason)
    {
        var @event = new DogSurrenderDeclinedV1(reason.Trim());
        Apply(@event);
        return @event;
    }

    /// <summary>
    /// The SurrenderingYourDogFullIntake chapter's "Perform Behavior Test"
    /// -> "Behavior Test Completed". State-guard (only valid from
    /// Accepted) lives in the handler.
    /// </summary>
    public BehaviorTestCompletedV1 CompleteBehaviorTest(Guid performedBy, bool suitableForRehoming, string behaviorNotes)
    {
        var @event = new BehaviorTestCompletedV1(performedBy, suitableForRehoming, behaviorNotes.Trim());
        Apply(@event);
        return @event;
    }

    /// <summary>The SurrenderingYourDogFullIntake chapter's "Schedule
    /// Intake Appointment" -> "Intake Appointment Scheduled". State-guard
    /// (only valid from Accepted, on a FullIntake-mode shelter) lives in
    /// the handler.</summary>
    public IntakeAppointmentScheduledV1 ScheduleIntakeAppointment(DateOnly appointmentDate)
    {
        var @event = new IntakeAppointmentScheduledV1(appointmentDate);
        Apply(@event);
        return @event;
    }

    /// <summary>SurrenderingYourDogFullIntake chapter's "Add To Waiting
    /// List" -> "Added To Waiting List". waitlistPosition is computed by
    /// the handler (a count over the owning shelter's other waitlisted
    /// requests), not supplied by the caller. State-guard (only valid from
    /// Accepted, FullIntake-mode shelter) lives in the handler.</summary>
    public AddedToWaitingListV1 AddToWaitingList(int waitlistPosition)
    {
        var @event = new AddedToWaitingListV1(waitlistPosition);
        Apply(@event);
        return @event;
    }

    /// <summary>The SurrenderingYourDogFullIntake chapter's "Complete
    /// Surrender Paperwork" -> "Surrender Paperwork Completed". State-guard
    /// (only valid from Accepted, and only for FullIntake-mode shelters)
    /// lives in the handler, same as the other independent intake-pipeline
    /// steps in this chapter.</summary>
    public SurrenderPaperworkCompletedV1 CompleteSurrenderPaperwork(bool legalTransferSigned, string ownershipProofType)
    {
        var @event = new SurrenderPaperworkCompletedV1(legalTransferSigned, ownershipProofType.Trim());
        Apply(@event);
        return @event;
    }
}

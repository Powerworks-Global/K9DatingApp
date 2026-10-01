namespace K9Crush.Modules.ShelterAdoption.Domain.Events;

/// <summary>ADR-031 event-sourcing retrofit, Phase 5/5 - one record per DogSurrenderRequest transition.</summary>
public sealed record DogSurrenderRequestedV1(
    Guid RequestedByOwnerId, string DogName, string Breed, int AgeInMonths,
    string ReasonForSurrender, string TemperamentNotes, string HealthNotes, DateTimeOffset RequestedAt);

public sealed record SurrenderRequestReviewedV1;

public sealed record AdditionalSurrenderDetailsRequestedV1(string Reason);

public sealed record AdditionalSurrenderDetailsSubmittedV1;

/// <summary>
/// ShelterAccountId is a disclosed gap-fill (SurrenderingYourDogFullIntake
/// chapter): the yaml's "Dog Surrender Accepted" event never re-lists it as
/// a prop, but the FullIntake pipeline's later steps (Schedule Intake
/// Appointment, Complete Surrender Paperwork, Add To Waiting List, etc.)
/// need to know which shelter owns the request for their Shelter-policy
/// ownership check, and AcceptDogSurrenderHandler already has it (as
/// AcceptDogSurrenderRequest.ShelterAccountId) - just wasn't being
/// persisted onto the aggregate before. Defaults to keep
/// DogSurrenderRequestTests.cs's existing `request.Accept();` call
/// compiling (CLAUDE.md: don't change existing test files).
/// </summary>
public sealed record DogSurrenderAcceptedV1(Guid ShelterAccountId = default);

public sealed record DogSurrenderDeclinedV1(string Reason);

/// <summary>SurrenderingYourDogFullIntake chapter - "Perform Behavior Test" -> "Behavior Test Completed".</summary>
public sealed record BehaviorTestCompletedV1(Guid PerformedBy, bool SuitableForRehoming, string BehaviorNotes);

/// <summary>SurrenderingYourDogFullIntake chapter's "Schedule Intake
/// Appointment" -> "Intake Appointment Scheduled".</summary>
public sealed record IntakeAppointmentScheduledV1(DateOnly AppointmentDate);

/// <summary>SurrenderingYourDogFullIntake chapter's "Add To Waiting List" -> "Added To Waiting List".</summary>
public sealed record AddedToWaitingListV1(int WaitlistPosition);

/// <summary>
/// SurrenderingYourDogFullIntake chapter's "Complete Surrender Paperwork"
/// -> "Surrender Paperwork Completed". One of several independent
/// intake-pipeline steps gated only on Status == Accepted (they don't
/// depend on each other, per the yaml's per-step `given: Dog Surrender
/// Accepted` tests) - doesn't transition DogSurrenderRequest.Status.
/// </summary>
public sealed record SurrenderPaperworkCompletedV1(bool LegalTransferSigned, string OwnershipProofType);

using System.Text.Json.Serialization;
using K9Crush.BuildingBlocks.Domain;
using K9Crush.Modules.ShelterAdoption.Domain.Events;

namespace K9Crush.Modules.ShelterAdoption.Domain;

/// <summary>
/// Represents one shelter/rescue org's application to operate on the
/// platform, from Spec/K9CRUSH.emlang.yaml's TheShelterRescueOrgSigningUp
/// chapter.
///
/// Self-aggregating event-sourced entity (ADR-031, Phase 5/5). Registered
/// as its own Inline snapshot - reviewer-facing read models query it
/// directly.
///
/// RequestedByOwnerId is the Identity module's OwnerAccount.Id (the
/// caller's own JWT sub) - the member who submitted this request, same
/// FK-by-convention pattern as DogListing.ShelterAccountId.
/// </summary>
public enum ShelterAccountStatus
{
    Requested,
    VerificationIssuesFound,
    Verified,
    Created,
    Rejected
}

/// <summary>
/// SurrenderingYourDogFullIntake chapter's "Configure Surrender Intake
/// Mode" - a single per-shelter switch, not per-step flags. Simple (the
/// default) is the existing DogSurrenderRequest flow (unchanged).
/// FullIntake unlocks that chapter's other 10 shelter-staff commands.
/// </summary>
public enum SurrenderIntakeMode
{
    Simple,
    FullIntake
}

public class ShelterAccount : Entity
{
    [JsonInclude] public Guid RequestedByOwnerId { get; private set; }
    [JsonInclude] public string BusinessDetails { get; private set; } = default!;
    [JsonInclude] public Guid UtilityBillDocumentId { get; private set; }
    [JsonInclude] public ShelterAccountStatus Status { get; private set; }
    [JsonInclude] public string? VerificationIssuesReason { get; private set; }
    [JsonInclude] public string? RejectionReason { get; private set; }
    [JsonInclude] public DateTimeOffset RequestedAt { get; private set; }
    [JsonInclude] public SurrenderIntakeMode SurrenderIntakeMode { get; private set; } = SurrenderIntakeMode.Simple;

    [JsonConstructor]
    private ShelterAccount() { }

    public static ShelterAccount Create(ShelterAccountRequestedV1 e) => new()
    {
        RequestedByOwnerId = e.RequestedByOwnerId,
        BusinessDetails = e.BusinessDetails,
        UtilityBillDocumentId = e.UtilityBillDocumentId,
        Status = ShelterAccountStatus.Requested,
        RequestedAt = e.RequestedAt
    };

    public void Apply(ShelterAccountVerifiedV1 e) => Status = ShelterAccountStatus.Verified;

    public void Apply(ShelterAccountVerificationIssuesFoundV1 e)
    {
        VerificationIssuesReason = e.Reason;
        Status = ShelterAccountStatus.VerificationIssuesFound;
    }

    public void Apply(ShelterAccountResubmittedV1 e)
    {
        BusinessDetails = e.BusinessDetails;
        UtilityBillDocumentId = e.UtilityBillDocumentId;
        VerificationIssuesReason = null;
        Status = ShelterAccountStatus.Requested;
    }

    public void Apply(ShelterAccountActivatedV1 e) => Status = ShelterAccountStatus.Created;

    public void Apply(ShelterAccountRejectedV1 e)
    {
        RejectionReason = e.Reason;
        Status = ShelterAccountStatus.Rejected;
    }

    /// <summary>The emlang yaml's "Request Shelter Account" -> "Shelter Account Requested".</summary>
    public static (ShelterAccount ShelterAccount, ShelterAccountRequestedV1 Event) RequestNew(
        Guid requestedByOwnerId, string businessDetails, Guid utilityBillDocumentId)
    {
        if (string.IsNullOrWhiteSpace(businessDetails))
            throw new ArgumentException("Business details are required.", nameof(businessDetails));

        var @event = new ShelterAccountRequestedV1(
            requestedByOwnerId, businessDetails.Trim(), utilityBillDocumentId, DateTimeOffset.UtcNow);
        return (Create(@event), @event);
    }

    /// <summary>
    /// Covers both "Shelter Verified" (first try) and "Shelter Reverified"
    /// (after fixing flagged issues) from the emlang yaml - same
    /// technical transition, same resulting status. State-guard (only
    /// valid from Requested) lives in the handler, not here.
    /// </summary>
    public ShelterAccountVerifiedV1 Verify()
    {
        var @event = new ShelterAccountVerifiedV1();
        Apply(@event);
        return @event;
    }

    /// <summary>
    /// The emlang yaml's "Flag Verification Issues" -> "Verification
    /// Issues Found". State-guard (only valid from Requested) lives in
    /// the handler.
    /// </summary>
    public ShelterAccountVerificationIssuesFoundV1 FlagVerificationIssues(string reason)
    {
        var @event = new ShelterAccountVerificationIssuesFoundV1(reason.Trim());
        Apply(@event);
        return @event;
    }

    /// <summary>
    /// The emlang yaml's "Resubmit Shelter Account Request" ->
    /// "Shelter Account Request Resubmitted". Resets status back to
    /// Requested. State-guard (only valid from VerificationIssuesFound)
    /// lives in the handler.
    /// </summary>
    public ShelterAccountResubmittedV1 Resubmit(string businessDetails, Guid utilityBillDocumentId)
    {
        if (string.IsNullOrWhiteSpace(businessDetails))
            throw new ArgumentException("Business details are required.", nameof(businessDetails));

        var @event = new ShelterAccountResubmittedV1(businessDetails.Trim(), utilityBillDocumentId);
        Apply(@event);
        return @event;
    }

    /// <summary>
    /// The emlang yaml's "Create Shelter Account" event - named Activate,
    /// not Create, to avoid colliding with the static Create() factory
    /// above. Shared by two different commands with two different
    /// preconditions - see CreateShelterAccountHandler and
    /// ApproveShelterAccountHandler.
    /// </summary>
    public ShelterAccountActivatedV1 Activate()
    {
        var @event = new ShelterAccountActivatedV1();
        Apply(@event);
        return @event;
    }

    /// <summary>
    /// The emlang yaml's "Reject Shelter Application" -> "Shelter
    /// Application Rejected". State-guard lives in the handler.
    /// </summary>
    public ShelterAccountRejectedV1 Reject(string reason)
    {
        var @event = new ShelterAccountRejectedV1(reason.Trim());
        Apply(@event);
        return @event;
    }

    public void Apply(SurrenderIntakeModeConfiguredV1 e) => SurrenderIntakeMode = e.Mode;

    /// <summary>
    /// SurrenderingYourDogFullIntake chapter's "Configure Surrender Intake
    /// Mode" -> "Surrender Intake Mode Configured". No state-guard - a
    /// shelter may switch modes at any time.
    /// </summary>
    public SurrenderIntakeModeConfiguredV1 ConfigureSurrenderIntakeMode(SurrenderIntakeMode mode)
    {
        var @event = new SurrenderIntakeModeConfiguredV1(mode);
        Apply(@event);
        return @event;
    }
}

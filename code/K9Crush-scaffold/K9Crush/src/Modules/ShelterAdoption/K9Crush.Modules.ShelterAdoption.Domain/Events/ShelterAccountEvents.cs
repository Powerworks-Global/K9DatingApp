namespace K9Crush.Modules.ShelterAdoption.Domain.Events;

/// <summary>ADR-031 event-sourcing retrofit, Phase 5/5 - one record per ShelterAccount transition.</summary>
public sealed record ShelterAccountRequestedV1(
    Guid RequestedByOwnerId, string BusinessDetails, Guid UtilityBillDocumentId, DateTimeOffset RequestedAt);

public sealed record ShelterAccountVerifiedV1;

public sealed record ShelterAccountVerificationIssuesFoundV1(string Reason);

public sealed record ShelterAccountResubmittedV1(string BusinessDetails, Guid UtilityBillDocumentId);

public sealed record ShelterAccountActivatedV1;

public sealed record ShelterAccountRejectedV1(string Reason);

public sealed record SurrenderIntakeModeConfiguredV1(SurrenderIntakeMode Mode);

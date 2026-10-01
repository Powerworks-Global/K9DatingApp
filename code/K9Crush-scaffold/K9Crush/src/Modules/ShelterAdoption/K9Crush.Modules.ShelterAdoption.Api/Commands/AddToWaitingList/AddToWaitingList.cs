namespace K9Crush.Modules.ShelterAdoption.Api.Commands.AddToWaitingList;

/// <summary>What this slice hands back to the caller.</summary>
public sealed record AddToWaitingListResponse(Guid SurrenderRequestId, int WaitlistPosition);

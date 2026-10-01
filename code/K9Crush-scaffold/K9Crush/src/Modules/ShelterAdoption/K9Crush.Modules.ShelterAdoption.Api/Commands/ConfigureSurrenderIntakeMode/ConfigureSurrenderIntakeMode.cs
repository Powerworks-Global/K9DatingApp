using K9Crush.Modules.ShelterAdoption.Domain;

namespace K9Crush.Modules.ShelterAdoption.Api.Commands.ConfigureSurrenderIntakeMode;

/// <summary>The request/command for this slice - what the caller sends.</summary>
public sealed record ConfigureSurrenderIntakeModeRequest(SurrenderIntakeMode Mode);

/// <summary>What this slice hands back to the caller.</summary>
public sealed record ConfigureSurrenderIntakeModeResponse(Guid ShelterAccountId, SurrenderIntakeMode Mode);

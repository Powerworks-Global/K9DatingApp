using System.ComponentModel.DataAnnotations;

namespace K9Crush.Modules.ShelterAdoption.Api.Commands.CompleteSurrenderPaperwork;

/// <summary>The request/command for this slice - what the caller sends.</summary>
public sealed record CompleteSurrenderPaperworkRequest(
    bool LegalTransferSigned,
    [property: Required, MaxLength(200)] string OwnershipProofType);

/// <summary>What this slice hands back to the caller.</summary>
public sealed record CompleteSurrenderPaperworkResponse(
    Guid SurrenderRequestId, bool LegalTransferSigned, string OwnershipProofType);

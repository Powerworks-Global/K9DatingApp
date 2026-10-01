using System.ComponentModel.DataAnnotations;

namespace K9Crush.Modules.ShelterAdoption.Api.Commands.PerformBehaviorTest;

/// <summary>The request/command for this slice - what the caller sends.</summary>
public sealed record PerformBehaviorTestRequest(
    Guid DogId,
    Guid PerformedBy,
    bool SuitableForRehoming,
    [property: Required, MaxLength(1000)] string BehaviorNotes) : IValidatableObject
{
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (DogId == Guid.Empty)
            yield return new ValidationResult("DogId is required.", [nameof(DogId)]);
        if (PerformedBy == Guid.Empty)
            yield return new ValidationResult("PerformedBy is required.", [nameof(PerformedBy)]);
    }
}

/// <summary>What this slice hands back to the caller.</summary>
public sealed record PerformBehaviorTestResponse(Guid SurrenderRequestId, bool SuitableForRehoming, string BehaviorNotes);

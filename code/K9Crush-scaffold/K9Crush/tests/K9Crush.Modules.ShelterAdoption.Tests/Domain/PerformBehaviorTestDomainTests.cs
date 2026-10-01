using FluentAssertions;
using K9Crush.Modules.ShelterAdoption.Domain;
using Xunit;

namespace K9Crush.Modules.ShelterAdoption.Tests.Domain;

/// <summary>
/// Layer 1 (TestingApproach.md) - pure unit test of
/// DogSurrenderRequest.CompleteBehaviorTest (SurrenderingYourDogFullIntake
/// chapter's "Perform Behavior Test" -> "Behavior Test Completed"). Kept
/// in its own file rather than added to DogSurrenderRequestTests.cs per
/// this project's CLAUDE.md ("do not change existing test files unless
/// explicitly instructed").
/// </summary>
public class PerformBehaviorTestDomainTests
{
    [Fact]
    public void CompleteBehaviorTest_WhenCalled_SetsFieldsTrimmed()
    {
        var request = DogSurrenderRequest.RequestNew(
            Guid.NewGuid(), "Cooper", "Terrier mix", 48, "Relocating for work", "Gentle", "Healthy").DogSurrenderRequest;
        request.Review();
        request.Accept();
        var performedBy = Guid.NewGuid();

        var @event = request.CompleteBehaviorTest(performedBy, true, "  Friendly, no aggression observed  ");

        @event.PerformedBy.Should().Be(performedBy);
        @event.SuitableForRehoming.Should().BeTrue();
        @event.BehaviorNotes.Should().Be("Friendly, no aggression observed");

        request.BehaviorTestPerformedBy.Should().Be(performedBy);
        request.BehaviorTestSuitableForRehoming.Should().BeTrue();
        request.BehaviorTestNotes.Should().Be("Friendly, no aggression observed");
        request.Status.Should().Be(SurrenderRequestStatus.Accepted, "the behavior test outcome does not change the request's overall status");
    }
}

using FluentAssertions;
using K9Crush.Modules.ShelterAdoption.Domain;
using Xunit;

namespace K9Crush.Modules.ShelterAdoption.Tests.Domain;

/// <summary>
/// Layer 1 (TestingApproach.md) - pure unit test of
/// DogSurrenderRequest.CompleteSurrenderPaperwork, the
/// SurrenderingYourDogFullIntake chapter's first intake-pipeline domain
/// method. Kept in its own file rather than added to
/// DogSurrenderRequestTests.cs per this project's CLAUDE.md rule against
/// editing existing test files.
/// </summary>
public class DogSurrenderRequestCompleteSurrenderPaperworkTests
{
    private static DogSurrenderRequest BuildAccepted()
    {
        var request = DogSurrenderRequest.RequestNew(
            Guid.NewGuid(), "Cooper", "Terrier mix", 48, "Relocating for work", "Gentle", "Healthy").DogSurrenderRequest;
        request.Review();
        request.Accept(Guid.NewGuid());
        return request;
    }

    [Fact]
    public void CompleteSurrenderPaperwork_WhenCalled_SetsFieldsTrimmedAndKeepsStatusAccepted()
    {
        var request = BuildAccepted();

        request.CompleteSurrenderPaperwork(true, "  dog_licence  ");

        request.LegalTransferSigned.Should().BeTrue();
        request.OwnershipProofType.Should().Be("dog_licence");
        request.Status.Should().Be(SurrenderRequestStatus.Accepted);
    }

    [Fact]
    public void Accept_WhenCalledWithShelterAccountId_SetsShelterAccountId()
    {
        var request = DogSurrenderRequest.RequestNew(
            Guid.NewGuid(), "Cooper", "Terrier mix", 48, "Relocating for work", "Gentle", "Healthy").DogSurrenderRequest;
        request.Review();
        var shelterAccountId = Guid.NewGuid();

        request.Accept(shelterAccountId);

        request.ShelterAccountId.Should().Be(shelterAccountId);
    }
}

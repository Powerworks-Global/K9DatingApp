using FluentAssertions;
using K9Crush.Modules.ShelterAdoption.Domain;
using K9Crush.Modules.ShelterAdoption.Domain.Events;
using Xunit;

namespace K9Crush.Modules.ShelterAdoption.Tests.Domain;

/// <summary>
/// Layer 1 (TestingApproach.md) - event-sourcing replay determinism.
/// Application's Apply(...) methods must derive every timestamp from the
/// event they're handed, never from the clock: Marten re-runs Apply when it
/// loads a stream, so a clock read inside Apply makes the same stream
/// hydrate to different state on every replay (and any projection built
/// from that replay drifts from what the original run produced).
/// Application.Create(ApplicationSubmittedV1) already read e.SubmittedAt;
/// the Draft submission and details-edit events used to call
/// DateTimeOffset.UtcNow instead.
/// </summary>
public class ApplicationEventReplayDeterminismTests
{
    private static readonly Guid ApplicantOwnerId = Guid.NewGuid();
    private static readonly Guid DogListingId = Guid.NewGuid();
    private static readonly Guid ShelterAccountId = Guid.NewGuid();

    /// <summary>A fixed instant, deliberately in the past - if Apply falls
    /// back to DateTimeOffset.UtcNow, the assertion below fails loudly
    /// rather than passing by coincidence.</summary>
    private static readonly DateTimeOffset RecordedAt = new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);

    [Fact]
    public void Apply_ApplicationDraftSubmittedV1_UsesTheEventsOwnTimestampRatherThanTheClock()
    {
        var application = StartDraft();

        application.Apply(new ApplicationDraftSubmittedV1(TestIntake.Default, RecordedAt));

        application.Status.Should().Be(ApplicationStatus.Pending);
        application.SubmittedAt.Should().Be(RecordedAt);
    }

    [Fact]
    public void Apply_ApplicationDetailsEditedV1_UsesTheEventsOwnTimestampRatherThanTheClock()
    {
        var application = StartDraft();

        application.Apply(new ApplicationDetailsEditedV1("fenced yard, two other dogs", RecordedAt));

        application.Details.Should().Be("fenced yard, two other dogs");
        application.LastEditedAt.Should().Be(RecordedAt);
    }

    [Fact]
    public void ReplayingTheSameEventsTwice_HydratesIdenticalTimestampsBothTimes()
    {
        var draftSubmitted = new ApplicationDraftSubmittedV1(TestIntake.Default, RecordedAt);
        var detailsEdited = new ApplicationDetailsEditedV1("fenced yard, two other dogs", RecordedAt);

        var firstRun = StartDraft();
        firstRun.Apply(draftSubmitted);
        firstRun.Apply(detailsEdited);

        var replayed = StartDraft();
        replayed.Apply(draftSubmitted);
        replayed.Apply(detailsEdited);

        replayed.SubmittedAt.Should().Be(firstRun.SubmittedAt);
        replayed.LastEditedAt.Should().Be(firstRun.LastEditedAt);
    }

    /// <summary>
    /// The Draft entry point - Application.StartDraftNew - rather than a
    /// directly-constructed Application, since it's the only public way to
    /// create one (its constructor is private, deliberately: every real
    /// transition goes through a named domain method).
    /// </summary>
    private static Application StartDraft() =>
        Application.StartDraftNew(ApplicantOwnerId, DogListingId, ShelterAccountId).Application;
}

using FluentAssertions;
using Marten;
using Microsoft.AspNetCore.Http.HttpResults;
using NSubstitute;
using K9Crush.Modules.Admin.Api.Commands.CreateTip;
using K9Crush.Modules.Admin.Domain;
using K9Crush.Modules.Admin.Domain.Events;
using Xunit;

namespace K9Crush.Modules.Admin.Tests.Handlers;

/// <summary>
/// Layer 2 (TestingApproach.md) - CreateTipHandler only calls
/// Events.StartStream/SaveChangesAsync, so IDocumentSession mocks cleanly
/// here (ADR-031). Same style as ResolveFeedbackHandlerTests; it just
/// can't use MartenEventStoreTestHelpers.BuildSessionWithFetchForWriting,
/// since this command creates its aggregate rather than fetching one -
/// there is no stream to stub a prior Aggregate on.
///
/// This is the emlang yaml's StaffDraftsANewTip test (no `given`, when
/// "Create Tip", then "Tip Drafted") - it has no failure branch to cover,
/// unlike ResolveFeedback's NotFound/Conflict cases.
/// </summary>
public class CreateTipHandlerTests
{
    [Fact]
    public async Task Handle_WhenCalled_DraftsTipAndStartsStream()
    {
        var session = Substitute.For<IDocumentSession>();
        var eventStore = Substitute.For<Marten.Events.IEventStoreOperations>();
        session.Events.Returns(eventStore);

        var result = await CreateTipHandler.Handle(session, CancellationToken.None);

        result.Should().BeOfType<Ok<CreateTipResponse>>();
        var response = result.Value!;
        response.TipId.Should().NotBeEmpty();
        response.DraftedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(10));

        // StartStream<TAggregate>(Guid id, params object[] events) - the params
        // array is what NSubstitute actually sees for verification, not a single
        // typed event. Plain casts, not `is` pattern-matching - Arg.Is's predicate
        // is an Expression<Predicate<T>>, and expression trees can't contain `is`
        // patterns (CS8122). See UploadMediaHandlerTests for the same note.
        eventStore.Received(1).StartStream<Tip>(
            response.TipId,
            Arg.Is<object[]>(events => events != null && events.Length == 1 && events[0] != null
                && ((TipDraftedV1)events[0]).TipId == response.TipId
                && ((TipDraftedV1)events[0]).DraftedAt == response.DraftedAt));
        await session.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}

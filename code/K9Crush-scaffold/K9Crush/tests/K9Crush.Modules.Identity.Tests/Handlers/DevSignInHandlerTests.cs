using JasperFx.Events;
using Marten;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using FluentAssertions;
using K9Crush.BuildingBlocks.Web;
using K9Crush.Modules.Identity.Api.Commands.DevSignIn;
using K9Crush.Modules.Identity.Domain;
using K9Crush.Modules.Identity.Domain.Events;
using Xunit;

namespace K9Crush.Modules.Identity.Tests.Handlers;

/// <summary>
/// Layer 2 (TestingApproach.md) - DevSignInHandler only calls
/// FetchForWriting/StartStream/AppendOne/SaveChangesAsync plus
/// ILocalDevTokenIssuer, so both mock cleanly here. FetchForWriting is
/// matched on any stream id because the handler derives the owner id from
/// the email internally (DevSignInHandler.DeriveOwnerId) rather than taking
/// one from a JWT.
/// </summary>
public class DevSignInHandlerTests
{
    private static IConfiguration BuildConfiguration(string provider = "Local")
    {
        var configuration = Substitute.For<IConfiguration>();
        configuration["Auth:Provider"].Returns(provider);
        return configuration;
    }

    private static (IDocumentSession Session, IEventStream<OwnerAccount> Stream) BuildSession(OwnerAccount? aggregate)
    {
        var session = Substitute.For<IDocumentSession>();
        var eventStore = Substitute.For<Marten.Events.IEventStoreOperations>();
        session.Events.Returns(eventStore);

        var stream = Substitute.For<IEventStream<OwnerAccount>>();
        stream.Aggregate.Returns(aggregate);
        eventStore.FetchForWriting<OwnerAccount>(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(stream));

        return (session, stream);
    }

    private static ILocalDevTokenIssuer BuildIssuer()
    {
        var issuer = Substitute.For<ILocalDevTokenIssuer>();
        issuer.IssueToken(Arg.Any<Guid>(), Arg.Any<string>()).Returns("local-dev-token");
        return issuer;
    }

    [Fact]
    public async Task Handle_WhenProviderIsNotLocal_ReturnsNotFoundAndMintsNothing()
    {
        var (session, _) = BuildSession(null);
        var issuer = BuildIssuer();

        var result = await DevSignInHandler.Handle(
            new DevSignInRequest("owner@example.com", "password"),
            BuildConfiguration(provider: "Supabase"), session, issuer, CancellationToken.None);

        result.Result.Should().BeOfType<NotFound>();
        issuer.DidNotReceiveWithAnyArgs().IssueToken(default, default!);
        await session.DidNotReceiveWithAnyArgs().SaveChangesAsync(default);
    }

    [Fact]
    public async Task Handle_WhenNewEmail_ProvisionsAndVerifiesOwnerAndReturnsToken()
    {
        var (session, _) = BuildSession(null);
        var issuer = BuildIssuer();

        // Email is deliberately messy to prove it is trimmed + lowercased.
        var result = await DevSignInHandler.Handle(
            new DevSignInRequest("  Owner@Example.COM ", "password"),
            BuildConfiguration(), session, issuer, CancellationToken.None);

        result.Result.Should().BeOfType<Ok<DevSignInResponse>>();
        var response = ((Ok<DevSignInResponse>)result.Result).Value!;
        response.AccessToken.Should().Be("local-dev-token");
        response.User.Email.Should().Be("owner@example.com");
        Guid.TryParse(response.User.Id, out _).Should().BeTrue();

        // Provision AND verify, in one stream - replaces both Supabase
        // webhook automations in dev. Asserted as separate event arguments
        // (matching how the handler calls the params overload) rather than
        // one Arg.Is<object[]>, which C# binds to Marten's IEnumerable
        // overload instead - see ProvisionOwnerOnSupabaseSignupHandlerTests
        // for the pre-existing version of this trap.
        session.Events.Received(1).StartStream<OwnerAccount>(
            Arg.Any<Guid>(),
            Arg.Is<object>(e => e is OwnerAccountCreatedV1),
            Arg.Is<object>(e => e is OwnerAccountVerifiedV1));
        await session.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        issuer.Received(1).IssueToken(Arg.Any<Guid>(), "owner@example.com");
    }

    [Fact]
    public async Task Handle_WhenOwnerExistsButIsUnverified_VerifiesWithoutRecreatingTheStream()
    {
        var (owner, _) = OwnerAccount.CreateNew(Guid.NewGuid(), "owner@example.com", DateTimeOffset.UtcNow);
        var (session, stream) = BuildSession(owner);
        var issuer = BuildIssuer();

        var result = await DevSignInHandler.Handle(
            new DevSignInRequest("owner@example.com", "password"),
            BuildConfiguration(), session, issuer, CancellationToken.None);

        result.Result.Should().BeOfType<Ok<DevSignInResponse>>();
        stream.Received(1).AppendOne(Arg.Any<object>());
        session.Events.DidNotReceive().StartStream<OwnerAccount>(
            Arg.Any<Guid>(), Arg.Any<object>(), Arg.Any<object>());
        await session.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenOwnerIsAlreadyVerified_IsIdempotent()
    {
        var (owner, _) = OwnerAccount.CreateNew(Guid.NewGuid(), "owner@example.com", DateTimeOffset.UtcNow);
        owner.MarkVerified();
        var (session, stream) = BuildSession(owner);
        var issuer = BuildIssuer();

        var result = await DevSignInHandler.Handle(
            new DevSignInRequest("owner@example.com", "password"),
            BuildConfiguration(), session, issuer, CancellationToken.None);

        result.Result.Should().BeOfType<Ok<DevSignInResponse>>();
        stream.DidNotReceiveWithAnyArgs().AppendOne(default!);
        await session.DidNotReceiveWithAnyArgs().SaveChangesAsync(default);
        issuer.Received(1).IssueToken(Arg.Any<Guid>(), "owner@example.com");
    }

    [Fact]
    public async Task Handle_DerivesTheSameOwnerIdForTheSameEmailRegardlessOfCaseOrWhitespace()
    {
        var issuer = BuildIssuer();

        var (sessionA, _) = BuildSession(null);
        var first = await DevSignInHandler.Handle(
            new DevSignInRequest("owner@example.com", "password"),
            BuildConfiguration(), sessionA, issuer, CancellationToken.None);

        var (sessionB, _) = BuildSession(null);
        var second = await DevSignInHandler.Handle(
            new DevSignInRequest("  OWNER@example.com  ", "password"),
            BuildConfiguration(), sessionB, issuer, CancellationToken.None);

        var idA = ((Ok<DevSignInResponse>)first.Result).Value!.User.Id;
        var idB = ((Ok<DevSignInResponse>)second.Result).Value!.User.Id;
        idA.Should().Be(idB);
    }
}

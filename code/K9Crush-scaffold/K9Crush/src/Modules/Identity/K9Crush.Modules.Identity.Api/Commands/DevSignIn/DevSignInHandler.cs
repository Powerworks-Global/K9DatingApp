using System.Security.Cryptography;
using System.Text;
using Marten;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Configuration;
using K9Crush.BuildingBlocks.Web;
using K9Crush.Modules.Identity.Domain;
using K9Crush.Modules.Identity.Domain.Events;
using Wolverine.Http;

namespace K9Crush.Modules.Identity.Api.Commands.DevSignIn;

/// <summary>
/// Development-only local sign-in (ADR-048). Stands in for the two
/// Supabase Database Webhook automations that normally provision and
/// verify an OwnerAccount (ProvisionOwnerOnSupabaseSignup,
/// VerifyOwnerOnSupabaseConfirmation) so a developer can authenticate
/// without a Supabase project, then mints a token with the same shape and
/// claims Supabase would issue.
///
/// Gated twice, so it can never act as a real backdoor: it 404s unless
/// Auth:Provider=Local, and Api.Host refuses to start with that provider
/// outside Development. The password is accepted but never checked.
///
/// Not an event-modeling slice from the board - it is local auth
/// infrastructure that happens to live in the Commands lane because it is
/// command-shaped (input -> events). Flagged here so a future reader
/// doesn't expect a matching slice.json entry.
/// </summary>
public static class DevSignInHandler
{
    [WolverinePost("/api/v1/identity/dev/sign-in")]
    public static async Task<Results<Ok<DevSignInResponse>, NotFound>> Handle(
        DevSignInRequest request,
        IConfiguration configuration,
        IDocumentSession session,
        ILocalDevTokenIssuer tokenIssuer,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(configuration["Auth:Provider"], "Local", StringComparison.OrdinalIgnoreCase))
            return TypedResults.NotFound();

        var normalizedEmail = request.Email.Trim().ToLowerInvariant();
        var ownerId = DeriveOwnerId(normalizedEmail);

        var stream = await session.Events.FetchForWriting<OwnerAccount>(ownerId, cancellationToken);
        if (stream.Aggregate is null)
        {
            var (_, created) = OwnerAccount.CreateNew(ownerId, normalizedEmail, DateTimeOffset.UtcNow);
            // Provision AND verify in one stream: in dev there is no
            // Supabase confirmation webhook to flip IsVerified later, and an
            // unverified owner can't reach any "VerifiedOwner" endpoint.
            session.Events.StartStream<OwnerAccount>(ownerId, created, new OwnerAccountVerifiedV1());
        }
        else if (!stream.Aggregate.IsVerified)
        {
            stream.AppendOne(stream.Aggregate.MarkVerified());
        }
        else
        {
            // Already provisioned and verified - idempotent re-sign-in,
            // nothing to persist.
            return TypedResults.Ok(BuildResponse(tokenIssuer, ownerId, normalizedEmail));
        }

        await session.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(BuildResponse(tokenIssuer, ownerId, normalizedEmail));
    }

    private static DevSignInResponse BuildResponse(ILocalDevTokenIssuer tokenIssuer, Guid ownerId, string email) =>
        new(tokenIssuer.IssueToken(ownerId, email), new DevSignInUser(ownerId.ToString(), email));

    /// <summary>
    /// OwnerAccount.Id must be stable for a given email so repeated dev
    /// sign-ins map to the same stream (and the same owner id, which is
    /// what every other module's OwnerId foreign key assumes). Real
    /// deployments use Supabase's own auth user id (the JWT "sub"); with no
    /// Supabase project there is no such id, so derive a deterministic Guid
    /// from the normalised email instead.
    /// </summary>
    private static Guid DeriveOwnerId(string normalizedEmail)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalizedEmail));
        return new Guid(hash.AsSpan(0, 16));
    }
}

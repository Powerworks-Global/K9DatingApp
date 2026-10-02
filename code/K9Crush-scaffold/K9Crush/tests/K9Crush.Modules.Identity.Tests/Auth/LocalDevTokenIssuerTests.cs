using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using NSubstitute;
using FluentAssertions;
using K9Crush.BuildingBlocks.Web;
using Xunit;

namespace K9Crush.Modules.Identity.Tests.Auth;

/// <summary>
/// Layer 1-ish, no infra: verifies the exact claim contract the local dev
/// token must carry, by validating a minted token with the same
/// TokenValidationParameters Api.Host configures for Auth:Provider=Local
/// (issuer, audience "authenticated", symmetric key, MapInboundClaims).
/// This is the piece that has to be right for VerifiedOwner/RoleRequirement
/// to accept a locally-issued token, so it is tested directly rather than
/// only through the endpoint.
/// </summary>
public class LocalDevTokenIssuerTests
{
    private const string SigningKey = "k9crush-local-dev-only-signing-key-change-me-0000";
    private const string Issuer = "k9crush-local";

    private static IConfiguration BuildConfiguration(string? signingKey = SigningKey, string? issuer = Issuer)
    {
        var configuration = Substitute.For<IConfiguration>();
        configuration["Auth:Local:SigningKey"].Returns(signingKey!);
        configuration["Auth:Local:Issuer"].Returns(issuer!);
        return configuration;
    }

    private static Task<TokenValidationResult> ValidateAsync(string token, string signingKey = SigningKey) =>
        new JsonWebTokenHandler { MapInboundClaims = true }.ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = Issuer,
            ValidateAudience = true,
            ValidAudience = "authenticated",
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
            ValidateLifetime = true
        });

    [Fact]
    public async Task IssueToken_ProducesATokenApiHostsLocalValidationAccepts()
    {
        var ownerId = Guid.NewGuid();
        var token = new LocalDevTokenIssuer(BuildConfiguration()).IssueToken(ownerId, "owner@example.com");

        var result = await ValidateAsync(token);

        result.IsValid.Should().BeTrue();
        // These two are what RoleAuthorizationHandler / handlers read - the
        // "sub" -> NameIdentifier mapping is exactly what MapInboundClaims
        // provides for real Supabase tokens too.
        result.ClaimsIdentity.FindFirst(ClaimTypes.NameIdentifier)?.Value.Should().Be(ownerId.ToString());
        result.ClaimsIdentity.FindFirst(ClaimTypes.Email)?.Value.Should().Be("owner@example.com");
    }

    [Fact]
    public async Task IssueToken_NestsEmailVerifiedInUserMetadataLikeSupabaseDoes()
    {
        var token = new LocalDevTokenIssuer(BuildConfiguration()).IssueToken(Guid.NewGuid(), "owner@example.com");

        var result = await ValidateAsync(token);

        // EmailVerifiedAuthorizationHandler parses this exact nested shape.
        using var metadata = JsonDocument.Parse(result.ClaimsIdentity.FindFirst("user_metadata")!.Value);
        metadata.RootElement.GetProperty("email_verified").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task IssueToken_IsRejectedWhenValidatedWithADifferentSigningKey()
    {
        var token = new LocalDevTokenIssuer(BuildConfiguration()).IssueToken(Guid.NewGuid(), "owner@example.com");

        var result = await ValidateAsync(token, signingKey: "a-completely-different-signing-key-00000000");

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void IssueToken_WhenSigningKeyIsMissing_Throws()
    {
        var issuer = new LocalDevTokenIssuer(BuildConfiguration(signingKey: null));

        var act = () => issuer.IssueToken(Guid.NewGuid(), "owner@example.com");

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void IssueToken_WhenSigningKeyIsTooShort_Throws()
    {
        var issuer = new LocalDevTokenIssuer(BuildConfiguration(signingKey: "too-short"));

        var act = () => issuer.IssueToken(Guid.NewGuid(), "owner@example.com");

        act.Should().Throw<InvalidOperationException>();
    }
}

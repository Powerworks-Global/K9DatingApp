using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace K9Crush.BuildingBlocks.Web;

/// <summary>
/// Auth:Provider=Local implementation of ILocalDevTokenIssuer - mints an
/// HS256 JWT signed with Auth:Local:SigningKey, shaped to match what
/// Api.Host's Local JwtBearer validation and the rest of the pipeline expect
/// (see ILocalDevTokenIssuer's doc comment for the gating rules that keep
/// this Development-only).
///
/// Co-located with the interface (and therefore in BuildingBlocks.Web, which
/// now carries the JwtBearer package) rather than in Api.Host, so it stays
/// unit-testable - it is the piece that must get the claim shape exactly
/// right for VerifiedOwner/RoleRequirement to work.
///
/// Uses JsonWebTokenHandler (not JwtSecurityTokenHandler) deliberately: the
/// latter applies the legacy outbound claim-type map, which would rewrite
/// "sub" to the long ClaimTypes.NameIdentifier URI on the way out and break
/// the inbound mapping Api.Host relies on (MapInboundClaims).
/// </summary>
public sealed class LocalDevTokenIssuer(IConfiguration configuration) : ILocalDevTokenIssuer
{
    public string IssueToken(Guid ownerId, string email)
    {
        var signingKey = configuration["Auth:Local:SigningKey"]
            ?? throw new InvalidOperationException(
                "Missing Auth:Local:SigningKey - required to issue local development tokens (Auth:Provider=Local).");
        if (signingKey.Length < 32)
            throw new InvalidOperationException(
                "Auth:Local:SigningKey must be at least 32 characters (HMAC-SHA256 minimum key size).");

        var issuer = configuration["Auth:Local:Issuer"] ?? "k9crush-local";

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer,
            // Same audience Supabase uses - kept identical so nothing
            // downstream needs to know which provider issued the token.
            Audience = "authenticated",
            IssuedAt = DateTime.UtcNow,
            Expires = DateTime.UtcNow.AddHours(8),
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
                SecurityAlgorithms.HmacSha256),
            Claims = new Dictionary<string, object>
            {
                ["sub"] = ownerId.ToString(),
                ["email"] = email,
                // Supabase nests email_verified inside a JSON "user_metadata"
                // claim rather than a top-level claim - EmailVerifiedAuthorizationHandler
                // parses exactly this shape, so the local token reproduces it.
                ["user_metadata"] = "{\"email_verified\":true}"
            }
        };

        return new JsonWebTokenHandler().CreateToken(descriptor);
    }
}

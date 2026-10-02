using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace K9Crush.Modules.Identity.Api.Commands.DevSignIn;

/// <summary>
/// Request for the Development-only local sign-in endpoint (ADR-048).
/// Password is present only because the shared login/register form posts
/// one - it is deliberately never checked: this stand-in is not a
/// credential store (Supabase owns credentials in every real environment),
/// it just needs a stable identity to provision an OwnerAccount for.
/// </summary>
public sealed record DevSignInRequest(
    [property: Required, EmailAddress] string Email,
    [property: Required] string Password);

/// <summary>
/// Deliberately shaped like Supabase's own /auth/v1 token response, so
/// Blazor's AuthSession/AuthUser DTOs and the cookie claim extraction in
/// Login.razor/Register.razor work unchanged against either provider.
/// </summary>
public sealed record DevSignInResponse(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("user")] DevSignInUser User);

public sealed record DevSignInUser(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("email")] string Email);

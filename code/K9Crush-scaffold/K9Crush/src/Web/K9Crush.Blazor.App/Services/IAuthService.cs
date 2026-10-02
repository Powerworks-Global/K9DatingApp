using System.Text.Json.Serialization;

namespace K9Crush.Blazor.App.Services;

/// <summary>
/// Provider-agnostic sign-in/sign-up surface for the Blazor app. The
/// implementation is selected by Auth:Provider (ADR-048): SupabaseAuthService
/// in every real environment, LocalDevAuthService for the Development-only
/// local stand-in.
/// </summary>
public interface IAuthService
{
    Task<AuthResult> SignInWithPasswordAsync(string email, string password, CancellationToken cancellationToken);

    Task<AuthResult> SignUpAsync(string email, string password, CancellationToken cancellationToken);
}

/// <summary>
/// Provider-agnostic auth outcome. Both implementations speak the same
/// { access_token, user } shape, so Login.razor/Register.razor don't branch
/// on which provider is active.
/// </summary>
public sealed record AuthResult(bool IsSuccess, string? ErrorMessage, AuthSession? Session)
{
    public static AuthResult Succeeded(AuthSession? session) => new(true, null, session);
    public static AuthResult Failed(string errorMessage) => new(false, errorMessage, null);
}

public sealed record AuthSession(
    [property: JsonPropertyName("access_token")] string? AccessToken,
    [property: JsonPropertyName("user")] AuthUser? User);

public sealed record AuthUser(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("email")] string? Email);

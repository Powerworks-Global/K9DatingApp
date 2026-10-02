using System.Net.Http.Json;

namespace K9Crush.Blazor.App.Services;

/// <summary>
/// IAuthService for Auth:Provider=Local (ADR-048) - the Development-only
/// stand-in that removes the need for a Supabase Cloud project locally.
///
/// Calls Api.Host's /api/v1/identity/dev/sign-in, which provisions and
/// verifies the OwnerAccount itself and returns a token shaped exactly like
/// Supabase's, so the rest of the app (cookie claims, AuthorizedApiClient,
/// Api.Host validation) is unchanged. Sign-up and sign-in are deliberately
/// the same operation locally: there is no confirmation email to wait for.
/// </summary>
public sealed class LocalDevAuthService(IHttpClientFactory httpClientFactory) : IAuthService
{
    public Task<AuthResult> SignInWithPasswordAsync(string email, string password, CancellationToken cancellationToken) =>
        SignInAsync(email, password, cancellationToken);

    public Task<AuthResult> SignUpAsync(string email, string password, CancellationToken cancellationToken) =>
        SignInAsync(email, password, cancellationToken);

    private async Task<AuthResult> SignInAsync(string email, string password, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient("K9CrushApi");
        var response = await client.PostAsJsonAsync(
            "api/v1/identity/dev/sign-in", new { email, password }, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            // A 404 here means Api.Host is running with Auth:Provider=Supabase
            // (the endpoint only exists in Local mode).
            return AuthResult.Failed(
                "Local development sign-in failed - make sure Api.Host is running with Auth:Provider=Local.");
        }

        var session = await response.Content.ReadFromJsonAsync<AuthSession>(cancellationToken: cancellationToken);
        return session is null
            ? AuthResult.Failed("Local development sign-in returned an empty response.")
            : AuthResult.Succeeded(session);
    }
}

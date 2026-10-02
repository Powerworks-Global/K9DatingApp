using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace K9Crush.Blazor.App.Services;

/// <summary>
/// IAuthService backed by Supabase's own /auth/v1 REST API - the default in
/// every real environment (ADR-005). Supabase owns the entire
/// signup/login/confirmation lifecycle; this app never issues or stores
/// credentials itself, only relays to Supabase and keeps the resulting
/// session (see Login.razor/Register.razor).
/// </summary>
public sealed class SupabaseAuthService(IHttpClientFactory httpClientFactory) : IAuthService
{
    public async Task<AuthResult> SignInWithPasswordAsync(
        string email, string password, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient("SupabaseAuth");
        var response = await client.PostAsJsonAsync(
            "token?grant_type=password", new SupabaseCredentials(email, password), cancellationToken);

        return await ReadResultAsync(response, cancellationToken);
    }

    public async Task<AuthResult> SignUpAsync(
        string email, string password, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient("SupabaseAuth");
        var response = await client.PostAsJsonAsync(
            "signup", new SupabaseCredentials(email, password), cancellationToken);

        // Confirm-email is on (Identity's ConfirmProfile flow, ADR-005) so a
        // fresh signup has no session yet - AccessToken stays null until the
        // confirmation email is clicked and Supabase's own webhook fires
        // VerifyOwnerOnSupabaseConfirmationHandler. Register.razor branches
        // on that rather than assuming a session always comes back.
        return await ReadResultAsync(response, cancellationToken);
    }

    private static async Task<AuthResult> ReadResultAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadFromJsonAsync<SupabaseAuthError>(cancellationToken: cancellationToken);
            return AuthResult.Failed(error?.ErrorDescription ?? error?.Msg ?? "Something went wrong - please try again.");
        }

        var session = await response.Content.ReadFromJsonAsync<AuthSession>(cancellationToken: cancellationToken);
        return AuthResult.Succeeded(session);
    }

    private sealed record SupabaseCredentials(
        [property: JsonPropertyName("email")] string Email,
        [property: JsonPropertyName("password")] string Password);

    private sealed record SupabaseAuthError(
        [property: JsonPropertyName("msg")] string? Msg,
        [property: JsonPropertyName("error_description")] string? ErrorDescription);
}

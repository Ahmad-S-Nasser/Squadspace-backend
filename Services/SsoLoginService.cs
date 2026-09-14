using System.Net.Http.Headers;
using System.Text.Json;
using RafeeqyNotes.Api.Models;
using RafeeqyNotes.Api.Repositories;

namespace RafeeqyNotes.Api.Services
{
    public sealed class SsoResult
    {
        public bool Success { get; init; }

        /// <summary>Safe to show a user. Never says which check failed.</summary>
        public string Error { get; init; }

        public Contributor Contributor { get; init; }
        public string OrganizationId { get; init; }
    }

    public interface ISsoLoginService
    {
        string BuildAuthorizeUrl(string provider, string clientId, string redirectUri, string state, string nonce, OrgSsoConfig config);
        Task<SsoResult> CompleteAsync(string provider, string code, SsoState state, OrgSsoConfig config);
    }

    /// <summary>
    /// Completes a Google or Microsoft sign-in and maps it to a local account.
    /// </summary>
    /// <remarks>
    /// Replaces two hand-rolled flows that between them had every classic OAuth flaw: no state, no
    /// nonce, no id_token validation, the Microsoft /common/ authority, the session JWT delivered
    /// in a URL query string, and account linking on an unverified address.
    ///
    /// The linking rule is the one worth stating plainly, because it is where "sign in with X"
    /// turns into account takeover. An existing local account is only ever adopted when the
    /// provider VOUCHES for the address AND the address is inside the organization's configured
    /// domain. Anything less means someone who can obtain a token asserting an address can walk
    /// into whatever account already holds it.
    /// </remarks>
    public class SsoLoginService : ISsoLoginService
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IOidcTokenValidator _validator;
        private readonly IContributorRepository _contributors;
        private readonly IOrganizationRepository _organizations;
        private readonly IConfiguration _config;
        private readonly ILogger<SsoLoginService> _logger;

        public SsoLoginService(
            IHttpClientFactory httpClientFactory,
            IOidcTokenValidator validator,
            IContributorRepository contributors,
            IOrganizationRepository organizations,
            IConfiguration config,
            ILogger<SsoLoginService> logger)
        {
            _httpClientFactory = httpClientFactory;
            _validator = validator;
            _contributors = contributors;
            _organizations = organizations;
            _config = config;
            _logger = logger;
        }

        /// <summary>
        /// The authority for a Microsoft login.
        /// </summary>
        /// <remarks>
        /// A CONCRETE tenant whenever the organization pinned one. The previous flow hardcoded
        /// /common/, which accepts every Entra tenant in the world plus personal Microsoft
        /// accounts - so the "Microsoft" button proved nothing beyond "this person has some
        /// Microsoft account". For SSO that is not a weaker guarantee, it is no guarantee.
        ///
        /// /organizations excludes personal accounts and is the least-bad fallback for the
        /// consumer button, which has no organization behind it.
        /// </remarks>
        private static string MicrosoftAuthority(OrgSsoConfig config) =>
            string.IsNullOrWhiteSpace(config?.MicrosoftTenantId) ? "organizations" : config.MicrosoftTenantId;

        public string BuildAuthorizeUrl(
            string provider, string clientId, string redirectUri, string state, string nonce, OrgSsoConfig config)
        {
            var common =
                $"client_id={Uri.EscapeDataString(clientId)}" +
                $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
                "&response_type=code" +
                $"&state={Uri.EscapeDataString(state)}" +
                $"&nonce={Uri.EscapeDataString(nonce)}";

            if (string.Equals(provider, OidcTokenValidator.Google, StringComparison.OrdinalIgnoreCase))
            {
                var url = "https://accounts.google.com/o/oauth2/v2/auth?" + common +
                          "&scope=" + Uri.EscapeDataString("openid email profile");

                // hd restricts the account chooser to one Workspace domain. It is a UX hint AND
                // it is re-checked on the returned token - Google documents that hd alone must
                // not be trusted as an authorization decision.
                if (!string.IsNullOrWhiteSpace(config?.GoogleHostedDomain))
                {
                    url += "&hd=" + Uri.EscapeDataString(config.GoogleHostedDomain);
                }

                return url;
            }

            return $"https://login.microsoftonline.com/{MicrosoftAuthority(config)}/oauth2/v2.0/authorize?"
                   + common + "&scope=" + Uri.EscapeDataString("openid email profile");
        }

        public async Task<SsoResult> CompleteAsync(
            string provider, string code, SsoState state, OrgSsoConfig config)
        {
            var isGoogle = string.Equals(provider, OidcTokenValidator.Google, StringComparison.OrdinalIgnoreCase);

            var clientId = _config[isGoogle ? "Google:ClientId" : "Microsoft:ClientId"];
            var clientSecret = _config[isGoogle ? "Google:ClientSecret" : "Microsoft:ClientSecret"];
            var redirectUri = _config[isGoogle ? "Google:RedirectUri" : "Microsoft:RedirectUri"];

            if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
            {
                _logger.LogError("SSO attempted for {Provider} with no client credentials configured.", provider);
                return Fail("Single sign-on is not configured.");
            }

            var tokenEndpoint = isGoogle
                ? "https://oauth2.googleapis.com/token"
                : $"https://login.microsoftonline.com/{MicrosoftAuthority(config)}/oauth2/v2.0/token";

            // A client from the factory, never a shared one. The old code assigned
            // DefaultRequestHeaders.Authorization on an injected HttpClient - shared mutable state
            // on a singleton, so two concurrent logins could read each other's access token and
            // return the wrong person's identity.
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(15);

            JsonElement tokenData;
            try
            {
                var response = await client.PostAsync(tokenEndpoint, new FormUrlEncodedContent(
                    new Dictionary<string, string>
                    {
                        { "client_id", clientId },
                        { "client_secret", clientSecret },
                        { "code", code },
                        { "redirect_uri", redirectUri },
                        { "grant_type", "authorization_code" },
                    }));

                tokenData = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SSO token exchange failed for {Provider}.", provider);
                return Fail("Sign-in failed. Please try again.");
            }

            if (tokenData.ValueKind != JsonValueKind.Object
                || tokenData.TryGetProperty("error", out _)
                || !tokenData.TryGetProperty("id_token", out var idTokenElement))
            {
                return Fail("Sign-in failed. Please try again.");
            }

            var identity = await _validator.ValidateAsync(
                idTokenElement.GetString(), provider, clientId, state.Nonce,
                isGoogle ? null : MicrosoftAuthority(config));

            if (identity == null || string.IsNullOrWhiteSpace(identity.Email))
            {
                return Fail("We could not verify that sign-in.");
            }

            // The provider must vouch for the address. Without this, an account that merely
            // CLAIMS victim@company.com is enough to adopt the local account holding it.
            if (!identity.EmailVerified)
            {
                _logger.LogWarning("SSO rejected: unverified email from {Provider}.", provider);
                return Fail("Your email address is not verified with your provider.");
            }

            var email = identity.Email.Trim().ToLowerInvariant();
            var domain = email.Contains('@') ? email.Split('@').Last() : string.Empty;

            if (config != null)
            {
                var denied = CheckOrganizationBinding(identity, config, domain);
                if (denied != null) return denied;
            }

            var contributor = await _contributors.GetByEmailAsync(email);
            if (contributor == null)
            {
                if (config != null && !config.AutoProvisionUsers)
                {
                    return Fail("No account exists for this address, and automatic provisioning is off.");
                }

                contributor = new Contributor
                {
                    Email = email,
                    Name = string.IsNullOrWhiteSpace(identity.Name) ? email : identity.Name,
                    DisplayName = string.IsNullOrWhiteSpace(identity.Name) ? email : identity.Name,
                    Provider = provider,
                    IsEmailVerified = true,
                    SubscriptionExpiry = DateTime.UtcNow.AddMonths(1),
                };

                await _contributors.CreateAsync(contributor);
                _logger.LogInformation("SSO provisioned a new user via {Provider}.", provider);
            }

            return new SsoResult
            {
                Success = true,
                Contributor = contributor,
                OrganizationId = config?.OrganizationId,
            };
        }

        /// <summary>
        /// Checks the token actually came from the customer's directory.
        /// </summary>
        /// <remarks>
        /// Two independent gates, because neither is sufficient alone. The tenant/domain claim
        /// says which directory issued the token; the domain allowlist says which addresses this
        /// organization accepts. A guest invited into the customer's Entra tenant passes the first
        /// while having an outside address, which is exactly what the second is for.
        /// </remarks>
        private SsoResult CheckOrganizationBinding(OidcIdentity identity, OrgSsoConfig config, string domain)
        {
            if (string.Equals(config.Provider, OidcTokenValidator.Google, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(config.GoogleHostedDomain)
                && !string.Equals(identity.HostedDomain, config.GoogleHostedDomain, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("SSO rejected: hosted domain mismatch for organization {OrganizationId}.",
                    config.OrganizationId);
                return Fail("That account is not part of this organization's workspace.");
            }

            if (string.Equals(config.Provider, OidcTokenValidator.Microsoft, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(config.MicrosoftTenantId)
                && !string.Equals(identity.TenantId, config.MicrosoftTenantId, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("SSO rejected: tenant mismatch for organization {OrganizationId}.",
                    config.OrganizationId);
                return Fail("That account is not part of this organization's directory.");
            }

            if (config.AllowedEmailDomains is { Count: > 0 }
                && !config.AllowedEmailDomains.Any(d =>
                    string.Equals(d?.Trim(), domain, StringComparison.OrdinalIgnoreCase)))
            {
                _logger.LogWarning("SSO rejected: email domain not allowed for organization {OrganizationId}.",
                    config.OrganizationId);
                return Fail("That email domain is not permitted for this organization.");
            }

            return null;
        }

        private static SsoResult Fail(string error) => new() { Success = false, Error = error };
    }
}

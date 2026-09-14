using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.IdentityModel.Tokens;

namespace RafeeqyNotes.Api.Services
{
    /// <summary>Identity proven by a provider's id_token.</summary>
    public sealed class OidcIdentity
    {
        public string Subject { get; init; }
        public string Email { get; init; }
        public string Name { get; init; }

        /// <summary>The provider asserts this address is verified and belongs to the user.</summary>
        public bool EmailVerified { get; init; }

        /// <summary>Google Workspace hosted domain (`hd`), when present.</summary>
        public string HostedDomain { get; init; }

        /// <summary>Microsoft Entra tenant id (`tid`), when present.</summary>
        public string TenantId { get; init; }
    }

    public interface IOidcTokenValidator
    {
        /// <summary>
        /// Validates an id_token and returns the identity it proves, or null if it is not valid.
        /// </summary>
        Task<OidcIdentity> ValidateAsync(string idToken, string provider, string audience, string nonce, string tenantId);
    }

    /// <summary>
    /// Validates provider id_tokens against the provider's published signing keys.
    /// </summary>
    /// <remarks>
    /// The previous flows never looked at the id_token at all - they took the access token and
    /// called a userinfo endpoint. That is not, on its own, wrong: the access token was issued to
    /// our client. But it left the flow with no nonce binding, no signature check, and nothing
    /// tying the response to the login attempt that started it.
    ///
    /// What this checks, and why each one matters:
    ///
    /// - **Signature**, against the provider's current JWKS. Without it an id_token is a JSON
    ///   document anyone can write.
    /// - **Issuer**, exactly. For Microsoft this is the specific tenant, which is what stops a
    ///   token from some other Entra tenant being accepted.
    /// - **Audience** must be our client id. A token minted for a different application is not a
    ///   statement about a login here - this is the classic confused-deputy in OIDC.
    /// - **Expiry**, with a small clock skew.
    /// - **Nonce**, matching the one issued with the state. This is what makes the token
    ///   single-use for one login attempt rather than replayable.
    ///
    /// Keys are cached, because fetching JWKS on every login makes the identity provider a hard
    /// dependency of every sign-in, and refetched on an unknown key id so provider key rotation
    /// does not lock everyone out.
    /// </remarks>
    public class OidcTokenValidator : IOidcTokenValidator
    {
        public const string Google = "google";
        public const string Microsoft = "microsoft";

        private static readonly TimeSpan KeyCacheFor = TimeSpan.FromHours(12);
        private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(2);

        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IMemoryCache _cache;
        private readonly ILogger<OidcTokenValidator> _logger;

        public OidcTokenValidator(
            IHttpClientFactory httpClientFactory,
            IMemoryCache cache,
            ILogger<OidcTokenValidator> logger)
        {
            _httpClientFactory = httpClientFactory;
            _cache = cache;
            _logger = logger;
        }

        public async Task<OidcIdentity> ValidateAsync(
            string idToken, string provider, string audience, string nonce, string tenantId)
        {
            if (string.IsNullOrWhiteSpace(idToken) || string.IsNullOrWhiteSpace(audience)) return null;

            try
            {
                var (issuer, jwksUri) = Endpoints(provider, tenantId);
                if (issuer == null) return null;

                var keys = await GetSigningKeysAsync(jwksUri, refresh: false);

                var parameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = issuer,
                    ValidateAudience = true,
                    ValidAudience = audience,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKeys = keys,
                    ClockSkew = ClockSkew,

                    // Microsoft's v2 issuer for a specific tenant is templated; accept the exact
                    // tenant issuer only, which ValidIssuer above already pins.
                    RequireSignedTokens = true,
                    RequireExpirationTime = true,
                };

                var handler = new JwtSecurityTokenHandler();
                ClaimsPrincipal principal;

                try
                {
                    principal = handler.ValidateToken(idToken, parameters, out _);
                }
                catch (SecurityTokenSignatureKeyNotFoundException)
                {
                    // The provider rotated its keys. Refetch once rather than failing every login
                    // until the cache expires.
                    parameters.IssuerSigningKeys = await GetSigningKeysAsync(jwksUri, refresh: true);
                    principal = handler.ValidateToken(idToken, parameters, out _);
                }

                var tokenNonce = principal.FindFirst("nonce")?.Value;
                if (!string.IsNullOrEmpty(nonce)
                    && !string.Equals(tokenNonce, nonce, StringComparison.Ordinal))
                {
                    _logger.LogWarning("SSO id_token rejected: nonce mismatch for provider {Provider}.", provider);
                    return null;
                }

                var email = principal.FindFirst("email")?.Value
                            ?? principal.FindFirst(ClaimTypes.Email)?.Value
                            ?? principal.FindFirst("preferred_username")?.Value;

                return new OidcIdentity
                {
                    Subject = principal.FindFirst("sub")?.Value
                              ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value,
                    Email = email,
                    Name = principal.FindFirst("name")?.Value ?? email,
                    EmailVerified = ReadEmailVerified(principal, provider),
                    HostedDomain = principal.FindFirst("hd")?.Value,
                    TenantId = principal.FindFirst("tid")?.Value,
                };
            }
            catch (Exception ex)
            {
                // Never surface why a token failed - that detail helps an attacker tune the next
                // attempt and helps a legitimate user not at all.
                _logger.LogWarning(ex, "SSO id_token validation failed for provider {Provider}.", provider);
                return null;
            }
        }

        /// <summary>
        /// Whether the provider vouches for the address.
        /// </summary>
        /// <remarks>
        /// This gates account linking, so it is the difference between SSO and an account
        /// takeover: matching an existing account by an unverified address lets anyone who can get
        /// a token asserting <c>victim@company.com</c> walk into that account.
        ///
        /// Entra does not emit email_verified. An address from a verified tenant domain is
        /// vouched for by the tenant, which is why tenant pinning is required for Microsoft.
        /// </remarks>
        private static bool ReadEmailVerified(ClaimsPrincipal principal, string provider)
        {
            var claim = principal.FindFirst("email_verified")?.Value;
            if (!string.IsNullOrEmpty(claim))
            {
                return string.Equals(claim, "true", StringComparison.OrdinalIgnoreCase);
            }

            return string.Equals(provider, Microsoft, StringComparison.OrdinalIgnoreCase)
                   && !string.IsNullOrEmpty(principal.FindFirst("tid")?.Value);
        }

        private static (string Issuer, string JwksUri) Endpoints(string provider, string tenantId)
        {
            if (string.Equals(provider, Google, StringComparison.OrdinalIgnoreCase))
            {
                return ("https://accounts.google.com", "https://www.googleapis.com/oauth2/v3/certs");
            }

            if (string.Equals(provider, Microsoft, StringComparison.OrdinalIgnoreCase))
            {
                // A concrete tenant, never "common" or "organizations". The issuer must name one
                // directory, or a token from any tenant on earth validates - see MicrosoftAuthority.
                if (string.IsNullOrWhiteSpace(tenantId)) return (null, null);

                return (
                    $"https://login.microsoftonline.com/{tenantId}/v2.0",
                    $"https://login.microsoftonline.com/{tenantId}/discovery/v2.0/keys");
            }

            return (null, null);
        }

        private async Task<IEnumerable<SecurityKey>> GetSigningKeysAsync(string jwksUri, bool refresh)
        {
            var cacheKey = "jwks:" + jwksUri;

            if (!refresh && _cache.TryGetValue(cacheKey, out IEnumerable<SecurityKey> cached))
            {
                return cached;
            }

            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(10);

            var json = await client.GetStringAsync(jwksUri);
            var keys = new JsonWebKeySet(json).GetSigningKeys();

            _cache.Set(cacheKey, keys, KeyCacheFor);
            return keys;
        }
    }
}

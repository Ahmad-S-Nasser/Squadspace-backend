using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;
using RafeeqyNotes.Api.Helpers;
using RafeeqyNotes.Api.Models;
using RafeeqyNotes.Api.Repositories;
using RafeeqyNotes.Api.Services;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Net.Http;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;

namespace RafeeqyNotes.Api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class AuthController : ControllerBase
    {
        private readonly IConfiguration _config;
        private readonly IContributorRepository _contributors;
        private readonly IOrganizationRepository _organizations;
        private readonly IOrganizationInvitationRepository _invitations;
        private readonly IOrganizationSubscriptionRepository _subscriptions;
        private readonly HttpClient _httpClient;
        private readonly INotificationRepository _notifications;
        private readonly IOrgSsoConfigRepository _ssoConfigs;
        private readonly ISsoStateService _ssoState;
        private readonly ISsoTicketService _ssoTickets;
        private readonly ISsoLoginService _ssoLogin;
        private readonly ILogger<AuthController> _logger;

        public AuthController(
            IConfiguration config, 
            IContributorRepository contributors, 
            IOrganizationRepository organizations, 
            IHttpClientFactory httpClientFactory, 
            IOrganizationInvitationRepository invitations,
            IOrganizationSubscriptionRepository subscriptions,
            INotificationRepository notifications,
            IOrgSsoConfigRepository ssoConfigs,
            ISsoStateService ssoState,
            ISsoTicketService ssoTickets,
            ISsoLoginService ssoLogin,
            ILogger<AuthController> logger)
        {
            _config = config;
            _contributors = contributors;
            _organizations = organizations;
            _httpClient = httpClientFactory.CreateClient();
            _invitations = invitations;
            _subscriptions = subscriptions;
            _notifications = notifications;
            _ssoConfigs = ssoConfigs;
            _ssoState = ssoState;
            _ssoTickets = ssoTickets;
            _ssoLogin = ssoLogin;
            _logger = logger;
        }

        // POST /api/auth/signup
        [HttpPost("signup")]
        public async Task<IActionResult> Signup([FromBody] SignupRequest request)
        {
            var existing = await _contributors.GetByEmailAsync(request.Email);
            if (existing != null) return BadRequest("Contributor already exists");

            var contributor = new Contributor
            {
                Email = request.Email,
                Name = request.DisplayName,
                DisplayName = request.DisplayName,
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password),
                Provider = "local",
                SubscriptionExpiry = DateTime.UtcNow.AddMonths(1),
                IsEmailVerified = false,
                EmailVerificationToken = Guid.NewGuid().ToString()
            };
            await _contributors.CreateAsync(contributor);

            // Auto-create organization if name provided
            Organization? createdOrg = null;
            if (!string.IsNullOrWhiteSpace(request.OrganizationName))
            {
                var org = new Organization
                {
                    Name = request.OrganizationName,
                    OwnerId = contributor.Id,
                    Members = new List<OrganizationMember>
                    {
                        new OrganizationMember
                        {
                            Id = Guid.NewGuid().ToString(),
                            UserId = contributor.Id,
                            User = contributor,
                            Role = "owner",
                            JoinedAt = DateTime.UtcNow
                        }
                    }
                };
                createdOrg = _organizations.InsertOrganization(org);

                // Auto-create base subscription for the new organization.
                //
                // "free" replaced "personal": the old plan was one seat for one month, which is
                // not enough people or enough time to evaluate a team product. Free is five
                // seats with no end date, and is capped on the things that cost money to serve
                // (storage, repositories, advanced reporting) rather than on time.
                var subscription = new OrganizationSubscription
                {
                    OrganizationId = createdOrg.Id,
                    PlanId = RafeeqyNotes.Api.Services.EntitlementService.FallbackPlanId,
                    Status = "active",
                    BillingCycle = "monthly",
                    StartDate = DateTime.UtcNow,
                    // Free does not expire. The date is far out rather than null because the
                    // field is non-nullable and several reads assume a real value.
                    EndDate = DateTime.UtcNow.AddYears(100),
                    AutoRenew = false
                };
                await _subscriptions.CreateAsync(subscription);
            }

            var verifyUrl = $"{_config["FrontendUrl"] ?? "https://app.squadspace.net"}/verify-email?token={contributor.EmailVerificationToken}&email={System.Web.HttpUtility.UrlEncode(contributor.Email)}";
            await _notifications.SendEmailNotificationAsync(new NotificationRequest
            {
                Type = "email_verification",
                RecipientEmails = new List<string> { contributor.Email },
                Metadata = new Dictionary<string, string> { { "VerificationUrl", verifyUrl } }
            });

            // No JWT is issued here — the user must verify their email first.
            return Ok(new { message = "Registration successful. Please check your email to verify your account.", contributor = ContributorDto.From(contributor), organization = createdOrg });
        }

        // POST /api/auth/login
        [HttpPost("login")]
        public async Task<IActionResult> Login([FromBody] LoginRequest request)
        {
            var contributor = await _contributors.GetByEmailAsync(request.Email);
            if (contributor == null || !BCrypt.Net.BCrypt.Verify(request.Password, contributor.PasswordHash))
                return Unauthorized("Invalid credentials.");

            if (!contributor.IsEmailVerified)
                return BadRequest("Please verify your email address to log in. Check your inbox.");

            // An organization that enforces SSO must not be reachable with a local password -
            // otherwise revoking someone's directory account leaves a way in, which is the main
            // thing a security team buys SSO for.
            //
            // Checked AFTER the password verifies, deliberately: answering earlier would let
            // anyone probe which domains enforce SSO, and which addresses exist, without
            // credentials.
            var enforced = await _ssoConfigs.FindByEmailDomainAsync(DomainOf(contributor.Email));
            if (enforced is { Enabled: true, EnforceSso: true })
            {
                return StatusCode(StatusCodes.Status403Forbidden, new
                {
                    message = "Your organization requires single sign-on.",
                    ssoRequired = true,
                    provider = enforced.Provider,
                    organizationId = enforced.OrganizationId,
                });
            }

            contributor.Token = GenerateJwtToken(contributor.Id, contributor.Email, contributor.Role);
            
            var organizations = _organizations.FetchOrganizationsByUserId(contributor.Id);

            // AuthenticatedContributorDto keeps the token at `contributor.token`, which is
            // where both frontends read it from.
            return Ok(new { contributor = AuthenticatedContributorDto.WithToken(contributor), organizations });
        }

        // POST /api/auth/invite-signup
        [HttpPost("invite-signup")]
        public async Task<IActionResult> InviteSignup([FromBody] InviteSignupRequest request)
        {
            var invitation = _invitations.GetInvitationByToken(request.Token);
            if (invitation == null || invitation.IsUsed || invitation.ExpiresAt < DateTime.UtcNow)
                return BadRequest("Invalid or expired invitation token.");

            if (!invitation.Email.Equals(request.Email, StringComparison.OrdinalIgnoreCase))
                return BadRequest("Email does not match the invitation.");

            var existing = await _contributors.GetByEmailAsync(request.Email);
            if (existing != null) return BadRequest("Contributor already exists.");

            var contributor = new Contributor
            {
                Email = request.Email,
                Name = request.Name,
                DisplayName = request.Name,
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password),
                Provider = "local",
                SubscriptionExpiry = DateTime.UtcNow.AddMonths(1),
                IsFirstLogin = true
            };
            await _contributors.CreateAsync(contributor);

            var orgMember = new OrganizationMember
            {
                Id = Guid.NewGuid().ToString(),
                UserId = contributor.Id,
                User = contributor,
                Role = invitation.Role,
                JoinedAt = DateTime.UtcNow
            };
            _organizations.AddMember(invitation.OrganizationId, orgMember);

            _invitations.MarkInvitationAsUsed(invitation.Id);

            var userOrganizations = _organizations.FetchOrganizationsByUserId(contributor.Id);
            var token = GenerateJwtToken(contributor.Id, contributor.Email, contributor.Role);
            contributor.Token = token;

            return Ok(new { message = "Contributor created and joined organization successfully", token, contributor = AuthenticatedContributorDto.WithToken(contributor), organizations = userOrganizations });
        }

        // GET /api/auth/accept-invitation
        [HttpGet("accept-invitation")]
        public async Task<IActionResult> AcceptInvitation([FromQuery] string token)
        {
            var invitation = _invitations.GetInvitationByToken(token);
            if (invitation == null || invitation.IsUsed || invitation.ExpiresAt < DateTime.UtcNow)
            {
                if (Request.Headers["Accept"].ToString().Contains("application/json"))
                {
                    return BadRequest(new { message = "Invalid or expired invitation token." });
                }
                var frontendUrlFail = _config["FrontendUrl"] ?? "https://app.squadspace.net";
                return Redirect($"{frontendUrlFail}/auth?error=invalid_invitation");
            }

            var contributor = await _contributors.GetByEmailAsync(invitation.Email);
            if (contributor == null)
                return NotFound("User associated with this invitation not found.");

            // Added now: Actually add user to organization when they accept
            var orgMembers = _organizations.GetMembers(invitation.OrganizationId);
            if (!orgMembers.Any(m => m.UserId == contributor.Id))
            {
                _organizations.AddMember(invitation.OrganizationId, new OrganizationMember
                {
                    Id = Guid.NewGuid().ToString(),
                    UserId = contributor.Id,
                    User = contributor,
                    Role = invitation.Role ?? "viewer",
                    JoinedAt = DateTime.UtcNow
                });
            }

            // Mark invitation as used
            _invitations.MarkInvitationAsUsed(invitation.Id);

            // Generate JWT and redirect to frontend
            var jwt = GenerateJwtToken(contributor.Id, contributor.Email, contributor.Role);
            var frontendUrl = _config["FrontendUrl"] ?? "https://app.squadspace.net";
            
            // Append token and user info to URL so frontend can log them in
            var redirectUrl = $"{frontendUrl}/dashboard?token={jwt}&userId={contributor.Id}&email={System.Uri.EscapeDataString(contributor.Email)}&name={System.Uri.EscapeDataString(contributor.DisplayName ?? contributor.Name)}";
            
            if (contributor.IsFirstLogin)
            {
                redirectUrl += "&isFirstLogin=true";
            }

            if (Request.Headers["Accept"].ToString().Contains("application/json"))
            {
                return Ok(new
                {
                    token = jwt,
                    userId = contributor.Id,
                    email = contributor.Email,
                    name = contributor.DisplayName ?? contributor.Name,
                    isFirstLogin = contributor.IsFirstLogin
                });
            }

            return Redirect(redirectUrl);
        }

        // GET /api/auth/verify-email
        [HttpGet("verify-email")]
        public async Task<IActionResult> VerifyEmail([FromQuery] string token, [FromQuery] string email)
        {
            var contributor = await _contributors.GetByEmailAsync(email);
            if (contributor == null || contributor.EmailVerificationToken != token)
                return BadRequest("Invalid or expired verification link.");

            contributor.IsEmailVerified = true;
            contributor.EmailVerificationToken = null;
            await _contributors.UpdateAsync(contributor);

            var jwt = GenerateJwtToken(contributor.Id, contributor.Email, contributor.Role);
            contributor.Token = jwt;
            return Ok(new { message = "Email verified successfully!", token = jwt, contributor = AuthenticatedContributorDto.WithToken(contributor) });
        }

        // POST /api/auth/change-password
        /// <summary>
        /// Changes the caller's own password.
        /// </summary>
        /// <remarks>
        /// This was anonymous and took <c>userId</c> from the request body, so anyone could
        /// set any account's password — a one-request account takeover. The id now comes from
        /// the JWT, and the current password must be supplied.
        ///
        /// The exception is the first-login flow: an invited user is created with a random
        /// password they were never told, so <c>IsFirstLogin</c> accounts may set a password
        /// without proving the old one. They are still authenticated as themselves.
        /// </remarks>
        [Authorize]
        [HttpPost("change-password")]
        public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.newPassword))
                return BadRequest("A new password is required.");

            var callerId = OrgAccess.UserId(User);
            if (string.IsNullOrEmpty(callerId)) return Unauthorized(new { message = "Authentication required" });

            var contributor = await _contributors.GetByIdAsync(callerId);
            if (contributor == null) return NotFound("User not found");

            if (!contributor.IsFirstLogin)
            {
                if (string.IsNullOrWhiteSpace(request.currentPassword) ||
                    string.IsNullOrEmpty(contributor.PasswordHash) ||
                    !BCrypt.Net.BCrypt.Verify(request.currentPassword, contributor.PasswordHash))
                {
                    return BadRequest("Current password is incorrect.");
                }
            }

            contributor.PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.newPassword);
            contributor.Provider = "local";
            contributor.IsFirstLogin = false;
            contributor.Token = GenerateJwtToken(contributor.Id, contributor.Email, contributor.Role);
            await _contributors.UpdateAsync(contributor);
            return Ok(new { contributor = AuthenticatedContributorDto.WithToken(contributor) });
        }

        // POST /api/auth/profile
        /// <summary>
        /// Updates the caller's own profile.
        /// </summary>
        /// <remarks>
        /// This was anonymous and took <c>userId</c> from the body, so anyone could rewrite
        /// any user's display name and — critically — their <c>Email</c>, which combined with
        /// password reset was a second takeover route. The id now comes from the JWT.
        ///
        /// Email changes are ignored here: changing the address of a verified account needs a
        /// re-verification flow, which does not exist yet. Silently keeping the old address is
        /// the safe behaviour until it does.
        /// </remarks>
        [Authorize]
        [HttpPut("profile")]
        public async Task<IActionResult> UpdateProfile([FromBody] UpdateProfileRequest request)
        {
            if (request == null) return BadRequest("Request body is required.");

            var callerId = OrgAccess.UserId(User);
            if (string.IsNullOrEmpty(callerId)) return Unauthorized(new { message = "Authentication required" });

            var contributor = await _contributors.GetByIdAsync(callerId);
            if (contributor == null) return NotFound("User not found");

            contributor.DisplayName = request.name;
            if (request.theme != null) {
                contributor.Theme = request.theme;
            }
            if (request.status != null) {
                contributor.Status = request.status;
            }
            if (request.accentColor != null) {
                contributor.AccentColor = request.accentColor;
            }
            if (request.showBadges != null) {
                contributor.ShowBadges = request.showBadges.Value;
            }
            if (request.showAuraRings != null) {
                contributor.ShowAuraRings = request.showAuraRings.Value;
            }
            if (request.showMoodTags != null) {
                contributor.ShowMoodTags = request.showMoodTags.Value;
            }
            if (request.animateTransitions != null) {
                contributor.AnimateTransitions = request.animateTransitions.Value;
            }
            
            await _contributors.UpdateAsync(contributor);

            // Returned with the existing token so the client's stored user object keeps its
            // `contributor.token` field; no new token is issued here.
            return Ok(new { contributor = AuthenticatedContributorDto.WithToken(contributor) });
        }
        
        // ---------------------------------------------------------------- SSO
        //
        // Rewritten. The previous Google and Microsoft flows shared every classic OAuth flaw:
        // no state (login CSRF), no nonce (id_token replay), no id_token validation at all, the
        // Microsoft /common/ authority (any tenant, any personal account), account linking on an
        // unverified address, mutation of a shared HttpClient's default headers, and the session
        // JWT handed back in a URL query string.

        /// <summary>Starts a sign-in. `orgId` selects an organization's SSO configuration.</summary>
        [HttpGet("google")]
        public Task<IActionResult> GoogleAuth([FromQuery] string orgId = null, [FromQuery] string returnUrl = null) =>
            StartSsoAsync(OidcTokenValidator.Google, orgId, returnUrl);

        [HttpGet("microsoft")]
        public Task<IActionResult> MicrosoftAuth([FromQuery] string orgId = null, [FromQuery] string returnUrl = null) =>
            StartSsoAsync(OidcTokenValidator.Microsoft, orgId, returnUrl);

        [HttpGet("google/callback")]
        public Task<IActionResult> GoogleCallback([FromQuery] string code, [FromQuery] string state) =>
            CompleteSsoAsync(OidcTokenValidator.Google, code, state);

        [HttpGet("microsoft/callback")]
        public Task<IActionResult> MicrosoftCallback([FromQuery] string code, [FromQuery] string state) =>
            CompleteSsoAsync(OidcTokenValidator.Microsoft, code, state);

        /// <summary>
        /// Which provider an email domain should sign in with, so the app can route to SSO.
        /// </summary>
        /// <remarks>
        /// Deliberately says nothing about whether the ACCOUNT exists - only whether the domain is
        /// configured for SSO. Answering the former turns a login box into a way to enumerate
        /// which addresses hold accounts.
        /// </remarks>
        [HttpGet("sso/discover")]
        [AllowAnonymous]
        public async Task<IActionResult> DiscoverSso([FromQuery] string email)
        {
            var domain = (email ?? string.Empty).Trim().ToLowerInvariant();
            domain = domain.Contains('@') ? domain.Split('@').Last() : domain;

            var config = await _ssoConfigs.FindByEmailDomainAsync(domain);
            if (config == null || !config.Enabled) return Ok(new { ssoRequired = false });

            return Ok(new
            {
                ssoRequired = true,
                provider = config.Provider,
                organizationId = config.OrganizationId,
                enforced = config.EnforceSso,
            });
        }

        /// <summary>
        /// Exchanges the one-time code from the callback for the session token.
        /// </summary>
        /// <remarks>
        /// The callback redirects with an opaque short-lived code instead of the JWT itself. A
        /// token in a URL leaks into browser history, the Referer of every asset the landing page
        /// loads, analytics, and proxy logs - and it stays valid for 30 days. This way the token
        /// only travels in a response body over TLS.
        /// </remarks>
        [HttpPost("sso/exchange")]
        [AllowAnonymous]
        public IActionResult ExchangeSsoTicket([FromBody] SsoExchangeRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.Code))
            {
                return BadRequest(new { message = "A code is required." });
            }

            if (!_ssoTickets.TryConsume(request.Code, out var jwt))
            {
                // Single-use and short-lived: a replayed or stale code is simply invalid.
                return Unauthorized(new { message = "That sign-in link has already been used or has expired." });
            }

            return Ok(new { token = jwt });
        }

        private async Task<IActionResult> StartSsoAsync(string provider, string orgId, string returnUrl)
        {
            var isGoogle = string.Equals(provider, OidcTokenValidator.Google, StringComparison.OrdinalIgnoreCase);

            var clientId = _config[isGoogle ? "Google:ClientId" : "Microsoft:ClientId"];
            var redirectUri = _config[isGoogle ? "Google:RedirectUri" : "Microsoft:RedirectUri"];

            if (string.IsNullOrWhiteSpace(clientId))
            {
                return BadRequest(new { message = "Single sign-on is not configured." });
            }

            OrgSsoConfig ssoConfig = null;
            if (!string.IsNullOrWhiteSpace(orgId))
            {
                ssoConfig = await _ssoConfigs.GetByOrganizationIdAsync(orgId);

                if (ssoConfig is { Enabled: false }) ssoConfig = null;

                if (ssoConfig != null
                    && !string.Equals(ssoConfig.Provider, provider, StringComparison.OrdinalIgnoreCase))
                {
                    return BadRequest(new { message = "That organization uses a different sign-in provider." });
                }
            }

            var (state, nonce) = _ssoState.Issue(provider, ssoConfig?.OrganizationId, SafeReturnUrl(returnUrl));

            return Redirect(_ssoLogin.BuildAuthorizeUrl(provider, clientId, redirectUri, state, nonce, ssoConfig));
        }

        private async Task<IActionResult> CompleteSsoAsync(string provider, string code, string state)
        {
            var frontendUrl = _config["FrontendUrl"] ?? "https://app.squadspace.net";

            if (string.IsNullOrWhiteSpace(code) || !_ssoState.TryConsume(state, out var pending))
            {
                // An absent, unknown, expired or already-used state is the login-CSRF case. It is
                // refused without saying which, because the distinction only helps an attacker.
                return Redirect($"{frontendUrl}/auth?error=sso_state");
            }

            if (!string.Equals(pending.Provider, provider, StringComparison.Ordinal))
            {
                return Redirect($"{frontendUrl}/auth?error=sso_state");
            }

            OrgSsoConfig ssoConfig = null;
            if (!string.IsNullOrWhiteSpace(pending.OrganizationId))
            {
                ssoConfig = await _ssoConfigs.GetByOrganizationIdAsync(pending.OrganizationId);
            }

            var result = await _ssoLogin.CompleteAsync(provider, code, pending, ssoConfig);
            if (!result.Success)
            {
                return Redirect($"{frontendUrl}/auth?error=sso_denied");
            }

            if (ssoConfig is { AutoProvisionUsers: true })
            {
                await EnsureOrganizationMembershipAsync(result.Contributor, ssoConfig);
            }

            var jwt = GenerateJwtToken(result.Contributor.Id, result.Contributor.Email, result.Contributor.Role);
            var ticket = _ssoTickets.Issue(jwt);

            // A one-time code, never the token. See ExchangeSsoTicket.
            var target = string.IsNullOrWhiteSpace(pending.ReturnUrl) ? "/auth" : pending.ReturnUrl;
            return Redirect($"{frontendUrl}{target}?sso_code={Uri.EscapeDataString(ticket)}");
        }

        /// <summary>
        /// Adds a freshly authenticated user to the organization, if they are not already in it.
        /// </summary>
        /// <remarks>
        /// Just-in-time provisioning, always at the configured default role and never above
        /// member level - a directory login proves who someone is, not what they should be allowed
        /// to do here. Existing members keep whatever role they already have.
        /// </remarks>
        private async Task EnsureOrganizationMembershipAsync(Contributor contributor, OrgSsoConfig config)
        {
            try
            {
                var org = _organizations.FetchOrganizationById(config.OrganizationId);
                if (org == null) return;

                org.Members ??= new List<OrganizationMember>();
                if (org.Members.Any(m => string.Equals(m.UserId, contributor.Id, StringComparison.Ordinal)))
                {
                    return;
                }

                var role = config.DefaultRole;
                if (string.IsNullOrWhiteSpace(role)
                    || OrgAccess.Managers.Contains(role, StringComparer.OrdinalIgnoreCase))
                {
                    role = OrgAccess.Member;
                }

                org.Members.Add(new OrganizationMember
                {
                    Id = Guid.NewGuid().ToString(),
                    UserId = contributor.Id,
                    User = contributor,
                    Role = role,
                    JoinedAt = DateTime.UtcNow,
                });

                _organizations.UpdateOrganization(org.Id, org);
            }
            catch (Exception ex)
            {
                // A provisioning failure must not block a valid sign-in; they land without the
                // org and an admin can add them.
                _logger?.LogWarning(ex, "SSO membership provisioning failed for {OrganizationId}.",
                    config.OrganizationId);
            }
        }

        /// <summary>
        /// Keeps returnUrl to a path within the app.
        /// </summary>
        /// <remarks>
        /// An absolute URL here is an open redirect: the login flow would happily bounce a user to
        /// an attacker's page carrying whatever the callback appended. Only a rooted path is
        /// accepted, and "//evil.com" is rejected because browsers read it as protocol-relative.
        /// </remarks>
        private static string DomainOf(string email)
        {
            var value = (email ?? string.Empty).Trim().ToLowerInvariant();
            return value.Contains('@') ? value.Split('@').Last() : string.Empty;
        }

        private static string SafeReturnUrl(string returnUrl)
        {
            if (string.IsNullOrWhiteSpace(returnUrl)) return null;
            if (!returnUrl.StartsWith("/", StringComparison.Ordinal)) return null;
            if (returnUrl.StartsWith("//", StringComparison.Ordinal)) return null;
            return returnUrl;
        }

        [HttpPost("forgot-password")]
        public async Task<IActionResult> ForgotPassword([FromBody] ForgotPasswordRequest request)
        {
            var contributor = await _contributors.GetByEmailAsync(request.Email);
            if (contributor == null) return NotFound("User with this email not found.");

            var token = Guid.NewGuid().ToString("N").Substring(0, 6).ToUpper(); // Short secure token
            contributor.PasswordResetToken = token;
            contributor.PasswordResetTokenExpiry = DateTime.UtcNow.AddHours(1);
            await _contributors.UpdateAsync(contributor);

            var frontendUrl = _config["FrontendUrl"] ?? "https://app.squadspace.net";
            var resetUrl = $"{frontendUrl}/reset-password?token={token}";

            await _notifications.SendEmailNotificationAsync(new NotificationRequest
            {
                Type = "password_reset",
                RecipientEmails = new List<string> { contributor.Email },
                Metadata = new Dictionary<string, string> { { "ResetUrl", resetUrl } }
            });

            return Ok(new { message = "Password reset link sent to your email." });
        }

        [HttpPost("reset-password")]
        public async Task<IActionResult> ResetPassword([FromBody] ResetPasswordRequest request)
        {
            // Find contributor by token
            var contributors = await _contributors.GetAllAsync();
            var contributor = contributors.FirstOrDefault(c => c.PasswordResetToken == request.Token && c.PasswordResetTokenExpiry > DateTime.UtcNow);

            if (contributor == null)
            {
                return BadRequest("Invalid or expired reset token.");
            }

            contributor.PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.NewPassword);
            contributor.PasswordResetToken = null;
            contributor.PasswordResetTokenExpiry = null;
            contributor.Provider = "local";
            
            await _contributors.UpdateAsync(contributor);

            return Ok(new { message = "Password reset successful. You can now log in with your new password." });
        }

        private string GenerateJwtToken(string id, string email, string role)
        {
            var claims = new[]
            {
                new Claim(ClaimTypes.NameIdentifier, id),
                new Claim(ClaimTypes.Name, email),
                new Claim(ClaimTypes.Email, email),
                new Claim(ClaimTypes.Role, role)
            };

            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_config["JwtSettings:Key"]));
            var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

            var token = new JwtSecurityToken(
                issuer: _config["JwtSettings:Issuer"],
                audience: _config["JwtSettings:Audience"],
                claims: claims,
                expires: DateTime.UtcNow.AddDays(30),
                signingCredentials: creds);

            return new JwtSecurityTokenHandler().WriteToken(token);
        }
    }

    public record LoginRequest(string Email, string Password);
    // `userId` is retained so existing clients don't 400 on an unknown field, but it is
    // IGNORED — the account changed is always the JWT subject.
    public record ChangePasswordRequest(string userId, string newPassword, string? currentPassword);
    public record UpdateProfileRequest(
        string userId, 
        string name, 
        string email, 
        string? theme, 
        string? status,
        string? accentColor,
        bool? showBadges,
        bool? showAuraRings,
        bool? showMoodTags,
        bool? animateTransitions
    );
    public record SignupRequest(string Email, string Password, string DisplayName, string? OrganizationName);
    public record InviteSignupRequest(string Email, string Password, string Name, string Token);
    public record ForgotPasswordRequest(string Email);
    public record ResetPasswordRequest(string Token, string NewPassword);
}
namespace RafeeqyNotes.Api.Controllers
{
    /// <summary>Body of the one-time code exchange.</summary>
    public class SsoExchangeRequest
    {
        public string Code { get; set; }
    }
}

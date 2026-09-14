using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RafeeqyNotes.Api.Helpers;
using RafeeqyNotes.Api.Models;
using RafeeqyNotes.Api.Repositories;
using RafeeqyNotes.Api.Services;

namespace RafeeqyNotes.Api.Controllers
{
    /// <summary>
    /// An organization's single sign-on settings.
    /// </summary>
    /// <remarks>
    /// MANAGERS ONLY, not any member. This decides who can get into the organization and whether
    /// passwords still work - a member who could point SSO at a directory they control would be
    /// granting themselves a way to admit anyone.
    ///
    /// Gated on the `sso` entitlement, which sits inside the Enterprise tier rather than being
    /// sold as an add-on. Charging separately for a security control prices it away from the teams
    /// most likely to need it, and enterprise buyers read it as a toll.
    /// </remarks>
    [Route("api/organization/{orgId}/sso")]
    [ApiController]
    [Authorize]
    public class SsoConfigController : ControllerBase
    {
        private static readonly string[] SupportedProviders =
        {
            OidcTokenValidator.Google,
            OidcTokenValidator.Microsoft,
        };

        private readonly IOrgSsoConfigRepository _configs;
        private readonly IOrganizationRepository _organizations;
        private readonly IEntitlementService _entitlements;

        public SsoConfigController(
            IOrgSsoConfigRepository configs,
            IOrganizationRepository organizations,
            IEntitlementService entitlements)
        {
            _configs = configs;
            _organizations = organizations;
            _entitlements = entitlements;
        }

        /// <summary>The organization's SSO settings. Never returns a secret.</summary>
        [HttpGet]
        public async Task<IActionResult> Get(string orgId)
        {
            var auth = this.AuthorizeOrg(_organizations, orgId, OrgAccess.Managers);
            if (!auth.Allowed) return auth.Error;

            var config = await _configs.GetByOrganizationIdAsync(orgId);

            return Ok(new
            {
                configured = config != null,
                enabled = config?.Enabled ?? false,
                provider = config?.Provider,
                microsoftTenantId = config?.MicrosoftTenantId,
                googleHostedDomain = config?.GoogleHostedDomain,
                allowedEmailDomains = config?.AllowedEmailDomains ?? new List<string>(),
                autoProvisionUsers = config?.AutoProvisionUsers ?? true,
                defaultRole = config?.DefaultRole ?? OrgAccess.Member,
                enforceSso = config?.EnforceSso ?? false,
                supportedProviders = SupportedProviders,
            });
        }

        [HttpPut]
        public async Task<IActionResult> Update(string orgId, [FromBody] OrgSsoConfig request)
        {
            var auth = this.AuthorizeOrg(_organizations, orgId, OrgAccess.Managers);
            if (!auth.Allowed) return auth.Error;

            var entitlements = await _entitlements.ForOrganizationAsync(orgId);
            var allowed = this.RequireEntitlement(entitlements, Entitlement.Sso);
            if (!allowed.Allowed) return allowed.Error;

            if (request == null) return BadRequest(new { message = "A configuration is required." });

            var invalid = Validate(request);
            if (invalid != null) return BadRequest(new { message = invalid });

            var existing = await _configs.GetByOrganizationIdAsync(orgId) ?? new OrgSsoConfig();

            // Tenancy from the route, which the guard checked - never from the body.
            existing.OrganizationId = orgId;
            existing.Provider = request.Provider.Trim().ToLowerInvariant();
            existing.Enabled = request.Enabled;
            existing.MicrosoftTenantId = request.MicrosoftTenantId?.Trim();
            existing.GoogleHostedDomain = request.GoogleHostedDomain?.Trim().ToLowerInvariant();
            existing.AllowedEmailDomains = (request.AllowedEmailDomains ?? new List<string>())
                .Select(d => d?.Trim().TrimStart('@').ToLowerInvariant())
                .Where(d => !string.IsNullOrWhiteSpace(d))
                .Distinct()
                .ToList();
            existing.AutoProvisionUsers = request.AutoProvisionUsers;
            existing.EnforceSso = request.EnforceSso;
            existing.UpdatedBy = auth.UserId;

            // Auto-provisioning never grants a management role. A directory login proves identity,
            // not entitlement to administer this organization.
            var role = request.DefaultRole?.Trim().ToLowerInvariant();
            existing.DefaultRole = OrgAccess.Managers.Contains(role ?? string.Empty) || string.IsNullOrWhiteSpace(role)
                ? OrgAccess.Member
                : role;

            await _configs.UpsertAsync(existing);
            return Ok(new { saved = true });
        }

        [HttpDelete]
        public async Task<IActionResult> Delete(string orgId)
        {
            var auth = this.AuthorizeOrg(_organizations, orgId, OrgAccess.Managers);
            if (!auth.Allowed) return auth.Error;

            await _configs.DeleteAsync(orgId);
            return NoContent();
        }

        /// <summary>
        /// Rejects a configuration that would not actually pin anything.
        /// </summary>
        /// <remarks>
        /// The important one is the Microsoft tenant. Without it the flow falls back to a
        /// multi-tenant authority and "SSO" admits any Microsoft account in the world - which is
        /// worse than no SSO, because the organization believes it is protected.
        /// </remarks>
        private static string Validate(OrgSsoConfig config)
        {
            var provider = config.Provider?.Trim().ToLowerInvariant();

            if (!SupportedProviders.Contains(provider))
            {
                return "Provider must be 'google' or 'microsoft'.";
            }

            if (provider == OidcTokenValidator.Microsoft && config.Enabled
                && string.IsNullOrWhiteSpace(config.MicrosoftTenantId))
            {
                return "A Microsoft tenant ID is required — without it any Microsoft account could sign in.";
            }

            if (provider == OidcTokenValidator.Google && config.Enabled
                && string.IsNullOrWhiteSpace(config.GoogleHostedDomain)
                && (config.AllowedEmailDomains == null || config.AllowedEmailDomains.Count == 0))
            {
                return "A Google Workspace domain (or at least one allowed email domain) is required.";
            }

            return null;
        }
    }
}

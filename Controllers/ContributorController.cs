using Azure.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.Tokens;
using RafeeqyNotes.Api.Helpers;
using RafeeqyNotes.Api.Models;
using RafeeqyNotes.Api.Repositories;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;

namespace RafeeqyNotes.Api.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Microsoft.AspNetCore.Authorization.Authorize]
    public class ContributorController : ControllerBase
    {
        private readonly IContributorRepository _repo;
        private readonly IConfiguration _config;
        private readonly IOrganizationRepository _organizations;

        public ContributorController(
            IConfiguration config,
            IContributorRepository repo,
            IOrganizationRepository organizations)
        {
            _repo = repo;
            _config = config;
            _organizations = organizations;
        }

        /// <remarks>
        /// Restricted to yourself, people you share an organization with, and platform admins.
        /// It previously returned any user's profile to any caller who could name an id.
        /// </remarks>
        [HttpGet("{id}")]
        public async Task<ActionResult<ContributorDto>> GetContributorByID(string id)
        {
            var callerId = OrgAccess.UserId(User);
            if (string.IsNullOrEmpty(callerId)) return Unauthorized();

            var contributor = await _repo.GetByIdAsync(id);
            if (contributor == null) return NotFound();

            var isSelf = callerId.Equals(id, StringComparison.OrdinalIgnoreCase);
            if (!isSelf && !OrgAccess.IsPlatformAdmin(this) && !SharesAnOrganizationWith(callerId, id))
            {
                // Same 404 as "no such user", so this cannot be used to probe for ids.
                return NotFound();
            }

            return Ok(ContributorDto.From(contributor));
        }

        /// <summary>True when both users appear in at least one organization together.</summary>
        private bool SharesAnOrganizationWith(string callerId, string otherId)
        {
            var orgs = _organizations.FetchOrganizationsByUserId(callerId) ?? new List<Organization>();
            foreach (var org in orgs)
            {
                if (!string.IsNullOrEmpty(org?.OwnerId) &&
                    org.OwnerId.Equals(otherId, StringComparison.OrdinalIgnoreCase)) return true;

                if (org?.Members?.Any(m => !string.IsNullOrEmpty(m?.UserId) &&
                        m.UserId.Equals(otherId, StringComparison.OrdinalIgnoreCase)) == true) return true;
            }
            return false;
        }

        /// <summary>
        /// Directory of users. Returns the safe projection only.
        /// </summary>
        /// <remarks>
        /// This endpoint was anonymous and returned raw <see cref="Contributor"/> entities,
        /// i.e. every user's password hash, GitHub access + refresh tokens, password-reset
        /// token and email-verification token. That made a full account takeover possible
        /// with no email access: request a password reset, read the victim's reset token
        /// here, then redeem it.
        /// </remarks>
        /// <remarks>
        /// Scoped to people the caller actually shares an organization with. It previously
        /// returned EVERY user in the system — name and email — to any authenticated caller,
        /// which is a cross-tenant disclosure of personal data even though the credential
        /// fields were already stripped by <see cref="ContributorDto"/>.
        ///
        /// The legitimate use is populating assignee and member pickers, and those only ever
        /// need people from the caller's own organizations.
        /// </remarks>
        [HttpGet("")]
        public async Task<ActionResult<List<ContributorDto>>> GetAllContributors()
        {
            var userId = OrgAccess.UserId(User);
            if (string.IsNullOrEmpty(userId)) return Unauthorized();

            // Platform admins keep the full directory - it is an explicit allowlist.
            if (OrgAccess.IsPlatformAdmin(this))
            {
                var all = await _repo.GetAllAsync();
                return Ok(ContributorDto.From(all ?? new List<Contributor>()));
            }

            var orgs = _organizations.FetchOrganizationsByUserId(userId) ?? new List<Organization>();

            var visibleIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var org in orgs)
            {
                if (!string.IsNullOrEmpty(org?.OwnerId)) visibleIds.Add(org.OwnerId);
                foreach (var m in org?.Members ?? new List<OrganizationMember>())
                {
                    if (!string.IsNullOrEmpty(m?.UserId)) visibleIds.Add(m.UserId);
                }
            }

            // Always include the caller, so a user with no organization still resolves.
            visibleIds.Add(userId);

            var contributors = await _repo.GetAllAsync() ?? new List<Contributor>();
            var scoped = contributors.Where(c => c != null && visibleIds.Contains(c.Id)).ToList();

            return Ok(ContributorDto.From(scoped));
        }

        [HttpGet("with-roles")]
        public async Task<ActionResult<List<ContributorDto>>> GetAllContributorsWithRoles()
        {
            if (!OrgAccess.IsPlatformAdmin(this)) return PlatformAdminRequired();

            var contributors = await _repo.GetAllWithRoleAsync();
            if (contributors == null) return NotFound();
            return Ok(ContributorDto.From(contributors));
        }

        [HttpPost]
        public async Task<ActionResult<ContributorDto>> Create(Contributor contributor)
        {
            if (!OrgAccess.IsPlatformAdmin(this)) return PlatformAdminRequired();
            if (contributor == null) return BadRequest(new { message = "Contributor payload is required" });

            contributor.PasswordHash = BCrypt.Net.BCrypt.HashPassword(GenerateRandomPassword(8));
            contributor.Provider = "local";

            await _repo.CreateAsync(contributor);
            return CreatedAtAction(nameof(GetContributorByID), new { id = contributor.Id },
                ContributorDto.From(contributor));
        }

        private string GenerateJwtToken(string id, string email)
        {
            var claims = new[]
            {
                new Claim(ClaimTypes.NameIdentifier, id),
                new Claim(ClaimTypes.Name, email),
                new Claim(ClaimTypes.Email, email),
                new Claim(ClaimTypes.Role, "Contributor")
            };

            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_config["JwtSettings:Key"]));
            var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

            var token = new JwtSecurityToken(
                issuer: _config["JwtSettings:Issuer"],
                audience: _config["JwtSettings:Audience"],
                claims: claims,
                expires: DateTime.UtcNow.AddHours(1),
                signingCredentials: creds);

            return new JwtSecurityTokenHandler().WriteToken(token);
        }

        /// <summary>Deletes an account. Platform admins only — this was anonymous.</summary>
        [HttpDelete("{id}")]
        public async Task<ActionResult> Delete(string id)
        {
            if (!OrgAccess.IsPlatformAdmin(this)) return PlatformAdminRequired();

            await _repo.DeleteAsync(id);
            return NoContent();
        }

        /// <summary>
        /// Sets a user's platform role. Platform admins only.
        /// </summary>
        /// <remarks>
        /// This was anonymous, so anyone could set any account's role — including their own —
        /// to "Admin", which was the only value gating the one role-protected endpoint.
        /// Note that platform-admin authority now comes from the
        /// <c>Auth:PlatformAdminUserIds</c> allowlist, not from this field, so writing
        /// "Admin" here no longer confers cross-tenant access.
        /// </remarks>
        [HttpPut("{id}/role")]
        public async Task<ActionResult> UpdateRole(string id, [FromBody] RoleUpdateRequest request)
        {
            if (!OrgAccess.IsPlatformAdmin(this)) return PlatformAdminRequired();
            if (request == null || string.IsNullOrWhiteSpace(request.Role))
            {
                return BadRequest(new { message = "Role is required" });
            }

            var contributor = await _repo.GetByIdAsync(id);
            if (contributor == null) return NotFound();

            contributor.Role = request.Role;
            await _repo.UpdateAsync(contributor);
            return Ok(new { message = "Role updated successfully" });
        }

        private ActionResult PlatformAdminRequired() => StatusCode(403, new
        {
            message = "Insufficient permissions",
            detail = "This action requires a platform administrator."
        });

        public record RoleUpdateRequest(string Role);

        public static string GenerateRandomPassword(int length)
        {
            const string validChars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ1234567890!@#$%^&*()";
            char[] password = new char[length];
            using (var rng = RandomNumberGenerator.Create())
            {
                byte[] randomBytes = new byte[length];
                rng.GetBytes(randomBytes);
                for (int i = 0; i < length; i++)
                {
                    password[i] = validChars[randomBytes[i] % validChars.Length];
                }
            }
            return new string(password);
        }
    }
}

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using RafeeqyNotes.Api.Helpers;
using RafeeqyNotes.Api.Models;
using RafeeqyNotes.Api.Repositories;
using RafeeqyNotes.Api.Services;

namespace RafeeqyNotes.Api.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class OrganizationController : ControllerBase
    {
        private readonly IOrganizationRepository _repo;
        private readonly IContributorRepository _contributors;
        private readonly IOrganizationInvitationRepository _invitations;
        private readonly IOrganizationSubscriptionRepository _subscriptions;
        private readonly INotificationRepository _notificationRepo;
        private readonly IEntitlementService _entitlements;
        private readonly ITaskStatusService _taskStatuses;
        private readonly IOrgWorkspaceConfigRepository _workspaceConfig;

        public OrganizationController(
            IOrganizationRepository repo, 
            IContributorRepository contributors, 
            IOrganizationInvitationRepository invitations,
            IOrganizationSubscriptionRepository subscriptions,
            INotificationRepository notificationRepo,
            IEntitlementService entitlements,
            ITaskStatusService taskStatuses,
            IOrgWorkspaceConfigRepository workspaceConfig)
        {
            _repo = repo;
            _contributors = contributors;
            _invitations = invitations;
            _subscriptions = subscriptions;
            _notificationRepo = notificationRepo;
            _entitlements = entitlements;
            _taskStatuses = taskStatuses;
            _workspaceConfig = workspaceConfig;
        }

        /// <summary>The organization's task-status workflow.</summary>
        /// <remarks>
        /// A separate endpoint rather than a field on the organization payload, for two
        /// reasons. It has a different cache lifetime and a different invalidation trigger.
        /// And decisively, the Organization object is embedded in every Project, which is
        /// embedded in every Board, Note and Task - hanging configuration off it would copy
        /// that configuration across the whole database.
        ///
        /// Seeded on first read, so an organization that has never been asked about still
        /// answers with the standard five.
        /// </remarks>
        [HttpGet("{id}/task-statuses")]
        public async Task<IActionResult> GetTaskStatuses(string id)
        {
            var auth = this.AuthorizeOrg(_repo, id);
            if (!auth.Allowed) return auth.Error;

            var set = await _taskStatuses.ForOrganizationAsync(id);
            // Statuses and custom fields share one document, so this is still a single read.
            var config = await _workspaceConfig.GetByOrganizationIdAsync(id);

            return Ok(new
            {
                organizationId = id,
                statuses = set.Statuses.Select(s => new
                {
                    slug = s.Slug,
                    label = s.Label,
                    color = s.Color,
                    order = s.Order,
                    isTerminal = s.IsTerminal,
                    isStarted = s.IsStarted,
                    isDefault = s.IsDefault,
                    isArchived = s.IsArchived,
                }),
                defaultSlug = set.DefaultSlug,
                customFields = (config?.CustomFields ?? new List<CustomFieldDefinition>())
                    .Where(f => !f.IsArchived)
                    .OrderBy(f => f.Order)
                    .Select(f => new
                    {
                        slug = f.Slug,
                        label = f.Label,
                        type = f.Type,
                        appliesTo = f.AppliesTo,
                        options = f.Options,
                        order = f.Order,
                    }),
            });
        }

        [HttpGet("{id}")]
        public ActionResult<Organization> GetOrganization(string id)
        {
            var auth = this.AuthorizeOrg(_repo, id);
            if (!auth.Allowed) return auth.Error;

            return Ok(auth.Org);
        }

        /// <summary>
        /// Organizations the caller belongs to.
        /// </summary>
        /// <remarks>
        /// The <c>userId</c> and <c>role</c> query parameters are accepted for backwards
        /// compatibility with existing clients but are deliberately IGNORED. They used to
        /// drive the response, which meant <c>?role=Admin</c> returned every organization
        /// in the system to any caller. Scope now comes from the signed JWT only.
        /// </remarks>
        [HttpGet]
        public ActionResult<List<Organization>> GetAllOrganizations([FromQuery] string? userId, [FromQuery] string? role)
        {
            // Platform admin, established from the configured allowlist — never from the
            // query string, and never from Contributor.Role (see OrgAccess.IsPlatformAdmin).
            if (OrgAccess.IsPlatformAdmin(this))
            {
                return Ok(_repo.FetchAllOrganizations());
            }

            var callerId = OrgAccess.UserId(User);
            if (string.IsNullOrEmpty(callerId)) return Ok(new List<Organization>());

            return Ok(_repo.FetchOrganizationsByUserId(callerId));
        }

        [HttpGet("{id}/projects")]
        public ActionResult<List<Project>> GetProjectsByOrganizationId(string id)
        {
            var auth = this.AuthorizeOrg(_repo, id);
            if (!auth.Allowed) return auth.Error;

            return Ok(_repo.FetchProjectsByOrganizationId(id));
        }

        [HttpPost]
        public async Task<ActionResult<Organization>> CreateOrganization([FromBody] Organization org)
        {
            if (org == null) return BadRequest(new { message = "Organization payload is required" });

            var callerId = OrgAccess.UserId(User);
            if (string.IsNullOrEmpty(callerId))
            {
                return Unauthorized(new { message = "Authentication required" });
            }

            // The creator is always the owner. Previously OwnerId came from the request
            // body, so a caller could create an organization owned by someone else — and
            // sidestep the personal-plan check below by supplying a different id.
            org.OwnerId = callerId;
            org.Id = Guid.NewGuid().ToString();

            // Check if user already owns organizations
            if (!string.IsNullOrEmpty(org.OwnerId))
            {
                var existingOrgs = _repo.FetchOrganizationsByUserId(org.OwnerId)
                    .Where(o => o.OwnerId == org.OwnerId)
                    .ToList();

                if (existingOrgs.Any())
                {
                    // Check if any of their owned orgs are stuck on a Free/Personal plan
                    foreach (var existingOrg in existingOrgs)
                    {
                        var sub = await _subscriptions.GetByOrganizationIdAsync(existingOrg.Id);

                        // Both ids are checked deliberately: "free" is the current free plan,
                        // "personal" is the retired one that existing organizations may still
                        // point at. Dropping the legacy id here would silently let every
                        // grandfathered free organization create unlimited new ones.
                        var isFreeTier = sub != null && sub.Status == "active" &&
                            (sub.PlanId.Equals(RafeeqyNotes.Api.Services.EntitlementService.FallbackPlanId, StringComparison.OrdinalIgnoreCase) ||
                             sub.PlanId.Equals("personal", StringComparison.OrdinalIgnoreCase));

                        if (isFreeTier)
                        {
                            return StatusCode(403, new
                            {
                                message = "Upgrade required",
                                detail = "Your current organization is on the Free tier. You cannot create additional organizations without upgrading to a paid plan."
                            });
                        }
                    }
                }
            }

            var createdOrg = _repo.InsertOrganization(org);

            // Issue the Free plan automatically to the new organization.
            var subscription = new OrganizationSubscription
            {
                OrganizationId = createdOrg.Id,
                PlanId = RafeeqyNotes.Api.Services.EntitlementService.FallbackPlanId,
                Status = "active",
                BillingCycle = "monthly",
                StartDate = DateTime.UtcNow,
                EndDate = DateTime.UtcNow.AddYears(100), // Free does not expire
                AutoRenew = false
            };

            await _subscriptions.CreateAsync(subscription);

            return Ok(createdOrg);
        }

        [HttpPut("{id}")]
        public ActionResult<Organization> UpdateOrganization(string id, [FromBody] Organization org)
        {
            var auth = this.AuthorizeOrg(_repo, id, OrgAccess.Managers);
            if (!auth.Allowed) return auth.Error;
            if (org == null) return BadRequest(new { message = "Organization payload is required" });

            // This is a full ReplaceOne underneath, so only copy the fields that are
            // actually editable. Accepting the whole document would let a manager rewrite
            // OwnerId and Members and take over the organization.
            var existing = auth.Org;
            existing.Name = org.Name;
            existing.Description = org.Description;
            existing.Logo = org.Logo;
            existing.UpdatedAt = DateTime.UtcNow;

            return Ok(_repo.UpdateOrganization(id, existing));
        }

        [HttpDelete("{id}")]
        public IActionResult DeleteOrganization(string id)
        {
            var auth = this.AuthorizeOrg(_repo, id, OrgAccess.Owner);
            if (!auth.Allowed) return auth.Error;

            _repo.DeleteOrganization(id);
            return NoContent();
        }

        [HttpPost("{id}/members")]
        public async Task<ActionResult<OrganizationMember>> AddMember(string id, [FromBody] OrganizationMember member)
        {
            var auth = this.AuthorizeOrg(_repo, id, OrgAccess.Managers);
            if (!auth.Allowed) return auth.Error;
            if (member == null) return BadRequest(new { message = "Member payload is required" });

            var org = auth.Org;

            var currentMembers = _repo.GetMembers(id);

            // Seat limit, resolved through the entitlement service.
            //
            // This used to resolve the plan inline via a service locator and was FAIL-OPEN:
            // an organization with no subscription, or one not in "active" status, got no
            // limit at all. The service resolves a missing subscription to the free plan
            // instead, so the absence of a record no longer grants unlimited seats.
            var entitlements = await _entitlements.ForOrganizationAsync(id);
            var seatCheck = this.RequireQuota(entitlements, Entitlement.Seats, currentMembers.Count);
            if (!seatCheck.Allowed) return seatCheck.Error;

            var result = _repo.AddMember(id, member);

            // Notify the added member
            await _notificationRepo.CreateUserNotificationAsync(new Notification
            {
                UserId = member.UserId,
                Type = "organization_joined",
                Title = "Joined Organization",
                Message = $"You have been added to the organization: {org.Name}",
                Link = $"/organizations/{id}/settings",
                CreatedAt = DateTime.UtcNow,
                Metadata = new Dictionary<string, string> { { "organizationId", id } }
            });

            return Ok(result);
        }

        [HttpDelete("{id}/members/{memberId}")]
        public IActionResult RemoveMember(string id, string memberId)
        {
            var auth = this.AuthorizeOrg(_repo, id, OrgAccess.Managers);
            if (!auth.Allowed) return auth.Error;

            if (IsOwnerMember(auth.Org, memberId))
            {
                return StatusCode(403, new
                {
                    message = "Cannot remove the owner",
                    detail = "The organization owner cannot be removed. Transfer ownership first."
                });
            }

            _repo.RemoveMember(id, memberId);
            return NoContent();
        }

        [HttpPut("{id}/members/{memberId}")]
        public IActionResult UpdateMemberRole(string id, string memberId, [FromBody] OrganizationRoleUpdate request)
        {
            var auth = this.AuthorizeOrg(_repo, id, OrgAccess.Managers);
            if (!auth.Allowed) return auth.Error;
            if (request == null || string.IsNullOrWhiteSpace(request.Role))
            {
                return BadRequest(new { message = "Role is required" });
            }

            if (IsOwnerMember(auth.Org, memberId))
            {
                return StatusCode(403, new
                {
                    message = "Cannot change the owner's role",
                    detail = "The organization owner's role is fixed. Transfer ownership first."
                });
            }

            // Ownership is conferred by Organization.OwnerId, not by a member row.
            // Allowing "owner" here would create a second, inconsistent owner.
            if (OrgAccess.Owner.Equals(request.Role, StringComparison.OrdinalIgnoreCase))
            {
                return BadRequest(new
                {
                    message = "Invalid role",
                    detail = "Ownership cannot be granted through a role change."
                });
            }

            var updated = _repo.UpdateMemberRole(id, memberId, request.Role);
            if (!updated)
                return NotFound(new { message = $"Member '{memberId}' not found in this organization." });

            return Ok(new { message = "Role updated successfully" });
        }

        [HttpGet("{id}/members")]
        public ActionResult<List<OrganizationMember>> GetOrganizationMembers(string id)
        {
            var auth = this.AuthorizeOrg(_repo, id);
            if (!auth.Allowed) return auth.Error;

            return Ok(_repo.GetMembers(id));
        }

        /// <summary>
        /// True when the member row identified by <paramref name="memberId"/> belongs to
        /// the organization owner. Callers pass either the member row id or the user id,
        /// so both are checked.
        /// </summary>
        private static bool IsOwnerMember(Organization org, string memberId)
        {
            if (org == null || string.IsNullOrEmpty(memberId)) return false;
            if (string.IsNullOrEmpty(org.OwnerId)) return false;

            if (org.OwnerId.Equals(memberId, StringComparison.OrdinalIgnoreCase)) return true;

            var row = org.Members?.FirstOrDefault(m =>
                m != null &&
                ((!string.IsNullOrEmpty(m.Id) && m.Id.Equals(memberId, StringComparison.OrdinalIgnoreCase)) ||
                 (!string.IsNullOrEmpty(m.UserId) && m.UserId.Equals(memberId, StringComparison.OrdinalIgnoreCase))));

            return row != null
                && !string.IsNullOrEmpty(row.UserId)
                && row.UserId.Equals(org.OwnerId, StringComparison.OrdinalIgnoreCase);
        }

        [HttpPost("{id}/invitations")]
        public async Task<ActionResult<OrganizationInvitation>> CreateInvitation(string id, [FromBody] InvitationRequest request)
        {
            var auth = this.AuthorizeOrg(_repo, id, OrgAccess.Managers);
            if (!auth.Allowed) return auth.Error;
            if (request == null || string.IsNullOrWhiteSpace(request.Email))
            {
                return BadRequest(new { message = "Email is required" });
            }

            var org = auth.Org;

            var currentMembers = _repo.GetMembers(id);
            var activeSub = await _subscriptions.GetByOrganizationIdAsync(id);
            var pendingInvites = _invitations.GetPendingInvitationsByOrganization(id);
            
            // Count only members who do NOT already have a matching pending invitation, so an
            // invited-but-not-yet-joined person occupies one slot rather than two.
            var actualMembers = currentMembers.Where(m =>
                m.User == null ||
                !pendingInvites.Any(i => i.Email.Equals(m.User.Email, StringComparison.OrdinalIgnoreCase) && !i.IsUsed)
            ).ToList();

            // Same entitlement check as AddMember - previously a second, separate fail-open copy.
            var inviteEntitlements = await _entitlements.ForOrganizationAsync(id);
            var inviteSeatCheck = this.RequireQuota(inviteEntitlements, Entitlement.Seats, actualMembers.Count);
            if (!inviteSeatCheck.Allowed) return inviteSeatCheck.Error;

            // Check if invitation already exists for this email
            var existingInvite = pendingInvites.FirstOrDefault(i => i.Email.Equals(request.Email, StringComparison.OrdinalIgnoreCase));
            if (existingInvite != null)
            {
                // Just resend the email instead of creating a new invitation
                var ownerForResend = await _contributors.GetByIdAsync(org.OwnerId);
                if (ownerForResend != null)
                {
                    var notificationRequest = new NotificationRequest
                    {
                        Type = "organization_invitation",
                        RecipientEmails = new List<string> { request.Email },
                        CcEmails = new List<string> { ownerForResend.Email },
                        Metadata = new Dictionary<string, string>
                        {
                            { "OrganizationName", org.Name },
                            { "InvitationToken", existingInvite.InvitationToken }
                        }
                    };
                    _ = _notificationRepo.SendEmailNotificationAsync(notificationRequest);
                }
                return Ok(existingInvite);
            }

            // Find or create contributor
            var contributor = await _contributors.GetByEmailAsync(request.Email);
            if (contributor == null)
            {
                // Create a placeholder account with first-time login flag
                var tempPassword = Guid.NewGuid().ToString("N").Substring(0, 10);
                contributor = new Contributor
                {
                    Email = request.Email,
                    Name = request.Email.Split('@')[0],
                    DisplayName = request.Email.Split('@')[0],
                    PasswordHash = BCrypt.Net.BCrypt.HashPassword(tempPassword),
                    IsFirstLogin = true,
                    IsEmailVerified = true, // We trust admin invitations
                    CreatedAt = DateTime.UtcNow,
                    SubscriptionExpiry = DateTime.UtcNow.AddMonths(1)
                };
                await _contributors.CreateAsync(contributor);
            }

            // [REMOVED] Automatic member addition - users will now be added ONLY when they ACCEPT the invitation.

            var invitation = new OrganizationInvitation
            {
                Email = request.Email,
                OrganizationId = id,
                Role = request.Role ?? "viewer"
            };

            var created = _invitations.CreateInvitation(invitation);

            // Fetch owner email for CC
            var owner = await _contributors.GetByIdAsync(org.OwnerId);
            if (owner != null)
            {
                var notificationRequest = new NotificationRequest
                {
                    Type = "organization_invitation",
                    RecipientEmails = new List<string> { request.Email },
                    CcEmails = new List<string> { owner.Email },
                    Metadata = new Dictionary<string, string>
                    {
                        { "OrganizationName", org.Name },
                        { "InvitationToken", created.InvitationToken }
                    }
                };

                // Non-blocking email send
                _ = _notificationRepo.SendEmailNotificationAsync(notificationRequest);
            }

            return Ok(created);
        }

        [HttpGet("{id}/invitations")]
        public ActionResult<List<OrganizationInvitation>> GetPendingInvitations(string id)
        {
            var auth = this.AuthorizeOrg(_repo, id, OrgAccess.Managers);
            if (!auth.Allowed) return auth.Error;

            return Ok(_invitations.GetPendingInvitationsByOrganization(id));
        }

        [HttpDelete("invitations/{invitationId}")]
        public IActionResult DeleteInvitation(string invitationId)
        {
            // The route carries no organization id, so resolve it from the invitation
            // and authorize against that organization.
            var invitation = _invitations.GetInvitationById(invitationId);
            if (invitation == null) return NotFound(new { message = "Invitation not found" });

            var auth = this.AuthorizeOrg(_repo, invitation.OrganizationId, OrgAccess.Managers);
            if (!auth.Allowed) return auth.Error;

            _invitations.DeleteInvitation(invitationId);
            return NoContent();
        }
    }

    public record InvitationRequest(string Email, string? Role);
    public record OrganizationRoleUpdate(string Role);
}

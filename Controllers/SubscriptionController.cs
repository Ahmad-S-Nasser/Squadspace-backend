using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RafeeqyNotes.Api.Helpers;
using RafeeqyNotes.Api.Models;
using RafeeqyNotes.Api.Repositories;

namespace RafeeqyNotes.Api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class SubscriptionController : ControllerBase
    {
        private readonly IContributorRepository _contributors;
        private readonly IOrganizationRepository _organizations;
        private readonly ISubscriptionPlanRepository _subscriptionPlans;
        private readonly IOrganizationSubscriptionRepository _orgSubscriptions;
        private readonly RafeeqyNotes.Api.Services.IEntitlementService _entitlements;
        private readonly IOrganizationInvitationRepository _invitations;

        public SubscriptionController(
            IContributorRepository contributors, 
            IOrganizationRepository organizations, 
            ISubscriptionPlanRepository subscriptionPlans,
            IOrganizationSubscriptionRepository orgSubscriptions,
            IOrganizationInvitationRepository invitations,
            RafeeqyNotes.Api.Services.IEntitlementService entitlements)
        {
            _contributors = contributors;
            _organizations = organizations;
            _subscriptionPlans = subscriptionPlans;
            _orgSubscriptions = orgSubscriptions;
            _invitations = invitations;
            _entitlements = entitlements;
        }

        /// <summary>
        /// Legacy self-service plan change. Scheduled for deletion once Kashier checkout
        /// lands; it will then return 410 Gone for one release.
        /// </summary>
        /// <remarks>
        /// This endpoint used to take <c>request.UserId</c> from the body with no
        /// authentication at all, so any caller could hand any account a paid plan.
        /// The caller is now always the JWT subject, and only free plans may be
        /// self-granted — see <see cref="IsSelfServiceablePlan"/>.
        /// </remarks>
        [HttpPost("upgrade")]
        public async Task<IActionResult> UpgradePlan([FromBody] UpgradePlanRequest request)
        {
            if (request == null) return BadRequest("Request body is required");

            var callerId = OrgAccess.UserId(User);
            if (string.IsNullOrEmpty(callerId)) return Unauthorized(new { message = "Authentication required" });

            var contributor = await _contributors.GetByIdAsync(callerId);
            if (contributor == null) return NotFound("User not found");

            // Paid plans are granted only by a verified payment. Before this check the
            // endpoint applied whatever plan the client asked for, for free.
            if (!await IsSelfServiceablePlanAsync(request.Plan))
            {
                return StatusCode(402, new
                {
                    message = "Payment required",
                    detail = "Paid plans cannot be self-assigned. Start a checkout session to upgrade."
                });
            }

            if (request.Plan == "Personal")
            {
                if (contributor.HasUsedTrial)
                {
                    return BadRequest("You have already used your Test plan trial.");
                }

                // Check for derivative emails
                var normalizedEmail = NormalizeEmail(contributor.Email);
                var allUsers = await _contributors.GetAllAsync();
                
                foreach (var user in allUsers)
                {
                   if (user.Id == contributor.Id) continue;
                   if (user.HasUsedTrial && NormalizeEmail(user.Email) == normalizedEmail)
                   {
                       return BadRequest("A user with this email (or a similar alias) has already used the Test plan.");
                   }
                }
                
                contributor.HasUsedTrial = true;
            }

            contributor.SubscriptionPlan = request.Plan;
            contributor.SubscriptionStatus = "Active";
            contributor.SubscriptionExpiry = request.Plan == "Enterprise" ? DateTime.UtcNow.AddYears(1) : DateTime.UtcNow.AddMonths(1);

            // Set limits based on plan
            switch (request.Plan)
            {
                case "Personal": // Formerly Test
                    contributor.MaxMembers = 1;
                    contributor.SubscriptionExpiry = DateTime.UtcNow.AddMonths(1);
                    break;
                case "Starter":
                    contributor.MaxMembers = 5;
                    // Standard expiry logic
                    break;
                case "Pro":
                    contributor.MaxMembers = 10;
                    // Standard expiry logic
                    break;
                case "Custom": // Formerly Enterprise
                    contributor.MaxMembers = 9999; 
                    // Manual handling usually, but for now set unlimited
                    break;
                default:
                    contributor.MaxMembers = 5;
                    break;
            }

            await _contributors.UpdateAsync(contributor);

            // Auto-create organization if name provided and user doesn't already own one
            Organization? createdOrg = null;
            if (!string.IsNullOrWhiteSpace(request.OrganizationName))
            {
                var existingOrgs = _organizations.FetchOrganizationsByUserId(contributor.Id);
                if (!existingOrgs.Any(o => o.OwnerId == contributor.Id))
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
                }
            }

            return Ok(new { message = "Plan upgraded successfully", contributor, organization = createdOrg });
        }

        /// <summary>
        /// A plan may be self-assigned only when it costs nothing. The seeded "custom"
        /// plan uses -1 as a contact-sales sentinel, so it is deliberately excluded:
        /// a negative price is not a free price.
        /// </summary>
        /// <remarks>
        /// <paramref name="plan"/> may be either a slug ("pro") or a display name ("Pro"),
        /// because the legacy upgrade endpoint sends display names.
        /// </remarks>
        private async Task<bool> IsSelfServiceablePlanAsync(string plan)
        {
            if (string.IsNullOrWhiteSpace(plan)) return false;

            var all = await _subscriptionPlans.GetAllActiveAsync();
            var match = all.FirstOrDefault(p =>
                (p.PlanId != null && p.PlanId.Equals(plan, StringComparison.OrdinalIgnoreCase)) ||
                (p.Name != null && p.Name.Equals(plan, StringComparison.OrdinalIgnoreCase)));

            if (match == null) return false;

            return match.MonthlyPrice == 0m && match.YearlyPrice == 0m;
        }

        private string NormalizeEmail(string email)
        {
            if (string.IsNullOrWhiteSpace(email)) return string.Empty;
            try
            {
                var parts = email.Split('@');
                if (parts.Length != 2) return email.ToLowerInvariant();
                
                var local = parts[0];
                var domain = parts[1].ToLowerInvariant();

                // Remove +aliases
                var plusIndex = local.IndexOf('+');
                if (plusIndex >= 0) local = local.Substring(0, plusIndex);

                // For gmail, remove dots
                if (domain == "gmail.com") local = local.Replace(".", "");

                return $"{local}@{domain}";
            }
            catch
            {
                return email.ToLowerInvariant();
            }
        }

        [HttpGet("status/{userId}")]
        public async Task<IActionResult> GetStatus(string userId)
        {
            // Subscription state is personal data: only the user themselves or a
            // platform admin may read it.
            var callerId = OrgAccess.UserId(User);
            if (!OrgAccess.IsPlatformAdmin(this) &&
                !string.Equals(callerId, userId, StringComparison.OrdinalIgnoreCase))
            {
                return StatusCode(403, new
                {
                    message = "Insufficient permissions",
                    detail = "You can only read your own subscription status."
                });
            }

            var contributor = await _contributors.GetByIdAsync(userId);
            if (contributor == null) return NotFound("User not found");

            return Ok(new
            {
                contributor.SubscriptionPlan,
                contributor.SubscriptionStatus,
                contributor.SubscriptionExpiry,
                contributor.MaxMembers
            });
        }

        // The public pricing page reads this before anyone has an account.
        [AllowAnonymous]
        [HttpGet("plans")]
        public async Task<IActionResult> GetSubscriptionPlans()
        {
            var plans = await _subscriptionPlans.GetAllActiveAsync();
            return Ok(plans.Select(p => new
            {
                Id = p.PlanId,
                Name = p.Name,
                Description = p.Description,
                MonthlyPrice = p.MonthlyPrice,
                YearlyPrice = p.YearlyPrice,
                Currency = p.Currency,
                UserLimit = p.UserLimit,
                Features = p.Features,
                IsActive = p.IsActive,
                SortOrder = p.SortOrder,
                CreatedAt = p.CreatedAt,
                UpdatedAt = p.UpdatedAt
            }));
        }

        /// <summary>
        /// The organization's effective entitlements — plan grants with add-ons merged in.
        /// </summary>
        /// <remarks>
        /// Deliberately a separate endpoint returning the RESOLVED set, rather than letting the
        /// client read plan.features and work it out. The client must never re-derive
        /// entitlements: the moment it does, its idea of what is allowed can drift from what
        /// the server enforces, and the user sees an upgrade prompt for something they own, or
        /// a button that 402s when they press it. One source of truth, computed once, here.
        /// </remarks>
        [HttpGet("organization/{orgId}/entitlements")]
        public async Task<IActionResult> GetOrganizationEntitlements(string orgId)
        {
            var auth = this.AuthorizeOrg(_organizations, orgId);
            if (!auth.Allowed) return auth.Error;

            var set = await _entitlements.ForOrganizationAsync(orgId);
            return Ok(new { organizationId = orgId, entitlements = set.All });
        }

        [HttpGet("organization/{orgId}")]
        public async Task<IActionResult> GetOrganizationSubscription(string orgId)
        {
            var auth = this.AuthorizeOrg(_organizations, orgId);
            if (!auth.Allowed) return auth.Error;

            var sub = await _orgSubscriptions.GetByOrganizationIdAsync(orgId);
            if (sub == null) return NotFound("Subscription not found for this organization");

            // Attach the plan details
            sub.Plan = await _subscriptionPlans.GetByPlanIdAsync(sub.PlanId);

            return Ok(new
            {
                Id = sub.Id,
                OrganizationId = sub.OrganizationId,
                PlanId = sub.PlanId,
                Plan = sub.Plan != null ? new {
                    Id = sub.Plan.PlanId,
                    Name = sub.Plan.Name,
                    Description = sub.Plan.Description,
                    MonthlyPrice = sub.Plan.MonthlyPrice,
                    YearlyPrice = sub.Plan.YearlyPrice,
                    Currency = sub.Plan.Currency,
                    UserLimit = sub.Plan.UserLimit,
                    Features = sub.Plan.Features,
                    IsActive = sub.Plan.IsActive,
                    SortOrder = sub.Plan.SortOrder
                } : null,
                Status = sub.Status,
                BillingCycle = sub.BillingCycle,
                StartDate = sub.StartDate,
                EndDate = sub.EndDate,
                AutoRenew = sub.AutoRenew,
                CurrentUserCount = 1, // Optional: Could fetch real member count
                CreatedAt = sub.CreatedAt,
                UpdatedAt = sub.UpdatedAt
            });
        }

        [HttpGet("organization/{orgId}/user-limit")]
        public async Task<IActionResult> GetOrganizationUserLimit(string orgId)
        {
            var auth = this.AuthorizeOrg(_organizations, orgId);
            if (!auth.Allowed) return auth.Error;

            var sub = await _orgSubscriptions.GetByOrganizationIdAsync(orgId);
            var members = _organizations.GetMembers(orgId);
            var pendingInvites = _invitations.GetPendingInvitationsByOrganization(orgId);
            
            // Resolved through the entitlement service so this agrees with what AddMember and
            // CreateInvitation actually enforce. This was a THIRD copy of the seat lookup with
            // its own hardcoded default of 3 — so the number the UI displayed could differ
            // from the number the server enforced, which is worse than either being wrong.
            var entitlements = await _entitlements.ForOrganizationAsync(orgId);
            var seatLimit = entitlements.Limit(RafeeqyNotes.Api.Helpers.Entitlement.Seats);
            int limit = seatLimit == long.MaxValue ? int.MaxValue : (int)seatLimit;

            string planName = "Free";
            if (sub != null)
            {
                var plan = await _subscriptionPlans.GetByPlanIdAsync(sub.PlanId);
                if (plan != null) planName = plan.Name;
            }

            // Efficient Filtering: Count only members who do NOT have a matching pending invitation.
            // This gives the correct "3/10" count without modifying the database on every request.
            var actualMembers = members.Where(m => 
                m.User == null || 
                !pendingInvites.Any(i => i.Email.Equals(m.User.Email, StringComparison.OrdinalIgnoreCase) && !i.IsUsed)
            ).ToList();

            // Count actual accepted members only
            int currentCount = actualMembers.Count;
            bool canAdd = limit == -1 || currentCount < limit;

            return Ok(new
            {
                canAdd = canAdd,
                currentCount = currentCount,
                limit = limit,
                isOverLimit = limit != -1 && currentCount >= limit,
                percentUsed = limit == -1 ? 0 : (double)currentCount / limit * 100,
                planName = planName
            });
        }

        [HttpPost]
        public async Task<IActionResult> CreateSubscription([FromBody] CreateSubscriptionRequest data)
        {
            if (data == null) return BadRequest("Request body is required");

            var auth = this.AuthorizeOrg(_organizations, data.OrganizationId, OrgAccess.Managers);
            if (!auth.Allowed) return auth.Error;

            var existing = await _orgSubscriptions.GetByOrganizationIdAsync(data.OrganizationId);
            if (existing != null) return BadRequest("Organization already has a subscription.");

            var plan = await _subscriptionPlans.GetByPlanIdAsync(data.PlanId);
            if (plan == null) return BadRequest("Invalid Plan ID.");

            // A paid subscription may only be created by a verified payment. Until the
            // Kashier webhook exists, this endpoint provisions free plans only.
            if (!await IsSelfServiceablePlanAsync(data.PlanId))
            {
                return StatusCode(402, new
                {
                    message = "Payment required",
                    detail = "Paid subscriptions must be created through checkout, not directly."
                });
            }

            // Status and the period dates are server-owned; anything the client sent is ignored.
            var newSub = new OrganizationSubscription
            {
                OrganizationId = data.OrganizationId,
                PlanId = data.PlanId,
                Status = "active",
                BillingCycle = data.BillingCycle ?? "monthly",
                StartDate = DateTime.UtcNow,
                EndDate = data.BillingCycle == "yearly" ? DateTime.UtcNow.AddYears(1) : DateTime.UtcNow.AddMonths(1),
                AutoRenew = true
            };

            await _orgSubscriptions.CreateAsync(newSub);
            return Ok(newSub);
        }

        [HttpPut("{id}")]
        public async Task<IActionResult> UpdateSubscription(string id, [FromBody] UpdateSubscriptionRequest data)
        {
            var sub = await _orgSubscriptions.GetByIdAsync(id);
            if (sub == null) return NotFound();
            if (data == null) return BadRequest("Request body is required");

            // The route carries the subscription id, so the organization to authorize
            // against comes from the stored record — never from the request.
            var auth = this.AuthorizeOrg(_organizations, sub.OrganizationId, OrgAccess.Managers);
            if (!auth.Allowed) return auth.Error;

            bool changed = false;

            if (!string.IsNullOrEmpty(data.PlanId) && sub.PlanId != data.PlanId)
            {
                var plan = await _subscriptionPlans.GetByPlanIdAsync(data.PlanId);
                if (plan == null) return BadRequest("Invalid Plan ID.");

                // Moving onto a paid plan is an upgrade and requires payment. Moving onto
                // a free plan is a self-service downgrade and is allowed.
                if (!await IsSelfServiceablePlanAsync(data.PlanId))
                {
                    return StatusCode(402, new
                    {
                        message = "Payment required",
                        detail = "Upgrading to a paid plan must go through checkout."
                    });
                }

                sub.PlanId = data.PlanId;
                changed = true;
            }

            if (!string.IsNullOrEmpty(data.BillingCycle) && sub.BillingCycle != data.BillingCycle)
            {
                sub.BillingCycle = data.BillingCycle;
                changed = true;
                // Optionally adjust EndDate here based on new cycle
            }

            // data.Status is deliberately ignored. It was directly assignable, so a client
            // could set "active" on an expired subscription and restore full entitlements.
            // Status is moved only by the server: payment outcomes and the renewal sweep.

            if (changed)
            {
                await _orgSubscriptions.UpdateAsync(sub);
            }

            return Ok(sub);
        }

        [HttpPost("{id}/cancel")]
        public async Task<IActionResult> CancelSubscription(string id, [FromBody] object data)
        {
            var sub = await _orgSubscriptions.GetByIdAsync(id);
            if (sub == null) return NotFound();

            var auth = this.AuthorizeOrg(_organizations, sub.OrganizationId, OrgAccess.Managers);
            if (!auth.Allowed) return auth.Error;

            sub.AutoRenew = false;
            sub.Status = "cancelled";
            await _orgSubscriptions.UpdateAsync(sub);

            return Ok(sub);
        }
    }

    public record UpgradePlanRequest(string UserId, string Plan, string? OrganizationName);
    public record CreateSubscriptionRequest(string OrganizationId, string PlanId, string BillingCycle, string? PaymentProvider);
    public record UpdateSubscriptionRequest(string? PlanId, string? BillingCycle, string? Status);
}

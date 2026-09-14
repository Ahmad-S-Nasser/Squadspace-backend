using Microsoft.AspNetCore.Mvc;
using RafeeqyNotes.Api.Models;
using RafeeqyNotes.Api.Repositories;
using System.Security.Claims;

namespace RafeeqyNotes.Api.Helpers
{
    /// <summary>
    /// Outcome of an organization access check. When <see cref="Allowed"/> is false,
    /// <see cref="Error"/> holds the response the caller should return.
    /// </summary>
    public sealed class OrgAuth
    {
        public bool Allowed { get; init; }

        /// <summary>Caller's role in the organization, or null if not a member.</summary>
        public string Role { get; init; }

        /// <summary>Caller's id, taken from the JWT — never from the request body or query string.</summary>
        public string UserId { get; init; }

        public Organization Org { get; init; }

        /// <summary>
        /// Non-null only when <see cref="Allowed"/> is false.
        /// Typed as <see cref="ActionResult"/> rather than <see cref="IActionResult"/> so it
        /// can be returned directly from both <c>IActionResult</c> and <c>ActionResult&lt;T&gt;</c>
        /// actions — only the former has an implicit conversion to the latter.
        /// </summary>
        public ActionResult Error { get; init; }

        public bool IsOwner => OrgAccess.Owner.Equals(Role, StringComparison.OrdinalIgnoreCase);
        public bool IsManager => OrgAccess.Managers.Contains(Role, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Organization membership and role checks.
    ///
    /// The identity always comes from the validated JWT. Before this existed, endpoints
    /// trusted a <c>userId</c> and <c>role</c> supplied by the client in the query string,
    /// which meant passing <c>?role=Admin</c> returned every organization in the system.
    /// Never reintroduce a code path that reads identity from client input.
    /// </summary>
    public static class OrgAccess
    {
        public const string Owner = "owner";
        public const string Admin = "admin";
        public const string Member = "member";
        public const string Viewer = "viewer";

        /// <summary>Roles permitted to change billing, membership and organization settings.</summary>
        public static readonly string[] Managers = { Owner, Admin };

        /// <summary>Any role that grants read access to the organization.</summary>
        public static readonly string[] AnyMember = { Owner, Admin, Member, Viewer };

        /// <summary>
        /// "log"    - evaluate and log would-be denials, but allow everything through.
        /// "enforce"- actually deny. Deploy once in "log", confirm the logs are quiet, then flip.
        /// </summary>
        private const string GuardModeKey = "Auth:OrgGuardMode";
        private const string EnforceMode = "enforce";

        public static string UserId(ClaimsPrincipal user) =>
            user?.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        public static string Email(ClaimsPrincipal user) =>
            user?.FindFirst(ClaimTypes.Email)?.Value;

        private const string PlatformAdminsKey = "Auth:PlatformAdminUserIds";

        /// <summary>
        /// Platform-wide admin — a cross-tenant capability, so it is granted only by an
        /// explicit allowlist of user ids in configuration. Empty by default: nobody.
        /// </summary>
        /// <remarks>
        /// This deliberately does NOT use the JWT role claim. <c>Contributor.Role</c> carries
        /// app-level values ("Viewer", "admin", "editor", "Contributor"), not a platform
        /// superuser flag, and several ordinary users already hold "admin". Testing
        /// <c>IsInRole("Admin")</c> would hand every such user read access to every
        /// organization — the same cross-tenant leak as the old <c>?role=Admin</c> query
        /// parameter, just relocated into the server. The only reason it did not already
        /// leak is that <see cref="ClaimsIdentity.HasClaim(string,string)"/> compares claim
        /// values case-sensitively, so "admin" happened not to match "Admin". Do not rely
        /// on that.
        /// </remarks>
        public static bool IsPlatformAdmin(ControllerBase controller)
        {
            var userId = UserId(controller?.User);
            if (string.IsNullOrEmpty(userId)) return false;

            var config = controller.HttpContext?.RequestServices?.GetService<IConfiguration>();
            var allowed = config?.GetSection(PlatformAdminsKey).Get<string[]>();
            if (allowed == null || allowed.Length == 0) return false;

            return allowed.Contains(userId, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The caller's role within an organization, or null when they are not a member.
        /// The owner is authoritative even if they also appear in the Members list.
        /// </summary>
        public static string RoleIn(Organization org, string userId)
        {
            if (org == null || string.IsNullOrEmpty(userId)) return null;

            if (!string.IsNullOrEmpty(org.OwnerId) &&
                org.OwnerId.Equals(userId, StringComparison.OrdinalIgnoreCase))
            {
                return Owner;
            }

            var membership = org.Members?.FirstOrDefault(m =>
                m != null && !string.IsNullOrEmpty(m.UserId) &&
                m.UserId.Equals(userId, StringComparison.OrdinalIgnoreCase));

            if (membership == null) return null;

            return string.IsNullOrWhiteSpace(membership.Role) ? Member : membership.Role.ToLowerInvariant();
        }

        /// <summary>
        /// Verifies the caller is authenticated and holds one of <paramref name="allowedRoles"/>
        /// in the organization. Pass no roles to require membership only.
        /// </summary>
        public static OrgAuth AuthorizeOrg(
            this ControllerBase controller,
            IOrganizationRepository organizations,
            string orgId,
            params string[] allowedRoles)
        {
            var user = controller.User;
            var userId = UserId(user);

            // These three are never bypassed by "log" mode: there is no organization to
            // operate on, so letting the request through would only NullReference later.
            if (string.IsNullOrEmpty(userId))
            {
                return Hard(controller, null, orgId, null,
                    controller.Unauthorized(new
                    {
                        message = "Authentication required",
                        detail = "A valid bearer token is required for this request."
                    }));
            }

            if (string.IsNullOrWhiteSpace(orgId))
            {
                return Hard(controller, userId, orgId, null,
                    controller.BadRequest(new
                    {
                        message = "Organization required",
                        detail = "No organization id was supplied."
                    }));
            }

            var org = organizations.FetchOrganizationById(orgId);

            if (org == null)
            {
                return Hard(controller, userId, orgId, null,
                    controller.NotFound(new
                    {
                        message = "Organization not found",
                        detail = "No organization exists with that id, or you do not have access to it."
                    }));
            }

            var role = RoleIn(org, userId);

            if (IsPlatformAdmin(controller))
            {
                return new OrgAuth
                {
                    Allowed = true,
                    Role = role ?? Admin,
                    UserId = userId,
                    Org = org
                };
            }

            if (role == null)
            {
                return Deny(controller, userId, orgId, org,
                    controller.NotFound(new
                    {
                        message = "Organization not found",
                        detail = "No organization exists with that id, or you do not have access to it."
                    }));
            }

            var roleAccepted = allowedRoles == null
                            || allowedRoles.Length == 0
                            || allowedRoles.Contains(role, StringComparer.OrdinalIgnoreCase);

            if (!roleAccepted)
            {
                return Deny(controller, userId, orgId, org,
                    controller.StatusCode(403, new
                    {
                        message = "Insufficient permissions",
                        detail = $"This action requires one of: {string.Join(", ", allowedRoles)}. Your role is '{role}'."
                    }),
                    role);
            }

            return new OrgAuth { Allowed = true, Role = role, UserId = userId, Org = org };
        }

        /// <summary>
        /// An unconditional denial. Not affected by the rollout switch.
        /// </summary>
        private static OrgAuth Hard(
            ControllerBase controller,
            string userId,
            string orgId,
            Organization org,
            ActionResult error,
            string role = null)
        {
            Log(controller, userId, orgId, role, denied: true);
            return new OrgAuth { Allowed = false, Role = role, UserId = userId, Org = org, Error = error };
        }

        /// <summary>
        /// An authorization denial, honouring the rollout switch: in "log" mode the denial
        /// is recorded but the request is allowed through, so the guard can be observed
        /// against production traffic before it starts rejecting anyone.
        /// </summary>
        private static OrgAuth Deny(
            ControllerBase controller,
            string userId,
            string orgId,
            Organization org,
            ActionResult error,
            string role = null)
        {
            var config = controller.HttpContext?.RequestServices?.GetService<IConfiguration>();
            var enforcing = !string.Equals(config?[GuardModeKey], "log", StringComparison.OrdinalIgnoreCase);

            Log(controller, userId, orgId, role, denied: enforcing);

            return enforcing
                ? new OrgAuth { Allowed = false, Role = role, UserId = userId, Org = org, Error = error }
                : new OrgAuth { Allowed = true, Role = role, UserId = userId, Org = org };
        }

        private static void Log(ControllerBase controller, string userId, string orgId, string role, bool denied)
        {
            var logger = controller.HttpContext?.RequestServices
                ?.GetService<ILoggerFactory>()?.CreateLogger("OrgAccess");
            if (logger == null) return;

            var path = controller.HttpContext?.Request?.Path.Value ?? "(unknown)";

            if (denied)
            {
                logger.LogInformation(
                    "OrgAccess denied: user={UserId} org={OrgId} role={Role} path={Path}",
                    userId ?? "(anonymous)", orgId, role ?? "(none)", path);
            }
            else
            {
                logger.LogWarning(
                    "OrgAccess would-deny (guard in log mode, request allowed): user={UserId} org={OrgId} role={Role} path={Path}",
                    userId ?? "(anonymous)", orgId, role ?? "(none)", path);
            }
        }
    }
}

using MongoDB.Bson.Serialization.Attributes;

namespace RafeeqyNotes.Api.Models
{
    /// <summary>
    /// One organization's single sign-on settings.
    /// </summary>
    /// <remarks>
    /// This is the difference between "sign in with Google" and SSO. Consumer social login proves
    /// somebody controls an inbox; SSO proves they are a current member of a directory the
    /// customer controls, and stops being true the moment that directory says so.
    ///
    /// The pinning fields are what carry that guarantee:
    ///
    /// - Microsoft REQUIRES a tenant id. The old flow used the /common/ authority, which accepts
    ///   any Entra tenant and any personal Microsoft account - so "sign in with Microsoft" let
    ///   anyone in, from anywhere, and told the product nothing about who they worked for.
    /// - Google uses the Workspace hosted domain (`hd`). Without it a personal gmail.com account
    ///   is indistinguishable from an employee.
    ///
    /// Deliberately NOT sold as an add-on. Charging separately for a security control prices it
    /// out of reach of the teams most likely to be breached, and enterprise buyers read it as a
    /// toll. It belongs inside the plan tier.
    /// </remarks>
    [BsonIgnoreExtraElements]
    public class OrgSsoConfig
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();

        public string OrganizationId { get; set; }

        /// <summary>"google" or "microsoft".</summary>
        public string Provider { get; set; }

        public bool Enabled { get; set; }

        /// <summary>
        /// Entra tenant (GUID or verified domain). Required for Microsoft.
        /// </summary>
        public string MicrosoftTenantId { get; set; }

        /// <summary>Google Workspace hosted domain, e.g. "company.com". Required for Google.</summary>
        public string GoogleHostedDomain { get; set; }

        /// <summary>
        /// Email domains accepted for this organization. Empty means any the provider vouches for.
        /// </summary>
        /// <remarks>
        /// A second, independent check alongside tenant/domain pinning. A guest account invited
        /// into a customer's Entra tenant carries that tenant's `tid` while having an outside
        /// address, so the tenant claim alone would admit them.
        /// </remarks>
        public List<string> AllowedEmailDomains { get; set; } = new();

        /// <summary>
        /// Creates a user and adds them to the organization on first successful SSO login.
        /// </summary>
        /// <remarks>
        /// Just-in-time provisioning. It is what makes SSO worth having for an admin - otherwise
        /// every new hire still needs inviting by hand. It is NOT deprovisioning: removing someone
        /// from the directory does not remove them here. That needs SCIM, which is not built.
        /// </remarks>
        public bool AutoProvisionUsers { get; set; } = true;

        /// <summary>Role given to auto-provisioned members. Never "owner" or "admin".</summary>
        public string DefaultRole { get; set; } = "member";

        /// <summary>
        /// Password sign-in is refused for members of this organization.
        /// </summary>
        /// <remarks>
        /// The point of SSO for a security team: revoking the directory account revokes access,
        /// with no local password left behind as a way around it.
        /// </remarks>
        public bool EnforceSso { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
        public string UpdatedBy { get; set; }
    }
}

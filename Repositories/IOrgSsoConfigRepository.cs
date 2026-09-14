using RafeeqyNotes.Api.Models;

namespace RafeeqyNotes.Api.Repositories
{
    public interface IOrgSsoConfigRepository
    {
        Task<OrgSsoConfig> GetByOrganizationIdAsync(string organizationId);

        /// <summary>Enabled config matching an email's domain, for domain-routed login.</summary>
        Task<OrgSsoConfig> FindByEmailDomainAsync(string domain);

        Task UpsertAsync(OrgSsoConfig config);
        Task DeleteAsync(string organizationId);
    }
}

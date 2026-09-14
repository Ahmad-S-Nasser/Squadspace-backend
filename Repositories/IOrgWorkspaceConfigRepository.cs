using RafeeqyNotes.Api.Models;

namespace RafeeqyNotes.Api.Repositories
{
    public interface IOrgWorkspaceConfigRepository
    {
        Task<OrgWorkspaceConfig> GetByOrganizationIdAsync(string organizationId);

        /// <summary>Creates the organization's config if it has none, and returns what is stored.</summary>
        Task<OrgWorkspaceConfig> EnsureAsync(string organizationId, List<TaskStatusDefinition> seed);

        Task UpsertAsync(OrgWorkspaceConfig config);
    }
}

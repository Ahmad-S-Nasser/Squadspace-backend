using RafeeqyNotes.Api.Models;

namespace RafeeqyNotes.Api.Repositories
{
    public interface IImportJobRepository
    {
        Task<List<ImportJob>> GetForOrganizationAsync(string organizationId, int limit = 25);
        Task<ImportJob> GetByIdAsync(string id);
        Task CreateAsync(ImportJob job);
        Task UpdateAsync(ImportJob job);
    }
}

using RafeeqyNotes.Api.Models;

namespace RafeeqyNotes.Api.Repositories
{
    public interface ISavedReportRepository
    {
        /// <summary>Reports the caller may see: their own, plus anything shared with the org.</summary>
        Task<List<SavedReport>> GetVisibleAsync(string organizationId, string userId);

        Task<SavedReport> GetByIdAsync(string id);
        Task CreateAsync(SavedReport report);
        Task UpdateAsync(SavedReport report);
        Task DeleteAsync(string id);
    }
}

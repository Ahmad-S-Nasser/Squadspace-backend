using RafeeqyNotes.Api.Models;

namespace RafeeqyNotes.Api.Repositories
{
    public interface IContributorRepository
    {
        Task<Contributor?> GetByIdAsync(string id);
        Task<List<Contributor>> GetAllAsync();
        Task<List<Contributor>> GetAllWithRoleAsync();
        //Task<List<Contributor>> GetContributorsByNoteIDAsync(string note_id);
        Task<Contributor?> GetByEmailAsync(string email);
        Task CreateAsync(Contributor contributor);
        Task UpdateAsync(Contributor contributor);
        Task DeleteAsync(string id);
    }
}

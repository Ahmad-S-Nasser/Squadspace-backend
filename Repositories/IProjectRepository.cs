using RafeeqyNotes.Api.Models;

namespace RafeeqyNotes.Api.Repositories
{
    public interface IProjectRepository
    {
        Task<List<Project>> GetAllAsync();
        Task<List<Project>> GetByOrganizationIdAsync(string organizationId);
        Task<Project?> GetByIdAsync(string id);
        Task<List<OrganizationMember>> GetMembersByProjectID(string id) ;
        Task CreateAsync(Project project);
        Task UpdateAsync(Project project);
        Task DeleteAsync(string id);
        //Task CreateMemberAsync(OrganizationMember member);
    }
}
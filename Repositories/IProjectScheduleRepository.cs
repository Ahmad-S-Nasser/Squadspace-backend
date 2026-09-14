using RafeeqyNotes.Api.Models;

namespace RafeeqyNotes.Api.Repositories
{
    public interface IProjectScheduleRepository
    {
        Task<ProjectSchedule?> GetByProjectIdAsync(string projectId);

        /// <summary>Schedules for many projects in ONE round trip, keyed by project id.</summary>
        Task<Dictionary<string, ProjectSchedule>> GetByProjectIdsAsync(IEnumerable<string> projectIds);
        Task UpsertAsync(ProjectSchedule schedule);
    }
}

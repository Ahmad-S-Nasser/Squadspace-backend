using RafeeqyNotes.Api.Models;

namespace RafeeqyNotes.Api.Repositories
{
    public interface ISprintRepository
    {
        List<Sprint> FetchSprintsByProjectId(string projectId);
        Sprint FetchSprintsById(string Id);
        Sprint InsertSprint(Sprint sprint);
        Sprint UpdateSprint(string id, Sprint sprint);
        void DeleteSprint(string id);
        // Terminal statuses are supplied by the caller for the same reason as in
        // IAnalyticsRepository: repositories are singletons and must not depend on the status
        // service. CalculateSprintVelocity also has no organization id of its own.
        SprintVelocity CalculateSprintVelocity(string sprintId, IReadOnlyCollection<string> terminalStatuses);
    }
}

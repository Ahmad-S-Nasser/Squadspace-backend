using RafeeqyNotes.Api.Models;
using RafeeqyNotes.Api.Services;

namespace RafeeqyNotes.Api.Repositories
{
    public interface IAnalyticsRepository
    {
        // The status workflow is passed in rather than resolved here. Every repository in this
        // app is a singleton, so a repository depending on ITaskStatusService - which depends on
        // a repository - is a construction-time cycle. The controller has the org context and
        // resolves it there.
        DashboardStats FetchDashboardStats(string orgId, string? projectId, DateTime? startDate, DateTime? endDate, TaskStatusSet statuses);
        TaskAnalytics FetchTaskAnalytics(string orgId, string? projectId, DateTime? startDate, DateTime? endDate, TaskStatusSet statuses);
        MeetingAnalytics FetchMeetingAnalytics(string orgId, string? projectId, DateTime? startDate, DateTime? endDate);
        List<TeamActivity> FetchTeamActivity(string orgId, int limit);
    }
}

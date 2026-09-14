using MongoDB.Driver;
using RafeeqyNotes.Api.Config;
using RafeeqyNotes.Api.Models;
using RafeeqyNotes.Models;
using RafeeqyNotes.Api.Services;

namespace RafeeqyNotes.Api.Repositories
{
    public class AnalyticsRepository : IAnalyticsRepository
    {
        private readonly IMongoCollection<NoteTask> _tasks;
        private readonly IMongoCollection<Project> _projects;
        private readonly IMongoCollection<Meeting> _meetings;
        private readonly IMongoCollection<Contributor> _users;
        //private readonly IMongoCollection<ActivityLog> _activity;

        public AnalyticsRepository(MongoDbSettings settings)
        {
            var client = new MongoClient(settings.ConnectionString);
            var db = client.GetDatabase(settings.DatabaseName);
            _tasks = db.GetCollection<NoteTask>("NoteTasks");
            _projects = db.GetCollection<Project>("Projects");
            _meetings = db.GetCollection<Meeting>("Meetings");
            _users = db.GetCollection<Contributor>("Contributors");
            //_activity = db.GetCollection<ActivityLog>("ActivityLogs");
        }

        public DashboardStats FetchDashboardStats(string orgId, string? projectId, DateTime? startDate, DateTime? endDate, TaskStatusSet statuses)
        {
            var taskFilter = BuildTaskFilter(orgId, projectId, startDate, endDate);
            var projectFilter = BuildProjectFilter(orgId, projectId, startDate, endDate);
            var meetingFilter = BuildMeetingFilter(orgId, projectId, startDate, endDate);

            return new DashboardStats
            {
                TotalProjects = (int)_projects.CountDocuments(projectFilter),
                TotalTasks = (int)_tasks.CountDocuments(taskFilter),
                // Was Eq(Status, "done") - which missed every task stored as "Done", while the
                // completion rate below counted both. The two numbers disagreed on the same data.
                CompletedTasks = (int)_tasks.CountDocuments(
                    taskFilter & Builders<NoteTask>.Filter.In(t => t.Status, TerminalValues(statuses))),
                UpcomingMeetings = (int)_meetings.CountDocuments(meetingFilter & Builders<Meeting>.Filter.Gt(m => m.ScheduledAt, DateTime.UtcNow)),
            };
        }

        public TaskAnalytics FetchTaskAnalytics(string orgId, string? projectId, DateTime? startDate, DateTime? endDate, TaskStatusSet statuses)
        {
            var filter = BuildTaskFilter(orgId, projectId, startDate, endDate);
            var tasks = _tasks.Find(filter).ToList();

            return new TaskAnalytics
            {
                Tasks = tasks,
                // Grouped on the resolved slug, so "To-do", "To Do" and "todo" stop appearing
                // as three separate buckets in the same chart.
                ByStatus = tasks.GroupBy(t => statuses.Resolve(t.Status) ?? statuses.DefaultSlug)
                               .Select(g => new StatusCount
                               {
                                   Status = g.Key,
                                   Label = statuses.Find(g.Key)?.Label ?? g.Key,
                                   Count = g.Count(),
                               })
                               .ToList(),
                ByPriority = tasks.GroupBy(t => t.Priority ?? "Medium")
                                 .Select(g => new PriorityCount { Priority = g.Key, Count = g.Count() })
                                 .ToList(),
                CompletionRate = tasks.Count == 0 ? 0
                    : Math.Round((double)tasks.Count(t => statuses.IsTerminal(t.Status)) / tasks.Count * 100, 2)
            };
        }

        public MeetingAnalytics FetchMeetingAnalytics(string orgId, string? projectId, DateTime? startDate, DateTime? endDate)
        {
            var filter = BuildMeetingFilter(orgId, projectId, startDate, endDate);

            var total = _meetings.CountDocuments(filter);
            var upcoming = _meetings.CountDocuments(filter & Builders<Meeting>.Filter.Gt(m => m.ScheduledAt, DateTime.UtcNow));
            
            // Calculate average duration from filtered meetings
            var meetings = _meetings.Find(filter).Project(m => m.Duration).ToList();
            var avgDuration = meetings.Count > 0 ? meetings.Average(d => d ?? 0) : 0;

            return new MeetingAnalytics
            {
                TotalMeetings = (int)total,
                UpcomingMeetings = (int)upcoming,
                AverageDurationMinutes = Math.Round((double)avgDuration, 2)
            };
        }

        public List<TeamActivity> FetchTeamActivity(string orgId, int limit)
        {
            // Placeholder for now as activity logging might not be fully implemented or linked to org
            return new List<TeamActivity>
            {
                new() { Id = "1", Type = "test", Description = "Activity scoping to org pending implementation", UserName = "System", CreatedAt = DateTime.UtcNow }
            };
        }

        /// <summary>Every stored spelling that counts as finished, for a server-side filter.</summary>
        /// <remarks>
        /// Slugs AND their aliases: this runs in Mongo, which cannot call the resolver, so the
        /// legacy spellings have to be enumerated into the query until the data is normalized.
        /// </remarks>
        private static List<string> TerminalValues(TaskStatusSet statuses) =>
            statuses.Statuses
                .Where(s => s.IsTerminal)
                .SelectMany(s => new[] { s.Slug }.Concat(s.Aliases ?? new List<string>()))
                .Distinct()
                .ToList();

        private FilterDefinition<NoteTask> BuildTaskFilter(string orgId, string? projectId, DateTime? startDate, DateTime? endDate)
        {
            var builder = Builders<NoteTask>.Filter;

        // Queries the flat id rather than the embedded path. Equivalent by construction:
        // M001 copies the embedded value verbatim, so a document missing the flat id is a
        // document whose embedded path was missing too - and it now uses an index instead of
        // scanning the collection.
            var filter = builder.Eq(t => t.OrganizationId, orgId);

            if (!string.IsNullOrEmpty(projectId) && projectId != "all")
                filter &= builder.Eq(t => t.ProjectId, projectId);

            if (startDate.HasValue)
                filter &= builder.Gte(t => t.CreatedAt, startDate.Value);

            if (endDate.HasValue)
                filter &= builder.Lte(t => t.CreatedAt, endDate.Value);

            return filter;
        }

        private FilterDefinition<Project> BuildProjectFilter(string orgId, string? projectId, DateTime? startDate, DateTime? endDate)
        {
            var builder = Builders<Project>.Filter;
            var filter = builder.Eq(p => p.OrganizationId, orgId);

            if (!string.IsNullOrEmpty(projectId) && projectId != "all")
                filter &= builder.Eq(p => p.Id, projectId);

            if (startDate.HasValue)
                filter &= builder.Gte(p => p.CreatedAt, startDate.Value);

            if (endDate.HasValue)
                filter &= builder.Lte(p => p.CreatedAt, endDate.Value);

            return filter;
        }

        private FilterDefinition<Meeting> BuildMeetingFilter(string orgId, string? projectId, DateTime? startDate, DateTime? endDate)
        {
            var builder = Builders<Meeting>.Filter;
            
            if (!string.IsNullOrEmpty(projectId) && projectId != "all")
            {
                var f = builder.Eq(m => m.ProjectId, projectId);
                if (startDate.HasValue) f &= builder.Gte(m => m.ScheduledAt, startDate.Value);
                if (endDate.HasValue) f &= builder.Lte(m => m.ScheduledAt, endDate.Value);
                return f;
            }

            // For "all" projects, find all project IDs in the organization
            var projectIds = _projects.Find(p => p.OrganizationId == orgId).Project(p => p.Id).ToList();
            var filter = builder.In(m => m.ProjectId, projectIds);

            if (startDate.HasValue)
                filter &= builder.Gte(m => m.ScheduledAt, startDate.Value);

            if (endDate.HasValue)
                filter &= builder.Lte(m => m.ScheduledAt, endDate.Value);

            return filter;
        }
        //_activity.Find(_ => true)
        //    .SortByDescending(a => a.CreatedAt)
        //    .Limit(limit)
        //    .Project(a => new TeamActivity
        //    {
        //        Id = a.Id.ToString(),
        //        Type = a.Type,
        //        Description = a.Description,
        //        UserId = a.UserId,
        //        UserName = a.UserName,
        //        CreatedAt = a.CreatedAt
        //    })
        //    .ToList();
    }
}

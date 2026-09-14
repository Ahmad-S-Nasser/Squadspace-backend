using MongoDB.Driver;
using RafeeqyNotes.Api.Models;
using RafeeqyNotes.Api.Config;

namespace RafeeqyNotes.Api.Repositories
{
    public class ProjectScheduleRepository : IProjectScheduleRepository
    {
        private readonly IMongoCollection<ProjectSchedule> _schedules;

        public ProjectScheduleRepository(MongoDbSettings settings)
        {
            var client = new MongoClient(settings.ConnectionString);
            var database = client.GetDatabase(settings.DatabaseName);
            // Defaulting collection name here for simplicity
            _schedules = database.GetCollection<ProjectSchedule>("ProjectSchedules");
        }

        public async Task<ProjectSchedule?> GetByProjectIdAsync(string projectId)
        {
            return await _schedules.Find(s => s.ProjectId == projectId)
                .SortByDescending(s => s.ModifiedAt) // Always get the latest tracked schedule changes
                .FirstOrDefaultAsync();
        }

        /// <remarks>
        /// One query for every project on the page, instead of one query per project awaited in
        /// a loop. Each project's newest row wins, matching GetByProjectIdAsync - the collection
        /// is an audit log, so a project can hold several rows and only the latest is current.
        ///
        /// Sorted and de-duplicated in memory rather than with an aggregation: the row count per
        /// project is tiny, and the alternative ($sort + $group + $first) is markedly harder to
        /// read for no measurable gain at this size.
        /// </remarks>
        public async Task<Dictionary<string, ProjectSchedule>> GetByProjectIdsAsync(
            IEnumerable<string> projectIds)
        {
            var ids = (projectIds ?? Enumerable.Empty<string>())
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct()
                .ToList();

            var latest = new Dictionary<string, ProjectSchedule>(StringComparer.OrdinalIgnoreCase);
            if (ids.Count == 0) return latest;

            var rows = await _schedules
                .Find(Builders<ProjectSchedule>.Filter.In(s => s.ProjectId, ids))
                .SortByDescending(s => s.ModifiedAt)
                .ToListAsync();

            foreach (var row in rows)
            {
                // Rows arrive newest-first, so the first one seen for a project is the current
                // one and later rows are older history.
                if (!string.IsNullOrEmpty(row.ProjectId) && !latest.ContainsKey(row.ProjectId))
                {
                    latest[row.ProjectId] = row;
                }
            }

            return latest;
        }

        public async Task UpsertAsync(ProjectSchedule schedule)
        {
            schedule.Id = Guid.NewGuid().ToString(); // A new record acts as an audit log entry
            schedule.ModifiedAt = DateTime.UtcNow;
            await _schedules.InsertOneAsync(schedule);
        }
    }
}

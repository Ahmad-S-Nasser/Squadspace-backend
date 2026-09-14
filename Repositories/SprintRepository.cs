using MongoDB.Driver;
using RafeeqyNotes.Api.Config;
using RafeeqyNotes.Api.Models;
using RafeeqyNotes.Models;

namespace RafeeqyNotes.Api.Repositories
{
    public class SprintRepository : ISprintRepository
    {
        private readonly IMongoCollection<Sprint> _sprints;
        private readonly IMongoCollection<NoteTask> _tasks;

        public SprintRepository(MongoDbSettings settings)
        {
            var client = new MongoClient(settings.ConnectionString);
            var database = client.GetDatabase(settings.DatabaseName);

            _sprints = database.GetCollection<Sprint>("Sprints");
            _tasks = database.GetCollection<NoteTask>("Tasks");
        }

        public List<Sprint> FetchSprintsByProjectId(string projectId) =>
            _sprints.Find(s => s.ProjectId == projectId).ToList();
        public Sprint FetchSprintsById(string id) =>
            _sprints.Find(s => s.Id == id).FirstOrDefault();
        public Sprint InsertSprint(Sprint sprint)
        {
            sprint.CreatedAt = DateTime.UtcNow;
            sprint.UpdatedAt = DateTime.UtcNow;
            _sprints.InsertOne(sprint);
            return sprint;
        }

        public Sprint UpdateSprint(string id, Sprint sprint)
        {
            sprint.UpdatedAt = DateTime.UtcNow;
            _sprints.ReplaceOne(s => s.Id == id, sprint);
            return sprint;
        }

        public void DeleteSprint(string id) =>
            _sprints.DeleteOne(s => s.Id == id);

        public SprintVelocity CalculateSprintVelocity(string sprintId, IReadOnlyCollection<string> terminalStatuses)
        {
            var sprint = _sprints.Find(s => s.Id == sprintId).FirstOrDefault();
            if (sprint == null) return null;

            var tasks = _tasks.Find(t => t.SprintId == sprintId).ToList();

            var plannedPoints = tasks.Sum(t => t.EstimatedDuration);
            // Was a case-sensitive == "done", so a task stored as "Done" contributed nothing to
            // velocity. Matching is folded and alias-aware via the caller's terminal set.
            bool IsDone(NoteTask t) => t.Status != null && terminalStatuses.Contains(t.Status, StringComparer.OrdinalIgnoreCase);

            var completedTasks = tasks.Count(IsDone);
            var completedPoints = tasks.Where(IsDone).Sum(t => t.EstimatedDuration);

            return new SprintVelocity
            {
                SprintId = sprint.Id,
                SprintName = sprint.Name,
                PlannedPoints = plannedPoints??0,
                CompletedPoints = completedPoints??0,
                TaskCount = tasks.Count,
                CompletedTasks = completedTasks
            };
        }
    }
}

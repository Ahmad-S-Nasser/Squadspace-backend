using System.Globalization;
using System.Text;
using MongoDB.Driver;
using RafeeqyNotes.Api.Config;
using RafeeqyNotes.Api.Models;
using RafeeqyNotes.Models;

namespace RafeeqyNotes.Api.Services
{
    public interface IProjectExportService
    {
        /// <summary>CSV for one project, or every project in the organization when projectId is null.</summary>
        Task<string> ExportCsvAsync(string organizationId, string projectId, TaskStatusSet statuses);
    }

    /// <summary>
    /// Exports a project hierarchy as CSV.
    /// </summary>
    /// <remarks>
    /// "Basic export" has been a line in the Starter plan's feature list with no code behind it.
    /// This is the code.
    ///
    /// The column order is deliberately a SUPERSET of what the existing CSV importer reads. That
    /// importer takes fields positionally and ignores anything past index 7, so the first eight
    /// columns keep the exact meaning it expects and the rest ride along for a real importer to
    /// use. Export then import returns the same shape - which is the cheapest possible proof that
    /// the entity model can survive a round trip, and the thing a Jira importer has to be
    /// measured against.
    ///
    /// Note the shipped template disagrees with its own header: its task rows carry seven fields,
    /// so "todo" lands in Tags and the priority lands in Status. This writes all columns, always,
    /// so an exported file does not inherit that.
    ///
    /// Rows are ordered project -> board -> note -> task, because the importer's hierarchy is
    /// positional: a task belongs to the note above it.
    /// </remarks>
    public class ProjectExportService : IProjectExportService
    {
        /// <summary>
        /// The first eight are the legacy importer's positional contract and must not be reordered.
        /// </summary>
        private const string Header =
            "Level,Name,Description,Color,Mood,Tags,Status,Priority," +
            "Category,StartDate,DueDate,EstimatedMinutes,ActualMinutes,SprintId,ExternalId,ExternalSource,Id";

        private readonly IMongoCollection<Project> _projects;
        private readonly IMongoCollection<Board> _boards;
        private readonly IMongoCollection<Note> _notes;
        private readonly IMongoCollection<NoteTask> _tasks;

        public ProjectExportService(MongoDbSettings settings)
        {
            var database = new MongoClient(settings.ConnectionString).GetDatabase(settings.DatabaseName);
            _projects = database.GetCollection<Project>(settings.ProjectsCollection);
            _boards = database.GetCollection<Board>(settings.BoardsCollection);
            _notes = database.GetCollection<Note>(settings.NotesCollection);
            _tasks = database.GetCollection<NoteTask>("NoteTasks");
        }

        public async Task<string> ExportCsvAsync(string organizationId, string projectId, TaskStatusSet statuses)
        {
            var sb = new StringBuilder();
            sb.AppendLine(Header);

            if (string.IsNullOrWhiteSpace(organizationId)) return sb.ToString();

            // Organization first, always - the same rule as every other read path. A projectId is
            // an additional narrowing, never a way around the tenant filter.
            var projectFilter = Builders<Project>.Filter.Eq(p => p.OrganizationId, organizationId);
            if (!string.IsNullOrWhiteSpace(projectId))
            {
                projectFilter &= Builders<Project>.Filter.Eq(p => p.Id, projectId);
            }

            var projects = await _projects.Find(projectFilter).ToListAsync();
            if (projects.Count == 0) return sb.ToString();

            var projectIds = projects.Select(p => p.Id).ToList();

            // Three reads for the whole export rather than a query per node. The flat parent ids
            // from Phase 2 are what make this possible at all - the embedded walk could not be
            // filtered on server-side.
            var boards = await _boards
                .Find(Builders<Board>.Filter.In(b => b.ProjectId, projectIds))
                .ToListAsync();

            var boardIds = boards.Select(b => b.Id).ToList();

            var notes = await _notes
                .Find(Builders<Note>.Filter.And(
                    Builders<Note>.Filter.In(n => n.BoardId, boardIds),
                    Builders<Note>.Filter.Ne(n => n.IsDeleted, true)))
                .ToListAsync();

            var noteIds = notes.Select(n => n.Id).ToList();

            var tasks = await _tasks
                .Find(Builders<NoteTask>.Filter.In(t => t.NoteId, noteIds))
                .ToListAsync();

            var boardsByProject = boards.GroupBy(b => b.ProjectId ?? string.Empty)
                .ToDictionary(g => g.Key, g => g.ToList());
            var notesByBoard = notes.GroupBy(n => n.BoardId ?? string.Empty)
                .ToDictionary(g => g.Key, g => g.ToList());
            var tasksByNote = tasks.GroupBy(t => t.NoteId ?? string.Empty)
                .ToDictionary(g => g.Key, g => g.ToList());

            foreach (var project in projects.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
            {
                Row(sb, "project", project.Name, project.Description, project.Color,
                    externalId: project.ExternalId, externalSource: project.ExternalSource, id: project.Id);

                foreach (var board in Get(boardsByProject, project.Id).OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase))
                {
                    Row(sb, "board", board.Name, board.Description,
                        externalId: board.ExternalId, externalSource: board.ExternalSource, id: board.Id);

                    foreach (var note in Get(notesByBoard, board.Id).OrderBy(n => n.CreatedAt))
                    {
                        Row(sb, "note", note.Title, note.Content, mood: note.Mood,
                            tags: note.Tags == null ? null : string.Join(";", note.Tags),
                            externalId: note.ExternalId, externalSource: note.ExternalSource, id: note.Id);

                        foreach (var task in Get(tasksByNote, note.Id).OrderBy(t => t.CreatedAt))
                        {
                            Row(sb, "task", task.Title, task.Description,
                                // Canonical slug, not whatever spelling happens to be stored, so an
                                // exported file is consistent even where the data is not yet.
                                status: statuses.Resolve(task.Status),
                                priority: task.Priority,
                                category: task.Category,
                                startDate: task.StartDate,
                                dueDate: task.DueDate,
                                estimated: task.EstimatedDuration,
                                actual: task.RealDuration,
                                sprintId: task.SprintId,
                                externalId: task.ExternalId,
                                externalSource: task.ExternalSource,
                                id: task.Id);
                        }
                    }
                }
            }

            return sb.ToString();
        }

        private static List<T> Get<T>(Dictionary<string, List<T>> map, string key) =>
            key != null && map.TryGetValue(key, out var list) ? list : new List<T>();

        private static void Row(
            StringBuilder sb, string level, string name, string description = null,
            string color = null, string mood = null, string tags = null,
            string status = null, string priority = null, string category = null,
            DateTime? startDate = null, DateTime? dueDate = null,
            int? estimated = null, int? actual = null, string sprintId = null,
            string externalId = null, string externalSource = null, string id = null)
        {
            var cells = new[]
            {
                level, name, description, color, mood, tags, status, priority, category,
                Iso(startDate), Iso(dueDate),
                estimated?.ToString(CultureInfo.InvariantCulture),
                actual?.ToString(CultureInfo.InvariantCulture),
                sprintId, externalId, externalSource, id,
            };

            sb.AppendLine(string.Join(",", cells.Select(Escape)));
        }

        private static string Iso(DateTime? value) =>
            value?.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

        /// <summary>
        /// RFC 4180 escaping.
        /// </summary>
        /// <remarks>
        /// Note descriptions hold rich text with commas, quotes and newlines in them, so this is
        /// load-bearing rather than defensive: without it a single note body shifts every later
        /// column and the file silently imports as nonsense.
        /// </remarks>
        private static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;

            var needsQuotes = value.Contains(',') || value.Contains('"')
                              || value.Contains('\n') || value.Contains('\r');

            if (!needsQuotes) return value;

            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
    }
}

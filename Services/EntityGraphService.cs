using MongoDB.Driver;
using RafeeqyNotes.Api.Config;
using RafeeqyNotes.Api.Models;
using RafeeqyNotes.Models;

namespace RafeeqyNotes.Api.Services
{
    /// <inheritdoc cref="IEntityGraphService"/>
    public class EntityGraphService : IEntityGraphService
    {
        private readonly IMongoCollection<Organization> _organizations;
        private readonly IMongoCollection<Project> _projects;
        private readonly IMongoCollection<Board> _boards;
        private readonly IMongoCollection<Note> _notes;

        // Reads collections directly rather than taking the repository interfaces. Every
        // repository would otherwise have to depend on this service while this service depends
        // on them, and the whole DI graph is singleton - that is a hard cycle at construction
        // time, which in this app means startupError and a blanket 500 on every route.
        public EntityGraphService(MongoDbSettings settings)
        {
            var database = new MongoClient(settings.ConnectionString).GetDatabase(settings.DatabaseName);

            // "Organizations" is a literal here because MongoDbSettings has no property for it,
            // matching OrganizationRepository.cs:18. appsettings.json declares the key but the
            // settings class never bound it, so the literal IS the effective configuration.
            _organizations = database.GetCollection<Organization>("Organizations");
            _projects = database.GetCollection<Project>(settings.ProjectsCollection);
            _boards = database.GetCollection<Board>(settings.BoardsCollection);
            _notes = database.GetCollection<Note>(settings.NotesCollection);
        }

        public async Task HydrateProjectAsync(Project project, string organizationId = null)
        {
            if (project == null) return;

            var orgId = FirstNonBlank(organizationId, project.Organization?.Id);
            project.OrganizationId = null;
            if (string.IsNullOrWhiteSpace(orgId)) return;

            var org = await _organizations.Find(o => o.Id == orgId).FirstOrDefaultAsync();
            if (org == null)
            {
                // The id resolved to nothing. Record it anyway: a project whose organization
                // cannot be read still belongs to that organization, and OrgScope fails closed
                // on the id it cannot resolve rather than treating the resource as public.
                project.OrganizationId = orgId;
                return;
            }

            // Re-read rather than persisting the client's copy. The submitted Organization
            // carries a member list, and that list is what ends up embedded in every project,
            // board, note and task written afterwards.
            project.Organization = org;
            project.OrganizationId = org.Id;
        }

        public async Task HydrateBoardAsync(Board board, string projectId = null)
        {
            if (board == null) return;

            var pid = FirstNonBlank(projectId, board.Project?.Id);
            board.ProjectId = null;
            board.OrganizationId = null;
            if (string.IsNullOrWhiteSpace(pid)) return;

            var project = await _projects.Find(p => p.Id == pid).FirstOrDefaultAsync();
            if (project == null)
            {
                board.ProjectId = pid;
                return;
            }

            board.Project = project;
            board.ProjectId = project.Id;
            board.OrganizationId = OrgIdOf(project);
        }

        public async Task HydrateNoteAsync(Note note, string boardId = null)
        {
            if (note == null) return;

            var bid = FirstNonBlank(boardId, note.Board?.Id);
            note.BoardId = null;
            note.ProjectId = null;
            note.OrganizationId = null;
            if (string.IsNullOrWhiteSpace(bid)) return;

            var board = await _boards.Find(b => b.Id == bid).FirstOrDefaultAsync();
            if (board == null)
            {
                note.BoardId = bid;
                return;
            }

            note.Board = board;
            ApplyBoardLineage(board, id => note.BoardId = id, id => note.ProjectId = id, id => note.OrganizationId = id);
        }

        public async Task HydrateTaskAsync(NoteTask task, string noteId = null)
        {
            if (task == null) return;

            var nid = FirstNonBlank(noteId, task.Note?.Id);
            ClearTaskLinkage(task);
            if (string.IsNullOrWhiteSpace(nid)) return;

            var note = await _notes.Find(n => n.Id == nid).FirstOrDefaultAsync();
            if (note == null)
            {
                task.NoteId = nid;
                return;
            }

            ApplyNoteToTask(task, note);
        }

        public async Task HydrateTasksAsync(IEnumerable<NoteTask> tasks)
        {
            if (tasks == null) return;

            var list = tasks.Where(t => t != null).ToList();
            if (list.Count == 0) return;

            var noteIds = list
                .Select(t => t.Note?.Id)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct()
                .ToList();

            // One read for the whole batch. Importing a sprint's worth of tasks under one note
            // should not be N round trips for the same parent.
            var notes = noteIds.Count == 0
                ? new List<Note>()
                : await _notes.Find(Builders<Note>.Filter.In(n => n.Id, noteIds)).ToListAsync();

            var byId = notes
                .Where(n => !string.IsNullOrWhiteSpace(n.Id))
                .GroupBy(n => n.Id)
                .ToDictionary(g => g.Key, g => g.First());

            foreach (var task in list)
            {
                var nid = task.Note?.Id;
                ClearTaskLinkage(task);
                if (string.IsNullOrWhiteSpace(nid)) continue;

                if (byId.TryGetValue(nid, out var note)) ApplyNoteToTask(task, note);
                else task.NoteId = nid;
            }
        }

        private static void ApplyNoteToTask(NoteTask task, Note note)
        {
            task.Note = note;
            task.NoteId = note.Id;

            // A note written before this release has no flat ids of its own, so fall back to
            // its embedded board the same way OrgScope does.
            task.BoardId = FirstNonBlank(note.BoardId, note.Board?.Id);
            task.ProjectId = FirstNonBlank(note.ProjectId, note.Board?.Project?.Id);
            task.OrganizationId = FirstNonBlank(note.OrganizationId, OrgIdOf(note.Board?.Project));
        }

        private static void ApplyBoardLineage(
            Board board, Action<string> setBoardId, Action<string> setProjectId, Action<string> setOrgId)
        {
            setBoardId(board.Id);
            setProjectId(FirstNonBlank(board.ProjectId, board.Project?.Id));
            setOrgId(FirstNonBlank(board.OrganizationId, OrgIdOf(board.Project)));
        }

        private static void ClearTaskLinkage(NoteTask task)
        {
            task.NoteId = null;
            task.BoardId = null;
            task.ProjectId = null;
            task.OrganizationId = null;
        }

        private static string OrgIdOf(Project project) =>
            FirstNonBlank(project?.OrganizationId, project?.Organization?.Id);

        private static string FirstNonBlank(params string[] candidates)
        {
            foreach (var c in candidates)
            {
                if (!string.IsNullOrWhiteSpace(c)) return c;
            }
            return null;
        }
    }
}

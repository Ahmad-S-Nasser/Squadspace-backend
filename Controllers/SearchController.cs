using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using RafeeqyNotes.Api.Helpers;
using RafeeqyNotes.Api.Repositories;
using RafeeqyNotes.Api.Models;
using RafeeqyNotes.Models;

namespace RafeeqyNotes.Api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    // SECURITY: this searched every note, project and board in the database and returned
    // matches to anyone - including note CONTENT, so a query for a competitor's name or the
    // word "password" surfaced other tenants' material. Results are now restricted to the
    // caller's organizations, and the restriction is applied BEFORE the result cap so hidden
    // rows cannot consume the 20 slots and mask a caller's own matches.
    [Authorize]
    public class SearchController : ControllerBase
    {
        private readonly INoteRepository _notes;
        private readonly IProjectRepository _projects;
        private readonly IBoardRepository _boards;
        private readonly INoteTaskRepository _tasks;

        private readonly IOrganizationRepository _organizations;

        public SearchController(
            INoteRepository notes,
            IProjectRepository projects,
            IBoardRepository boards,
            INoteTaskRepository tasks,
            IOrganizationRepository organizations)
        {
            _notes = notes;
            _projects = projects;
            _boards = boards;
            _tasks = tasks;
            _organizations = organizations;
        }

        private HashSet<string> MyOrganizationIds()
        {
            var orgs = _organizations.FetchOrganizationsByUserId(OrgAccess.UserId(User))
                       ?? new List<Organization>();
            return new HashSet<string>(
                orgs.Where(o => !string.IsNullOrEmpty(o?.Id)).Select(o => o.Id),
                StringComparer.OrdinalIgnoreCase);
        }

        [HttpGet]
        public async Task<ActionResult<SearchResult>> Search([FromQuery] string q)
        {
            if (string.IsNullOrWhiteSpace(q))
                return Ok(new SearchResult());

            var query = q.ToLowerInvariant();

            try
            {
                // Fetch all collections in parallel
                var notesTask = _notes.GetAllAsync();
                var projectsTask = _projects.GetAllAsync();
                var boardsTask = _boards.GetAllAsync();

                await Task.WhenAll(notesTask, projectsTask, boardsTask);

                var myOrgIds = MyOrganizationIds();

                // Restrict to the caller's organizations FIRST, so the Take(20) below cannot
                // be filled with rows the caller may not see.
                var allNotes = (await notesTask ?? new List<Note>())
                    .Where(n => n?.Board?.Project?.Organization?.Id is string o && myOrgIds.Contains(o));
                var allProjects = (await projectsTask ?? new List<Project>())
                    .Where(p => OrgScope.OrgIdOf(p) is string o && myOrgIds.Contains(o));
                var allBoards = (await boardsTask ?? new List<Board>())
                    .Where(b => OrgScope.OrgIdOf(b?.Project) is string o && myOrgIds.Contains(o));

                var matchedNotes = allNotes
                    .Where(n =>
                        (!string.IsNullOrEmpty(n.Title) && n.Title.Contains(query, StringComparison.OrdinalIgnoreCase)) ||
                        (!string.IsNullOrEmpty(n.Content) && n.Content.Contains(query, StringComparison.OrdinalIgnoreCase)) ||
                        (n.Tags != null && n.Tags.Any(t => t.Contains(query, StringComparison.OrdinalIgnoreCase))))
                    .Take(20)
                    .ToList();

                var matchedProjects = allProjects
                    .Where(p =>
                        (!string.IsNullOrEmpty(p.Name) && p.Name.Contains(query, StringComparison.OrdinalIgnoreCase)) ||
                        (!string.IsNullOrEmpty(p.Description) && p.Description.Contains(query, StringComparison.OrdinalIgnoreCase)))
                    .Take(20)
                    .ToList();

                var matchedBoards = allBoards
                    .Where(b =>
                        (!string.IsNullOrEmpty(b.Name) && b.Name.Contains(query, StringComparison.OrdinalIgnoreCase)) ||
                        (!string.IsNullOrEmpty(b.Description) && b.Description.Contains(query, StringComparison.OrdinalIgnoreCase)))
                    .Take(20)
                    .ToList();

                // Search tasks via matched notes (safe approach)
                var matchedTasks = new List<NoteTask>();
                foreach (var note in matchedNotes.Take(5))
                {
                    if (string.IsNullOrEmpty(note.Id)) continue;
                    try
                    {
                        var noteTasks = await _tasks.GetByNoteIdAsync(note.Id);
                        if (noteTasks != null)
                            matchedTasks.AddRange(noteTasks);
                    }
                    catch
                    {
                        // Skip if task query fails for this note
                    }
                }

                return Ok(new SearchResult
                {
                    Notes = matchedNotes,
                    Projects = matchedProjects,
                    Boards = matchedBoards,
                    Tasks = matchedTasks.Take(20).ToList()
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Search error: {ex.Message}");
                return Ok(new SearchResult()); // Return empty results on error
            }
        }
    }

    public class SearchResult
    {
        public List<Note> Notes { get; set; } = new();
        public List<Project> Projects { get; set; } = new();
        public List<Board> Boards { get; set; } = new();
        public List<NoteTask> Tasks { get; set; } = new();
    }
}
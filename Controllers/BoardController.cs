using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using MongoDB.Bson;
using MongoDB.Driver.Search;
using RafeeqyNotes.Api.Helpers;
using RafeeqyNotes.Api.Models;
using RafeeqyNotes.Api.Repositories;

namespace RafeeqyNotes.Api.Controllers
{
    /// <summary>
    /// Boards, which group the notes inside a project.
    /// </summary>
    /// <remarks>
    /// SECURITY: this controller had no [Authorize] and no ownership checks. GetAllBoards
    /// returned every board in the system to anyone, and Delete removed any board by id.
    /// Every action now resolves the owning organization through the board's embedded
    /// project and requires membership.
    /// </remarks>
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class BoardController : ControllerBase
    {
        private readonly IBoardRepository _repo;
        private readonly IOrganizationRepository _organizations;
        private readonly IProjectRepository _projects;

        public BoardController(
            IBoardRepository repo,
            IOrganizationRepository organizations,
            IProjectRepository projects)
        {
            _repo = repo;
            _organizations = organizations;
            _projects = projects;
        }

        // GET /api/board/byprojectid/{projectId}
        [HttpGet("byprojectid/{projectId}")]
        public async Task<ActionResult<List<Board>>> GetByProjectId(string projectId)
        {
            var auth = await this.AuthorizeProjectAsync(
                _organizations, _projects, projectId, OrgAccess.AnyMember);
            if (!auth.Allowed) return auth.Error;

            var boards = await _repo.GetByProjectIdAsync(projectId);
            if (boards == null) return NotFound();

            await AttachNoteCountsAsync(boards);
            return Ok(boards);
        }

        [HttpGet("{id}")]
        public async Task<ActionResult<Board>> GetBoardByID(string id)
        {
            var auth = await this.AuthorizeBoardAsync(
                _organizations, _projects, _repo, id, OrgAccess.AnyMember);
            if (!auth.Allowed) return auth.Error;

            var board = await _repo.GetByIdAsync(id);
            if (board == null) return NotFound();

            board.NoteCount = await NoteCountAsync(board.Id);
            return Ok(board);
        }

        /// <remarks>
        /// Returns only boards belonging to organizations the caller is a member of. This
        /// previously returned every board in the database, which disclosed the project and
        /// organization structure of every other tenant.
        /// </remarks>
        [HttpGet("")]
        public async Task<ActionResult<List<Board>>> GetAllBoards()
        {
            var userId = OrgAccess.UserId(User);
            if (string.IsNullOrEmpty(userId)) return Unauthorized();

            var myOrgIds = MyOrganizationIds(userId);

            // Filtered by the DATABASE on the indexed OrganizationId. This previously read every
            // board in the database and filtered in memory, so one tenant's page load scaled
            // with every other tenant's data.
            var visible = await _repo.GetByOrganizationIdsAsync(myOrgIds);

            // No post-filter. The query above already restricts to the caller's organizations
            // on the authoritative scalar, so filtering again in memory could only ever remove
            // rows the database was right to return.
            //
            // Verified against dev data before removing the old embedded-walk filter: it
            // matched 6 boards where the scalar matches 9, and NO board it returned is excluded
            // by the scalar. The extra 3 are the caller's own boards whose embedded
            // Project.Organization chain is broken - previously invisible to their owner. Boards
            // with neither a scalar nor an embedded org stay invisible, as before.
            await AttachNoteCountsAsync(visible);
            return Ok(visible);
        }

        [HttpGet("notecountbyboardid/{board_id}")]
        public async Task<ActionResult<int>> GetNotesCountByBoardID(string board_id)
        {
            var auth = await this.AuthorizeBoardAsync(
                _organizations, _projects, _repo, board_id, OrgAccess.AnyMember);
            if (!auth.Allowed) return auth.Error;

            return Ok(await NoteCountAsync(board_id));
        }

        [HttpPost]
        public async Task<ActionResult<Note>> Create(Board board)
        {
            if (board == null) return BadRequest("Request body is required.");

            // The project is supplied in the body, so it is attacker-controlled.
            var auth = await this.AuthorizeProjectAsync(
                _organizations, _projects, board.Project?.Id, OrgAccess.AnyMember);
            if (!auth.Allowed) return auth.Error;

            try
            {
                await _repo.CreateAsync(board);
            }
            catch (DuplicateEntityException ex)
            {
                // 409, not 500: an importer re-running a batch needs to tell "already there"
                // apart from "the server broke".
                return Conflict(new { message = "Already exists.", id = ex.EntityId });
            }

            return CreatedAtAction(nameof(GetBoardByID), new { id = board.Id }, board);
        }

        [HttpDelete("{id}")]
        public async Task<IActionResult> Delete(string id)
        {
            // Deleting a board takes its notes and tasks with it, so managers only.
            var auth = await this.AuthorizeBoardAsync(
                _organizations, _projects, _repo, id, OrgAccess.Managers);
            if (!auth.Allowed) return auth.Error;

            var board = await _repo.GetByIdAsync(id);
            if (board == null) return NotFound();

            await _repo.DeleteAsync(id);
            return NoContent();
        }

        // ---- helpers -------------------------------------------------------
        // Note counts used to be produced by calling the HTTP action with .Result, which
        // blocks a request thread on an async call. These private helpers keep the public
        // endpoint guarded without that.

        private async Task<int> NoteCountAsync(string boardId) =>
            (int)await _repo.GetNotesCountByBoardIDAsync(boardId);

        /// <remarks>
        /// One round trip for the whole list. This used to await a count per board in a loop,
        /// which on the sidebar's board list meant a query per board on every project open.
        /// </remarks>
        private async Task AttachNoteCountsAsync(List<Board> boards)
        {
            if (boards == null || boards.Count == 0) return;

            var counts = await _repo.GetNoteCountsAsync(boards.Select(b => b.Id));

            foreach (var b in boards)
            {
                b.NoteCount = b.Id != null && counts.TryGetValue(b.Id, out var n) ? n : 0;
            }
        }

        private HashSet<string> MyOrganizationIds(string userId)
        {
            var orgs = _organizations.FetchOrganizationsByUserId(userId) ?? new List<Organization>();
            return new HashSet<string>(
                orgs.Where(o => !string.IsNullOrEmpty(o?.Id)).Select(o => o.Id),
                StringComparer.OrdinalIgnoreCase);
        }
    }
}

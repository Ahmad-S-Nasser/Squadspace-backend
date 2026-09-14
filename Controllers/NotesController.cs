using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using RafeeqyNotes.Api.Helpers;
using RafeeqyNotes.Api.Models;
using RafeeqyNotes.Api.Repositories;

namespace RafeeqyNotes.Api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    // SECURITY: default-deny. Only the two public share endpoints below are marked
    // [AllowAnonymous]; everything else requires authentication AND membership of the
    // organization owning the note's board. GetAllNotes previously returned every note
    // in the database, and share links could be minted or revoked on any note by id.
    [Authorize]
    public class NotesController : ControllerBase
    {
        private readonly INoteRepository _repo;

        private readonly IOrganizationRepository _organizations;
        private readonly IProjectRepository _projects;

        public NotesController(
            INoteRepository repo,
            IOrganizationRepository organizations,
            IProjectRepository projects)
        {
            _repo = repo;
            _organizations = organizations;
            _projects = projects;
        }

        /// <summary>Authorizes against the organization owning the note's board project.</summary>
        private async Task<OrgAuth> AuthorizeNoteAsync(string noteId)
        {
            var note = await _repo.GetByIdAsync(noteId);
            return await this.AuthorizeProjectAsync(
                _organizations, _projects, note?.Board?.Project?.Id, OrgAccess.AnyMember);
        }

        private HashSet<string> MyOrganizationIds()
        {
            var orgs = _organizations.FetchOrganizationsByUserId(OrgAccess.UserId(User))
                       ?? new List<Organization>();
            return new HashSet<string>(
                orgs.Where(o => !string.IsNullOrEmpty(o?.Id)).Select(o => o.Id),
                StringComparer.OrdinalIgnoreCase);
        }

        [HttpGet("byid/{id}")]
        public async Task<ActionResult<Note>> GetNoteByID(string id)
        {
            var auth = await AuthorizeNoteAsync(id);
            if (!auth.Allowed) return auth.Error;

            var note = await _repo.GetByIdAsync(id);
            if (note == null) return NotFound();
            return Ok(note);
        }

        [HttpGet("")]
        public async Task<ActionResult<List<Note>>> GetAllNotes()
        {
            // Was every note in the database. Scoped to the caller's organizations.
            var myOrgIds = MyOrganizationIds();

            var notes = await _repo.GetAllAsync();
            if (notes == null) return NotFound();

            var visible = notes
                .Where(n => n?.Board?.Project?.Organization?.Id is string o && myOrgIds.Contains(o))
                .ToList();

            return Ok(visible);
        }

        [HttpGet("byboardid/{board_id}")]
        public async Task<ActionResult<List<Note>>> GetNotesByBoardID(string board_id)
        {
            var notes = await _repo.GetNotesByBoardIDAsync(board_id);
            if (notes == null) return NotFound();

            // Addressed by board id, and notes embed their board's project, so the first
            // note resolves the organization for the whole board without an extra read.
            // An empty board has nothing to disclose, so it is returned as-is.
            if (notes.Count > 0)
            {
                var auth = await this.AuthorizeProjectAsync(
                    _organizations, _projects, notes[0].Board?.Project?.Id, OrgAccess.AnyMember);
                if (!auth.Allowed) return auth.Error;
            }

            return Ok(notes);
        }

        [HttpPost]
        public async Task<ActionResult<Note>> Create(Note note)
        {
            if (note == null) return BadRequest("Request body is required.");

            // Parent board comes from the body, so it is attacker-controlled.
            var auth = await this.AuthorizeProjectAsync(
                _organizations, _projects, note.Board?.Project?.Id, OrgAccess.AnyMember);
            if (!auth.Allowed) return auth.Error;

            try
            {
                await _repo.CreateAsync(note);
            }
            catch (DuplicateEntityException ex)
            {
                // 409, not 500: an importer re-running a batch needs to tell "already there"
                // apart from "the server broke".
                return Conflict(new { message = "Already exists.", id = ex.EntityId });
            }

            return CreatedAtAction(nameof(GetNoteByID), new { id = note.Id }, note);
        }

        /// <summary>Updates a note's editable fields.</summary>
        /// <remarks>
        /// There was no update route on this controller at all, while the frontend has always
        /// called PUT /api/Notes/{id} — so every save 404'd and note editing simply did not
        /// work. (The clipped Save button in the note dialog masked this: users usually
        /// couldn't reach the button to find out.)
        ///
        /// Editing is allowed for the note's author AND anyone in its Contributors list,
        /// which is what that list exists for — notes are collaborative. Deletion stays
        /// author-only, because it is destructive.
        ///
        /// Only title/content/mood/tags are writable. AuthorId, Board, IsPublic, ShareToken
        /// and the soft-delete fields are deliberately NOT taken from the request: accepting
        /// the whole entity would let a caller reassign authorship or silently publish a note.
        /// </remarks>
        [Authorize]
        [HttpPut("{id}")]
        public async Task<ActionResult<Note>> UpdateNote(string id, [FromBody] UpdateNoteRequest request)
        {
            var auth = await AuthorizeNoteAsync(id);
            if (!auth.Allowed) return auth.Error;

            var callerId = OrgAccess.UserId(User);
            if (string.IsNullOrEmpty(callerId))
                return Unauthorized(new { message = "Authentication required" });
            if (request == null)
                return BadRequest(new { message = "Request body is required" });

            var note = await _repo.GetByIdAsync(id);
            if (note == null) return NotFound(new { message = "Note not found" });

            var isAuthor = string.Equals(note.AuthorId, callerId, StringComparison.OrdinalIgnoreCase);
            var isContributor = note.Contributors != null && note.Contributors.Any(c =>
                c != null && string.Equals(c.Id, callerId, StringComparison.OrdinalIgnoreCase));

            if (!isAuthor && !isContributor)
            {
                return StatusCode(403, new
                {
                    message = "Insufficient permissions",
                    detail = "Only the author or a contributor of this note can edit it."
                });
            }

            if (request.Title != null) note.Title = request.Title;
            if (request.Content != null) note.Content = request.Content;
            if (request.Tags != null) note.Tags = request.Tags;

            // Mood is title-cased on create; keep the stored form consistent and don't
            // index past the end of an empty string the way CreateAsync does.
            if (!string.IsNullOrWhiteSpace(request.Mood))
            {
                note.Mood = char.ToUpper(request.Mood[0]) + request.Mood.Substring(1);
            }

            note.UpdatedAt = DateTime.UtcNow;

            await _repo.UpdateAsync(note);
            return Ok(note);
        }

        /// <summary>Deletes a note. Only its author may do this.</summary>
        /// <remarks>
        /// This is a SOFT delete: the document is retained with IsDeleted/DeletedAt/DeletedBy
        /// so reporting and analytics keep a complete history, and every read path filters
        /// deleted notes out. Any public share link is revoked at the same time, otherwise a
        /// "deleted" note would remain readable by URL.
        ///
        /// There was previously no delete endpoint at all — only DELETE {id}/share — which is
        /// why testers reported being unable to delete their own notes.
        /// </remarks>
        [Authorize]
        [HttpDelete("{id}")]
        public async Task<IActionResult> DeleteNote(string id)
        {
            var auth = await AuthorizeNoteAsync(id);
            if (!auth.Allowed) return auth.Error;

            var callerId = OrgAccess.UserId(User);
            if (string.IsNullOrEmpty(callerId))
                return Unauthorized(new { message = "Authentication required" });

            // Read including deleted so a repeat delete reports "already gone" rather than
            // a misleading 404-as-if-never-existed.
            var note = await _repo.GetByIdIncludingDeletedAsync(id);
            if (note == null) return NotFound(new { message = "Note not found" });

            if (!string.Equals(note.AuthorId, callerId, StringComparison.OrdinalIgnoreCase))
            {
                return StatusCode(403, new
                {
                    message = "Insufficient permissions",
                    detail = "Only the author of a note can delete it."
                });
            }

            if (note.IsDeleted) return NoContent();

            await _repo.SoftDeleteAsync(id, callerId);
            return NoContent();
        }

        [HttpPost("{id}/share")]
        public async Task<ActionResult<object>> ShareNote(string id)
        {
            var auth = await AuthorizeNoteAsync(id);
            if (!auth.Allowed) return auth.Error;

            var note = await _repo.GetByIdAsync(id);
            if (note == null) return NotFound();

            note.IsPublic = true;
            note.ShareToken = Guid.NewGuid().ToString("N");
            await _repo.UpdateAsync(note);

            return Ok(new { shareToken = note.ShareToken });
        }

        [HttpDelete("{id}/share")]
        public async Task<ActionResult> RevokeShare(string id)
        {
            var auth = await AuthorizeNoteAsync(id);
            if (!auth.Allowed) return auth.Error;

            var note = await _repo.GetByIdAsync(id);
            if (note == null) return NotFound();

            note.IsPublic = false;
            note.ShareToken = null;
            await _repo.UpdateAsync(note);

            return NoContent();
        }

        [HttpGet("shared/{token}")]
        [Microsoft.AspNetCore.Authorization.AllowAnonymous]
        public async Task<ActionResult<object>> GetSharedNote(string token)
        {
            var note = await _repo.GetByShareTokenAsync(token);
            if (note == null) return NotFound();

            // Return a limited "shared" object to avoid leaking sensitive info if any
            return Ok(new
            {
                note.Title,
                note.Content,
                note.Mood,
                note.Tags,
                AuthorName = note.Author?.Name ?? "Anonymous",
                note.UpdatedAt
            });
        }

        [HttpGet("public-id/{id}")]
        [Microsoft.AspNetCore.Authorization.AllowAnonymous]
        public async Task<ActionResult<object>> GetPublicNoteById(string id)
        {
            var note = await _repo.GetByIdAsync(id);
            if (note == null || !note.IsPublic) return NotFound();

            return Ok(new
            {
                note.Title,
                note.Content,
                note.Mood,
                note.Tags,
                AuthorName = note.Author?.Name ?? "Anonymous",
                note.UpdatedAt
            });
        }
    }

    /// <summary>
    /// The editable surface of a note. Anything not listed here is server-owned:
    /// authorship, board membership, share state and soft-delete flags cannot be set
    /// by a client. Null means "leave unchanged".
    /// </summary>
    public record UpdateNoteRequest(string? Title, string? Content, string? Mood, List<string>? Tags);
}

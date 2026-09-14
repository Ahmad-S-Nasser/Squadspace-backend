using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using RafeeqyNotes.Api.Helpers;
using RafeeqyNotes.Api.Models;
using RafeeqyNotes.Api.Repositories;
using RafeeqyNotes.Api.Hubs;

namespace RafeeqyNotes.Api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    // Authentication is required for every endpoint here. This controller previously had
    // no [Authorize] at all, so any anonymous caller could reach it. Org-level scoping
    // (who may see WHICH org's data) is a separate, later step - see deploy\DEPLOY.md.
    [Authorize]
    public class WhiteboardController : ControllerBase
    {
        private readonly IWhiteboardRepository _repo;
        private readonly IHubContext<WhiteboardHub> _hubContext;

        // In-memory presence store (could be replaced with Redis for scaling)
        private static readonly Dictionary<string, List<WhiteboardPresence>> _presence
            = new Dictionary<string, List<WhiteboardPresence>>();


        private readonly IOrganizationRepository _organizations;
        private readonly IProjectRepository _projects;

        public WhiteboardController(
            IWhiteboardRepository repo,
            IHubContext<WhiteboardHub> hubContext,
            IOrganizationRepository organizations,
            IProjectRepository projects)
        {
            _repo = repo;
            _hubContext = hubContext;
            _organizations = organizations;
            _projects = projects;
        }

        /// <summary>
        /// Authorizes against the organization owning the whiteboard's project.
        /// A missing whiteboard and one in another organization both yield the same 404.
        /// </summary>
        private async Task<OrgAuth> AuthorizeWhiteboardAsync(string whiteboardId)
        {
            var board = await _repo.GetByIdAsync(whiteboardId);
            return await this.AuthorizeProjectAsync(
                _organizations, _projects, board?.ProjectId, OrgAccess.AnyMember);
        }

        // 1. GET /api/Whiteboard/project/{projectId}
        [HttpGet("project/{projectId}")]
        public async Task<ActionResult<List<Whiteboard>>> GetByProjectId(string projectId)
        {
            var auth = await this.AuthorizeProjectAsync(
                _organizations, _projects, projectId, OrgAccess.AnyMember);
            if (!auth.Allowed) return auth.Error;

            var boards = await _repo.GetByProjectIdAsync(projectId);
            return Ok(boards);
        }

        // 2. GET /api/Whiteboard/{id}
        [HttpGet("{id}")]
        public async Task<ActionResult<Whiteboard>> GetById(string id)
        {
            var auth = await AuthorizeWhiteboardAsync(id);
            if (!auth.Allowed) return auth.Error;

            var board = await _repo.GetByIdAsync(id);
            if (board == null) return NotFound();
            return Ok(board);
        }

        // 3. POST /api/Whiteboard
        [HttpPost]
        public async Task<ActionResult<Whiteboard>> Create([FromBody] CreateWhiteboardInput input)
        {
            if (input == null) return BadRequest("Request body is required.");

            // Project comes from the body, so it is attacker-controlled.
            var auth = await this.AuthorizeProjectAsync(
                _organizations, _projects, input.ProjectId, OrgAccess.AnyMember);
            if (!auth.Allowed) return auth.Error;

            var board = new Whiteboard
            {
                Id = Guid.NewGuid().ToString(),
                ProjectId = input.ProjectId,
                Name = input.Name,
                Description = input.Description,
                LinkedItem = input.LinkedItem,
                SharedWith = input.SharedWith,
                Elements = new List<WhiteboardElement>(),
                CreatedBy = auth.UserId,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            await _repo.CreateAsync(board);
            return CreatedAtAction(nameof(GetById), new { id = board.Id }, board);
        }

        // 4. PUT /api/Whiteboard/{id}
        [HttpPut("{id}")]
        public async Task<ActionResult<Whiteboard>> Update(string id, [FromBody] UpdateWhiteboardInput input)
        {
            var auth = await AuthorizeWhiteboardAsync(id);
            if (!auth.Allowed) return auth.Error;

            var board = await _repo.GetByIdAsync(id);
            if (board == null) return NotFound();

            if (!string.IsNullOrEmpty(input.Name)) board.Name = input.Name;
            if (!string.IsNullOrEmpty(input.Description)) board.Description = input.Description;
            board.UpdatedAt = DateTime.UtcNow;

            await _repo.UpdateAsync(board);
            return Ok(board);
        }

        // 5. DELETE /api/Whiteboard/{id}
        [HttpDelete("{id}")]
        public async Task<ActionResult<bool>> Delete(string id)
        {
            var auth = await AuthorizeWhiteboardAsync(id);
            if (!auth.Allowed) return auth.Error;

            var board = await _repo.GetByIdAsync(id);
            if (board == null) return NotFound();

            var result = await _repo.DeleteAsync(id);
            return Ok(result);
        }

        // 6. POST /api/Whiteboard/{whiteboardId}/elements
        [HttpPost("{whiteboardId}/elements")]
        public async Task<ActionResult<WhiteboardElement>> AddElement(string whiteboardId, [FromBody] WhiteboardElement element)
        {
            var auth = await AuthorizeWhiteboardAsync(whiteboardId);
            if (!auth.Allowed) return auth.Error;

            var board = await _repo.GetByIdAsync(whiteboardId);
            if (board == null) return NotFound();

            element.Id = Guid.NewGuid().ToString();
            element.CreatedAt = DateTime.UtcNow;
            element.UpdatedAt = DateTime.UtcNow;
            element.CreatedBy = auth.UserId;
            switch (element)
            {
                case ConnectionElement conn when element is ConnectionElement updatedConn:
                    conn.FromElementId = "";// updatedConn.FromElementId ?? conn.FromElementId;
                    conn.ToElementId = "";// updatedConn.ToElementId ?? conn.ToElementId;
                    conn.StrokeColor = updatedConn.StrokeColor ?? conn.StrokeColor;
                    conn.StrokeWidth = updatedConn.StrokeWidth;
                    conn.ArrowHead = updatedConn.ArrowHead;
                    conn.StartPoint = updatedConn.StartPoint;
                    conn.EndPoint = updatedConn.EndPoint;
                    break;
            }

            await _repo.AddElementAsync(whiteboardId, element);

            // Broadcast via SignalR
            await _hubContext.Clients.Group(whiteboardId).SendAsync("ElementAdded", element);

            return Ok(element);
        }

        // PUT /api/Whiteboard/{whiteboardId}/elements/{elementId}
        [HttpPut("{whiteboardId}/elements/{elementId}")]
        public async Task<ActionResult<WhiteboardElement>> UpdateElement(string whiteboardId, string elementId, [FromBody] WhiteboardElement updatedElement)
        {
            var auth = await AuthorizeWhiteboardAsync(whiteboardId);
            if (!auth.Allowed) return auth.Error;

            var board = await _repo.GetByIdAsync(whiteboardId);
            if (board == null) return NotFound();

            var element = board.Elements.FirstOrDefault(e => e.Id == elementId);
            if (element == null) return NotFound();

            // Update common fields
            element.Position = updatedElement.Position ?? element.Position;
            element.ZIndex = updatedElement.ZIndex;
            element.Opacity = updatedElement.Opacity ?? element.Opacity;
            element.UpdatedAt = DateTime.UtcNow;
            
            // Type-specific updates
            switch (element)
            {
                case StickyNoteElement sticky when updatedElement is StickyNoteElement updatedSticky:
                    sticky.Content = updatedSticky.Content ?? sticky.Content;
                    sticky.Color = updatedSticky.Color ?? sticky.Color;
                    sticky.Size = updatedSticky.Size ?? sticky.Size;
                    sticky.LinkedItem = updatedSticky.LinkedItem ?? sticky.LinkedItem;
                    break;

                case ShapeElement shape when updatedElement is ShapeElement updatedShape:
                    shape.ShapeType = updatedShape.ShapeType ?? shape.ShapeType;
                    shape.Size = updatedShape.Size ?? shape.Size;
                    shape.Fill = updatedShape.Fill ?? shape.Fill;
                    shape.Stroke = updatedShape.Stroke ?? shape.Stroke;
                    shape.StrokeWidth = updatedShape.StrokeWidth;
                    shape.Text = updatedShape.Text ?? shape.Text;
                    shape.FromElementId = updatedShape.FromElementId;
                    shape.ToElementId = updatedShape.ToElementId;
                    shape.FromPort = updatedShape.FromPort;
                    shape.ToPort = updatedShape.ToPort;
                    shape.StartPoint = updatedShape.StartPoint;
                    shape.EndPoint = updatedShape.EndPoint;
                    shape.StrokeType = updatedShape.StrokeType ?? shape.StrokeType;
                    break;

                case ImageElement image when updatedElement is ImageElement updatedImage:
                    image.Size = updatedImage.Size ?? image.Size;
                    break;

                case TextElement text when updatedElement is TextElement updatedText:
                    text.Content = updatedText.Content ?? text.Content;
                    text.Size = updatedText.Size ?? text.Size;
                    text.FontSize = updatedText.FontSize;
                    text.FontWeight = updatedText.FontWeight ?? text.FontWeight;
                    text.Color = updatedText.Color ?? text.Color;
                    break;

                case ConnectionElement conn when updatedElement is ConnectionElement updatedConn:
                    conn.FromElementId = updatedConn.FromElementId ?? conn.FromElementId;
                    conn.ToElementId = updatedConn.ToElementId ?? conn.ToElementId;
                    conn.StrokeColor = updatedConn.StrokeColor ?? conn.StrokeColor;
                    conn.StrokeWidth = updatedConn.StrokeWidth;
                    conn.ArrowHead = updatedConn.ArrowHead;
                    conn.StartPoint = updatedConn.StartPoint;
                    conn.EndPoint = updatedConn.EndPoint;
                    break;
            }

            await _repo.UpdateElementAsync(whiteboardId, elementId, element);

            // Broadcast via SignalR
            await _hubContext.Clients.Group(whiteboardId).SendAsync("ElementUpdated", element);

            return Ok(element);
        }

        // DELETE /api/Whiteboard/{whiteboardId}/elements/{elementId}
        [HttpDelete("{whiteboardId}/elements/{elementId}")]
        public async Task<ActionResult<bool>> DeleteElement(string whiteboardId, string elementId)
        {
            var auth = await AuthorizeWhiteboardAsync(whiteboardId);
            if (!auth.Allowed) return auth.Error;

            var board = await _repo.GetByIdAsync(whiteboardId);
            if (board == null) return NotFound();

            var element = board.Elements.FirstOrDefault(e => e.Id == elementId);
            if (element == null) return NotFound();

            await _repo.RemoveElementAsync(whiteboardId, elementId);

            // Broadcast via SignalR
            await _hubContext.Clients.Group(whiteboardId).SendAsync("ElementDeleted", elementId);

            return Ok(true);
        }
        // --- Presence Endpoints ---

        // GET /api/Whiteboard/{id}/presence
        [HttpGet("{id}/presence")]
        public async Task<ActionResult<List<WhiteboardPresence>>> GetPresence(string id)
        {
            // Presence names who is currently on a board, so it is org data like any other.
            var auth = await AuthorizeWhiteboardAsync(id);
            if (!auth.Allowed) return auth.Error;

            if (!_presence.ContainsKey(id)) return Ok(new List<WhiteboardPresence>());
            return Ok(_presence[id]);
        }

        // POST /api/Whiteboard/{id}/presence
        [HttpPost("{id}/presence")]
        public async Task<ActionResult<WhiteboardPresence>> AddPresence(string id, [FromBody] WhiteboardPresence presence)
        {
            var auth = await AuthorizeWhiteboardAsync(id);
            if (!auth.Allowed) return auth.Error;

            if (!_presence.ContainsKey(id))
                _presence[id] = new List<WhiteboardPresence>();

            var existing = _presence[id].FirstOrDefault(p => p.UserId == presence.UserId);
            if (existing == null)
            {
                _presence[id].Add(presence);
            }
            else
            {
                existing.IsEditing = presence.IsEditing;
                existing.LastActive = DateTime.UtcNow;
            }

            // Broadcast via SignalR
            await _hubContext.Clients.Group(id).SendAsync("PresenceUpdated", presence);

            return Ok(presence);
        }

        // DELETE /api/Whiteboard/{id}/presence/{userId}
        [HttpDelete("{id}/presence/{userId}")]
        public async Task<ActionResult<bool>> RemovePresence(string id, string userId)
        {
            var auth = await AuthorizeWhiteboardAsync(id);
            if (!auth.Allowed) return auth.Error;

            if (!_presence.ContainsKey(id)) return Ok(false);

            var existing = _presence[id].FirstOrDefault(p => p.UserId == userId);
            if (existing != null)
            {
                _presence[id].Remove(existing);

                // Broadcast via SignalR
                await _hubContext.Clients.Group(id).SendAsync("UserLeftWhiteboard", userId);

                return Ok(true);
            }

            return Ok(false);
        }

    }
}
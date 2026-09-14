using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using RafeeqyNotes.Api.Helpers;
using RafeeqyNotes.Api.Models;
using RafeeqyNotes.Api.Repositories;
using RafeeqyNotes.Api.Services;

namespace RafeeqyNotes.Api.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class AttachmentsController : ControllerBase
    {
        private readonly IAttachmentRepository _repo;
        private readonly IWebHostEnvironment _env;

        private readonly IOrganizationRepository _organizations;
        private readonly IProjectRepository _projects;
        private readonly IBoardRepository _boards;
        private readonly INoteRepository _notes;
        private readonly INoteTaskRepository _tasks;
        private readonly IWhiteboardRepository _whiteboards;
        private readonly IEntitlementService _entitlements;

        public AttachmentsController(
            IAttachmentRepository repo,
            IWebHostEnvironment env,
            IOrganizationRepository organizations,
            IProjectRepository projects,
            IBoardRepository boards,
            INoteRepository notes,
            INoteTaskRepository tasks,
            IWhiteboardRepository whiteboards,
            IEntitlementService entitlements)
        {
            _repo = repo;
            _env = env;
            _organizations = organizations;
            _projects = projects;
            _boards = boards;
            _notes = notes;
            _tasks = tasks;
            _whiteboards = whiteboards;
            _entitlements = entitlements;
        }

        /// <summary>
        /// Authorizes against the organization owning whatever the attachment hangs off.
        /// </summary>
        /// <remarks>
        /// Attachments are addressed polymorphically by (entityType, entityId), so the owning
        /// project has to be resolved per type. An unrecognised type is refused rather than
        /// waved through — a new entity type must be added here deliberately, not inherit
        /// unrestricted access by default.
        /// </remarks>
        private async Task<OrgAuth> AuthorizeEntityAsync(string entityType, string entityId)
        {
            string projectId = null;

            switch ((entityType ?? string.Empty).ToLowerInvariant())
            {
                case "project":
                    projectId = entityId;
                    break;
                case "board":
                    var board = await _boards.GetByIdAsync(entityId);
                    projectId = board?.Project?.Id;
                    break;
                case "note":
                    var note = await _notes.GetByIdAsync(entityId);
                    projectId = note?.Board?.Project?.Id;
                    break;
                case "task":
                    var task = await _tasks.GetByIdAsync(entityId);
                    projectId = OrgScope.ProjectIdOfTask(task);
                    break;
                case "whiteboard":
                    var whiteboard = await _whiteboards.GetByIdAsync(entityId);
                    projectId = whiteboard?.ProjectId;
                    break;
            }

            // A null projectId (unknown type, missing entity, or an entity whose parent chain
            // cannot be resolved) yields the same 404 as "not yours".
            return await this.AuthorizeProjectAsync(
                _organizations, _projects, projectId, OrgAccess.AnyMember);
        }

        /// <summary>Authorizes an existing attachment by resolving the entity it belongs to.</summary>
        private async Task<OrgAuth> AuthorizeAttachmentAsync(Attachment attachment) =>
            await AuthorizeEntityAsync(attachment?.EntityType, attachment?.EntityId);

        /// <summary>Maximum size of a single upload. The UI already claims "up to 10MB".</summary>
        /// <remarks>
        /// There was previously no limit of any kind — no per-file cap and no
        /// MultipartBodyLengthLimit — so a single request could exhaust the disk. Per-plan
        /// total storage quota is enforced separately once EntitlementService lands; this is
        /// the per-file floor that must hold regardless of plan.
        /// </remarks>
        public const long MaxUploadBytes = 10L * 1024 * 1024;

        [HttpPost("upload")]
        [RequestSizeLimit(MaxUploadBytes + (1024 * 1024))] // payload + multipart overhead
        public async Task<ActionResult<Attachment>> UploadAttachment(
            [FromForm] IFormFile file,
            [FromForm] string entityType,
            [FromForm] string entityId,
            [FromForm] string uploaderId)
        {
            if (file == null || file.Length == 0)
                return BadRequest("No file uploaded");

            if (file.Length > MaxUploadBytes)
            {
                return StatusCode(StatusCodes.Status413PayloadTooLarge, new
                {
                    message = "File too large",
                    detail = $"Maximum upload size is {MaxUploadBytes / (1024 * 1024)}MB."
                });
            }

            // The uploader is the caller. It used to be a form field, so an upload could be
            // attributed to anyone.
            var callerId = OrgAccess.UserId(User);
            if (string.IsNullOrEmpty(callerId))
                return Unauthorized(new { message = "Authentication required" });

            var auth = await AuthorizeEntityAsync(entityType, entityId);
            if (!auth.Allowed) return auth.Error;

            var orgId = auth.Org?.Id;

            // Storage quota. The per-file cap above is a floor that holds on every plan; this
            // is the plan's total allowance, and it is checked BEFORE the file is written to
            // disk — accepting the bytes and then rejecting the record would leave an orphan.
            var entitlements = await _entitlements.ForOrganizationAsync(orgId);
            var limitGb = entitlements.Limit(Entitlement.StorageGb);

            if (limitGb != long.MaxValue)
            {
                var usedBytes = await _repo.SumSizeByOrganizationAsync(orgId);
                var limitBytes = limitGb * 1024L * 1024L * 1024L;

                // Compared against usage PLUS this file, so the quota is a ceiling rather
                // than something a single large upload can overshoot.
                if (usedBytes + file.Length > limitBytes)
                {
                    var quota = this.RequireQuota(entitlements, Entitlement.StorageGb, limitGb);
                    if (!quota.Allowed)
                    {
                        return StatusCode(StatusCodes.Status402PaymentRequired, new
                        {
                            message = "Storage limit reached",
                            detail = $"This plan includes {limitGb} GB. " +
                                     $"{usedBytes / (1024.0 * 1024.0):F1} MB is in use and this file is " +
                                     $"{file.Length / (1024.0 * 1024.0):F1} MB.",
                            entitlement = Entitlement.StorageGb
                        });
                    }
                }
            }

            var attachment = await _repo.SaveAttachment(file, entityType, entityId, callerId, orgId);
            return Ok(attachment);
        }

        [HttpGet("{entityType}/{entityId}")]
        public async Task<ActionResult<List<Attachment>>> GetAttachments(string entityType, string entityId)
        {
            var auth = await AuthorizeEntityAsync(entityType, entityId);
            if (!auth.Allowed) return auth.Error;

            var attachments = _repo.GetAttachments(entityType, entityId);
            return Ok(attachments);
        }
        [HttpGet("download/{id}")]
        public async Task<IActionResult> DownloadAttachment(string id)
        {
            var attachment = _repo.GetAttachmentById(id);
            if (attachment == null)
                return NotFound();

            // Files were downloadable by id alone, with no check on who owned them.
            var auth = await AuthorizeAttachmentAsync(attachment);
            if (!auth.Allowed) return auth.Error;

            var filePath = Path.Combine(_env.WebRootPath ?? Path.Combine(_env.ContentRootPath, "wwwroot"), "attachments", Path.GetFileName(attachment.FileUrl));
            if (!System.IO.File.Exists(filePath))
                return NotFound();

            var mimeType = GetMimeType(filePath);
            var fileBytes = System.IO.File.ReadAllBytes(filePath);
            return File(fileBytes, mimeType, attachment.FileName);
        }
        
        [HttpDelete("{id}")]
        public async Task<IActionResult> DeleteAttachment(string id)
        {
            var existing = _repo.GetAttachmentById(id);
            if (existing == null) return NotFound();

            var auth = await AuthorizeAttachmentAsync(existing);
            if (!auth.Allowed) return auth.Error;

            var result = _repo.DeleteAttachment(id);
            if (!result.Success)
                return NotFound(result);

            return Ok(result);
        }

        private string GetMimeType(string filePath)
        {
            var provider = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();
            if (!provider.TryGetContentType(filePath, out var contentType))
            {
                contentType = "application/octet-stream";
            }
            return contentType;
        }

    }
}

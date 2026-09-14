using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using RafeeqyNotes.Api.Helpers;
using RafeeqyNotes.Api.Models;
using RafeeqyNotes.Api.Repositories;
using RafeeqyNotes.Models;

namespace RafeeqyNotes.Api.Controllers
{
    /// <summary>
    /// Projects and their scheduling data.
    /// </summary>
    /// <remarks>
    /// SECURITY: this controller had no [Authorize] and no ownership checks. Any caller could
    /// read the member list of any project, update any project, and — with a single id —
    /// DELETE any project in the system. Create additionally took both the owner id and the
    /// target organization from the request body, so a caller could plant a project inside an
    /// organization they had nothing to do with and attribute it to someone else.
    ///
    /// Two rules to preserve here:
    ///   1. The owner is taken from the JWT, never from the body.
    ///   2. Update and Delete authorize against the STORED project's organization, not against
    ///      anything in the request — otherwise a caller can name an organization they belong
    ///      to while acting on a project they do not.
    /// </remarks>
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class ProjectController : ControllerBase
    {
        private readonly IProjectRepository _projectRepo;
        private readonly IBoardRepository _boardRepo;
        private readonly IProjectScheduleRepository _scheduleRepo;
        private readonly IOrganizationRepository _organizations;

        public ProjectController(
            IProjectRepository projectRepo,
            IBoardRepository boardRepo,
            IProjectScheduleRepository scheduleRepo,
            IOrganizationRepository organizations)
        {
            _projectRepo = projectRepo;
            _boardRepo = boardRepo;
            _scheduleRepo = scheduleRepo;
            _organizations = organizations;
        }

        // GET /api/project
        [HttpGet]
        public async Task<ActionResult<List<Project>>> GetAll([FromQuery] string? organizationId)
        {
            var currentUserId = OrgAccess.UserId(User);

            List<Project> projects;
            if (!string.IsNullOrEmpty(organizationId))
            {
                projects = await _projectRepo.GetByOrganizationIdAsync(organizationId);
            }
            else
            {
                // Fallback for unscoped or legacy requests
                projects = await _projectRepo.GetAllAsync();
            }

            // Filter out projects the user doesn't belong to. [Authorize] now guarantees a
            // user id is present, so this can no longer fall through and return everything.
            projects = projects.Where(p =>
                p.OwnerId == currentUserId ||
                (p.Members != null && p.Members.Any(m => m.UserId == currentUserId))
            ).ToList();

            // Scheduling data for every project in ONE query. This was a query per project
            // awaited in a loop, so listing a customer's projects cost a round trip each and got
            // slower with every project they added.
            var schedules = await _scheduleRepo.GetByProjectIdsAsync(projects.Select(p => p.Id));

            foreach (var project in projects)
            {
                if (project.Id != null && schedules.TryGetValue(project.Id, out var schedule))
                {
                    project.WorkingDays = schedule.WorkingDays;
                    project.WorkingHours = schedule.WorkingHours;
                    project.Holidays = schedule.Holidays;
                }
            }

            return Ok(projects);
        }

        // GET /api/project/{id}/members
        [HttpGet("{id}/members")]
        public async Task<ActionResult<List<OrganizationMember>>> GetMembersByProjectID(string id)
        {
            var auth = await this.AuthorizeProjectAsync(
                _organizations, _projectRepo, id, OrgAccess.AnyMember);
            if (!auth.Allowed) return auth.Error;

            var projects = await _projectRepo.GetMembersByProjectID(id);
            return Ok(projects);
        }

        // POST /api/project
        [HttpPost]
        public async Task<ActionResult<Project>> Create([FromBody] CreateProjectRequest request)
        {
            if (request == null) return BadRequest("Request body is required.");

            // The organization is supplied by the caller, so it is attacker-controlled and has
            // to be authorized rather than trusted.
            var orgId = request.Organization?.Id;
            var auth = this.AuthorizeOrg(_organizations, orgId, OrgAccess.AnyMember);
            if (!auth.Allowed) return auth.Error;

            var project = new Project
            {
                Id = request.Id,
                ExternalId = request.ExternalId,
                ExternalSource = request.ExternalSource,
                Name = request.Name,
                // Owner comes from the validated token. The client already sends its own id
                // here, so this changes nothing for legitimate callers — it only stops a
                // project being attributed to another user.
                OwnerId = auth.UserId,
                Color = request.Color,
                Description = request.Description ?? string.Empty,
                Organization = request.Organization
            };
            try
            {
                await _projectRepo.CreateAsync(project);
            }
            catch (DuplicateEntityException ex)
            {
                // 409, not 500: an importer re-running a batch needs to tell "already there"
                // apart from "the server broke".
                return Conflict(new { message = "Already exists.", id = ex.EntityId });
            }

            // Optionally create default board
            if (request.CreateDefaultBoard)
            {
                var board = new Board
                {
                    //ProjectId = project.Id,
                    Project = project,
                    Name = request.DefaultBoardName ?? $"{project.Name} Board",
                    MemberIds = new List<string> { project.OwnerId }
                };
                await _boardRepo.CreateAsync(board);
            }

            return CreatedAtAction(nameof(GetAll), new { id = project.Id }, project);
        }

        // PUT /api/project/{id}
        [HttpPut("{id}")]
        public async Task<ActionResult<Project>> Update(string id, [FromBody] Project project)
        {
            if (project == null) return BadRequest("Request body is required.");

            // Authorize against the stored project, not the submitted one.
            var auth = await this.AuthorizeProjectAsync(
                _organizations, _projectRepo, id, OrgAccess.AnyMember);
            if (!auth.Allowed) return auth.Error;

            project.Id = id;
            await _projectRepo.UpdateAsync(project);

            // If the payload contains schedule updates, we record them into the tracking collection
            if (project.WorkingDays != null || project.WorkingHours != null || project.Holidays != null)
            {
                var schedule = new ProjectSchedule
                {
                    ProjectId = project.Id,
                    WorkingDays = project.WorkingDays ?? new List<int> { 1, 2, 3, 4, 5 },
                    WorkingHours = project.WorkingHours ?? new WorkingHours(),
                    Holidays = project.Holidays ?? new List<string>(),
                    ModifiedByUserId = auth.UserId,
                    ModifiedByUserName = User?.Identity?.Name ?? "Unknown User"
                };
                await _scheduleRepo.UpsertAsync(schedule);
            }

            return Ok(project);
        }

        // DELETE /api/project/{id}
        [HttpDelete("{id}")]
        public async Task<IActionResult> Delete(string id)
        {
            // Deleting a project takes everything under it with it, so this is restricted to
            // the organization's managers rather than any member.
            var auth = await this.AuthorizeProjectAsync(
                _organizations, _projectRepo, id, OrgAccess.Managers);
            if (!auth.Allowed) return auth.Error;

            await _projectRepo.DeleteAsync(id);
            return NoContent();
        }
    }

    public record CreateProjectRequest(
        string Name,
        string OwnerId,
        string Color,
        bool CreateDefaultBoard = true,
        string? DefaultBoardName = null,
        string? Description = null,
        Organization? Organization = null,
        // Supplied by an importer so a re-run lands on the same document rather than a copy.
        // Ignored by the app, which sends neither.
        string? Id = null,
        string? ExternalId = null,
        string? ExternalSource = null
    );
}

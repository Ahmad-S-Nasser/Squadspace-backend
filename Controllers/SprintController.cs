using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using RafeeqyNotes.Api.Helpers;
using RafeeqyNotes.Api.Models;
using RafeeqyNotes.Api.Repositories;
using RafeeqyNotes.Api.Services;

namespace RafeeqyNotes.Api.Controllers
{
    /// <summary>
    /// Sprints, addressed either by project or by sprint id.
    /// </summary>
    /// <remarks>
    /// SECURITY: every action resolves the owning organization and checks membership. Before
    /// this, the controller had no [Authorize] and no org check, so any caller could read or
    /// delete any organization's sprints given an id.
    ///
    /// Scope note: these guards require MEMBERSHIP, not a particular role, which matches the
    /// behaviour that existed inside an organization before. Restricting writes to non-viewers
    /// is a separate policy change — it would alter what existing users can do, so it is not
    /// bundled into a security fix.
    /// </remarks>
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class SprintController : ControllerBase
    {
        private readonly ISprintRepository _repo;
        private readonly IOrganizationRepository _organizations;
        private readonly IProjectRepository _projects;
        private readonly ITaskStatusService _taskStatuses;

        public SprintController(
            ISprintRepository repo,
            IOrganizationRepository organizations,
            IProjectRepository projects,
            ITaskStatusService taskStatuses)
        {
            _repo = repo;
            _organizations = organizations;
            _projects = projects;
            _taskStatuses = taskStatuses;
        }

        /// <summary>Authorizes against the organization owning the sprint's project.</summary>
        private async Task<OrgAuth> AuthorizeSprintAsync(string sprintId)
        {
            var sprint = _repo.FetchSprintsById(sprintId);
            // Same 404 whether the sprint is missing or belongs to another organization, so
            // the response never confirms that an id exists.
            return await this.AuthorizeProjectAsync(
                _organizations, _projects, sprint?.ProjectId, OrgAccess.AnyMember);
        }

        [HttpGet("{id}")]
        public async Task<ActionResult<Sprint>> GetSprintsById(string id)
        {
            var auth = await AuthorizeSprintAsync(id);
            if (!auth.Allowed) return auth.Error;

            return Ok(_repo.FetchSprintsById(id));
        }

        [HttpGet("Project/{projectId}")]
        public async Task<ActionResult<List<Sprint>>> GetSprintsByProjectId(string projectId)
        {
            var auth = await this.AuthorizeProjectAsync(
                _organizations, _projects, projectId, OrgAccess.AnyMember);
            if (!auth.Allowed) return auth.Error;

            return Ok(_repo.FetchSprintsByProjectId(projectId));
        }

        [HttpPost]
        public async Task<ActionResult<Sprint>> CreateSprint([FromBody] Sprint sprint)
        {
            if (sprint == null) return BadRequest("Request body is required.");

            // The project comes from the body here, so it is attacker-controlled — which is
            // exactly why it must be authorized rather than trusted.
            var auth = await this.AuthorizeProjectAsync(
                _organizations, _projects, sprint.ProjectId, OrgAccess.AnyMember);
            if (!auth.Allowed) return auth.Error;

            return Ok(_repo.InsertSprint(sprint));
        }

        [HttpPut("{id}")]
        public async Task<ActionResult<Sprint>> UpdateSprint(string id, [FromBody] Sprint sprint)
        {
            if (sprint == null) return BadRequest("Request body is required.");

            // Authorize against the STORED sprint, not the submitted one: otherwise a caller
            // could pass a project they do belong to while updating a sprint they do not.
            var auth = await AuthorizeSprintAsync(id);
            if (!auth.Allowed) return auth.Error;

            return Ok(_repo.UpdateSprint(id, sprint));
        }

        [HttpDelete("{id}")]
        public async Task<IActionResult> DeleteSprint(string id)
        {
            var auth = await AuthorizeSprintAsync(id);
            if (!auth.Allowed) return auth.Error;

            _repo.DeleteSprint(id);
            return NoContent();
        }

        [HttpGet("velocity/{sprintId}")]
        public async Task<ActionResult<SprintVelocity>> GetSprintVelocity(string sprintId)
        {
            var auth = await AuthorizeSprintAsync(sprintId);
            if (!auth.Allowed) return auth.Error;

            // Velocity has to agree with the dashboard's completion count, so both read the
            // same organization workflow rather than each hardcoding "done".
            var statuses = await _taskStatuses.ForOrganizationAsync(auth.Org?.Id);
            var terminal = statuses.Statuses
                .Where(s => s.IsTerminal)
                .SelectMany(s => new[] { s.Slug }.Concat(s.Aliases ?? new List<string>()))
                .Distinct()
                .ToList();

            return Ok(_repo.CalculateSprintVelocity(sprintId, terminal));
        }
    }
}

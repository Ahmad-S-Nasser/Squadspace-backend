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
    /// Organization-wide reporting.
    /// </summary>
    /// <remarks>
    /// SECURITY: every endpoint here takes <c>orgId</c> from the query string. Before the
    /// guards below, the controller carried no [Authorize] and performed no membership check,
    /// so anyone who could guess or read an organization id — they appear in URLs throughout
    /// the app — could retrieve that organization's task counts, completion rates, meeting
    /// load and full team activity feed without an account.
    ///
    /// The membership check must stay on EVERY action. An analytics endpoint is a read of the
    /// whole organization by definition, so an unguarded one leaks more than a single record.
    /// </remarks>
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class AnalyticsController : ControllerBase
    {
        private readonly IAnalyticsRepository _repo;
        private readonly IOrganizationRepository _organizations;
        private readonly ITaskStatusService _taskStatuses;

        public AnalyticsController(
            IAnalyticsRepository repo,
            IOrganizationRepository organizations,
            ITaskStatusService taskStatuses)
        {
            _repo = repo;
            _organizations = organizations;
            _taskStatuses = taskStatuses;
        }

        [HttpGet("stats")]
        public async Task<ActionResult<DashboardStats>> GetDashboardStats(
            [FromQuery] string orgId,
            [FromQuery] string? projectId = null,
            [FromQuery] DateTime? startDate = null,
            [FromQuery] DateTime? endDate = null)
        {
            var auth = this.AuthorizeOrg(_organizations, orgId, OrgAccess.AnyMember);
            if (!auth.Allowed) return auth.Error;

            var statuses = await _taskStatuses.ForOrganizationAsync(orgId);
            return Ok(_repo.FetchDashboardStats(orgId, projectId, startDate, endDate, statuses));
        }

        [HttpGet("tasks")]
        public async Task<ActionResult<TaskAnalytics>> GetTaskAnalytics(
            [FromQuery] string orgId,
            [FromQuery] string? projectId = null,
            [FromQuery] DateTime? startDate = null,
            [FromQuery] DateTime? endDate = null)
        {
            var auth = this.AuthorizeOrg(_organizations, orgId, OrgAccess.AnyMember);
            if (!auth.Allowed) return auth.Error;

            var statuses = await _taskStatuses.ForOrganizationAsync(orgId);
            return Ok(_repo.FetchTaskAnalytics(orgId, projectId, startDate, endDate, statuses));
        }

        [HttpGet("meetings")]
        public ActionResult<MeetingAnalytics> GetMeetingAnalytics(
            [FromQuery] string orgId,
            [FromQuery] string? projectId = null,
            [FromQuery] DateTime? startDate = null,
            [FromQuery] DateTime? endDate = null)
        {
            var auth = this.AuthorizeOrg(_organizations, orgId, OrgAccess.AnyMember);
            if (!auth.Allowed) return auth.Error;

            return Ok(_repo.FetchMeetingAnalytics(orgId, projectId, startDate, endDate));
        }

        [HttpGet("activity")]
        public ActionResult<List<TeamActivity>> GetTeamActivity(
            [FromQuery] string orgId,
            [FromQuery] int limit = 50)
        {
            var auth = this.AuthorizeOrg(_organizations, orgId, OrgAccess.AnyMember);
            if (!auth.Allowed) return auth.Error;

            return Ok(_repo.FetchTeamActivity(orgId, limit));
        }
    }
}

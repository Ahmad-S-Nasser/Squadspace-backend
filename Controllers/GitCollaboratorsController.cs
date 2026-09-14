using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using RafeeqyNotes.Api.Models;
using RafeeqyNotes.Api.Helpers;
using RafeeqyNotes.Api.Repositories;

namespace RafeeqyNotes.Api.Controllers
{
    [ApiController]
    [Route("api/Git/repositories/{repoId}/collaborators")]
    // SECURITY: routed as api/Git/repositories/{repoId}/... and previously had neither
    // [Authorize] nor any ownership check, so any caller could read or modify another
    // organization's source code by supplying a repository id. Every action now
    // resolves the repository's project and requires organization membership.
    [Authorize]
    public class GitCollaboratorsController : ControllerBase
    {
        private readonly IGitRepositoryService _service;

        private readonly IOrganizationRepository _organizations;
        private readonly IProjectRepository _projects;
        private readonly IContributorRepository _contributors;

        public GitCollaboratorsController(
            IGitRepositoryService service,
            IOrganizationRepository organizations,
            IProjectRepository projects,
            IContributorRepository contributors)
        {
            _service = service;
            _organizations = organizations;
            _projects = projects;
            _contributors = contributors;
        }

        /// <summary>Authorizes against the organization owning this repository.</summary>
        private Task<OrgAuth> AuthorizeRepoAsync(string repoId) =>
            this.AuthorizeRepoAsync(_organizations, _projects, _service, repoId, OrgAccess.AnyMember);

        private async Task<RepoCollaboratorResponse> ToResponseAsync(Collaborator c)
        {
            var user = await _contributors.GetByIdAsync(c.UserId);
            return new RepoCollaboratorResponse
            {
                Id = c.Id,
                UserId = c.UserId,
                RepositoryId = c.RepoId,
                Role = c.Role,
                AddedAt = c.AddedAt,
                AddedBy = c.AddedBy,
                User = new GitPersonWithEmailResponse
                {
                    Id = c.UserId,
                    Name = user?.Name ?? "Unknown user",
                    Email = user?.Email ?? "",
                },
            };
        }

        [HttpGet]
        public async Task<IActionResult> GetAll(string repoId)
        {
            var auth = await AuthorizeRepoAsync(repoId);
            if (!auth.Allowed) return auth.Error;

            var collaborators = _service.GetCollaborators(repoId);
            var responses = new List<RepoCollaboratorResponse>();
            foreach (var c in collaborators) responses.Add(await ToResponseAsync(c));
            return Ok(responses);
        }

        [HttpPost]
        public async Task<IActionResult> Add(string repoId, [FromBody] AddCollaboratorRequest request)
        {
            if (request == null || (string.IsNullOrWhiteSpace(request.UserId) && string.IsNullOrWhiteSpace(request.Email)))
                return BadRequest("A user id or email is required.");

            var auth = await AuthorizeRepoAsync(repoId);
            if (!auth.Allowed) return auth.Error;

            var userId = request.UserId;
            if (string.IsNullOrWhiteSpace(userId))
            {
                // The Add Collaborator dialog only ever collects an email — resolve it to a
                // real user id instead of the old placeholder behaviour of using the raw
                // email string as if it were one.
                var contributor = await _contributors.GetByEmailAsync(request.Email.Trim());
                if (contributor == null)
                    return NotFound(new { message = $"No user found with email '{request.Email}'." });
                userId = contributor.Id;
            }

            var added = _service.AddCollaborator(repoId, userId, request.Role, auth.UserId);
            return Ok(await ToResponseAsync(added));
        }

        [HttpPut("{userId}")]
        public async Task<IActionResult> Update(string repoId, string userId, [FromBody] UpdateCollaboratorRequest request)
        {
            var auth = await AuthorizeRepoAsync(repoId);
            if (!auth.Allowed) return auth.Error;

            var success = _service.UpdateCollaborator(repoId, userId, request?.Role);
            return success ? Ok() : NotFound();
        }

        [HttpDelete("{userId}")]
        public async Task<IActionResult> Remove(string repoId, string userId)
        {
            var auth = await AuthorizeRepoAsync(repoId);
            if (!auth.Allowed) return auth.Error;

            var success = _service.RemoveCollaborator(repoId, userId);
            return success ? Ok() : NotFound();
        }
    }
}

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using RafeeqyNotes.Api.Helpers;
using RafeeqyNotes.Api.Models;
using RafeeqyNotes.Api.Repositories;

namespace RafeeqyNotes.Api.Controllers
{
    [ApiController]
    [Route("api/Git/repositories/{repoId}/branches")]
    // SECURITY: routed as api/Git/repositories/{repoId}/... and previously had neither
    // [Authorize] nor any ownership check, so any caller could read or modify another
    // organization's source code by supplying a repository id. Every action now
    // resolves the repository's project and requires organization membership.
    [Authorize]
    public class GitBranchesController : ControllerBase
    {
        private readonly IGitRepositoryService _service;

        private readonly IOrganizationRepository _organizations;
        private readonly IProjectRepository _projects;

        public GitBranchesController(IGitRepositoryService service, IOrganizationRepository organizations, IProjectRepository projects)
        {
            _service = service;
            _organizations = organizations;
            _projects = projects;
        }

        /// <summary>Authorizes against the organization owning this repository.</summary>
        private Task<OrgAuth> AuthorizeRepoAsync(string repoId) =>
            this.AuthorizeRepoAsync(_organizations, _projects, _service, repoId, OrgAccess.AnyMember);

        [HttpGet]
        public async Task<IActionResult> GetAll(string repoId)
        {
            var auth = await AuthorizeRepoAsync(repoId);
            if (!auth.Allowed) return auth.Error;

            var repo = _service.GetRepository(repoId);
            var branches = _service.GetBranches(repoId)
                .Select(name =>
                {
                    var last = _service.GetCommits(repoId, name, 1, 1).commits.FirstOrDefault();
                    return new GitBranchResponse
                    {
                        Name = name,
                        IsDefault = name == repo?.DefaultBranch,
                        LastCommit = last == null ? null : GitMappers.ToCommitResponse(last),
                    };
                });
            return Ok(branches);
        }

        [HttpPost]
        public async Task<IActionResult> Create(string repoId, [FromBody] CreateBranchRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.Name))
                return BadRequest("Branch name is required.");

            var auth = await AuthorizeRepoAsync(repoId);
            if (!auth.Allowed) return auth.Error;

            var repo = _service.GetRepository(repoId);
            var sourceBranch = string.IsNullOrWhiteSpace(request.SourceBranch) ? repo?.DefaultBranch : request.SourceBranch;

            string created;
            try
            {
                created = _service.CreateBranch(repoId, request.Name.Trim(), sourceBranch);
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }

            return Ok(new GitBranchResponse { Name = created, IsDefault = false });
        }

        [HttpDelete("{name}")]
        public async Task<IActionResult> Delete(string repoId, string name)
        {
            var auth = await AuthorizeRepoAsync(repoId);
            if (!auth.Allowed) return auth.Error;

            var success = _service.DeleteBranch(repoId, name);
            return success ? Ok() : NotFound();
        }

        [HttpPost("{name}/merge")]
        public async Task<IActionResult> Merge(string repoId, string name, [FromBody] MergeBranchRequest request)
        {
            var auth = await AuthorizeRepoAsync(repoId);
            if (!auth.Allowed) return auth.Error;

            var target = string.IsNullOrWhiteSpace(request?.TargetBranch) ? "main" : request.TargetBranch;
            var (authorName, authorEmail) = GitAuthorHelper.FromClaims(User);

            var (success, conflict) = _service.MergeBranch(repoId, name, target, authorName, authorEmail, request?.CommitMessage);
            if (conflict) return Conflict(new { message = "Merge conflict — the branches have overlapping changes that can't be merged automatically." });
            return success ? Ok() : BadRequest();
        }
    }
}

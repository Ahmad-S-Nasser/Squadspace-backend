using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using RafeeqyNotes.Api.Helpers;
using RafeeqyNotes.Api.Models;
using RafeeqyNotes.Api.Repositories;

namespace RafeeqyNotes.Api.Controllers
{
    [ApiController]
    [Route("api/Git/repositories/{repoId}/files")]
    // SECURITY: routed as api/Git/repositories/{repoId}/... and previously had neither
    // [Authorize] nor any ownership check, so any caller could read or modify another
    // organization's source code by supplying a repository id. Every action now
    // resolves the repository's project and requires organization membership.
    [Authorize]
    public class GitFilesController : ControllerBase
    {
        private readonly IGitRepositoryService _service;

        private readonly IOrganizationRepository _organizations;
        private readonly IProjectRepository _projects;

        public GitFilesController(IGitRepositoryService service, IOrganizationRepository organizations, IProjectRepository projects)
        {
            _service = service;
            _organizations = organizations;
            _projects = projects;
        }

        /// <summary>Authorizes against the organization owning this repository.</summary>
        private Task<OrgAuth> AuthorizeRepoAsync(string repoId) =>
            this.AuthorizeRepoAsync(_organizations, _projects, _service, repoId, OrgAccess.AnyMember);

        [HttpGet("/api/Git/repositories/{repoId}/tree")]
        public async Task<IActionResult> GetTree(string repoId, [FromQuery] string path, [FromQuery] string branch)
        {
            var auth = await AuthorizeRepoAsync(repoId);
            if (!auth.Allowed) return auth.Error;

            var entries = _service.GetTree(repoId, branch, path);
            var items = entries.Select(e => new GitTreeItemResponse
            {
                Name = e.Name,
                Path = e.Path,
                Type = e.Type == "dir" ? "dir" : "file",
                Size = e.Size,
                Sha = e.Sha,
            }).ToList();

            return Ok(new GitTreeResponse { Items = items, Truncated = false });
        }

        [HttpGet("{path}")]
        public async Task<IActionResult> Get(string repoId, string path, [FromQuery] string branch)
        {
            var auth = await AuthorizeRepoAsync(repoId);
            if (!auth.Allowed) return auth.Error;

            var (content, size, sha) = _service.GetFileContent(repoId, branch, path);
            if (content == null) return NotFound();

            return Ok(new GitFileContentResponse
            {
                Name = path.Contains('/') ? path[(path.LastIndexOf('/') + 1)..] : path,
                Path = path,
                Sha = sha,
                Size = size,
                Content = content,
                Encoding = "base64",
            });
        }

        [HttpPut("{path}")]
        public async Task<IActionResult> Update(string repoId, string path, [FromBody] CreateFileRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.Message))
                return BadRequest("A commit message is required.");

            var auth = await AuthorizeRepoAsync(repoId);
            if (!auth.Allowed) return auth.Error;

            byte[] bytes;
            try { bytes = Convert.FromBase64String(request.Content ?? string.Empty); }
            catch (FormatException) { return BadRequest("Content must be base64-encoded."); }

            var (authorName, authorEmail) = GitAuthorHelper.FromClaims(User);
            var commit = _service.UpdateFile(repoId, request.Branch, path, bytes, request.Message, authorName, authorEmail);
            if (commit == null) return BadRequest();

            return Ok(GitMappers.ToCommitResponse(commit));
        }

        [HttpDelete("{path}")]
        public async Task<IActionResult> Delete(string repoId, string path, [FromBody] DeleteFileRequest request)
        {
            var auth = await AuthorizeRepoAsync(repoId);
            if (!auth.Allowed) return auth.Error;

            var message = string.IsNullOrWhiteSpace(request?.Message) ? $"Delete {path}" : request.Message;
            var (authorName, authorEmail) = GitAuthorHelper.FromClaims(User);
            var commit = _service.DeleteFile(repoId, request?.Branch, path, message, authorName, authorEmail);
            return commit == null ? NotFound() : Ok(GitMappers.ToCommitResponse(commit));
        }
    }
}

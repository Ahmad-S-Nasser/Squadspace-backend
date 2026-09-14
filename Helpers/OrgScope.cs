using Microsoft.AspNetCore.Mvc;
using RafeeqyNotes.Api.Models;
using RafeeqyNotes.Api.Repositories;

namespace RafeeqyNotes.Api.Helpers
{
    /// <summary>
    /// Resolves the organization that owns a resource, so <see cref="OrgAccess.AuthorizeOrg"/>
    /// can be applied to controllers that are addressed by project / board id rather than by
    /// organization id.
    /// </summary>
    /// <remarks>
    /// Most controllers take a projectId or boardId, not an orgId, which is why they were left
    /// unscoped: [Authorize] proves WHO the caller is, but not WHICH organization's data they
    /// may read. Without this resolver a signed-in member of org A can still fetch org B's
    /// boards, sprints, meetings and whiteboards by supplying an id.
    ///
    /// The entity graph is denormalized — a Board embeds its whole Project, which embeds its
    /// Organization — so most lookups need a single read and no join. Where an embedded copy
    /// is missing (documents written before a field existed), resolution falls back to a
    /// direct read rather than assuming.
    ///
    /// Resolution failure is deliberately NOT the same as access denial. A missing resource
    /// returns null here and the caller decides; a resource whose organization cannot be
    /// determined is treated as inaccessible rather than public — fail closed.
    /// </remarks>
    public static class OrgScope
    {
        /// <summary>Organization id owning a project, or null if it cannot be determined.</summary>
        public static async Task<string> OrgIdForProjectAsync(IProjectRepository projects, string projectId)
        {
            if (projects == null || string.IsNullOrWhiteSpace(projectId)) return null;

            var project = await projects.GetByIdAsync(projectId);
            return OrgIdOf(project);
        }

        /// <summary>Organization id owning a board, via its embedded project.</summary>
        public static async Task<string> OrgIdForBoardAsync(
            IBoardRepository boards, IProjectRepository projects, string boardId)
        {
            if (boards == null || string.IsNullOrWhiteSpace(boardId)) return null;

            var board = await boards.GetByIdAsync(boardId);
            if (board == null) return null;

            // The board's own flat id, written server-side and backfilled. No walk, no read.
            if (!string.IsNullOrWhiteSpace(board.OrganizationId)) return board.OrganizationId;

            // The embedded project usually carries the organization already.
            var embedded = OrgIdOf(board.Project);
            if (!string.IsNullOrWhiteSpace(embedded))
            {
                OrgScopeDiagnostics.Fallback("board organization (embedded project)", board.Id);
                return embedded;
            }

            // Older board documents may embed a project snapshot taken before the
            // organization was attached. Fall back to reading the project itself.
            var projectId = ProjectIdOf(board);
            if (string.IsNullOrWhiteSpace(projectId)) return null;

            OrgScopeDiagnostics.Fallback("board organization (project re-read)", board.Id);
            return await OrgIdForProjectAsync(projects, projectId);
        }

        /// <summary>Organization id recorded on a project, tolerating the older shapes.</summary>
        /// <remarks>
        /// Prefers the flat OrganizationId, which EntityGraphService writes server-side on every
        /// write and the backfill migrations filled in for existing documents. The embedded walk
        /// stays as a fallback and is reported, because a document still needing it is a document
        /// the backfill could not reach - see OrgScopeDiagnostics.
        /// </remarks>
        public static string OrgIdOf(Project project)
        {
            if (project == null) return null;

            if (!string.IsNullOrWhiteSpace(project.OrganizationId)) return project.OrganizationId;

            var embedded = project.Organization?.Id;
            if (string.IsNullOrWhiteSpace(embedded)) return null;

            OrgScopeDiagnostics.Fallback("project organization", project.Id);
            return embedded;
        }

        /// <summary>
        /// Authorizes the caller against the organization owning <paramref name="projectId"/>.
        ///
        /// A project that does not exist, or whose organization cannot be resolved, produces
        /// the same 404 as an organization the caller cannot see — deliberately
        /// indistinguishable, so the response does not confirm whether an id exists.
        /// </summary>
        public static async Task<OrgAuth> AuthorizeProjectAsync(
            this ControllerBase controller,
            IOrganizationRepository organizations,
            IProjectRepository projects,
            string projectId,
            params string[] allowedRoles)
        {
            var orgId = await OrgIdForProjectAsync(projects, projectId);
            if (string.IsNullOrWhiteSpace(orgId)) return NotFound(controller);

            return controller.AuthorizeOrg(organizations, orgId, allowedRoles);
        }

        /// <summary>As <see cref="AuthorizeProjectAsync"/>, addressed by board id.</summary>
        public static async Task<OrgAuth> AuthorizeBoardAsync(
            this ControllerBase controller,
            IOrganizationRepository organizations,
            IProjectRepository projects,
            IBoardRepository boards,
            string boardId,
            params string[] allowedRoles)
        {
            var orgId = await OrgIdForBoardAsync(boards, projects, boardId);
            if (string.IsNullOrWhiteSpace(orgId)) return NotFound(controller);

            return controller.AuthorizeOrg(organizations, orgId, allowedRoles);
        }

        /// <summary>
        /// Organization id owning a task, read from the embedded Note -> Board -> Project chain.
        /// </summary>
        /// <remarks>
        /// Tasks embed their whole parent graph, so this normally needs no extra read. When the
        /// embedded copy predates the organization being attached, the caller should fall back
        /// to resolving the project id directly.
        /// </remarks>
        public static string OrgIdOfTask(RafeeqyNotes.Models.NoteTask task)
        {
            if (task == null) return null;

            if (!string.IsNullOrWhiteSpace(task.OrganizationId)) return task.OrganizationId;

            // Previously this had no fallback at all: a task whose embedded chain was incomplete
            // resolved to null and simply vanished from every list endpoint, silently. It now
            // falls back and says so.
            var embedded = OrgIdOf(task.Note?.Board?.Project);
            if (string.IsNullOrWhiteSpace(embedded)) return null;

            OrgScopeDiagnostics.Fallback("task organization", task.Id);
            return embedded;
        }

        /// <summary>Project id owning a task, for the fallback path.</summary>
        public static string ProjectIdOfTask(RafeeqyNotes.Models.NoteTask task)
        {
            if (task == null) return null;

            if (!string.IsNullOrWhiteSpace(task.ProjectId)) return task.ProjectId;

            var embedded = task.Note?.Board?.Project?.Id;
            if (string.IsNullOrWhiteSpace(embedded)) return null;

            OrgScopeDiagnostics.Fallback("task project", task.Id);
            return embedded;
        }

        /// <summary>Project id owning a board, preferring the flat id.</summary>
        public static string ProjectIdOf(Board board)
        {
            if (board == null) return null;

            if (!string.IsNullOrWhiteSpace(board.ProjectId)) return board.ProjectId;

            var embedded = board.Project?.Id;
            if (string.IsNullOrWhiteSpace(embedded)) return null;

            OrgScopeDiagnostics.Fallback("board project", board.Id);
            return embedded;
        }

        /// <summary>
        /// Authorizes against the organization owning a git repository, via its project.
        /// </summary>
        /// <remarks>
        /// Repositories hold source code, so this is the most sensitive resolution path here.
        /// The Git controllers are all routed as <c>api/Git/repositories/{repoId}/...</c> and
        /// had no ownership check at all: any signed-in user could read another organization's
        /// commits, file contents, branches and pull requests by supplying a repository id.
        /// </remarks>
        public static async Task<OrgAuth> AuthorizeRepoAsync(
            this ControllerBase controller,
            IOrganizationRepository organizations,
            IProjectRepository projects,
            IGitRepositoryService git,
            string repoId,
            params string[] allowedRoles)
        {
            if (git == null || string.IsNullOrWhiteSpace(repoId)) return NotFound(controller);

            var repo = git.GetRepository(repoId);
            if (repo == null) return NotFound(controller);

            return await controller.AuthorizeProjectAsync(
                organizations, projects, repo.ProjectId, allowedRoles);
        }

        private static OrgAuth NotFound(ControllerBase controller) => new OrgAuth
        {
            Allowed = false,
            UserId = OrgAccess.UserId(controller.User),
            Error = controller.NotFound(new
            {
                message = "Not found",
                detail = "No such resource, or you do not have access to it."
            })
        };
    }
}

using RafeeqyNotes.Api.Models;

namespace RafeeqyNotes.Api.Repositories
{
    public interface IGitRepositoryService
    {
        // Repository CRUD
        GitRepository CreateRepository(GitRepository repo, string creatorUserId);
        GitRepository GetRepository(string id);
        IEnumerable<GitRepository> GetAllRepositories();
        bool UpdateRepository(string id, UpdateRepositoryRequest update);
        bool DeleteRepository(string id);

        /// <summary>The calling user's role for a repo: an explicit collaborator row, "admin"
        /// for the creator, or "read" for any other org member (matching AuthorizeRepoAsync's
        /// existing AnyMember gate — org membership alone already grants read access to every
        /// endpoint here, so a role of "read" reflects reality rather than under-reporting it
        /// as null/no-access).</summary>
        string GetUserRole(string repoId, string userId);

        // Branches
        IEnumerable<string> GetBranches(string repoId);
        string CreateBranch(string repoId, string branchName, string sourceBranch);
        bool DeleteBranch(string repoId, string branchName);

        /// <summary>Merges branchName into targetBranch. Returns (success, conflict) — a real
        /// merge conflict is reported back rather than throwing.</summary>
        (bool success, bool conflict) MergeBranch(string repoId, string branchName, string targetBranch, string authorName, string authorEmail, string commitMessage = null);

        // Commits
        (IEnumerable<GitCommit> commits, int totalCount) GetCommits(string repoId, string branch, int page, int perPage);
        GitCommit GetCommit(string repoId, string sha);
        (List<GitFileChange> files, int additions, int deletions) GetCommitDiff(string repoId, string sha);

        // Files
        IEnumerable<GitFileEntry> GetTree(string repoId, string branch, string path);
        (string content, long size, string sha) GetFileContent(string repoId, string branch, string path);

        /// <summary>Commits a file add/update directly against the branch tip via the object
        /// database — no working-directory checkout, so concurrent edits to different
        /// branches (or by different users) never race on a shared checked-out HEAD.</summary>
        GitCommit UpdateFile(string repoId, string branch, string path, byte[] content, string commitMessage, string authorName, string authorEmail);
        GitCommit DeleteFile(string repoId, string branch, string path, string commitMessage, string authorName, string authorEmail);

        // Collaborators
        IEnumerable<Collaborator> GetCollaborators(string repoId);
        Collaborator AddCollaborator(string repoId, string userId, string role, string addedBy);
        bool UpdateCollaborator(string repoId, string userId, string role);
        bool RemoveCollaborator(string repoId, string userId);

        // Pull Requests
        (IEnumerable<PullRequest> pullRequests, int totalCount) GetPullRequests(string repoId, string state, int page, int perPage);
        PullRequest CreatePullRequest(string repoId, string title, string body, string sourceBranch, string targetBranch, bool isDraft, string authorId);
        PullRequest GetPullRequest(string repoId, int number);
        bool UpdatePullRequest(string repoId, int number, UpdatePullRequestRequest update);

        /// <summary>Merges a pull request's source into its target. Returns (success, conflict).</summary>
        (bool success, bool conflict) MergePullRequest(string repoId, int number, string mergeMethod, string commitMessage, string mergedById, string authorName, string authorEmail);

        (int additions, int deletions, int changedFiles, int commits) GetPullRequestStats(string repoId, PullRequest pr);
        List<GitFileChange> GetPullRequestFiles(string repoId, PullRequest pr);

        // Pull Request Comments
        IEnumerable<PRComment> GetPullRequestComments(string repoId, int number);
        PRComment AddPullRequestComment(string repoId, int number, string body, string authorId, string path, int? line, string commitSha);
    }
}

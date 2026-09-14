using System.Security.Claims;
using RafeeqyNotes.Api.Models;

namespace RafeeqyNotes.Api.Helpers
{
    /// <summary>Maps the Git service's internal working types onto the wire DTOs the
    /// frontend's src/types/git.ts expects. Shared across the Git controllers so a commit
    /// or file change is shaped identically wherever it appears (commit list, commit detail,
    /// PR file list, branch's last commit, ...).</summary>
    public static class GitMappers
    {
        public static GitCommitResponse ToCommitResponse(GitCommit c) => new GitCommitResponse
        {
            Sha = c.Sha,
            ShortSha = c.Sha != null && c.Sha.Length > 7 ? c.Sha.Substring(0, 7) : c.Sha,
            Message = c.Message,
            Author = new GitCommitAuthorResponse { Name = c.AuthorName, Email = c.AuthorEmail, Date = c.AuthorWhen },
            Committer = new GitCommitAuthorResponse { Name = c.CommitterName, Email = c.CommitterEmail, Date = c.CommitterWhen },
            ParentShas = c.ParentShas ?? new List<string>(),
        };

        public static GitCommitFileResponse ToFileResponse(GitFileChange f) => new GitCommitFileResponse
        {
            Filename = f.Filename,
            Status = f.Status,
            Additions = f.Additions,
            Deletions = f.Deletions,
            Patch = f.Patch,
            PreviousFilename = f.PreviousFilename,
        };
    }

    /// <summary>Resolves the (name, email) pair used to author a commit created through the
    /// API — the actual signed-in user, from the same JWT claims used everywhere else in this
    /// app, rather than a hardcoded "SquadSpace" placeholder that didn't reflect who made the
    /// change.</summary>
    public static class GitAuthorHelper
    {
        public static (string name, string email) FromClaims(ClaimsPrincipal user)
        {
            var name = user.FindFirst(ClaimTypes.Name)?.Value;
            var email = user.FindFirst(ClaimTypes.Email)?.Value;
            return (
                string.IsNullOrWhiteSpace(name) ? "SquadSpace User" : name,
                string.IsNullOrWhiteSpace(email) ? "noreply@squadspace.com" : email
            );
        }
    }
}

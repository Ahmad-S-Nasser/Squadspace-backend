using LibGit2Sharp;
using MongoDB.Driver;
using RafeeqyNotes.Api.Config;
using RafeeqyNotes.Api.Models;

namespace RafeeqyNotes.Api.Repositories
{
    /// <summary>
    /// A self-hosted git host: repositories are real git repos on local disk (LibGit2Sharp),
    /// with metadata, collaborators, pull requests and PR comments in MongoDB.
    /// </summary>
    /// <remarks>
    /// Branch reads and writes never check out a working directory — they operate directly on
    /// branch tips via the object database (TreeDefinition / ObjectDatabase.CreateCommit /
    /// Refs.UpdateTarget). This is what makes it safe to have several requests, for different
    /// branches or different users, land concurrently: nothing shares a mutable checked-out
    /// HEAD, so there is nothing to race on.
    /// </remarks>
    public class GitRepositoryService : IGitRepositoryService
    {
        private readonly string _basePath;
        private readonly IMongoCollection<GitRepository> _repositories;
        private readonly IMongoCollection<Collaborator> _collaborators;
        private readonly IMongoCollection<PullRequest> _pullRequests;
        private readonly IMongoCollection<PRComment> _prComments;

        public GitRepositoryService(GitSettings config, MongoDbSettings settings)
        {
            _basePath = string.IsNullOrWhiteSpace(config.BasePath) ? "wwwroot/git-repos" : config.BasePath;
            if (!Directory.Exists(_basePath))
                Directory.CreateDirectory(_basePath);

            var client = new MongoClient(settings.ConnectionString);
            var db = client.GetDatabase(settings.DatabaseName);
            _repositories = db.GetCollection<GitRepository>("GitRepositories");
            _collaborators = db.GetCollection<Collaborator>("Collaborators");
            _pullRequests = db.GetCollection<PullRequest>("PullRequests");
            _prComments = db.GetCollection<PRComment>("PRComments");
        }

        private string RepoPath(string repoId) => Path.Combine(_basePath, repoId);

        // ================= Repository CRUD =================

        public GitRepository CreateRepository(GitRepository repo, string creatorUserId)
        {
            if (string.IsNullOrWhiteSpace(repo.Id))
                repo.Id = Guid.NewGuid().ToString();

            Repository.Init(RepoPath(repo.Id));

            repo.DefaultBranch = string.IsNullOrWhiteSpace(repo.DefaultBranch) ? "main" : repo.DefaultBranch;
            repo.CreatedBy = creatorUserId;
            repo.CreatedAt = DateTime.UtcNow;
            _repositories.InsertOne(repo);

            // The creator is always an admin collaborator, which is also what GetUserRole
            // falls back to for CreatedBy even if this row is ever missing.
            _collaborators.InsertOne(new Collaborator
            {
                Id = Guid.NewGuid().ToString(),
                RepoId = repo.Id,
                UserId = creatorUserId,
                Role = "admin",
                AddedBy = creatorUserId,
                AddedAt = DateTime.UtcNow,
            });

            return repo;
        }

        public GitRepository GetRepository(string id) =>
            string.IsNullOrWhiteSpace(id) ? null : _repositories.Find(r => r.Id == id).FirstOrDefault();

        public IEnumerable<GitRepository> GetAllRepositories() => _repositories.Find(_ => true).ToList();

        public bool UpdateRepository(string id, UpdateRepositoryRequest update)
        {
            var builder = Builders<GitRepository>.Update;
            var updates = new List<UpdateDefinition<GitRepository>> { builder.Set(r => r.UpdatedAt, DateTime.UtcNow) };
            if (update.Name != null) updates.Add(builder.Set(r => r.Name, update.Name));
            if (update.Description != null) updates.Add(builder.Set(r => r.Description, update.Description));
            if (update.DefaultBranch != null) updates.Add(builder.Set(r => r.DefaultBranch, update.DefaultBranch));
            if (update.IsPrivate.HasValue) updates.Add(builder.Set(r => r.IsPrivate, update.IsPrivate.Value));

            var result = _repositories.UpdateOne(r => r.Id == id, builder.Combine(updates));
            return result.MatchedCount > 0;
        }

        public bool DeleteRepository(string id)
        {
            var repoPath = RepoPath(id);
            if (Directory.Exists(repoPath))
            {
                // Git's own object files are written read-only; clear that before recursive
                // delete or File.Delete throws UnauthorizedAccessException.
                foreach (var file in Directory.GetFiles(repoPath, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(repoPath, true);
            }

            var result = _repositories.DeleteOne(r => r.Id == id);
            _collaborators.DeleteMany(c => c.RepoId == id);
            _pullRequests.DeleteMany(p => p.RepoId == id);
            _prComments.DeleteMany(c => c.RepoId == id);
            return result.DeletedCount > 0;
        }

        public string GetUserRole(string repoId, string userId)
        {
            var collaborator = _collaborators.Find(c => c.RepoId == repoId && c.UserId == userId).FirstOrDefault();
            if (collaborator != null) return collaborator.Role;

            var repo = GetRepository(repoId);
            if (repo?.CreatedBy == userId) return "admin";

            // Reaching here means AuthorizeRepoAsync already let the caller through on org
            // membership alone, which already grants read access to every endpoint - so
            // "read" reflects the real access level rather than under-reporting it as none.
            return "read";
        }

        // ================= Branches =================

        public IEnumerable<string> GetBranches(string repoId)
        {
            using var repo = new Repository(RepoPath(repoId));
            return repo.Branches.Where(b => !b.IsRemote).Select(b => b.FriendlyName).ToList();
        }

        public string CreateBranch(string repoId, string branchName, string sourceBranch)
        {
            using var repo = new Repository(RepoPath(repoId));
            var source = string.IsNullOrWhiteSpace(sourceBranch) ? repo.Head : repo.Branches[sourceBranch];
            if (source?.Tip == null)
                throw new InvalidOperationException($"Source branch '{sourceBranch}' was not found or has no commits.");

            repo.Refs.Add($"refs/heads/{branchName}", source.Tip.Id);
            return branchName;
        }

        public bool DeleteBranch(string repoId, string branchName)
        {
            using var repo = new Repository(RepoPath(repoId));
            var branch = repo.Branches[branchName];
            if (branch == null) return false;
            repo.Branches.Remove(branch);
            return true;
        }

        public (bool success, bool conflict) MergeBranch(string repoId, string branchName, string targetBranch, string authorName, string authorEmail, string commitMessage = null)
        {
            using var repo = new Repository(RepoPath(repoId));
            var source = repo.Branches[branchName];
            var target = repo.Branches[targetBranch];
            if (source?.Tip == null || target?.Tip == null) return (false, false);

            var message = string.IsNullOrWhiteSpace(commitMessage)
                ? $"Merge branch '{branchName}' into {targetBranch}"
                : commitMessage;
            return MergeInto(repo, source.Tip, target, "merge", message, authorName, authorEmail);
        }

        /// <summary>
        /// Merges <paramref name="source"/> into <paramref name="target"/> via
        /// ObjectDatabase.MergeCommits — a checkout-free 3-way merge — then moves
        /// <paramref name="target"/>'s ref to a newly created commit. Returns success=false,
        /// conflict=true on a real merge conflict instead of throwing.
        /// </summary>
        private (bool success, bool conflict) MergeInto(Repository repo, Commit source, Branch target, string mode, string message, string authorName, string authorEmail)
        {
            if (source.Id == target.Tip.Id) return (true, false); // already up to date, nothing to merge

            var mergeResult = repo.ObjectDatabase.MergeCommits(target.Tip, source, new MergeTreeOptions());
            if (mergeResult.Status == MergeTreeStatus.Conflicts) return (false, true);

            var author = new Signature(
                string.IsNullOrWhiteSpace(authorName) ? "SquadSpace" : authorName,
                string.IsNullOrWhiteSpace(authorEmail) ? "noreply@squadspace.com" : authorEmail,
                DateTimeOffset.Now);

            // "squash" (and the "rebase" approximation, see MergePullRequest) produce a single
            // commit with only the target as parent; a plain merge always creates an explicit
            // 2-parent merge commit rather than fast-forwarding, so every merge leaves a clear,
            // traceable commit regardless of whether target had moved.
            var parents = mode == "squash" ? new[] { target.Tip } : new[] { target.Tip, source };
            var newCommit = repo.ObjectDatabase.CreateCommit(author, author, message, mergeResult.Tree, parents, false);
            repo.Refs.UpdateTarget(repo.Refs[target.CanonicalName], newCommit.Id);
            return (true, false);
        }

        // ================= Commits =================

        public (IEnumerable<GitCommit> commits, int totalCount) GetCommits(string repoId, string branch, int page, int perPage)
        {
            using var repo = new Repository(RepoPath(repoId));
            var tip = ResolveTip(repo, branch);
            if (tip == null) return (Enumerable.Empty<GitCommit>(), 0);

            var filter = new CommitFilter { IncludeReachableFrom = tip, SortBy = CommitSortStrategies.Topological | CommitSortStrategies.Time };
            var all = repo.Commits.QueryBy(filter).ToList();
            var effectivePerPage = perPage <= 0 ? 20 : perPage;
            var effectivePage = page <= 0 ? 1 : page;

            var pageItems = all.Skip((effectivePage - 1) * effectivePerPage).Take(effectivePerPage).Select(ToGitCommit).ToList();
            return (pageItems, all.Count);
        }

        public GitCommit GetCommit(string repoId, string sha)
        {
            using var repo = new Repository(RepoPath(repoId));
            var commit = LookupCommit(repo, sha);
            return commit == null ? null : ToGitCommit(commit);
        }

        public (List<GitFileChange> files, int additions, int deletions) GetCommitDiff(string repoId, string sha)
        {
            using var repo = new Repository(RepoPath(repoId));
            var commit = LookupCommit(repo, sha);
            if (commit == null) return (new List<GitFileChange>(), 0, 0);

            var parentTree = commit.Parents.FirstOrDefault()?.Tree;
            return DiffTrees(repo, parentTree, commit.Tree);
        }

        private static (List<GitFileChange> files, int additions, int deletions) DiffTrees(Repository repo, Tree oldTree, Tree newTree)
        {
            var patch = repo.Diff.Compare<Patch>(oldTree, newTree);
            var files = new List<GitFileChange>();
            int additions = 0, deletions = 0;
            foreach (var entry in patch)
            {
                files.Add(new GitFileChange
                {
                    Filename = entry.Path,
                    Status = MapChangeKind(entry.Status),
                    Additions = entry.LinesAdded,
                    Deletions = entry.LinesDeleted,
                    Patch = entry.Patch,
                    PreviousFilename = !string.IsNullOrEmpty(entry.OldPath) && entry.OldPath != entry.Path ? entry.OldPath : null,
                });
                additions += entry.LinesAdded;
                deletions += entry.LinesDeleted;
            }
            return (files, additions, deletions);
        }

        private static string MapChangeKind(ChangeKind kind) => kind switch
        {
            ChangeKind.Added => "added",
            ChangeKind.Deleted => "removed",
            ChangeKind.Renamed => "renamed",
            ChangeKind.Copied => "renamed",
            _ => "modified",
        };

        private static Commit ResolveTip(Repository repo, string branch) =>
            string.IsNullOrWhiteSpace(branch) ? repo.Head.Tip : repo.Branches[branch]?.Tip;

        private static Commit LookupCommit(Repository repo, string sha)
        {
            if (string.IsNullOrWhiteSpace(sha)) return null;
            return repo.Lookup<Commit>(sha)
                ?? repo.Commits.FirstOrDefault(c => c.Sha.StartsWith(sha, StringComparison.OrdinalIgnoreCase));
        }

        private static GitCommit ToGitCommit(Commit c) => new GitCommit
        {
            Sha = c.Sha,
            AuthorName = c.Author.Name,
            AuthorEmail = c.Author.Email,
            AuthorWhen = c.Author.When,
            CommitterName = c.Committer.Name,
            CommitterEmail = c.Committer.Email,
            CommitterWhen = c.Committer.When,
            Message = c.Message,
            ParentShas = c.Parents.Select(p => p.Sha).ToList(),
        };

        // ================= Files =================

        public IEnumerable<GitFileEntry> GetTree(string repoId, string branch, string path)
        {
            using var repo = new Repository(RepoPath(repoId));
            var tip = ResolveTip(repo, branch);
            if (tip == null) return Enumerable.Empty<GitFileEntry>();

            var tree = tip.Tree;
            if (!string.IsNullOrWhiteSpace(path))
            {
                var entry = tip[path];
                if (entry?.Target is not Tree subtree) return Enumerable.Empty<GitFileEntry>();
                tree = subtree;
            }

            return tree.Select(e => new GitFileEntry
            {
                Name = e.Name,
                Path = e.Path,
                Type = e.TargetType == TreeEntryTargetType.Tree ? "dir" : "file",
                Size = e.Target is Blob blob ? blob.Size : (long?)null,
                Sha = e.Target?.Sha,
            }).ToList();
        }

        public (string content, long size, string sha) GetFileContent(string repoId, string branch, string path)
        {
            using var repo = new Repository(RepoPath(repoId));
            var tip = ResolveTip(repo, branch);
            var entry = tip?[path];
            if (entry?.Target is not Blob blob) return (null, 0, null);

            return (Convert.ToBase64String(ReadBlob(blob)), blob.Size, entry.Target.Sha);
        }

        private static byte[] ReadBlob(Blob blob)
        {
            using var stream = blob.GetContentStream();
            using var mem = new MemoryStream();
            stream.CopyTo(mem);
            return mem.ToArray();
        }

        public GitCommit UpdateFile(string repoId, string branch, string path, byte[] content, string commitMessage, string authorName, string authorEmail)
        {
            using var repo = new Repository(RepoPath(repoId));
            var branchName = string.IsNullOrWhiteSpace(branch) ? repo.Head.FriendlyName : branch;
            var target = repo.Branches[branchName];

            var treeDefinition = target?.Tip?.Tree != null ? TreeDefinition.From(target.Tip.Tree) : new TreeDefinition();
            using var contentStream = new MemoryStream(content);
            var blob = repo.ObjectDatabase.CreateBlob(contentStream);
            treeDefinition.Add(path, blob, Mode.NonExecutableFile);

            var tree = repo.ObjectDatabase.CreateTree(treeDefinition);
            var author = new Signature(authorName, authorEmail, DateTimeOffset.Now);
            var parents = target?.Tip != null ? new[] { target.Tip } : Array.Empty<Commit>();
            var commit = repo.ObjectDatabase.CreateCommit(author, author, commitMessage, tree, parents, false);

            if (target != null)
                repo.Refs.UpdateTarget(repo.Refs[target.CanonicalName], commit.Id);
            else
                repo.Refs.Add($"refs/heads/{branchName}", commit.Id);

            TouchPushedAt(repoId);
            return ToGitCommit(commit);
        }

        public GitCommit DeleteFile(string repoId, string branch, string path, string commitMessage, string authorName, string authorEmail)
        {
            using var repo = new Repository(RepoPath(repoId));
            var branchName = string.IsNullOrWhiteSpace(branch) ? repo.Head.FriendlyName : branch;
            var target = repo.Branches[branchName];
            if (target?.Tip == null) return null;

            var treeDefinition = TreeDefinition.From(target.Tip.Tree);
            treeDefinition.Remove(path);

            var tree = repo.ObjectDatabase.CreateTree(treeDefinition);
            var author = new Signature(authorName, authorEmail, DateTimeOffset.Now);
            var commit = repo.ObjectDatabase.CreateCommit(author, author, commitMessage, tree, new[] { target.Tip }, false);
            repo.Refs.UpdateTarget(repo.Refs[target.CanonicalName], commit.Id);

            TouchPushedAt(repoId);
            return ToGitCommit(commit);
        }

        private void TouchPushedAt(string repoId) =>
            _repositories.UpdateOne(r => r.Id == repoId, Builders<GitRepository>.Update.Set(r => r.PushedAt, DateTime.UtcNow));

        // ================= Collaborators =================

        public IEnumerable<Collaborator> GetCollaborators(string repoId) =>
            _collaborators.Find(c => c.RepoId == repoId).ToList();

        public Collaborator AddCollaborator(string repoId, string userId, string role, string addedBy)
        {
            var collaborator = new Collaborator
            {
                Id = Guid.NewGuid().ToString(),
                RepoId = repoId,
                UserId = userId,
                Role = string.IsNullOrWhiteSpace(role) ? "read" : role,
                AddedBy = addedBy,
                AddedAt = DateTime.UtcNow,
            };
            _collaborators.InsertOne(collaborator);
            return collaborator;
        }

        public bool UpdateCollaborator(string repoId, string userId, string role)
        {
            var update = Builders<Collaborator>.Update.Set(c => c.Role, role);
            var result = _collaborators.UpdateOne(c => c.RepoId == repoId && c.UserId == userId, update);
            return result.ModifiedCount > 0;
        }

        public bool RemoveCollaborator(string repoId, string userId)
        {
            var result = _collaborators.DeleteOne(c => c.RepoId == repoId && c.UserId == userId);
            return result.DeletedCount > 0;
        }

        // ================= Pull Requests =================

        public (IEnumerable<PullRequest> pullRequests, int totalCount) GetPullRequests(string repoId, string state, int page, int perPage)
        {
            var filter = Builders<PullRequest>.Filter.Eq(p => p.RepoId, repoId);
            if (!string.IsNullOrWhiteSpace(state))
            {
                // The frontend's "closed" bucket covers both a plain-closed PR and a merged
                // one (it tells them apart afterwards via MergedAt), so "closed" here means
                // "anything that isn't open" rather than an exact Status match.
                filter = state == "closed"
                    ? Builders<PullRequest>.Filter.And(filter, Builders<PullRequest>.Filter.Ne(p => p.Status, "open"))
                    : Builders<PullRequest>.Filter.And(filter, Builders<PullRequest>.Filter.Eq(p => p.Status, state));
            }

            var totalCount = (int)_pullRequests.CountDocuments(filter);
            var effectivePerPage = perPage <= 0 ? 20 : perPage;
            var effectivePage = page <= 0 ? 1 : page;
            var items = _pullRequests.Find(filter)
                .SortByDescending(p => p.Number)
                .Skip((effectivePage - 1) * effectivePerPage)
                .Limit(effectivePerPage)
                .ToList();
            return (items, totalCount);
        }

        public PullRequest CreatePullRequest(string repoId, string title, string body, string sourceBranch, string targetBranch, bool isDraft, string authorId)
        {
            var pr = new PullRequest
            {
                Id = Guid.NewGuid().ToString(),
                RepoId = repoId,
                Number = GetNextPullRequestNumber(repoId),
                Title = title,
                Body = body,
                SourceBranch = sourceBranch,
                TargetBranch = targetBranch,
                IsDraft = isDraft,
                Status = "open",
                AuthorId = authorId,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            _pullRequests.InsertOne(pr);
            return pr;
        }

        public PullRequest GetPullRequest(string repoId, int number) =>
            _pullRequests.Find(p => p.RepoId == repoId && p.Number == number).FirstOrDefault();

        public bool UpdatePullRequest(string repoId, int number, UpdatePullRequestRequest update)
        {
            var builder = Builders<PullRequest>.Update;
            var updates = new List<UpdateDefinition<PullRequest>> { builder.Set(p => p.UpdatedAt, DateTime.UtcNow) };
            if (update.Title != null) updates.Add(builder.Set(p => p.Title, update.Title));
            if (update.Body != null) updates.Add(builder.Set(p => p.Body, update.Body));
            if (!string.IsNullOrWhiteSpace(update.State))
            {
                updates.Add(builder.Set(p => p.Status, update.State));
                updates.Add(builder.Set(p => p.ClosedAt, update.State == "closed" ? DateTime.UtcNow : (DateTime?)null));
            }

            var result = _pullRequests.UpdateOne(p => p.RepoId == repoId && p.Number == number, builder.Combine(updates));
            return result.MatchedCount > 0;
        }

        public (bool success, bool conflict) MergePullRequest(string repoId, int number, string mergeMethod, string commitMessage, string mergedById, string authorName, string authorEmail)
        {
            var pr = GetPullRequest(repoId, number);
            if (pr == null || pr.Status != "open") return (false, false);

            using var repo = new Repository(RepoPath(repoId));
            var source = repo.Branches[pr.SourceBranch];
            var target = repo.Branches[pr.TargetBranch];
            if (source?.Tip == null || target?.Tip == null) return (false, false);

            // "rebase" is approximated here as a single squashed commit (see MergeInto) rather
            // than a true commit-by-commit replay onto target — a full rebase is a much larger
            // feature and this preserves the same net result for the target branch.
            var mode = mergeMethod == "squash" || mergeMethod == "rebase" ? "squash" : "merge";
            var message = string.IsNullOrWhiteSpace(commitMessage)
                ? $"Merge pull request #{number} from {pr.SourceBranch}\n\n{pr.Title}"
                : commitMessage;

            var (success, conflict) = MergeInto(repo, source.Tip, target, mode, message, authorName, authorEmail);
            if (!success) return (success, conflict);

            var update = Builders<PullRequest>.Update
                .Set(p => p.Status, "merged")
                .Set(p => p.MergedAt, DateTime.UtcNow)
                .Set(p => p.MergedById, mergedById)
                .Set(p => p.UpdatedAt, DateTime.UtcNow);
            _pullRequests.UpdateOne(p => p.RepoId == repoId && p.Number == number, update);
            TouchPushedAt(repoId);

            return (true, false);
        }

        public (int additions, int deletions, int changedFiles, int commits) GetPullRequestStats(string repoId, PullRequest pr)
        {
            using var repo = new Repository(RepoPath(repoId));
            var source = repo.Branches[pr.SourceBranch];
            var target = repo.Branches[pr.TargetBranch];
            if (source?.Tip == null || target?.Tip == null) return (0, 0, 0, 0);

            var (files, additions, deletions) = DiffTrees(repo, target.Tip.Tree, source.Tip.Tree);

            var filter = new CommitFilter { IncludeReachableFrom = source.Tip, ExcludeReachableFrom = target.Tip };
            var commitCount = repo.Commits.QueryBy(filter).Count();

            return (additions, deletions, files.Count, commitCount);
        }

        public List<GitFileChange> GetPullRequestFiles(string repoId, PullRequest pr)
        {
            using var repo = new Repository(RepoPath(repoId));
            var source = repo.Branches[pr.SourceBranch];
            var target = repo.Branches[pr.TargetBranch];
            if (source?.Tip == null || target?.Tip == null) return new List<GitFileChange>();

            var (files, _, _) = DiffTrees(repo, target.Tip.Tree, source.Tip.Tree);
            return files;
        }

        private int GetNextPullRequestNumber(string repoId)
        {
            var last = _pullRequests.Find(pr => pr.RepoId == repoId)
                .SortByDescending(pr => pr.Number)
                .FirstOrDefault();
            return (last?.Number ?? 0) + 1;
        }

        // ================= Pull Request Comments =================

        public IEnumerable<PRComment> GetPullRequestComments(string repoId, int number) =>
            _prComments.Find(c => c.RepoId == repoId && c.PrNumber == number).SortBy(c => c.CreatedAt).ToList();

        public PRComment AddPullRequestComment(string repoId, int number, string body, string authorId, string path, int? line, string commitSha)
        {
            var comment = new PRComment
            {
                Id = Guid.NewGuid().ToString(),
                RepoId = repoId,
                PrNumber = number,
                Body = body,
                AuthorId = authorId,
                Path = path,
                Line = line,
                CommitSha = commitSha,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            _prComments.InsertOne(comment);
            return comment;
        }
    }
}

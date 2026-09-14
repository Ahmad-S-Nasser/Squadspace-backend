using MongoDB.Driver;

namespace RafeeqyNotes.Api.Migrations
{
    /// <summary>
    /// Fills the parent ids that M001 could not, by reading the live parent instead of the
    /// document's own embedded snapshot.
    /// </summary>
    /// <remarks>
    /// M001 can only copy what is already inside the document. Many snapshots were taken before
    /// the organization was attached to the project, so the id it needs is simply not in there -
    /// on this dataset that left 8 of 14 boards with a ProjectId but no OrganizationId, even
    /// though the live Projects document knew the answer perfectly well.
    ///
    /// This is the same fallback OrgScope.OrgIdForBoardAsync already performs at request time
    /// ("older board documents may embed a project snapshot taken before the organization was
    /// attached"), applied once in bulk instead of on every read.
    ///
    /// Walks parent to child, so a value rescued on Boards is available to Notes, and one
    /// rescued on Notes is available to NoteTasks, within the same pass.
    /// </remarks>
    public class M002_ResolveStaleParentIds : IMigration
    {
        public string Id => "002-resolve-stale-parent-ids";

        public string Description => "Fill parent ids from the live parent where the embedded snapshot predates them";

        private static readonly ParentIdResolver.Link[] Chain =
        {
            new("Boards",    "ProjectId", "Projects", new[] { "OrganizationId" }),
            new("Notes",     "BoardId",   "Boards",   new[] { "ProjectId", "OrganizationId" }),
            new("NoteTasks", "NoteId",    "Notes",    new[] { "BoardId", "ProjectId", "OrganizationId" }),
        };

        public Task<string> RunAsync(IMongoDatabase database) =>
            ParentIdResolver.ResolveAsync(database, Chain);
    }
}

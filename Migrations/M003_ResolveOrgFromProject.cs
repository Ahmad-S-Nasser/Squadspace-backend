using MongoDB.Driver;

namespace RafeeqyNotes.Api.Migrations
{
    /// <summary>
    /// Last-resort organization resolution: straight from the document's own ProjectId.
    /// </summary>
    /// <remarks>
    /// M002 walks the ownership chain one link at a time, which fails when a link in the middle
    /// is gone. A task whose parent note has been deleted keeps a perfectly good ProjectId, and
    /// that project knows its organization - the chain simply had no way to reach it.
    ///
    /// This is not redundant with M002: it answers a different question. M002 asks "what does my
    /// parent say?"; this asks "what does my project say?", and only for documents still missing
    /// the answer after the chain has run. Ordering matters, so it is a separate migration rather
    /// than extra links appended to M002 - which has already been applied and must not change.
    ///
    /// Whatever remains after this genuinely has no organization anywhere in reach. Those
    /// documents were already unreachable through the API before this work: OrgScope fails closed
    /// on an unresolvable organization, so every request for them returns 404 today.
    /// </remarks>
    public class M003_ResolveOrgFromProject : IMigration
    {
        public string Id => "003-resolve-org-from-project";

        public string Description => "Resolve OrganizationId directly from ProjectId where the ownership chain is broken";

        private static readonly ParentIdResolver.Link[] Links =
        {
            new("Boards",    "ProjectId", "Projects", new[] { "OrganizationId" }),
            new("Notes",     "ProjectId", "Projects", new[] { "OrganizationId" }),
            new("NoteTasks", "ProjectId", "Projects", new[] { "OrganizationId" }),
        };

        public Task<string> RunAsync(IMongoDatabase database) =>
            ParentIdResolver.ResolveAsync(database, Links);
    }
}

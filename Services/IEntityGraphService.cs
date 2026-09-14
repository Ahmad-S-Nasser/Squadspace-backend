using RafeeqyNotes.Api.Models;
using RafeeqyNotes.Models;

namespace RafeeqyNotes.Api.Services
{
    /// <summary>
    /// Populates an entity's parent linkage from the database immediately before it is written.
    /// </summary>
    /// <remarks>
    /// The entity graph is denormalized: a NoteTask embeds its whole Note, which embeds its
    /// Board, which embeds its Project, which embeds its Organization. Organization resolution
    /// (<see cref="Helpers.OrgScope"/>) walks that chain, so the chain is security-relevant and
    /// cannot simply be dropped.
    ///
    /// This service is what makes the flat parent ids trustworthy. The client already sends
    /// fields called <c>projectId</c>, <c>boardId</c> and <c>noteId</c> alongside the snapshot,
    /// so those names bind straight off the request body. If anything resolved tenancy from the
    /// bound value, a caller could submit a legitimate embedded parent (passing the guard) and a
    /// forged scalar, and every later read of that document would resolve to someone else's
    /// organization. So hydration CLEARS the incoming scalars and rewrites them from what it
    /// actually read out of the database.
    ///
    /// Two ways to name the parent:
    ///
    /// - Pass the id explicitly. The caller must have authorized it first. This is the path a
    ///   scalar-only create endpoint uses, and it is why callers no longer have to ship a
    ///   four-level nested object graph to create one task.
    /// - Pass nothing, and the parent id is taken from the embedded snapshot — which is the same
    ///   value the controller's guard already checked, so it is exactly as trustworthy as the
    ///   authorization that preceded it.
    ///
    /// Hydration is deliberately tolerant. If the parent cannot be read, the entity's existing
    /// embedded snapshot is left untouched and the scalars stay null; nothing throws. A parentless
    /// entity is a pre-existing condition in this data (the census found projects with no
    /// organization at all), and refusing the write would turn a stale document into an outage.
    /// </remarks>
    public interface IEntityGraphService
    {
        /// <param name="organizationId">Authorized organization id. Falls back to the embedded snapshot.</param>
        Task HydrateProjectAsync(Project project, string organizationId = null);

        /// <param name="projectId">Authorized project id. Falls back to the embedded snapshot.</param>
        Task HydrateBoardAsync(Board board, string projectId = null);

        /// <param name="boardId">Authorized board id. Falls back to the embedded snapshot.</param>
        Task HydrateNoteAsync(Note note, string boardId = null);

        /// <param name="noteId">Authorized note id. Falls back to the embedded snapshot.</param>
        Task HydrateTaskAsync(NoteTask task, string noteId = null);

        /// <summary>
        /// Hydrates a batch of tasks that may share parents, reading each distinct parent once.
        /// </summary>
        Task HydrateTasksAsync(IEnumerable<NoteTask> tasks);
    }
}

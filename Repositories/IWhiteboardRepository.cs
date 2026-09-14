using RafeeqyNotes.Api.Models;

namespace RafeeqyNotes.Api.Repositories
{
    public interface IWhiteboardRepository
    {
        Task<List<Whiteboard>> GetByProjectIdAsync(string projectId);
        Task<Whiteboard?> GetByIdAsync(string id);
        Task CreateAsync(Whiteboard board);
        Task UpdateAsync(Whiteboard board);
        Task<bool> DeleteAsync(string id);

        /// <summary>
        /// Targeted per-element writes, used by the element endpoints instead of UpdateAsync's
        /// whole-document replace so two users editing DIFFERENT elements concurrently can't
        /// silently discard each other's write (the whole-document replace races: both requests
        /// read the full board, mutate one element in memory, then replace the entire document -
        /// the second replace wins and drops whatever the first one wrote).
        /// </summary>
        Task AddElementAsync(string boardId, WhiteboardElement element);
        Task UpdateElementAsync(string boardId, string elementId, WhiteboardElement element);
        Task<bool> RemoveElementAsync(string boardId, string elementId);
    }
}

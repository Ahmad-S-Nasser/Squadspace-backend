using RafeeqyNotes.Api.Models;

namespace RafeeqyNotes.Api.Repositories
{
    public interface IBoardRepository
    {
        Task<List<Board>> GetByProjectIdAsync(string projectId);
        Task<Board?> GetByIdAsync(string id);
        Task<List<Board>> GetAllAsync();

        /// <summary>Boards in any of the given organizations, filtered by the database.</summary>
        Task<List<Board>> GetByOrganizationIdsAsync(IEnumerable<string> organizationIds);

        Task<long> GetNotesCountByBoardIDAsync(string board_id);

        /// <summary>Note counts for many boards in ONE round trip, keyed by board id.</summary>
        Task<Dictionary<string, int>> GetNoteCountsAsync(IEnumerable<string> boardIds);
        Task CreateAsync(Board board);
        Task UpdateAsync(Board board);
        Task DeleteAsync(string id);
    }
}

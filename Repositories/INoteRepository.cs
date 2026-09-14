using RafeeqyNotes.Api.Models;

namespace RafeeqyNotes.Api.Repositories
{
    public interface INoteRepository
    {
        Task<Note?> GetByIdAsync(string id);

        /// <summary>Includes soft-deleted notes. For ownership checks and reporting only.</summary>
        Task<Note?> GetByIdIncludingDeletedAsync(string id);

        Task<List<Note>> GetAllAsync();
        Task<List<Note>> GetNotesByBoardIDAsync(string board_id);
        Task CreateAsync(Note note);
        Task UpdateAsync(Note note);

        /// <summary>Permanently removes the document. Prefer <see cref="SoftDeleteAsync"/>.</summary>
        Task DeleteAsync(string id);

        /// <summary>Marks a note deleted while retaining it for reporting and analytics.</summary>
        Task<bool> SoftDeleteAsync(string id, string deletedBy);

        Task<Note?> GetByShareTokenAsync(string shareToken);
    }
}
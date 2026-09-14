using Microsoft.AspNetCore.Mvc;
using RafeeqyNotes.Models;

namespace RafeeqyNotes.Api.Repositories
{
    public interface INoteTaskRepository
    {
        Task<List<NoteTask>> GetByNoteIdAsync(string noteId);
        Task<NoteTask?> GetByIdAsync(string id);
        Task<List<NoteTask>> GetByCreatorId(string contributorId);
        Task<List<NoteTask>> GetByReviewerId(string contributorId);
        Task<List<NoteTask>> GetByAssignedToID(string contributorId);
        Task<List<NoteTask>> GetBySprintID(string sprintId);
        Task<List<NoteTask>> GetByProjectIdAsync(string projectId);
        /// <summary>Tasks with a timer still running for this user, across every project.</summary>
        /// <remarks>
        /// Backs the one-running-timer-per-user rule. Without it, people leave three tasks
        /// ticking overnight and every one of those totals is wrong.
        /// </remarks>
        Task<List<NoteTask>> GetWithRunningTimerAsync(string userId);

        Task CreateAsync(NoteTask task);
        Task UpdateAsync(NoteTask task);
        Task DeleteAsync(string id);
    }
}

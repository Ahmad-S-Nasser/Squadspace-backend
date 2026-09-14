using MongoDB.Driver;
using RafeeqyNotes.Api.Models;
using RafeeqyNotes.Api.Config;
using RafeeqyNotes.Api.Services;
using RafeeqyNotes.Api.Helpers;

namespace RafeeqyNotes.Api.Repositories
{
    public class BoardRepository : IBoardRepository
    {
        private readonly IMongoCollection<Project> _projects;
        private readonly IMongoCollection<Board> _boards;
        private readonly IMongoCollection<Note> _notes;

        private readonly IEntityGraphService _graph;

        public BoardRepository(MongoDbSettings settings, IEntityGraphService graph)
        {
            _graph = graph;
            var client = new MongoClient(settings.ConnectionString);
            var database = client.GetDatabase(settings.DatabaseName);
            _boards = database.GetCollection<Board>(settings.BoardsCollection);
            _notes = database.GetCollection<Note>(settings.NotesCollection);
            _projects = database.GetCollection<Project>(settings.ProjectsCollection);
        }

        // Queries the flat id rather than the embedded path. Equivalent by construction:
        // M001 copies the embedded value verbatim, so a document missing the flat id is a
        // document whose embedded path was missing too - and it now uses an index instead of
        // scanning the collection.
        public async Task<List<Board>> GetByProjectIdAsync(string projectId) =>
            await _boards.Find(b => b.ProjectId == projectId).ToListAsync();

        public async Task<Board?> GetByIdAsync(string id) =>
            await _boards.Find(b => b.Id == id).FirstOrDefaultAsync();

        public async Task<List<Board>> GetAllAsync() =>
            await _boards.Find(_ => true).ToListAsync();

        /// <remarks>
        /// The sidebar's board list used to load EVERY board in the database and filter it down
        /// in C#. That is a collection scan whose cost is set by the size of the whole tenancy
        /// rather than by the caller's own data. This filters on the indexed OrganizationId, so
        /// the database returns only what the caller may see.
        /// </remarks>
        public async Task<List<Board>> GetByOrganizationIdsAsync(IEnumerable<string> organizationIds)
        {
            var ids = (organizationIds ?? Enumerable.Empty<string>())
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (ids.Count == 0) return new List<Board>();

            return await _boards
                .Find(Builders<Board>.Filter.In(b => b.OrganizationId, ids))
                .ToListAsync();
        }

        public async Task<long> GetNotesCountByBoardIDAsync(string board_id) =>
            await _notes.Find(n => n.BoardId == board_id).CountDocumentsAsync();

        /// <remarks>
        /// One grouped aggregation instead of one CountDocuments per board awaited in a loop.
        /// The loop cost a round trip per board on a request that already returns every board
        /// the caller can see, so it got slower exactly as a customer grew.
        ///
        /// Boards with no notes are simply absent from the result; the caller defaults them to
        /// zero rather than this padding the dictionary with every id it was handed.
        /// </remarks>
        public async Task<Dictionary<string, int>> GetNoteCountsAsync(IEnumerable<string> boardIds)
        {
            var ids = (boardIds ?? Enumerable.Empty<string>())
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct()
                .ToList();

            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (ids.Count == 0) return counts;

            var grouped = await _notes.Aggregate()
                .Match(Builders<Note>.Filter.And(
                    Builders<Note>.Filter.In(n => n.BoardId, ids),
                    Builders<Note>.Filter.Ne(n => n.IsDeleted, true)))
                .Group(n => n.BoardId, g => new { BoardId = g.Key, Count = g.Count() })
                .ToListAsync();

            foreach (var row in grouped)
            {
                if (!string.IsNullOrEmpty(row.BoardId)) counts[row.BoardId] = row.Count;
            }

            return counts;
        }
        public async Task CreateAsync(Board board)
        {
            // Preserve a caller-supplied id instead of overwriting it. An importer has to be
            // able to choose ids, or a re-run creates duplicates rather than colliding. Same
            // shape as ContributorRepository.CreateAsync.
            if (string.IsNullOrWhiteSpace(board.Id)) board.Id = Guid.NewGuid().ToString();

            await _graph.HydrateBoardAsync(board);

            try { await _boards.InsertOneAsync(board); }
            catch (MongoWriteException ex) when (DuplicateEntityException.IsDuplicateKey(ex))
            { throw new DuplicateEntityException("board", board.Id, ex); }
        }


        // Tenancy is re-derived from the STORED record, never from the request body. These
        // endpoints replace the whole document, so without this a caller who may edit a
        // resource can reparent it by naming a different parent in the body - and every guard
        // downstream then authorizes against the organization the caller chose. This is the
        // same "authorize against the stored record" rule the Phase 0 guards follow.
        public async Task UpdateAsync(Board board)
        {
            var stored = await _boards.Find(b => b.Id == board.Id).FirstOrDefaultAsync();
            await _graph.HydrateBoardAsync(board, stored?.ProjectId ?? stored?.Project?.Id);

            await _boards.ReplaceOneAsync(b => b.Id == board.Id, board);
        }

        public async Task DeleteAsync(string id) =>
            await _boards.DeleteOneAsync(b => b.Id == id);
    }
}
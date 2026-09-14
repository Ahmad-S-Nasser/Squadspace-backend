using MongoDB.Driver;
using RafeeqyNotes.Api.Models;
using RafeeqyNotes.Api.Config;
using RafeeqyNotes.Api.Services;
using RafeeqyNotes.Api.Helpers;

namespace RafeeqyNotes.Api.Repositories
{
    public class NoteRepository : INoteRepository
    {
        private readonly IMongoCollection<Note> _notes;
        private readonly IMongoCollection<Contributor> _contributor;
        private readonly ElasticSearchService _elastic;

        private readonly IEntityGraphService _graph;

        public NoteRepository(MongoDbSettings settings, ElasticSearchService elastic, IEntityGraphService graph)
        {
            _graph = graph;
            var client = new MongoClient(settings.ConnectionString);
            var database = client.GetDatabase(settings.DatabaseName);
            _notes = database.GetCollection<Note>(settings.NotesCollection);
            _contributor= database.GetCollection<Contributor>(settings.ContributorsCollection);

            _elastic = elastic;
        }

        // Soft-deleted notes are retained for reporting and analytics but must never surface
        // through a normal read. Every query below composes this filter; add it to any new one.
        private static FilterDefinition<Note> NotDeleted =>
            Builders<Note>.Filter.Ne(n => n.IsDeleted, true);

        public async Task<Note?> GetByIdAsync(string id) =>
            await _notes.Find(Builders<Note>.Filter.And(
                Builders<Note>.Filter.Eq(n => n.Id, id), NotDeleted)).FirstOrDefaultAsync();

        /// <summary>Includes soft-deleted notes. For ownership checks and reporting only.</summary>
        public async Task<Note?> GetByIdIncludingDeletedAsync(string id) =>
            await _notes.Find(n => n.Id == id).FirstOrDefaultAsync();

        public async Task<List<Note>> GetAllAsync() =>
            await _notes.Find(NotDeleted).ToListAsync();

        // Queries the flat id rather than the embedded path. Equivalent by construction:
        // M001 copies the embedded value verbatim, so a document missing the flat id is a
        // document whose embedded path was missing too - and it now uses an index instead of
        // scanning the collection.
        public async Task<List<Note>> GetNotesByBoardIDAsync(string board_id) =>
            await _notes.Find(Builders<Note>.Filter.And(
                Builders<Note>.Filter.Eq(n => n.BoardId, board_id), NotDeleted)).ToListAsync();

        public async Task CreateAsync(Note note)
        {
            // Preserve a caller-supplied id instead of overwriting it. An importer has to be
            // able to choose ids, or a re-run creates duplicates rather than colliding. Same
            // shape as ContributorRepository.CreateAsync.
            if (string.IsNullOrWhiteSpace(note.Id)) note.Id = Guid.NewGuid().ToString();

            await _graph.HydrateNoteAsync(note);
            note.Author=_contributor.Find(c => c.Id == note.AuthorId).FirstOrDefaultAsync().Result;
            note.Contributors.Add(note.Author);
            note.Mood = char.ToUpper(note.Mood[0]) + note.Mood.Substring(1);
            try { await _notes.InsertOneAsync(note); }
            catch (MongoWriteException ex) when (DuplicateEntityException.IsDuplicateKey(ex))
            { throw new DuplicateEntityException("note", note.Id, ex); }

            // Index in ElasticSearch
            await _elastic.IndexNoteAsync(note);
        }


        // Tenancy is re-derived from the STORED record, never from the request body. These
        // endpoints replace the whole document, so without this a caller who may edit a
        // resource can reparent it by naming a different parent in the body - and every guard
        // downstream then authorizes against the organization the caller chose. This is the
        // same "authorize against the stored record" rule the Phase 0 guards follow.
        public async Task UpdateAsync(Note note)
        {
            var stored = await _notes.Find(n => n.Id == note.Id).FirstOrDefaultAsync();
            await _graph.HydrateNoteAsync(note, stored?.BoardId ?? stored?.Board?.Id);

            await _notes.ReplaceOneAsync(n => n.Id == note.Id, note);

            // Re-index updated note
            await _elastic.IndexNoteAsync(note);
        }

        public async Task DeleteAsync(string id)
        {
            await _notes.DeleteOneAsync(n => n.Id == id);
            // Optional: remove from ElasticSearch (future extension)
        }

        /// <summary>
        /// Marks a note deleted without removing the document, so reporting and analytics
        /// keep a complete history. Returns false if the note doesn't exist or is already
        /// deleted (which makes a repeated delete idempotent rather than an error).
        /// </summary>
        public async Task<bool> SoftDeleteAsync(string id, string deletedBy)
        {
            var filter = Builders<Note>.Filter.And(
                Builders<Note>.Filter.Eq(n => n.Id, id), NotDeleted);

            var update = Builders<Note>.Update
                .Set(n => n.IsDeleted, true)
                .Set(n => n.DeletedAt, DateTime.UtcNow)
                .Set(n => n.DeletedBy, deletedBy)
                .Set(n => n.UpdatedAt, DateTime.UtcNow)
                // Revoke any public share link, or a deleted note stays readable by URL.
                .Set(n => n.IsPublic, false)
                .Set(n => n.ShareToken, null);

            var result = await _notes.UpdateOneAsync(filter, update);
            return result.ModifiedCount > 0;
        }

        public async Task<Note?> GetByShareTokenAsync(string shareToken) =>
            await _notes.Find(Builders<Note>.Filter.And(
                Builders<Note>.Filter.Eq(n => n.ShareToken, shareToken),
                Builders<Note>.Filter.Eq(n => n.IsPublic, true),
                NotDeleted)).FirstOrDefaultAsync();
    }
}

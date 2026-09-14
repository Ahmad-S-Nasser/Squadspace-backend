using MongoDB.Bson;
using MongoDB.Driver;

namespace RafeeqyNotes.Api.Migrations
{
    /// <summary>
    /// Copies the parent ids buried in each document's embedded snapshot up into flat fields.
    /// </summary>
    /// <remarks>
    /// New writes get these from EntityGraphService. Everything written before this release has
    /// them only inside the nested graph, so until they are backfilled nothing can safely read
    /// the flat field.
    ///
    /// The source paths use <c>_id</c>, not <c>Id</c>: the driver's default convention maps a
    /// member named <c>Id</c> onto <c>_id</c>, so the stored key is <c>Note.Board.Project._id</c>.
    /// A pipeline written against <c>.Id</c> matches every document, sets nothing, and reports
    /// success - which is the failure mode this comment exists to prevent.
    ///
    /// Two independent reasons this is re-runnable: <c>$ifNull</c> never overwrites a value that
    /// is already there, and the inner <c>$$REMOVE</c> means a document whose snapshot is also
    /// missing the id is left without the field rather than being given an explicit null. An
    /// explicit null would look "present" to the next run and poison it.
    ///
    /// It cannot invent what is not there. The census found projects carrying no organization at
    /// all; those keep a missing OrganizationId, stay unresolvable, and are reported as leftovers
    /// rather than quietly counted as done.
    /// </remarks>
    public class M001_BackfillParentIds : IMigration
    {
        public string Id => "001-backfill-parent-ids";

        public string Description => "Lift NoteId/BoardId/ProjectId/OrganizationId out of the embedded snapshots";

        private static readonly (string Collection, (string Field, string Source)[] Fields)[] Plan =
        {
            ("Projects", new[]
            {
                ("OrganizationId", "$Organization._id"),
            }),
            ("Boards", new[]
            {
                ("ProjectId",      "$Project._id"),
                ("OrganizationId", "$Project.Organization._id"),
            }),
            ("Notes", new[]
            {
                ("BoardId",        "$Board._id"),
                ("ProjectId",      "$Board.Project._id"),
                ("OrganizationId", "$Board.Project.Organization._id"),
            }),
            ("NoteTasks", new[]
            {
                ("NoteId",         "$Note._id"),
                ("BoardId",        "$Note.Board._id"),
                ("ProjectId",      "$Note.Board.Project._id"),
                ("OrganizationId", "$Note.Board.Project.Organization._id"),
            }),
        };

        public async Task<string> RunAsync(IMongoDatabase database)
        {
            var parts = new List<string>();

            foreach (var (collectionName, fields) in Plan)
            {
                var collection = database.GetCollection<BsonDocument>(collectionName);

                // Only touch documents actually missing one of the flat fields.
                var filter = Builders<BsonDocument>.Filter.Or(
                    fields.Select(f => Builders<BsonDocument>.Filter.Exists(f.Field, false)));

                var before = await collection.CountDocumentsAsync(filter);

                var set = new BsonDocument();
                foreach (var (field, source) in fields)
                {
                    // Keep an existing value; otherwise take it from the snapshot; otherwise
                    // leave the field absent entirely.
                    set[field] = new BsonDocument("$ifNull", new BsonArray
                    {
                        "$" + field,
                        new BsonDocument("$ifNull", new BsonArray { source, "$$REMOVE" }),
                    });
                }

                var pipeline = new BsonDocument[] { new BsonDocument("$set", set) };
                var result = await collection.UpdateManyAsync(
                    filter, Builders<BsonDocument>.Update.Pipeline(pipeline));

                var leftover = await collection.CountDocumentsAsync(filter);

                parts.Add($"{collectionName}: {before} incomplete -> modified {result.ModifiedCount}, "
                          + $"{leftover} still unresolvable");
            }

            return string.Join("; ", parts);
        }
    }
}

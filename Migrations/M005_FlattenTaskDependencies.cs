using MongoDB.Bson;
using MongoDB.Driver;

namespace RafeeqyNotes.Api.Migrations
{
    /// <summary>
    /// Replaces the embedded dependency objects with a list of ids.
    /// </summary>
    /// <remarks>
    /// <c>NoteTask.Dependencies</c> stored whole task documents - each carrying its own
    /// Note - Board - Project chain and three embedded people. On the largest task in dev that
    /// was 3,635 bytes, 46% of the document, to express what is a list of ids.
    ///
    /// SAFE BY ORDERING, like M004. The model change ships in the same release and already reads
    /// <c>DependencyIds</c>, hydrating the summaries on the way out. A document this has not
    /// reached yet simply has an empty <c>DependencyIds</c> and shows no dependencies until it
    /// does - it never shows wrong ones - and the runner retries on the next start.
    ///
    /// IDEMPOTENT BY CONSTRUCTION. The filter requires <c>Dependencies</c> to exist, and the
    /// update removes it, so a second run matches nothing. It is also atomic per document: one
    /// aggregation-pipeline update sets the ids and drops the old array together, so there is no
    /// window in which a task has lost its dependencies without having gained its ids.
    ///
    /// NOT REVERSIBLE, and worth being plain about. The embedded copies are discarded, and the
    /// ids cannot rebuild them. That is the point - they were a stale snapshot of data that lives
    /// authoritatively on the dependency task itself - but the rollback story here is the deploy
    /// zip plus a database restore, not this migration.
    /// </remarks>
    public class M005_FlattenTaskDependencies : IMigration
    {
        public string Id => "005-flatten-task-dependencies";

        public string Description =>
            "Replace embedded NoteTask.Dependencies objects with a DependencyIds string list";

        public async Task<string> RunAsync(IMongoDatabase database)
        {
            var tasks = database.GetCollection<BsonDocument>("NoteTasks");

            var before = await tasks.CountDocumentsAsync(
                Builders<BsonDocument>.Filter.Exists("Dependencies"));

            if (before == 0) return "no documents carry an embedded Dependencies array";

            // $map over the old array picking _id, with $ifNull guarding a document whose
            // Dependencies is null rather than absent, and $filter dropping any entry that has
            // no _id at all - those cannot be resolved later and an empty string in the list
            // would only produce a lookup that never matches.
            var pipeline = new BsonDocument[]
            {
                new("$set", new BsonDocument("DependencyIds",
                    new BsonDocument("$filter", new BsonDocument
                    {
                        { "input", new BsonDocument("$map", new BsonDocument
                            {
                                { "input", new BsonDocument("$ifNull", new BsonArray { "$Dependencies", new BsonArray() }) },
                                { "as", "d" },
                                { "in", "$$d._id" },
                            })
                        },
                        { "as", "id" },
                        { "cond", new BsonDocument("$and", new BsonArray
                            {
                                new BsonDocument("$ne", new BsonArray { "$$id", BsonNull.Value }),
                                new BsonDocument("$ne", new BsonArray { "$$id", "" }),
                            })
                        },
                    }))),

                new("$unset", "Dependencies"),
            };

            var result = await tasks.UpdateManyAsync(
                Builders<BsonDocument>.Filter.Exists("Dependencies"),
                Builders<BsonDocument>.Update.Pipeline(pipeline));

            var remaining = await tasks.CountDocumentsAsync(
                Builders<BsonDocument>.Filter.Exists("Dependencies"));

            var withIds = await tasks.CountDocumentsAsync(
                Builders<BsonDocument>.Filter.And(
                    Builders<BsonDocument>.Filter.Exists("DependencyIds"),
                    Builders<BsonDocument>.Filter.Ne("DependencyIds", new BsonArray())));

            return $"flattened {result.ModifiedCount} of {before} documents; " +
                   $"{withIds} now carry dependency ids; {remaining} still hold the old array";
        }
    }
}

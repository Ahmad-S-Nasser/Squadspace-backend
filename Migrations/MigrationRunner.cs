using MongoDB.Bson;
using MongoDB.Driver;
using RafeeqyNotes.Api.Config;

namespace RafeeqyNotes.Api.Migrations
{
    /// <summary>
    /// Applies pending schema migrations and ensures indexes, once per process start.
    /// </summary>
    /// <remarks>
    /// This deliberately does NOT live in a repository constructor, which is where the two
    /// existing indexes in this codebase are built (SubscriptionPlanRepository.cs:19-22). Those
    /// run inside the DI registration block in Program.cs, and that block is wrapped in a try
    /// whose catch sets startupError - after which the middleware returns a fixed 500 for every
    /// request, with no detail. So an index whose options later change would throw
    /// IndexOptionsConflict and take the entire product down.
    ///
    /// Running here instead means the worst case is a migration that did not apply: import
    /// idempotency degrades, the product keeps serving. That is the right trade. The failure is
    /// loud in stdout, which is where the deploy runbook already tells you to look.
    /// </remarks>
    public static class MigrationRunner
    {
        private const string LedgerCollection = "_SchemaMigrations";

        public static async Task RunAsync(MongoDbSettings settings, IEnumerable<IMigration> migrations)
        {
            var database = new MongoClient(settings.ConnectionString).GetDatabase(settings.DatabaseName);
            var ledger = database.GetCollection<BsonDocument>(LedgerCollection);

            foreach (var migration in migrations)
            {
                try
                {
                    var already = await ledger
                        .Find(Builders<BsonDocument>.Filter.Eq("_id", migration.Id))
                        .FirstOrDefaultAsync();

                    if (already != null)
                    {
                        Console.WriteLine($"[migration] {migration.Id} already applied, skipping.");
                        continue;
                    }

                    Console.WriteLine($"[migration] {migration.Id} running: {migration.Description}");

                    var startedAt = DateTime.UtcNow;
                    var summary = await migration.RunAsync(database);

                    // Recorded only after the work succeeded. A migration that throws stays
                    // unrecorded and is retried on the next start, which is what you want when
                    // the cause was transient - and harmless when it was not, because every
                    // migration is idempotent in its own right.
                    await ledger.InsertOneAsync(new BsonDocument
                    {
                        { "_id", migration.Id },
                        { "description", migration.Description },
                        { "summary", summary ?? string.Empty },
                        { "appliedAt", startedAt },
                        { "durationMs", (long)(DateTime.UtcNow - startedAt).TotalMilliseconds },
                    });

                    Console.WriteLine($"[migration] {migration.Id} applied. {summary}");
                }
                catch (Exception ex)
                {
                    // Never rethrow. A failed migration must not become a total outage.
                    Console.WriteLine($"[migration] {migration.Id} FAILED and was not recorded: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Creates an index, treating "already there" as success and never throwing.
        /// </summary>
        /// <remarks>
        /// Index creation runs on every start rather than once, so a fresh deployment gets the
        /// indexes without anyone remembering to clear a ledger. CreateOne on an identical
        /// index is a no-op; on one whose options differ it throws IndexOptionsConflict, and
        /// swallowing that here is the whole point - see the class remarks.
        /// </remarks>
        public static async Task EnsureIndexAsync(
            IMongoCollection<BsonDocument> collection,
            CreateIndexModel<BsonDocument> model,
            string label)
        {
            try
            {
                await collection.Indexes.CreateOneAsync(model);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[migration] index {label} not created: {ex.Message}");
            }
        }
    }
}

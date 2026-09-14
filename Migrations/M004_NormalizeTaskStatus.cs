using MongoDB.Bson;
using MongoDB.Driver;
using RafeeqyNotes.Api.Config;
using RafeeqyNotes.Api.Services;

namespace RafeeqyNotes.Api.Migrations
{
    /// <summary>
    /// Rewrites stored task statuses to their canonical slug.
    /// </summary>
    /// <remarks>
    /// ORDERING IS WHAT MAKES THIS SAFE. The tolerant resolver shipped first, so "To-do", "todo"
    /// and "To Do" already behave identically everywhere - analytics, velocity, the board. This
    /// migration is therefore semantically a NO-OP: nothing observable changes when it runs. It
    /// is not a cutover, it is a cleanup the running system is already indifferent to, which is
    /// what allows it on a live database with no transactions available. A half-completed run
    /// leaves a state the system handled correctly that morning.
    ///
    /// What it buys is convergence. Without it the analytics ByStatus grouping shows "To-do" and
    /// "todo" as separate buckets forever, and every future query has to remember the alias
    /// table. The resolver still stays permanently, because importers and integrations will keep
    /// sending unknown spellings.
    ///
    /// CENSUS GATE. It refuses to run if any stored value is unrecognised, rather than guessing
    /// or silently skipping. Refusing throws, which leaves the migration unrecorded and retried
    /// on the next start - loud, and non-fatal, because the runner never rethrows.
    ///
    /// The original is preserved in LegacyStatus, which is the rollback: one UpdateMany copying
    /// it back. That matters because the backup story here is the deploy zips.
    ///
    /// NoteTasks ONLY. Sprint status ("Planning" | "Active" | "Completed" | ...) and user
    /// presence status are separate vocabularies and are not touched.
    /// </remarks>
    public class M004_NormalizeTaskStatus : IMigration
    {
        public string Id => "004-normalize-task-status";

        public string Description => "Rewrite NoteTask.Status to canonical slugs, preserving the original in LegacyStatus";

        public async Task<string> RunAsync(IMongoDatabase database)
        {
            var tasks = database.GetCollection<BsonDocument>("NoteTasks");
            var registry = new TaskStatusSet(StatusCatalogue.Defaults());

            // Census first: every distinct value has to map, or nothing runs.
            var distinct = await tasks.Aggregate<BsonDocument>(new[]
            {
                new BsonDocument("$group", new BsonDocument
                {
                    { "_id", "$Status" },
                    { "n", new BsonDocument("$sum", 1) },
                }),
            }).ToListAsync();

            var unmapped = new List<string>();
            var rewrites = new Dictionary<string, string>();
            var nullCount = 0L;

            foreach (var row in distinct)
            {
                var value = row["_id"];
                var count = row["n"].ToInt64();

                if (value.IsBsonNull)
                {
                    nullCount = count;
                    continue;
                }

                var raw = value.AsString;
                var slug = registry.Find(raw)?.Slug;

                if (slug == null) unmapped.Add($"{raw} ({count})");
                else if (!string.Equals(slug, raw, StringComparison.Ordinal)) rewrites[raw] = slug;
            }

            if (unmapped.Count > 0)
            {
                throw new InvalidOperationException(
                    "Refusing to normalize: these stored statuses match no slug or alias in "
                    + $"StatusCatalogue - {string.Join(", ", unmapped)}. Add them as aliases (or as "
                    + "statuses) and redeploy; the migration will retry on the next start.");
            }

            var modified = 0L;

            foreach (var (raw, slug) in rewrites)
            {
                // Both fields are computed from the INPUT document, so LegacyStatus captures the
                // old value even though Status is rewritten in the same stage.
                //
                // The inner $$REMOVE keeps a document whose Status is absent from gaining an
                // explicit null LegacyStatus - an explicit null reads as "present" to $ifNull and
                // would let a later run overwrite the preserved value with the normalized one.
                var result = await tasks.UpdateManyAsync(
                    Builders<BsonDocument>.Filter.Eq("Status", raw),
                    Builders<BsonDocument>.Update.Pipeline(new[]
                    {
                        new BsonDocument("$set", new BsonDocument
                        {
                            { "LegacyStatus", new BsonDocument("$ifNull", new BsonArray
                                {
                                    "$LegacyStatus",
                                    new BsonDocument("$ifNull", new BsonArray { "$Status", "$$REMOVE" }),
                                }) },
                            { "Status", slug },
                        }),
                    }));

                modified += result.ModifiedCount;
            }

            // A task with no status at all takes the configured default.
            if (nullCount > 0)
            {
                var result = await tasks.UpdateManyAsync(
                    Builders<BsonDocument>.Filter.Eq("Status", BsonNull.Value),
                    Builders<BsonDocument>.Update.Set("Status", registry.DefaultSlug));

                modified += result.ModifiedCount;
            }

            var summary = rewrites.Count == 0 && nullCount == 0
                ? $"already canonical ({distinct.Count} distinct value(s), nothing to rewrite)"
                : $"rewrote {modified} document(s): "
                  + string.Join(", ", rewrites.Select(r => $"\"{r.Key}\" -> {r.Value}"))
                  + (nullCount > 0 ? $"{(rewrites.Count > 0 ? ", " : "")}{nullCount} null -> {registry.DefaultSlug}" : "");

            return summary;
        }
    }
}

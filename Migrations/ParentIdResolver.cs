using MongoDB.Bson;
using MongoDB.Driver;

namespace RafeeqyNotes.Api.Migrations
{
    /// <summary>
    /// Copies missing id fields onto a collection from the live parent it points at.
    /// </summary>
    /// <remarks>
    /// Shared by the migrations that repair parent linkage, so the resolution mechanics exist
    /// once. A migration supplies only the links to walk.
    /// </remarks>
    public static class ParentIdResolver
    {
        /// <summary>One link: documents in <paramref name="collectionName"/> whose
        /// <paramref name="localKey"/> names a document in <paramref name="from"/>.</summary>
        public readonly record struct Link(string Collection, string LocalKey, string From, string[] Fields);

        /// <summary>Walks each link in order and returns a per-link summary for the log.</summary>
        public static async Task<string> ResolveAsync(IMongoDatabase database, IEnumerable<Link> links)
        {
            var parts = new List<string>();

            foreach (var link in links)
            {
                var collection = database.GetCollection<BsonDocument>(link.Collection);

                var missingAny = Builders<BsonDocument>.Filter.And(
                    Builders<BsonDocument>.Filter.Exists(link.LocalKey, true),
                    Builders<BsonDocument>.Filter.Or(
                        link.Fields.Select(f => Builders<BsonDocument>.Filter.Exists(f, false))));

                var before = await collection.CountDocumentsAsync(missingAny);
                if (before == 0)
                {
                    parts.Add($"{link.Collection}.{link.LocalKey}: nothing to resolve");
                    continue;
                }

                var setStage = new BsonDocument();
                var projectStage = new BsonDocument { { "_id", 1 } };
                foreach (var field in link.Fields)
                {
                    // Keep whatever is already there; else take the parent's value; else leave
                    // the field out entirely, so $merge does not write an explicit null. An
                    // explicit null would read as "present" and make the next run a no-op.
                    setStage[field] = new BsonDocument("$ifNull", new BsonArray
                    {
                        "$" + field,
                        new BsonDocument("$ifNull", new BsonArray
                        {
                            new BsonDocument("$first", "$_parent." + field),
                            "$$REMOVE",
                        }),
                    });
                    projectStage[field] = 1;
                }

                var pipeline = new[]
                {
                    new BsonDocument("$match", new BsonDocument
                    {
                        { link.LocalKey, new BsonDocument("$exists", true) },
                        { "$or", new BsonArray(
                            link.Fields.Select(f => new BsonDocument(f, new BsonDocument("$exists", false)))) },
                    }),
                    new BsonDocument("$lookup", new BsonDocument
                    {
                        { "from", link.From },
                        { "localField", link.LocalKey },
                        { "foreignField", "_id" },
                        { "as", "_parent" },
                    }),
                    new BsonDocument("$set", setStage),
                    // Only the ids, so $merge rewrites nothing else on the document.
                    new BsonDocument("$project", projectStage),
                    new BsonDocument("$match", new BsonDocument("$or", new BsonArray(
                        link.Fields.Select(f => new BsonDocument(f, new BsonDocument("$exists", true)))))),
                    new BsonDocument("$merge", new BsonDocument
                    {
                        { "into", link.Collection },
                        { "on", "_id" },
                        { "whenMatched", "merge" },
                        { "whenNotMatched", "discard" },
                    }),
                };

                // AggregateToCollectionAsync, not AggregateAsync: a $merge pipeline produces no
                // documents to enumerate, and the plain overload hands back a cursor whose work
                // may never be driven. This overload is the one meant for $out / $merge.
                await collection.AggregateToCollectionAsync<BsonDocument>(pipeline);

                var leftover = await collection.CountDocumentsAsync(missingAny);
                parts.Add($"{link.Collection}.{link.LocalKey}: {before} stale -> resolved {before - leftover}, "
                          + $"{leftover} unresolvable");
            }

            return string.Join("; ", parts);
        }
    }
}

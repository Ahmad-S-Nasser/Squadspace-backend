using MongoDB.Bson;
using MongoDB.Driver;
using RafeeqyNotes.Api.Config;

namespace RafeeqyNotes.Api.Migrations
{
    /// <summary>
    /// Indexes backing the flat parent ids. Ensured on every start, never ledgered.
    /// </summary>
    /// <remarks>
    /// Before this, the four core collections carried nothing but their <c>_id</c> index, so
    /// every organization-scoped list and every analytics filter was a collection scan.
    ///
    /// All non-unique, and every one is individually swallowed on failure. An index is a
    /// performance property; losing one must not cost availability.
    /// </remarks>
    public static class SchemaIndexes
    {
        private static readonly (string Collection, string Field)[] Keys =
        {
            ("Projects",  "OrganizationId"),

            ("Boards",    "OrganizationId"),
            ("Boards",    "ProjectId"),

            ("Notes",     "OrganizationId"),
            ("Notes",     "BoardId"),
            ("Notes",     "ProjectId"),

            ("NoteTasks", "OrganizationId"),
            ("NoteTasks", "ProjectId"),
            ("NoteTasks", "NoteId"),
            ("NoteTasks", "SprintId"),

            // Multikey, backing the one-running-timer-per-user lookup. Every timer start runs
            // this query, so it must not be a collection scan.
            ("NoteTasks", "TimeEntries.UserId"),

            // Saved reports are always listed per organization.
            ("SavedReports", "OrganizationId"),

            // Payment orders. Listed per organization on the billing screen, and swept by
            // status for the approval queue - both of which are collection scans without these.
            ("PaymentOrders", "OrganizationId"),
            ("PaymentOrders", "Status"),
            ("SavedReports", "OwnerId"),

            // Import history is listed per organization, newest first.
            ("ImportJobs", "OrganizationId"),

            // SSO config is looked up per organization and by email domain on login.
            ("OrgSsoConfigs", "OrganizationId"),
            ("OrgSsoConfigs", "AllowedEmailDomains"),

            // The scheduler polls NextRunAt every minute; without this it is a scan of
            // every rule in the system on each tick.
            ("AutomationRules", "NextRunAt"),
            ("AutomationRules", "OrganizationId"),
            ("AutomationRuns", "OrganizationId"),
            ("AutomationRuns", "RuleId"),

            // Feedback is listed newest-first and rate-limited per user.
            ("Feedback", "CreatedAt"),
            ("Feedback", "UserId"),

            // Everything below is read when a project is opened, and every one of them was a
            // collection scan: these four collections carried nothing but their _id index.
            ("Meetings", "ProjectId"),
            ("Sprints", "ProjectId"),
            ("Whiteboards", "ProjectId"),

            // Read once per project on the projects list. The collection is an audit log, so
            // the sort field is part of the query and belongs in the index with it.
            ("ProjectSchedules", "ProjectId"),
        };

        public static async Task EnsureAsync(MongoDbSettings settings)
        {
            var database = new MongoClient(settings.ConnectionString).GetDatabase(settings.DatabaseName);
            var created = 0;

            foreach (var (collectionName, field) in Keys)
            {
                var collection = database.GetCollection<BsonDocument>(collectionName);

                await MigrationRunner.EnsureIndexAsync(
                    collection,
                    new CreateIndexModel<BsonDocument>(
                        Builders<BsonDocument>.IndexKeys.Ascending(field),
                        new CreateIndexOptions { Name = $"{field.Replace('.', '_')}_1", Background = true }),
                    $"{collectionName}.{field}");

                created++;
            }

            await EnsureExternalIdentityAsync(database);

            // One config document per organization. Safe to make unique: the collection is
            // new, so there is no existing data that could already violate it. It is also what
            // decides the race when two requests seed the same organization at once.
            await MigrationRunner.EnsureIndexAsync(
                database.GetCollection<BsonDocument>("OrgWorkspaceConfig"),
                new CreateIndexModel<BsonDocument>(
                    Builders<BsonDocument>.IndexKeys.Ascending("OrganizationId"),
                    new CreateIndexOptions { Name = "OrganizationId_1", Unique = true, Background = true }),
                "OrgWorkspaceConfig.OrganizationId");

            Console.WriteLine($"[migration] schema indexes ensured ({created} checked).");
        }

        /// <summary>
        /// The unique index that makes an import idempotent: one document per
        /// {ExternalSource, ExternalId} pair.
        /// </summary>
        /// <remarks>
        /// PARTIAL, and that is the whole point. Everything created inside the product has
        /// neither field, and a missing field indexes as null - so a plain unique compound index
        /// would see every existing document as the same (null, null) key and reject the second
        /// one written. On a populated database it would not even build.
        ///
        /// Filtering on ExternalId existing AND being a string also excludes a document that
        /// somehow carries an explicit null, which would otherwise re-introduce the collision.
        /// </remarks>
        private static async Task EnsureExternalIdentityAsync(IMongoDatabase database)
        {
            var partial = Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Exists("ExternalId", true),
                Builders<BsonDocument>.Filter.Type("ExternalId", BsonType.String));

            foreach (var collectionName in new[] { "Projects", "Boards", "Notes", "NoteTasks" })
            {
                await MigrationRunner.EnsureIndexAsync(
                    database.GetCollection<BsonDocument>(collectionName),
                    new CreateIndexModel<BsonDocument>(
                        Builders<BsonDocument>.IndexKeys
                            .Ascending("ExternalSource")
                            .Ascending("ExternalId"),
                        new CreateIndexOptions<BsonDocument>
                        {
                            Name = "ExternalSource_1_ExternalId_1",
                            Unique = true,
                            Background = true,
                            PartialFilterExpression = partial,
                        }),
                    $"{collectionName}.ExternalSource+ExternalId");
            }
        }
    }
}

using MongoDB.Driver;

namespace RafeeqyNotes.Api.Migrations
{
    /// <summary>
    /// A schema change applied to the database at startup, exactly once.
    /// </summary>
    /// <remarks>
    /// Migrations work on raw <c>BsonDocument</c>s rather than the C# models. A migration exists
    /// precisely because the stored shape and the model disagree, so binding it to the model
    /// couples it to a shape that will keep moving underneath it.
    ///
    /// Every migration must be idempotent regardless of the ledger. The ledger stops the work
    /// being repeated; being idempotent anyway is what makes it safe to clear a ledger entry and
    /// re-run one by hand, and it is the property the "restart twice, second run reports zero
    /// modified" check actually tests.
    /// </remarks>
    public interface IMigration
    {
        /// <summary>Stable identifier recorded in the ledger. Never reuse or renumber.</summary>
        string Id { get; }

        /// <summary>One line, logged on the run that applies it.</summary>
        string Description { get; }

        /// <summary>Applies the change and returns a human-readable summary for the log.</summary>
        Task<string> RunAsync(IMongoDatabase database);
    }
}

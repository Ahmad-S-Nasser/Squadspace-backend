using MongoDB.Driver;

namespace RafeeqyNotes.Api.Helpers
{
    /// <summary>
    /// A create collided with a document that already exists.
    /// </summary>
    /// <remarks>
    /// Raised when Mongo reports duplicate key (E11000), which happens on two paths that both
    /// matter to an importer: the caller supplied an id that is already taken, or the caller
    /// supplied an {ExternalSource, ExternalId} pair already imported.
    ///
    /// Until now that surfaced as an unhandled 500. For an importer that is the worst possible
    /// answer, because the collision IS the idempotency signal - "already done, move on" is
    /// indistinguishable from "the server is broken" when both are a 500. Controllers translate
    /// this to 409 Conflict.
    /// </remarks>
    public class DuplicateEntityException : Exception
    {
        /// <summary>Mongo's duplicate key error code.</summary>
        public const int DuplicateKeyCode = 11000;

        public string EntityId { get; }

        public DuplicateEntityException(string entity, string entityId, Exception inner)
            : base($"A {entity} with this identity already exists.", inner)
        {
            EntityId = entityId;
        }

        /// <summary>True when the exception is Mongo's duplicate-key error rather than any other write failure.</summary>
        public static bool IsDuplicateKey(MongoWriteException ex) =>
            ex?.WriteError != null && ex.WriteError.Code == DuplicateKeyCode;
    }
}

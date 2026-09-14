using MongoDB.Bson.Serialization.Attributes;

namespace RafeeqyNotes.Api.Models
{
    [BsonIgnoreExtraElements]
    public class Project
    {
        // No initializer: see the note on Board.Id.
        public string Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string OwnerId { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public string Color { get; set; } = "#14b8a6";
        public string Description { get; set; } = string.Empty;
        public Organization Organization {  get; set; }

        // Denormalized parent ids, populated SERVER-SIDE by EntityGraphService on every write.
        // Never trust the incoming value: the client already sends fields of these names
        // alongside the embedded snapshot, so binding them and then treating them as
        // authoritative for tenant resolution would let a caller name someone else's
        // organization. Hydration clears them and rewrites them from what it read.
        public string OrganizationId { get; set; }

        // Where this record came from, when it was imported rather than created here.
        // ExternalSource names the system ("jira", "clickup", "azure-devops"); ExternalId is
        // that system's own key. Together they carry a partial unique index, which is what
        // makes an import idempotent and resumable: re-running it collides instead of
        // duplicating, and the collision is reported as 409 rather than a 500.
        //
        // The index is partial because these are absent on everything created in the product
        // itself. A plain unique compound index would treat every one of those as the same
        // (null, null) key and reject the second document written.
        public string ExternalId { get; set; }
        public string ExternalSource { get; set; }

        // Per-organization custom fields, keyed by the field slug from OrgWorkspaceConfig.
        //
        // Values are strings even when the field is declared numeric or a date, exactly as
        // SubscriptionPlan.Entitlements stores "25" and "unlimited" side by side - the registry
        // declares the type and each edge parses. That idiom already exists on both sides of the
        // wire here, so this adds one concept rather than three.
        //
        // Keys are validated against ^[a-z][a-z0-9_]{0,39}$ on write: a BSON key containing "."
        // or starting with "$" produces a document that cannot be queried or updated normally.
        public Dictionary<string, string> CustomFields { get; set; } = new();
        public List<OrganizationMember> Members { get; set; } = new();// Selected from org members

        // These properties are received from the frontend Edit Project dialog but are stored in the separate ProjectSchedules collection
        [BsonIgnore]
        public List<int>? WorkingDays { get; set; }
        [BsonIgnore]
        public WorkingHours? WorkingHours { get; set; }
        [BsonIgnore]
        public List<string>? Holidays { get; set; }
    }
}

using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using System;
using System.Collections.Generic;

namespace RafeeqyNotes.Api.Models
{
    [BsonIgnoreExtraElements]
    public class SubscriptionPlan
    {
        [BsonId]
        [BsonRepresentation(BsonType.ObjectId)]
        public string Id { get; set; } = string.Empty;

        public string PlanId { get; set; } = string.Empty; // e.g. "personal", "starter", "pro", "custom"
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public decimal MonthlyPrice { get; set; }
        public decimal YearlyPrice { get; set; }
        public string Currency { get; set; } = "USD";
        public int UserLimit { get; set; }

        /// <summary>Marketing copy shown on pricing cards. Display only — never read by code.</summary>
        public List<string> Features { get; set; } = new();

        /// <summary>
        /// Machine-readable capabilities, keyed by <see cref="RafeeqyNotes.Api.Helpers.Entitlement"/>.
        /// </summary>
        /// <remarks>
        /// Distinct from <see cref="Features"/>, which is marketing text nothing enforces.
        /// Values are strings so booleans ("true"/"false") and numeric quotas ("5", "unlimited")
        /// share one bag and survive schema changes without a migration — the class is
        /// [BsonIgnoreExtraElements], so plans stored before this field existed simply come back
        /// with an empty dictionary and fall through to the defaults in EntitlementSet.
        /// </remarks>
        public Dictionary<string, string> Entitlements { get; set; } = new();
        public bool IsActive { get; set; } = true;
        public int SortOrder { get; set; }
        
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }
}

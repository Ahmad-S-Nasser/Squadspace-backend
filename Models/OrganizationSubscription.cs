using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using System;

namespace RafeeqyNotes.Api.Models
{
    [BsonIgnoreExtraElements]
    public class OrganizationSubscription
    {
        [BsonId]
        [BsonRepresentation(BsonType.ObjectId)]
        public string Id { get; set; } = string.Empty;

        public string OrganizationId { get; set; } = string.Empty;
        public string PlanId { get; set; } = string.Empty;
        
        [BsonIgnore]
        public SubscriptionPlan? Plan { get; set; } // Populated on read

        public string Status { get; set; } = "active"; // active, cancelled, expired, past_due
        public string BillingCycle { get; set; } = "monthly"; // monthly, yearly
        
        public DateTime StartDate { get; set; } = DateTime.UtcNow;
        public DateTime EndDate { get; set; }
        public bool AutoRenew { get; set; } = true;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }
}

using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace RafeeqyNotes.Api.Models
{
    public class NotificationPreferences
    {
        [BsonId]
        [BsonRepresentation(BsonType.ObjectId)]
        public string? Id { get; set; }

        public string? UserId { get; set; }
        public bool Email { get; set; } = true;
        public bool Push { get; set; } = true;
        public bool NewMessage { get; set; } = true;
        public bool TaskAssigned { get; set; } = true;
        public bool TaskCompleted { get; set; } = true;
        public bool Mentions { get; set; } = true;
        public bool OrgUpdates { get; set; } = true;
    }
}

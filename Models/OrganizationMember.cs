using MongoDB.Bson.Serialization.Attributes;

namespace RafeeqyNotes.Api.Models
{
    [BsonIgnoreExtraElements]
    public class OrganizationMember
    {
        public string Id { get; set; }
        public string UserId { get; set; }
        public Contributor User { get; set; }
        public string Role { get; set; } // "owner" | "admin" | "member"
        public DateTime JoinedAt { get; set; }
    }
}

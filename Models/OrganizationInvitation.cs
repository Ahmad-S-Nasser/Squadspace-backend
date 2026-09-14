using MongoDB.Bson.Serialization.Attributes;
using System;

namespace RafeeqyNotes.Api.Models
{
    [BsonIgnoreExtraElements]
    public class OrganizationInvitation
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Email { get; set; } = string.Empty;
        public string OrganizationId { get; set; } = string.Empty;
        public string Role { get; set; } = "viewer"; // Default role in organization
        public string InvitationToken { get; set; } = Guid.NewGuid().ToString();
        public DateTime ExpiresAt { get; set; } = DateTime.UtcNow.AddDays(7);
        public bool IsUsed { get; set; } = false;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}

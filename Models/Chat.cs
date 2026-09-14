using MongoDB.Bson.Serialization.Attributes;

namespace RafeeqyNotes.Api.Models
{
    [BsonIgnoreExtraElements]
    public class Chat
    {
        public string Id { get; set; } = Guid.NewGuid().ToString(); 
        public string Name { get; set; } = string.Empty; 
        public string Type { get; set; } = "general"; // "general" | "project" | "direct"
        public string? ProjectId { get; set; }
        public List<ChatParticipant> Participants { get; set; } = new(); 
        public ChatLastMessage? LastMessage { get; set; }
        public Dictionary<string, int> UnreadCounts { get; set; } = new(); 
    }
    public class ChatParticipant
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty; 
        public string? Avatar { get; set; } 
        public bool? IsOnline { get; set; }
    }
    public class ChatLastMessage 
    { 
        public string Content { get; set; } = string.Empty; 
        public string SenderName { get; set; } = string.Empty; 
        public string Timestamp { get; set; } = string.Empty; 
    }
    [BsonIgnoreExtraElements]
    public class ChatMessage
    {
        public string Id { get; set; } = string.Empty; 
        public string SenderId { get; set; } = string.Empty;
        public string SenderName { get; set; } = string.Empty;
        public string? SenderAvatar { get; set; }
        public string Content { get; set; } = string.Empty;
        public string Timestamp { get; set; } = string.Empty;
        public bool? IsEdited { get; set; }
        public string ChatId { get; set; } = string.Empty; 
        public ChatReplyTo? ReplyTo { get; set; }
        public List<ChatReaction>? Reactions { get; set; }
        public List<string> ReadBy { get; set; } = new();
    }
    public class ChatReplyTo 
    { 
        public string Id { get; set; } = string.Empty; 
        public string SenderName { get; set; } = string.Empty; 
        public string Content { get; set; } = string.Empty; 
    }
    public class ChatReaction 
    { 
        public string Emoji { get; set; } = string.Empty; 
        public List<string> UserIds { get; set; } = new();
    }
    public class CreateChatInput
    {
        public string Name { get; set; } = string.Empty; 
        public string Type { get; set; } = "general"; // "general" | "project" | "direct"
        public string? ProjectId { get; set; }
        public List<ChatParticipant> Participants { get; set; } = new();
    }
    public class CreateMessageInput
    {
        //{ "Id":"605bb22b-8f08-4c4a-b3fe-7ef3acd69d04",
        //"SenderId":"f992ac7a-584a-4472-b876-7650cdf803d6",
        //"SenderName":"Nasser",
        //"SenderAvatar":null,
        //"Content":"gg",
        //"ReplyToId":null,
        //"Timestamp":"2025-12-15T11:06:07.851Z"}
        public string Id { get; set; } = string.Empty;
        public string SenderId { get; set; } = string.Empty;
        public string SenderName { get; set; } = string.Empty;
        public string? SenderAvatar { get; set; } = string.Empty;
        public string ChatId { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
        public string? ReplyToId { get; set; }
    }
}

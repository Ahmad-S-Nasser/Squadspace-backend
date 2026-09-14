using MongoDB.Driver;
using RafeeqyNotes.Api.Models;
using RafeeqyNotes.Api.Config;

namespace RafeeqyNotes.Api.Repositories
{
    public class ChatRepository : IChatRepository
    {
        private readonly IMongoCollection<Chat> _chats;
        private readonly IMongoCollection<ChatMessage> _messages;

        public ChatRepository(MongoDbSettings settings)
        {
            var client = new MongoClient(settings.ConnectionString);
            var database = client.GetDatabase(settings.DatabaseName);
            _chats = database.GetCollection<Chat>("Chats");
            _messages = database.GetCollection<ChatMessage>("ChatMessages");
        }

        // Chats
        public async Task<List<Chat>> GetAllChatsAsync() =>
            await _chats.Find(_ => true).ToListAsync();

        public async Task<List<Chat>> GetChatsByUserIdAsync(string userId) =>
            await _chats.Find(c => c.Participants.Any(p => p.Id == userId)).ToListAsync();

        public async Task<Chat?> GetChatByIdAsync(string id) =>
            await _chats.Find(c => c.Id == id).FirstOrDefaultAsync();

        public async Task CreateChatAsync(Chat chat) =>
            await _chats.InsertOneAsync(chat);

        // Messages
        public async Task<List<ChatMessage>> GetMessagesAsync(string chatId, int? limit, string? before)
        {
            var filter = Builders<ChatMessage>.Filter.Eq(m => m.ChatId, chatId);

            if (!string.IsNullOrEmpty(before))
            {
                filter &= Builders<ChatMessage>.Filter.Lt(m => m.Timestamp, before);
            }

            var query = _messages.Find(filter).SortByDescending(m => m.Timestamp);

            if (limit.HasValue)
                query = (IOrderedFindFluent<ChatMessage, ChatMessage>)query.Limit(limit.Value);

            return await query.ToListAsync();
        }

        public async Task<ChatMessage?> GetMessageByIdAsync(string chatId, string messageId) =>
            await _messages.Find(m => m.ChatId == chatId && m.Id == messageId).FirstOrDefaultAsync();

        public async Task CreateMessageAsync(string chatId, ChatMessage message)
        {
            message.ChatId = chatId;
            // The sender has already read their own message
            message.ReadBy = new List<string> { message.SenderId };
            
            await _messages.InsertOneAsync(message);

            // Update last message in chat and increment unread counts for others
            var lastMessage = new ChatLastMessage
            {
                Content = message.Content,
                SenderName = message.SenderName,
                Timestamp = message.Timestamp
            };

            var chat = await GetChatByIdAsync(chatId);
            if (chat != null)
            {
                var update = Builders<Chat>.Update.Set(c => c.LastMessage, lastMessage);
                
                foreach (var participant in chat.Participants)
                {
                    if (participant.Id != message.SenderId)
                    {
                        update = update.Inc($"UnreadCounts.{participant.Id}", 1);
                    }
                    else
                    {
                        // Reset sender's unread count just in case
                        update = update.Set($"UnreadCounts.{participant.Id}", 0);
                    }
                }
                
                await _chats.UpdateOneAsync(c => c.Id == chatId, update);
            }
        }

        public async Task MarkMessagesAsReadAsync(string chatId, string userId)
        {
            // 1. Reset unread count for this user in the chat document
            var chatUpdate = Builders<Chat>.Update.Set($"UnreadCounts.{userId}", 0);
            await _chats.UpdateOneAsync(c => c.Id == chatId, chatUpdate);

            // 2. Add user to ReadBy list for all messages in this chat they haven't read yet
            var messageFilter = Builders<ChatMessage>.Filter.And(
                Builders<ChatMessage>.Filter.Eq(m => m.ChatId, chatId),
                Builders<ChatMessage>.Filter.Not(Builders<ChatMessage>.Filter.AnyEq(m => m.ReadBy, userId))
            );
            var messageUpdate = Builders<ChatMessage>.Update.AddToSet(m => m.ReadBy, userId);
            await _messages.UpdateManyAsync(messageFilter, messageUpdate);
        }

        public async Task UpdateMessageAsync(string chatId, ChatMessage message) =>
            await _messages.ReplaceOneAsync(m => m.ChatId == chatId && m.Id == message.Id, message);

        public async Task DeleteMessageAsync(string chatId, string messageId) =>
            await _messages.DeleteOneAsync(m => m.ChatId == chatId && m.Id == messageId);
    }
}
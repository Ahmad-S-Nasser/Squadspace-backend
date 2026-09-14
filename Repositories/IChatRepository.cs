using RafeeqyNotes.Api.Models;

public interface IChatRepository
{
    Task<List<Chat>> GetAllChatsAsync();
    Task<List<Chat>> GetChatsByUserIdAsync(string userId);
    Task<Chat?> GetChatByIdAsync(string id);
    Task CreateChatAsync(Chat chat);

    Task<List<ChatMessage>> GetMessagesAsync(string chatId, int? limit, string? before);
    Task<ChatMessage?> GetMessageByIdAsync(string chatId, string messageId);
    Task CreateMessageAsync(string chatId, ChatMessage message);
    Task MarkMessagesAsReadAsync(string chatId, string userId);
    Task UpdateMessageAsync(string chatId, ChatMessage message);
    Task DeleteMessageAsync(string chatId, string messageId);
}
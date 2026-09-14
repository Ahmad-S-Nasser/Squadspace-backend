using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using RafeeqyNotes.Api.Models;
using RafeeqyNotes.Api.Helpers;
using RafeeqyNotes.Api.Repositories;
using Microsoft.AspNetCore.SignalR;
using RafeeqyNotes.Api.Hubs;
using System.Linq;

[ApiController]
[Route("api/[controller]")]
    // Authentication is required for every endpoint here. This controller previously had
    // no [Authorize] at all, so any anonymous caller could reach it. Org-level scoping
    // (who may see WHICH org's data) is a separate, later step - see deploy\DEPLOY.md.
[Authorize]
public class ChatController : ControllerBase
{
    private readonly IChatRepository _repo;
    private readonly IHubContext<ChatHub> _chatHubContext;
    private readonly INotificationRepository _notificationRepo;
    private readonly IHubContext<NotificationHub> _notificationHubContext;

    public ChatController(
        IChatRepository repo, 
        IHubContext<ChatHub> chatHubContext,
        INotificationRepository notificationRepo,
        IHubContext<NotificationHub> notificationHubContext)
    {
        _repo = repo;
        _chatHubContext = chatHubContext;
        _notificationRepo = notificationRepo;
        _notificationHubContext = notificationHubContext;
    }

    /// <summary>
    /// Chats are private to their participants. Returns null when the caller may proceed,
    /// or the response to return when they may not.
    /// </summary>
    private async Task<ActionResult> DenyIfNotParticipantAsync(string chatId)
    {
        var userId = OrgAccess.UserId(User);
        if (string.IsNullOrEmpty(userId)) return Unauthorized();

        var chat = await _repo.GetChatByIdAsync(chatId);

        // Same 404 whether the chat is missing or the caller is not in it, so the response
        // never confirms that a conversation exists.
        if (chat == null || !IsParticipant(chat, userId))
        {
            return NotFound(new
            {
                message = "Not found",
                detail = "No such conversation, or you are not a participant."
            });
        }
        return null;
    }

    private static bool IsParticipant(Chat chat, string userId) =>
        chat?.Participants != null &&
        chat.Participants.Any(pt => !string.IsNullOrEmpty(pt?.Id) &&
                                    pt.Id.Equals(userId, StringComparison.OrdinalIgnoreCase));

    // 1. GET /api/Chat/rooms?userId=xxx
    [HttpGet("rooms")]
    public async Task<ActionResult<List<Chat>>> GetChats([FromQuery] string? userId)
    {
        // The userId query parameter is ignored: it allowed listing anyone's conversations,
        // and the empty case fell through to returning every chat on the server.
        var callerId = OrgAccess.UserId(User);
        if (string.IsNullOrEmpty(callerId)) return Unauthorized();

        var chats = await _repo.GetChatsByUserIdAsync(callerId);
        return Ok(chats ?? new List<Chat>());
    }

    // 2. GET /api/Chat/rooms/{chatId}
    [HttpGet("rooms/{chatId}")]
    public async Task<ActionResult<Chat>> GetChat(string chatId)
    {
        var denied = await DenyIfNotParticipantAsync(chatId);
        if (denied != null) return denied;

        var chat = await _repo.GetChatByIdAsync(chatId);
        if (chat == null) return NotFound();
        return Ok(chat);
    }

    // 3. POST /api/Chat/rooms
    [HttpPost("rooms")]
    public async Task<ActionResult<Chat>> CreateChat([FromBody] CreateChatInput input)
    {
        if (input == null) return BadRequest("Request body is required.");

        var callerId = OrgAccess.UserId(User);
        if (string.IsNullOrEmpty(callerId)) return Unauthorized();

        // The creator must be in the conversation. Without this, a caller could open a chat
        // between two other people and push them a "ChatCreated" notification over SignalR
        // while never being a participant themselves.
        input.Participants ??= new List<ChatParticipant>();
        if (!IsParticipant(new Chat { Participants = input.Participants }, callerId))
        {
            input.Participants.Add(new ChatParticipant { Id = callerId });
        }

        var chat = new Chat
        {
            Id = Guid.NewGuid().ToString(),
            Name = input.Name,
            Type = input.Type,
            ProjectId = input.ProjectId,
            Participants =  input.Participants,//input.ParticipantIds.Select(id => new ChatParticipant { Id = id }).ToList(),
            UnreadCounts = input.Participants.ToDictionary(p => p.Id, p => 0)
        };

        await _repo.CreateChatAsync(chat);

        // Notify all participants about the new chat
        foreach (var participant in chat.Participants)
        {
            await _chatHubContext.Clients.User(participant.Id).SendAsync("ChatCreated", chat);
        }

        return CreatedAtAction(nameof(GetChat), new { chatId = chat.Id }, chat);
    }

    // 4. GET /api/Chat/rooms/{chatId}/messages
    [HttpGet("rooms/{chatId}/messages")]
    public async Task<ActionResult<List<ChatMessage>>> GetChatMessages(string chatId, int? limit, string? before)
    {
        var denied = await DenyIfNotParticipantAsync(chatId);
        if (denied != null) return denied;

        var messages = await _repo.GetMessagesAsync(chatId, limit, before);
        return Ok(messages);
    }

    // 5. POST /api/Chat/rooms/{chatId}/messages
    [HttpPost("rooms/{chatId}/messages")]
    public async Task<ActionResult<ChatMessage>> SendChatMessage(string chatId, [FromBody] CreateMessageInput input)
    {
        var denied = await DenyIfNotParticipantAsync(chatId);
        if (denied != null) return denied;

        var message = new ChatMessage
        {
            Id = Guid.NewGuid().ToString(),
            SenderId = input.SenderId, // replace with auth context
            SenderName = input.SenderName,
            Content = input.Content,
            Timestamp = DateTime.UtcNow.ToString("o"),
            ReplyTo = input.ReplyToId != null ? new ChatReplyTo { Id = input.ReplyToId } : null
        };

        var chat = await _repo.GetChatByIdAsync(chatId);
        if (chat == null) return NotFound();

        await _repo.CreateMessageAsync(chatId, message);
        
        // Broadcast message to all participants in the chat
        await _chatHubContext.Clients.Group(chatId).SendAsync("ReceiveMessage", chatId, message);

        // Notify offline participants
        if (chat.Participants != null)
        {
            foreach (var participant in chat.Participants)
            {
                if (participant.Id != input.SenderId)
                {
                    var notification = new Notification
                    {
                        UserId = participant.Id,
                        Type = "new_message",
                        Title = $"New message from {input.SenderName}",
                        Message = input.Content,
                        Link = $"/chat?id={chatId}",
                        IsRead = false,
                        CreatedAt = DateTime.UtcNow,
                        Metadata = new Dictionary<string, string>
                        {
                            { "chatId", chatId },
                            { "messageId", message.Id }
                        }
                    };

                    await _notificationRepo.CreateUserNotificationAsync(notification);
                    await _notificationHubContext.Clients.User(participant.Id).SendAsync("ReceiveNotification", notification);
                }
            }
        }

        return Ok(message);
    }

    // 6. PUT /api/Chat/rooms/{chatId}/messages/{messageId}
    [HttpPut("rooms/{chatId}/messages/{messageId}")]
    public async Task<IActionResult> EditChatMessage(string chatId, string messageId, [FromBody] string content)
    {
        var denied = await DenyIfNotParticipantAsync(chatId);
        if (denied != null) return denied;

        var message = await _repo.GetMessageByIdAsync(chatId, messageId);
        if (message == null) return NotFound();

        message.Content = content;
        message.IsEdited = true;
        await _repo.UpdateMessageAsync(chatId, message);
        return NoContent();
    }

    // 7. DELETE /api/Chat/rooms/{chatId}/messages/{messageId}
    [HttpDelete("rooms/{chatId}/messages/{messageId}")]
    public async Task<IActionResult> DeleteChatMessage(string chatId, string messageId)
    {
        var denied = await DenyIfNotParticipantAsync(chatId);
        if (denied != null) return denied;

        var message = await _repo.GetMessageByIdAsync(chatId, messageId);
        if (message == null) return NotFound();

        await _repo.DeleteMessageAsync(chatId, messageId);
        return NoContent();
    }

    // 8. POST /api/Chat/rooms/{chatId}/messages/{messageId}/reactions
    [HttpPost("rooms/{chatId}/messages/{messageId}/reactions")]
    public async Task<IActionResult> AddReaction(string chatId, string messageId, [FromBody] string emoji)
    {
        var denied = await DenyIfNotParticipantAsync(chatId);
        if (denied != null) return denied;

        var message = await _repo.GetMessageByIdAsync(chatId, messageId);
        if (message == null) return NotFound();

        if (message.Reactions == null) message.Reactions = new List<ChatReaction>();
        var reaction = message.Reactions.FirstOrDefault(r => r.Emoji == emoji);
        if (reaction == null)
        {
            reaction = new ChatReaction { Emoji = emoji };
            message.Reactions.Add(reaction);
        }

        var userId = "system"; // replace with auth context
        if (!reaction.UserIds.Contains(userId))
            reaction.UserIds.Add(userId);

        await _repo.UpdateMessageAsync(chatId, message);
        return Ok(message.Reactions);
    }
    // 9. POST /api/Chat/rooms/{chatId}/read?userId=xxx
    [HttpPost("rooms/{chatId}/read")]
    public async Task<IActionResult> MarkAsRead(string chatId, [FromQuery] string userId)
    {
        var denied = await DenyIfNotParticipantAsync(chatId);
        if (denied != null) return denied;

        await _repo.MarkMessagesAsReadAsync(chatId, userId);
        
        // Broadcast to the chat group that this user has read the messages
        await _chatHubContext.Clients.Group(chatId).SendAsync("ChatRead", chatId, userId);
        
        return Ok();
    }
}
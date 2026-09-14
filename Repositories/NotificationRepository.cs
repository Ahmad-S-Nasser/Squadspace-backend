using MongoDB.Driver;
using RafeeqyNotes.Api.Config;
using RafeeqyNotes.Api.Models;
using RafeeqyNotes.Api.Repositories;
using System.Net;
using System.Net.Mail;
using System.Net.Mime;
using System.Text;
using Microsoft.AspNetCore.SignalR;
using RafeeqyNotes.Api.Hubs;
using Microsoft.Extensions.Configuration;

public class NotificationRepository : INotificationRepository
{
    private readonly IMongoCollection<NotificationRequest> _Notification;
    private readonly SmtpClient _smtpClient;
    private readonly string _fromAddress;
    private readonly IConfiguration _config;

    private readonly IMongoCollection<Notification> _UserNotifications;
    private readonly IMongoCollection<NotificationPreferences> _Preferences;
    private readonly IHubContext<NotificationHub> _notificationHubContext;

    public NotificationRepository(MongoDbSettings settings, SmtpSettings smtpSettings, IHubContext<NotificationHub> notificationHubContext, IConfiguration configuration)
    {
        var client = new MongoClient(settings.ConnectionString);
        var database = client.GetDatabase(settings.DatabaseName);
        _Notification = database.GetCollection<NotificationRequest>("Notifications");
        _UserNotifications = database.GetCollection<Notification>(settings.UserNotificationsCollection);
        _Preferences = database.GetCollection<NotificationPreferences>(settings.NotificationPreferencesCollection);

        _notificationHubContext = notificationHubContext;
        _fromAddress = smtpSettings.FromAddress;
        _config = configuration;

        _smtpClient = new SmtpClient(smtpSettings.Host, int.Parse(smtpSettings.Port))
        {
            Credentials = new NetworkCredential(smtpSettings.Username, smtpSettings.Password),
            EnableSsl = true
        };
    }

    public async Task<List<Notification>> GetUserNotificationsAsync(string userId)
    {
        return await _UserNotifications.Find(n => n.UserId == userId).SortByDescending(n => n.CreatedAt).ToListAsync();
    }

    public async Task<bool> MarkAsReadAsync(string id, string userId)
    {
        var filter = Builders<Notification>.Filter.And(
            Builders<Notification>.Filter.Eq(n => n.Id, id),
            Builders<Notification>.Filter.Eq(n => n.UserId, userId));

        var update = Builders<Notification>.Update.Set(n => n.IsRead, true);
        var result = await _UserNotifications.UpdateOneAsync(filter, update);
        return result.MatchedCount > 0;
    }

    public async Task<long> MarkAllAsReadAsync(string userId)
    {
        var filter = Builders<Notification>.Filter.And(
            Builders<Notification>.Filter.Eq(n => n.UserId, userId),
            Builders<Notification>.Filter.Eq(n => n.IsRead, false));

        var update = Builders<Notification>.Update.Set(n => n.IsRead, true);
        var result = await _UserNotifications.UpdateManyAsync(filter, update);
        return result.ModifiedCount;
    }

    public async Task<bool> DeleteUserNotificationAsync(string id, string userId)
    {
        var filter = Builders<Notification>.Filter.And(
            Builders<Notification>.Filter.Eq(n => n.Id, id),
            Builders<Notification>.Filter.Eq(n => n.UserId, userId));

        var result = await _UserNotifications.DeleteOneAsync(filter);
        return result.DeletedCount > 0;
    }

    public async Task CreateUserNotificationAsync(Notification notification)
    {
        if (string.IsNullOrEmpty(notification.Id))
        {
            notification.Id = Guid.NewGuid().ToString();
        }
        if (notification.CreatedAt == default)
        {
            notification.CreatedAt = DateTime.UtcNow;
        }

        await _UserNotifications.InsertOneAsync(notification);

        // Broadcast notification via SignalR
        try
        {
            await _notificationHubContext.Clients.User(notification.UserId).SendAsync("ReceiveNotification", notification);
        }
        catch (Exception ex)
        {
            // Log error but don't fail the operation
            Console.WriteLine($"Error broadcasting notification: {ex.Message}");
        }
    }

    public async Task<NotificationPreferences?> GetPreferencesAsync(string userId)
    {
        return await _Preferences.Find(p => p.UserId == userId).FirstOrDefaultAsync();
    }

    public async Task UpdatePreferencesAsync(NotificationPreferences preferences)
    {
        var filter = Builders<NotificationPreferences>.Filter.Eq(p => p.UserId, preferences.UserId);
        var update = Builders<NotificationPreferences>.Update
            .Set(p => p.Email, preferences.Email)
            .Set(p => p.Push, preferences.Push)
            .Set(p => p.NewMessage, preferences.NewMessage)
            .Set(p => p.TaskAssigned, preferences.TaskAssigned)
            .Set(p => p.TaskCompleted, preferences.TaskCompleted)
            .Set(p => p.Mentions, preferences.Mentions)
            .Set(p => p.OrgUpdates, preferences.OrgUpdates);
            
        await _Preferences.UpdateOneAsync(filter, update, new UpdateOptions { IsUpsert = true });
    }

    public async Task<NotificationResponse> SendEmailNotificationAsync(NotificationRequest request)
    {
        try
        {
            foreach (var recipient in request.RecipientEmails)
            {
                var mailMessage = new MailMessage
                {
                    From = new MailAddress(_fromAddress),
                    Subject = BuildSubject(request),
                    IsBodyHtml = true
                };

                // A calendar invite is an HTML mail with a text/calendar ALTERNATE VIEW, not an
                // attachment. Mail clients look for that content type to offer Accept/Decline; the
                // same bytes added as a plain attachment show up as a file to download instead,
                // which is the difference between an invite and an email about a meeting.
                if (!string.IsNullOrWhiteSpace(request.IcsContent))
                {
                    mailMessage.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(
                        BuildBody(request), null, "text/html"));

                    var method = string.IsNullOrWhiteSpace(request.IcsMethod) ? "REQUEST" : request.IcsMethod;
                    var calendarType = new ContentType("text/calendar");
                    calendarType.Parameters.Add("method", method);
                    calendarType.Parameters.Add("name", "invite.ics");

                    var calendarView = AlternateView.CreateAlternateViewFromString(
                        request.IcsContent, calendarType);
                    calendarView.TransferEncoding = TransferEncoding.SevenBit;
                    mailMessage.AlternateViews.Add(calendarView);

                    // Outlook in particular is happier when the file is also present by name.
                    var attachment = System.Net.Mail.Attachment.CreateAttachmentFromString(
                        request.IcsContent, "invite.ics", Encoding.UTF8, "text/calendar");
                    mailMessage.Attachments.Add(attachment);
                }
                else
                {
                    mailMessage.Body = BuildBody(request);
                }

                mailMessage.To.Add(recipient);

                if (request.CcEmails != null)
                {
                    foreach (var cc in request.CcEmails)
                    {
                        mailMessage.CC.Add(cc);
                    }
                }

                await _smtpClient.SendMailAsync(mailMessage);
                
                await _Notification.InsertOneAsync(request);
            }

            return new NotificationResponse
            {
                Success = true,
                Message = "Notification email sent successfully",
                NotificationId = Guid.NewGuid().ToString()
            };
        }
        catch (Exception ex)
        {
            return new NotificationResponse
            {
                Success = false,
                Message = $"Failed to send notification: {ex.Message}",
                NotificationId = null
            };
        }
    }

    private string BuildSubject(NotificationRequest request) =>
        request.Type switch
        {
            "task_assigned" => $"New Task Assigned: {request.TaskTitle}",
            "email_verification" => "Verify Your SquadSpace Account",
            "organization_invitation" => $"Invitation to join {request.Metadata?["OrganizationName"]} on SquadSpace",
            "password_reset" => "Reset Your SquadSpace Password",
            "meeting_invite" => $"Invitation: {request.TaskTitle}",
            "meeting_cancelled" => $"Cancelled: {request.TaskTitle}",

            // Fixed subject, as specified. Everything that distinguishes one report from
            // another is in the body, so mail clients thread these together.
            "product_feedback" => "SquadSpace feedback",
            _ => $"SquadSpace Notification: {request.Type}"
        };

    private string BuildBody(NotificationRequest request)
    {
        string content = "";
        string previewText = "";

        if (request.Type == "task_assigned")
        {
            previewText = $"New task assigned: {request.TaskTitle}";
            content = $@"
                <h2 style=""color: #0f172a; margin-top: 0; font-size: 20px;"">New Task Assigned</h2>
                <p style=""color: #334155; line-height: 1.6;"">You have been assigned a new task in your workspace. Here are the details:</p>
                <div style=""background-color: #f1f5f9; border-left: 4px solid #14b8a6; padding: 16px; margin: 24px 0;"">
                    <p style=""margin: 0; font-weight: 600; color: #0f172a;"">{request.TaskTitle}</p>
                    <p style=""margin: 4px 0 0 0; font-size: 14px; color: #64748b;"">ID: {request.TaskId}</p>
                </div>
                <p style=""color: #334155; line-height: 1.6;""><strong>Assignees:</strong> {string.Join(", ", request.AssigneeNames)}</p>
                <div style=""text-align: center; margin: 32px 0 0 0;"">
                    <a href=""{_config["FrontendUrl"] ?? "https://app.squadspace.net"}/tasks"" style=""background-color: #14b8a6; color: white; padding: 12px 32px; text-decoration: none; border-radius: 6px; font-weight: 600; display: inline-block;"">View Task</a>
                </div>";
        }
        else if (request.Type == "email_verification")
        {
            var verifyUrl = request.Metadata?.GetValueOrDefault("VerificationUrl", "#");
            previewText = "Welcome to SquadSpace! Please verify your account.";
            content = $@"
                <h2 style=""color: #0f172a; margin-top: 0; font-size: 24px;"">Welcome to SquadSpace!</h2>
                <p style=""color: #334155; line-height: 1.6;"">We're excited to have you on board. To get started with your new collaborative workspace, please verify your email address by clicking the button below:</p>
                <div style=""text-align: center; margin: 40px 0;"">
                    <a href=""{verifyUrl}"" style=""background-color: #14b8a6; color: white; padding: 14px 40px; text-decoration: none; border-radius: 6px; font-weight: 600; display: inline-block; font-size: 16px;"">Verify My Account</a>
                </div>
                <p style=""color: #64748b; font-size: 14px; line-height: 1.5;"">If the button doesn't work, you can copy and paste this link into your browser:</p>
                <p style=""color: #0ea5e9; font-size: 14px; word-break: break-all;"">{verifyUrl}</p>";
        }
        else if (request.Type == "organization_invitation")
        {
            var orgName = request.Metadata?.GetValueOrDefault("OrganizationName", "an organization");
            var token = request.Metadata?.GetValueOrDefault("InvitationToken", "");
            var frontendUrl = _config["FrontendUrl"] ?? "https://app.squadspace.net";
            var inviteUrl = $"{frontendUrl.TrimEnd('/')}/accept-invitation?token={token}";

            previewText = $"You've been invited to join {orgName}";
            content = $@"
                <h2 style=""color: #0f172a; margin-top: 0; font-size: 20px;"">You've been invited!</h2>
                <p style=""color: #334155; line-height: 1.6;"">You have been invited to join <strong>{orgName}</strong> on SquadSpace.</p>
                <p style=""color: #334155; line-height: 1.6;"">SquadSpace is your team's central hub for notes, tasks, and real-time collaboration. Click the button below to accept your invitation and get started.</p>
                <div style=""text-align: center; margin: 32px 0;"">
                    <a href=""{inviteUrl}"" style=""background-color: #14b8a6; color: white; padding: 12px 32px; text-decoration: none; border-radius: 6px; font-weight: 600; display: inline-block;"">Accept Invitation</a>
                </div>
                <p style=""color: #64748b; font-size: 13px; line-height: 1.4;"">This invitation will expire in 7 days.</p>
                <hr style=""border: 0; border-top: 1px solid #e2e8f0; margin: 24px 0;"" />
                <p style=""font-size: 13px; color: #64748b;"">Link: <a href=""{inviteUrl}"" style=""color: #0ea5e9; text-decoration: none;"">{inviteUrl}</a></p>";
        }
        else if (request.Type == "password_reset")
        {
            var resetUrl = request.Metadata?.GetValueOrDefault("ResetUrl", "#");
            previewText = "Instruction to reset your SquadSpace password";
            content = $@"
                <h2 style=""color: #0f172a; margin-top: 0; font-size: 20px;"">Reset your password</h2>
                <p style=""color: #334155; line-height: 1.6;"">We received a request to reset your SquadSpace password. Click the button below to choose a new one:</p>
                <div style=""text-align: center; margin: 32px 0;"">
                    <a href=""{resetUrl}"" style=""background-color: #14b8a6; color: white; padding: 12px 32px; text-decoration: none; border-radius: 6px; font-weight: 600; display: inline-block;"">Reset Password</a>
                </div>
                <p style=""color: #64748b; font-size: 14px; line-height: 1.6;"">If you didn't request this change, you can safely ignore this email. For security, this link will expire in 1 hour.</p>";
        }
        else if (request.Type == "product_feedback")
        {
            // Built for triage, not for looks: the kind of report and who sent it are the first
            // two things a reader needs, and the page and browser are what make a bug
            // reproducible without a follow-up email.
            var kind = request.Metadata?.GetValueOrDefault("Kind", "feedback");
            var body = request.Metadata?.GetValueOrDefault("Message", "");
            var who = request.Metadata?.GetValueOrDefault("From", "a user");
            var page = request.Metadata?.GetValueOrDefault("Page", "");
            var screen = request.Metadata?.GetValueOrDefault("ScreenSize", "");
            var agent = request.Metadata?.GetValueOrDefault("UserAgent", "");
            var org = request.Metadata?.GetValueOrDefault("OrganizationId", "");

            var isBug = string.Equals(kind, "bug", StringComparison.OrdinalIgnoreCase);
            var accent = isBug ? "#dc2626" : "#14b8a6";
            var heading = isBug ? "Bug report" : "Feature idea";

            previewText = $"{heading} from {who}";
            content = $@"
                <h2 style=""color: #0f172a; margin-top: 0; font-size: 20px;"">{heading}</h2>
                <div style=""background-color: #f1f5f9; border-left: 4px solid {accent}; padding: 16px; margin: 20px 0;"">
                    <p style=""margin: 0; color: #0f172a; line-height: 1.6; white-space: pre-wrap;"">{System.Net.WebUtility.HtmlEncode(body)}</p>
                </div>
                <table style=""width: 100%; font-size: 13px; color: #475569; border-collapse: collapse;"">
                    <tr><td style=""padding: 4px 12px 4px 0; color: #94a3b8;"">From</td><td>{System.Net.WebUtility.HtmlEncode(who)}</td></tr>
                    <tr><td style=""padding: 4px 12px 4px 0; color: #94a3b8;"">Page</td><td>{System.Net.WebUtility.HtmlEncode(page)}</td></tr>
                    <tr><td style=""padding: 4px 12px 4px 0; color: #94a3b8;"">Screen</td><td>{System.Net.WebUtility.HtmlEncode(screen)}</td></tr>
                    <tr><td style=""padding: 4px 12px 4px 0; color: #94a3b8;"">Organization</td><td>{System.Net.WebUtility.HtmlEncode(org)}</td></tr>
                    <tr><td style=""padding: 4px 12px 4px 0; color: #94a3b8; vertical-align: top;"">Browser</td><td style=""word-break: break-all;"">{System.Net.WebUtility.HtmlEncode(agent)}</td></tr>
                </table>";
        }
        else
        {
            previewText = "New notification from SquadSpace";
            content = $@"<p style=""color: #334155; line-height: 1.6;"">Notification type: {request.Type}</p>";
        }

        return WrapInBaseTemplate(content, previewText);
    }

    private string WrapInBaseTemplate(string content, string previewText)
    {
        return $@"
            <!DOCTYPE html>
            <html>
            <head>
                <meta charset=""utf-8"">
                <meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
                <title>SquadSpace Notification</title>
            </head>
            <body style=""margin: 0; padding: 0; background-color: #f8fafc; font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif; -webkit-font-smoothing: antialiased; -moz-osx-font-smoothing: grayscale;"">
                <div style=""display: none; max-height: 0px; overflow: hidden;"">
                    {previewText}
                </div>
                <table role=""presentation"" width=""100%"" cellspacing=""0"" cellpadding=""0"" border=""0"">
                    <tr>
                        <td align=""center"" style=""padding: 40px 20px;"">
                            <table role=""presentation"" width=""100%"" max-width=""600"" cellspacing=""0"" cellpadding=""0"" border=""0"" style=""max-width: 600px; background-color: #ffffff; border-radius: 12px; box-shadow: 0 4px 6px -1px rgba(0, 0, 0, 0.1), 0 2px 4px -1px rgba(0, 0, 0, 0.06); overflow: hidden;"">
                                <!-- Branded Header -->
                                <tr>
                                    <td align=""center"" style=""padding: 32px 40px 24px 40px;"">
                                        <table role=""presentation"" cellspacing=""0"" cellpadding=""0"" border=""0"">
                                            <tr>
                                                <td>
                                                    <div style=""background-color: #14b8a6; width: 48px; hieght: 48px; border-radius: 10px; display: inline-block; vertical-align: middle;"">
                                                        <span style=""color: white; font-size: 24px; font-weight: bold; line-height: 48px; text-align: center; display: block;"">S</span>
                                                    </div>
                                                </td>
                                                <td style=""padding-left: 12px;"">
                                                    <span style=""font-size: 22px; font-weight: 800; color: #0f172a; letter-spacing: -0.5px; vertical-align: middle;"">SquadSpace</span>
                                                </td>
                                            </tr>
                                        </table>
                                    </td>
                                </tr>
                                <!-- Main Content -->
                                <tr>
                                    <td style=""padding: 0 40px 40px 40px;"">
                                        {content}
                                    </td>
                                </tr>
                            </table>
                            <!-- Footer -->
                            <table role=""presentation"" width=""100%"" max-width=""600"" cellspacing=""0"" cellpadding=""0"" border=""0"" style=""max-width: 600px; margin-top: 24px;"">
                                <tr>
                                    <td align=""center"" style=""padding: 0 20px;"">
                                        <p style=""margin: 0; font-size: 12px; color: #94a3b8; line-height: 1.5;"">
                                            &copy; {DateTime.Now.Year} SquadSpace Admin. All rights reserved.<br>
                                            This is an automated notification, please do not reply directly to this email.
                                        </p>
                                    </td>
                                </tr>
                            </table>
                        </td>
                    </tr>
                </table>
            </body>
            </html>";
    }
}

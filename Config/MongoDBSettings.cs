namespace RafeeqyNotes.Api.Config
{
    public class MongoDbSettings
    {
        public string ConnectionString { get; set; } = string.Empty;
        public string DatabaseName { get; set; } = string.Empty;
        public string NotesCollection { get; set; } = string.Empty;
        public string ProjectsCollection { get; set; } = string.Empty;
        public string BoardsCollection { get; set; } = string.Empty;
        public string ContributorsCollection { get; set; } = string.Empty;
        public string MeetingsCollection { get; set; } = string.Empty;
        public string UserNotificationsCollection { get; set; } = string.Empty;
        public string NotificationPreferencesCollection { get; set; } = string.Empty;
    }
}
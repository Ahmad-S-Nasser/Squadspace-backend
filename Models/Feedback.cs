using MongoDB.Bson.Serialization.Attributes;

namespace RafeeqyNotes.Api.Models
{
    public static class FeedbackTypes
    {
        public const string Bug = "bug";
        public const string Idea = "idea";

        public static readonly string[] All = { Bug, Idea };
    }

    public static class FeedbackStates
    {
        public const string New = "new";
        public const string Triaged = "triaged";
        public const string Done = "done";
        public const string Declined = "declined";

        public static readonly string[] All = { New, Triaged, Done, Declined };
    }

    /// <summary>
    /// A bug report or an idea, sent by a user from inside the product.
    /// </summary>
    /// <remarks>
    /// Deliberately tiny. The value of in-product feedback comes from it being cheap enough to
    /// send in the moment someone is annoyed - a form that asks eight questions gets answered by
    /// nobody, and a survey that interrupts gets dismissed by everybody. One type, one message.
    ///
    /// The technical context is CAPTURED rather than asked for. "Which page were you on, and what
    /// browser?" is the part a user gets wrong or skips, and the part that decides whether a bug
    /// report is actionable.
    /// </remarks>
    [BsonIgnoreExtraElements]
    public class Feedback
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();

        /// <summary>"bug" or "idea".</summary>
        public string Type { get; set; } = FeedbackTypes.Idea;

        public string Message { get; set; }

        // ---- who, taken from the token rather than the form ----
        public string UserId { get; set; }
        public string UserName { get; set; }
        public string UserEmail { get; set; }

        /// <summary>Organization they were working in. Context, not ownership.</summary>
        public string OrganizationId { get; set; }

        // ---- captured context ----

        /// <summary>Route they were on when they opened the form.</summary>
        public string Page { get; set; }

        public string UserAgent { get; set; }

        /// <summary>Viewport, e.g. "1440x900" - layout bugs are usually size-specific.</summary>
        public string ScreenSize { get; set; }

        public string State { get; set; } = FeedbackStates.New;

        /// <summary>Internal triage note. Never shown to the reporter.</summary>
        public string Note { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }
}

using MongoDB.Bson.Serialization.Attributes;
using System.Text.Json.Serialization;

namespace RafeeqyNotes.Api.Models
{
    /// <summary>
    /// The user entity.
    /// </summary>
    /// <remarks>
    /// SECURITY: this entity is embedded inside other documents that ARE returned to
    /// clients — <c>Note.Contributors</c>, <c>Note.Author</c>, <c>OrganizationMember.User</c>,
    /// <c>NoteTask</c> actors — so it is not enough to project a DTO at each controller.
    /// Every credential-bearing property below carries <see cref="JsonIgnoreAttribute"/> so it
    /// can never be serialized, however deeply it is nested. `[JsonIgnore]` affects only
    /// System.Text.Json; MongoDB uses its own Bson serializer, so persistence is unaffected.
    ///
    /// Before this, <c>GET /api/Contributor</c> returned every user's password hash, GitHub
    /// OAuth access + refresh tokens, password-reset token and email-verification token to
    /// unauthenticated callers.
    ///
    /// Do NOT remove these attributes. To return a user to a client, use
    /// <see cref="ContributorDto"/>; to return a freshly issued JWT, use
    /// <see cref="AuthenticatedContributorDto"/>.
    ///
    /// PERFORMANCE: a second group of properties is ignored below for a different reason -
    /// size. Because this entity is embedded in tasks, notes and members, every actor on every
    /// task carried the owner's UI preferences and billing state. Measured on a task list:
    /// 523 bytes per embedded person, of which ~57% was settings no list has ever read, three
    /// or four people per task, and again inside every embedded dependency.
    ///
    /// Those fields belong to "me", not to "a person mentioned on this task". They reach the
    /// client through <see cref="ContributorDto"/> - a separate class, unaffected by these
    /// attributes - which is what every user-facing contributor endpoint already returns, and
    /// what the settings screen reads via currentUser.
    /// </remarks>
    [BsonIgnoreExtraElements]
    public class Contributor
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; }
        public string? Badge { get; set; } = "Contributor";
        public string Email { get; set; } = string.Empty;

        [JsonIgnore]
        public string? PasswordHash { get; set; } = string.Empty;

        public string? DisplayName { get; set; } = string.Empty;
        public string? Provider { get; set; } = "local"; // local, google, microsoft

        [JsonIgnore]
        public string? GitHubAccessToken { get; set; }

        [JsonIgnore]
        public string? GitHubRefreshToken { get; set; }

        [JsonIgnore]
        public DateTime? GitHubTokenExpiry { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        // Persisted and therefore readable through any embedded Contributor. Exposed only
        // via AuthenticatedContributorDto, to the user it belongs to.
        [JsonIgnore]
        public string? Token { get; set; } = "";
        public string Role { get; set; } = "Viewer";
        public string Status { get; set; } = "online"; // online, away, busy, offline
        
        // Subscription Fields
        //
        // Ignored on the ENTITY, not dropped: still persisted, still read server-side, still
        // returned to the user themselves through ContributorDto. What stops is shipping one
        // person's billing state inside every task another person can see.
        [JsonIgnore] public string SubscriptionPlan { get; set; } = "Starter"; // Starter, Pro, Enterprise
        [JsonIgnore] public string SubscriptionStatus { get; set; } = "Active"; // Active, Cancelled, Expired
        [JsonIgnore] public DateTime? SubscriptionExpiry { get; set; }
        [JsonIgnore] public int MaxMembers { get; set; } = 5; // Default limit for Starter plan
        [JsonIgnore] public bool HasUsedTrial { get; set; } = false;
        [JsonIgnore] public bool IsFirstLogin { get; set; } = false;
        [JsonIgnore] public string Theme { get; set; } = "system"; // light, dark, system
        [JsonIgnore] public bool IsEmailVerified { get; set; } = false;

        [JsonIgnore]
        public string? EmailVerificationToken { get; set; }
        
        // UI Settings (Persistent in DB)
        //
        // Read by UISettingsContext from the CURRENT user only, which it gets from
        // ContributorDto. Nothing has ever read them off another person, so serializing them on
        // every embedded actor was pure weight.
        //
        // [JsonIgnore] blocks deserialization too, so the platform-admin POST /api/Contributor
        // can no longer set these when creating a user. That is fine and arguably correct: each
        // one has a sensible default here, and an admin should not be choosing someone else's
        // accent colour. Every other write path binds a dedicated request type, not this entity.
        [JsonIgnore] public string? AccentColor { get; set; } = "230 60% 45%"; // Default primary color
        [JsonIgnore] public bool ShowBadges { get; set; } = true;
        [JsonIgnore] public bool ShowAuraRings { get; set; } = true;
        [JsonIgnore] public bool ShowMoodTags { get; set; } = true;
        [JsonIgnore] public bool AnimateTransitions { get; set; } = true;

        // Password Reset Fields
        [JsonIgnore]
        public string? PasswordResetToken { get; set; }

        [JsonIgnore]
        public DateTime? PasswordResetTokenExpiry { get; set; }
    }
}

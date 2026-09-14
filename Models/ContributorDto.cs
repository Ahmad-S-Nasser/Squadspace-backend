namespace RafeeqyNotes.Api.Models
{
    /// <summary>
    /// A user, as safe to return to a client. Contains no credential material.
    /// </summary>
    /// <remarks>
    /// <see cref="Contributor"/> also marks its secrets <c>[JsonIgnore]</c>, so returning the
    /// entity directly is no longer a leak — but prefer this type for anything user-facing so
    /// the safe shape is explicit rather than incidental.
    /// </remarks>
    public class ContributorDto
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Badge { get; set; }
        public string Email { get; set; }
        public string DisplayName { get; set; }
        public string Provider { get; set; }
        public DateTime CreatedAt { get; set; }
        public string Role { get; set; }
        public string Status { get; set; }

        public string SubscriptionPlan { get; set; }
        public string SubscriptionStatus { get; set; }
        public DateTime? SubscriptionExpiry { get; set; }
        public int MaxMembers { get; set; }
        public bool IsFirstLogin { get; set; }
        public string Theme { get; set; }
        public bool IsEmailVerified { get; set; }

        public string AccentColor { get; set; }
        public bool ShowBadges { get; set; }
        public bool ShowAuraRings { get; set; }
        public bool ShowMoodTags { get; set; }
        public bool AnimateTransitions { get; set; }

        /// <summary>True when the user has connected a GitHub account — never the token itself.</summary>
        public bool HasGitHubConnected { get; set; }

        public static ContributorDto From(Contributor c)
        {
            if (c == null) return null;

            return new ContributorDto
            {
                Id = c.Id,
                Name = c.Name,
                Badge = c.Badge,
                Email = c.Email,
                DisplayName = c.DisplayName,
                Provider = c.Provider,
                CreatedAt = c.CreatedAt,
                Role = c.Role,
                Status = c.Status,
                SubscriptionPlan = c.SubscriptionPlan,
                SubscriptionStatus = c.SubscriptionStatus,
                SubscriptionExpiry = c.SubscriptionExpiry,
                MaxMembers = c.MaxMembers,
                IsFirstLogin = c.IsFirstLogin,
                Theme = c.Theme,
                IsEmailVerified = c.IsEmailVerified,
                AccentColor = c.AccentColor,
                ShowBadges = c.ShowBadges,
                ShowAuraRings = c.ShowAuraRings,
                ShowMoodTags = c.ShowMoodTags,
                AnimateTransitions = c.AnimateTransitions,
                HasGitHubConnected = !string.IsNullOrEmpty(c.GitHubAccessToken),
            };
        }

        public static List<ContributorDto> From(IEnumerable<Contributor> items) =>
            items?.Select(From).Where(d => d != null).ToList() ?? new List<ContributorDto>();
    }

    /// <summary>
    /// A user plus a freshly issued JWT. Returned ONLY to the user it belongs to, from the
    /// authentication endpoints (login, signup, invite accept, password change, OAuth callback).
    /// </summary>
    /// <remarks>
    /// Both frontends read the token as <c>response.contributor.token</c>, so it must stay on
    /// this object rather than becoming a sibling field.
    /// </remarks>
    public class AuthenticatedContributorDto : ContributorDto
    {
        public string Token { get; set; }

        public static AuthenticatedContributorDto WithToken(Contributor c)
        {
            if (c == null) return null;

            var baseDto = From(c);
            return new AuthenticatedContributorDto
            {
                Id = baseDto.Id,
                Name = baseDto.Name,
                Badge = baseDto.Badge,
                Email = baseDto.Email,
                DisplayName = baseDto.DisplayName,
                Provider = baseDto.Provider,
                CreatedAt = baseDto.CreatedAt,
                Role = baseDto.Role,
                Status = baseDto.Status,
                SubscriptionPlan = baseDto.SubscriptionPlan,
                SubscriptionStatus = baseDto.SubscriptionStatus,
                SubscriptionExpiry = baseDto.SubscriptionExpiry,
                MaxMembers = baseDto.MaxMembers,
                IsFirstLogin = baseDto.IsFirstLogin,
                Theme = baseDto.Theme,
                IsEmailVerified = baseDto.IsEmailVerified,
                AccentColor = baseDto.AccentColor,
                ShowBadges = baseDto.ShowBadges,
                ShowAuraRings = baseDto.ShowAuraRings,
                ShowMoodTags = baseDto.ShowMoodTags,
                AnimateTransitions = baseDto.AnimateTransitions,
                HasGitHubConnected = baseDto.HasGitHubConnected,
                Token = c.Token,
            };
        }
    }
}

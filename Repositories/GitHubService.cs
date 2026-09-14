using RafeeqyNotes.Api.Models;
using RafeeqyNotes.Api.Repositories;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

public class GitHubService : IGitHubService
{
    private readonly IConfiguration _config;
    private readonly IHttpClientFactory _httpClientFactory;

    public GitHubService(IConfiguration config, IHttpClientFactory httpClientFactory)
    {
        _config = config;
        _httpClientFactory = httpClientFactory;
    }

    /// <summary>
    /// Builds the GitHub consent URL. <paramref name="state"/> must be an unguessable,
    /// single-use nonce from <c>IGitHubOAuthStateService</c> — never a user id, which is what
    /// it used to be (see GitHubAuthController for why that was exploitable).
    /// </summary>
    public string GetAuthUrl(string state)
    {
        var clientId = _config["GitHub:ClientId"];
        var redirectUri = _config["GitHub:RedirectUri"];
        return $"https://github.com/login/oauth/authorize?client_id={clientId}&redirect_uri={redirectUri}&scope=repo,user&state={Uri.EscapeDataString(state)}";
    }

    public async Task<GitHubTokenResponse> ExchangeCodeForTokenAsync(string code)
    {
        var clientId = _config["GitHub:ClientId"];
        var clientSecret = _config["GitHub:ClientSecret"];
        var redirectUri = _config["GitHub:RedirectUri"];

        var client = _httpClientFactory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, "https://github.com/login/oauth/access_token");
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            {"client_id", clientId},
            {"client_secret", clientSecret},
            {"code", code},
            {"redirect_uri", redirectUri}
        });
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<GitHubTokenResponse>(content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    public async Task<GitHubTokenResponse> RefreshTokenAsync(string refreshToken)
    {
        var clientId = _config["GitHub:ClientId"];
        var clientSecret = _config["GitHub:ClientSecret"];

        var client = _httpClientFactory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, "https://github.com/login/oauth/access_token");
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            {"client_id", clientId},
            {"client_secret", clientSecret},
            {"refresh_token", refreshToken},
            {"grant_type", "refresh_token"}
        });
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<GitHubTokenResponse>(content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    public record GitHubTokenResponse(string Access_Token, string? Refresh_Token, int? Expires_In, string? Refresh_Token_Expires_In, string Scope, string Token_Type);

    // GitHub's actual /user/repos response shape (snake_case), kept private — this was
    // previously deserialized straight into this app's own GitRepository domain model, which
    // has none of these fields (and whose Id is a string against GitHub's numeric id), so it
    // either produced a mostly-empty object or threw on the id's type mismatch. It's mapped
    // below to GitHubRepoResponse, which mirrors the frontend's GitHubRepo type instead.
    private class GitHubApiRepo
    {
        public long Id { get; set; }
        public string Name { get; set; }

        [JsonPropertyName("full_name")]
        public string FullName { get; set; }
        public string? Description { get; set; }

        [JsonPropertyName("html_url")]
        public string HtmlUrl { get; set; }

        [JsonPropertyName("clone_url")]
        public string CloneUrl { get; set; }

        [JsonPropertyName("ssh_url")]
        public string SshUrl { get; set; }

        [JsonPropertyName("private")]
        public bool Private { get; set; }

        [JsonPropertyName("default_branch")]
        public string DefaultBranch { get; set; }
        public string? Language { get; set; }

        [JsonPropertyName("stargazers_count")]
        public int StargazersCount { get; set; }

        [JsonPropertyName("forks_count")]
        public int ForksCount { get; set; }
        public GitHubApiOwner Owner { get; set; }
    }

    private class GitHubApiOwner
    {
        public string Login { get; set; }

        [JsonPropertyName("avatar_url")]
        public string AvatarUrl { get; set; }
    }

    public async Task<IEnumerable<GitHubRepoResponse>> GetUserRepositoriesAsync(string accessToken)
    {
        if (string.IsNullOrEmpty(accessToken))
            throw new ArgumentException("Access token is required.", nameof(accessToken));

        var client = _httpClientFactory.CreateClient("GitHubClient");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("RafeeqyNotes", "1.0"));

        var response = await client.GetAsync("https://api.github.com/user/repos");
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        var repos = JsonSerializer.Deserialize<List<GitHubApiRepo>>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? new List<GitHubApiRepo>();

        return repos.Select(r => new GitHubRepoResponse
        {
            Id = r.Id,
            Name = r.Name,
            FullName = r.FullName,
            Description = r.Description,
            HtmlUrl = r.HtmlUrl,
            CloneUrl = r.CloneUrl,
            SshUrl = r.SshUrl,
            IsPrivate = r.Private,
            DefaultBranch = r.DefaultBranch,
            Language = r.Language,
            Stars = r.StargazersCount,
            Forks = r.ForksCount,
            Owner = r.Owner == null ? null : new GitHubOwnerResponse { Login = r.Owner.Login, AvatarUrl = r.Owner.AvatarUrl },
        });
    }
}
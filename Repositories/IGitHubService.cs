using RafeeqyNotes.Api.Models;

namespace RafeeqyNotes.Api.Repositories
{
    public interface IGitHubService
    {
        string GetAuthUrl(string userId);
        Task<GitHubService.GitHubTokenResponse> ExchangeCodeForTokenAsync(string code);
        Task<GitHubService.GitHubTokenResponse> RefreshTokenAsync(string refreshToken);
        Task<IEnumerable<GitHubRepoResponse>> GetUserRepositoriesAsync(string accessToken);
    }
}

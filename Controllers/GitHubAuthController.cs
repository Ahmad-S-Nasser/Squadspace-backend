using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using RafeeqyNotes.Api.Helpers;
using RafeeqyNotes.Api.Repositories;
using RafeeqyNotes.Api.Services;

namespace RafeeqyNotes.Api.Controllers
{
    [ApiController]
    [Route("api/Git/github")]
    public class GitHubAuthController : ControllerBase
    {
        private readonly IGitHubService _service;
        private readonly IConfiguration _config;
        private readonly IContributorRepository _contributors;
        private readonly IGitHubOAuthStateService _oauthState;

        public GitHubAuthController(
            IGitHubService service,
            IConfiguration config,
            IContributorRepository contributors,
            IGitHubOAuthStateService oauthState)
        {
            _service = service;
            _config = config;
            _contributors = contributors;
            _oauthState = oauthState;
        }

        /// <summary>
        /// Starts the GitHub OAuth flow for the authenticated caller.
        /// </summary>
        /// <remarks>
        /// The <c>userId</c> query parameter is accepted for backwards compatibility but is
        /// IGNORED — the flow is always bound to the JWT subject. Previously the caller chose
        /// the id, and it was carried in the OAuth <c>state</c>: an attacker could start the
        /// flow naming a victim, finish it with their own GitHub account, and overwrite the
        /// victim's stored token, so the victim's later in-app GitHub actions ran against the
        /// attacker's repositories.
        ///
        /// <c>state</c> is now a signed, single-use, expiring nonce issued by
        /// <see cref="IGitHubOAuthStateService"/> — it is not the user id.
        /// </remarks>
        [Authorize]
        [HttpGet("auth")]
        public IActionResult Authenticate([FromQuery] string userId)
        {
            var callerId = OrgAccess.UserId(User);
            if (string.IsNullOrEmpty(callerId))
                return Unauthorized(new { message = "Authentication required" });

            var state = _oauthState.Issue(callerId);
            var url = _service.GetAuthUrl(state);
            return Redirect(url);
        }

        /// <summary>GitHub redirects the browser here, so it cannot carry a bearer token.</summary>
        /// <remarks>
        /// Security rests entirely on <c>state</c> being an unguessable, single-use, expiring
        /// nonce that maps back to the user who started the flow.
        /// </remarks>
        [AllowAnonymous]
        [HttpGet("callback")]
        public async Task<IActionResult> Callback([FromQuery] string code, [FromQuery] string state)
        {
            if (string.IsNullOrEmpty(code))
                return BadRequest("Missing code");

            if (string.IsNullOrEmpty(state))
                return BadRequest("Missing state");

            if (!_oauthState.TryConsume(state, out var userId))
                return BadRequest("Invalid or expired authorization state. Please start the connection again.");

            var tokenData = await _service.ExchangeCodeForTokenAsync(code);

            // Persist token for the user who actually started the flow
            var contributor = await _contributors.GetByIdAsync(userId);
            if (contributor != null)
            {
                contributor.GitHubAccessToken = tokenData.Access_Token;
                contributor.GitHubRefreshToken = tokenData.Refresh_Token;
                if (tokenData.Expires_In.HasValue)
                {
                    contributor.GitHubTokenExpiry = DateTime.UtcNow.AddSeconds(tokenData.Expires_In.Value);
                }
                await _contributors.UpdateAsync(contributor);
            }

            var frontendUrl = _config["FrontendUrl"] ?? "https://app.squadspace.net";
            return Redirect($"{frontendUrl}/repositories?github=success");
        }

        /// <summary>Lists the caller's own GitHub repositories.</summary>
        /// <remarks>
        /// The <c>userId</c> query parameter is accepted for compatibility but IGNORED. It was
        /// previously read from the query string with no authentication, so anyone could name
        /// any user and receive that user's PRIVATE repositories, fetched with the victim's
        /// stored token (OAuth scope <c>repo,user</c>) — and the handler would even refresh
        /// the victim's token, keeping the access alive indefinitely.
        /// </remarks>
        [Authorize]
        [HttpGet("user/repos")]
        public async Task<IActionResult> GetUserRepos([FromQuery] string userId)
        {
            var callerId = OrgAccess.UserId(User);
            if (string.IsNullOrEmpty(callerId))
                return Unauthorized(new { message = "Authentication required" });

            var contributor = await _contributors.GetByIdAsync(callerId);
            if (contributor == null || string.IsNullOrEmpty(contributor.GitHubAccessToken))
            {
                return Unauthorized("GitHub not connected");
            }

            // Check if token is expired (with a 5 minute buffer)
            if (contributor.GitHubTokenExpiry.HasValue && contributor.GitHubTokenExpiry.Value < DateTime.UtcNow.AddMinutes(5))
            {
                if (!string.IsNullOrEmpty(contributor.GitHubRefreshToken))
                {
                    try
                    {
                        var tokenData = await _service.RefreshTokenAsync(contributor.GitHubRefreshToken);
                        contributor.GitHubAccessToken = tokenData.Access_Token;
                        contributor.GitHubRefreshToken = tokenData.Refresh_Token;
                        if (tokenData.Expires_In.HasValue)
                        {
                            contributor.GitHubTokenExpiry = DateTime.UtcNow.AddSeconds(tokenData.Expires_In.Value);
                        }
                        await _contributors.UpdateAsync(contributor);
                    }
                    catch
                    {
                        return Unauthorized("GitHub session expired. Please reconnect.");
                    }
                }
                else
                {
                    return Unauthorized("GitHub token expired and no refresh token available.");
                }
            }

            var repos = await _service.GetUserRepositoriesAsync(contributor.GitHubAccessToken);
            return Ok(repos);
        }
    }
}

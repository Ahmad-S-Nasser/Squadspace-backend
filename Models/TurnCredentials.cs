namespace RafeeqyNotes.Api.Models
{
    /// <summary>
    /// Short-lived TURN credentials for one meeting call, minted per the standard coturn
    /// REST API long-term-credential convention (a time-limited username plus an HMAC-SHA1
    /// of it, keyed by a secret shared only between this API and the coturn server — never
    /// shipped to the browser). Not persisted.
    /// </summary>
    public class TurnCredentials
    {
        public string Username { get; set; } = string.Empty;
        public string Credential { get; set; } = string.Empty;
        public List<string> Urls { get; set; } = new();
        public int Ttl { get; set; }
    }
}

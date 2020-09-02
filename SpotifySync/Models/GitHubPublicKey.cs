using Newtonsoft.Json;

namespace SpotifySync.Models
{
    [JsonObject]
    public class GitHubPublicKey
    {
        [JsonProperty("key_id")]
        public string KeyId { get; set; }

        [JsonProperty("key")]
        public string Key { get; set; }
    }
}

using Newtonsoft.Json;

namespace SpotifySync.Models
{
    [JsonObject]
    public class GitHubSecretPayload
    {
        [JsonProperty("encrypted_value")]
        public string EncryptedValue { get; set; }

        [JsonProperty("key_id")]
        public string KeyId { get; set; }
    }
}

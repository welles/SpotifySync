using System;
using System.Threading.Tasks;
using Newtonsoft.Json;
using SpotifyAPI.Web;

namespace SpotifySync
{
    public static class Program
    {
        public static async Task Main()
        {
            var token = Environment.GetEnvironmentVariable("SPOTIFY_TOKEN");

            if (string.IsNullOrWhiteSpace(token))
            {
                throw new InvalidOperationException("SPOTIFY_TOKEN environment variable is null or empty.");
            }

            var pkceToken = JsonConvert.DeserializeObject<PKCETokenResponse>(token, new JsonSerializerSettings { Error = (sender, args) => throw args.ErrorContext.Error});

            if (pkceToken == null)
            {
                throw new InvalidOperationException("PKCE Token is null.");
            }

            Console.WriteLine("Scopes: " + pkceToken.Scope);
        }
    }
}

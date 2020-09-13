using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SpotifyAPI.Web;

namespace SpotifySync
{
    public static class Program
    {
        public static async Task Main()
        {
            Console.Write("Loading environment variables... ");

            var spotifyClientId = Program.GetEnvironmentVariable("SPOTIFY_CLIENT_ID");
            var spotifyClientSecret = Program.GetEnvironmentVariable("SPOTIFY_CLIENT_SECRET");
            var spotifyRefreshToken = Program.GetEnvironmentVariable("SPOTIFY_REFRESH_TOKEN");
            var spotifyPlaylistId = Program.GetEnvironmentVariable("SPOTIFY_PLAYLIST_ID");

            Console.WriteLine("[Ok]");

            Console.Write("Loading Spotify token... ");

            var spotifyToken = await Program.GetSpotifyToken(spotifyClientId, spotifyClientSecret, spotifyRefreshToken);

            Console.WriteLine("[Ok]");

            Console.Write("Authenticating with Spotify... ");

            var spotifyClient = await Program.GetSpotifyClient(spotifyToken);

            Console.WriteLine("[Ok]");

            Console.Write("Loading library songs list... ");

            var librarySongs = await Program.GetLibrarySongs(spotifyClient);

            Console.WriteLine($"[Ok: {librarySongs.Count} songs]");

            Console.Write("Loading playlist songs list... ");

            var playlistSongs = await Program.GetPlaylistSongs(spotifyClient, spotifyPlaylistId);

            Console.WriteLine($"[Ok: {playlistSongs.Count} songs]");
        }

        private static async Task<string> GetSpotifyToken(string spotifyClientId, string spotifyClientSecret, string spotifyRefreshToken)
        {
            using (var client = new HttpClient())
            using (var request = new HttpRequestMessage(HttpMethod.Post, "https://accounts.spotify.com/api/token"))
            {
                var spotifyClientString = spotifyClientId + ":" + spotifyClientSecret;
                var spotifyClientBytes = Encoding.UTF8.GetBytes(spotifyClientString);
                var spotifyClientStringEncoded = Convert.ToBase64String(spotifyClientBytes);

                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", spotifyClientStringEncoded);

                var body = new List<KeyValuePair<string, string>>
                {
                    new KeyValuePair<string, string>("grant_type", "refresh_token"),
                    new KeyValuePair<string, string>("refresh_token", spotifyRefreshToken)
                };

                request.Content = new FormUrlEncodedContent(body);

                var response = await client.SendAsync(request);

                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();

                    var token = JObject.Parse(content).Value<string>("access_token");

                    return token;
                }
            }

            throw new InvalidOperationException("Could not load Spotify token.");
        }

        private static string GetEnvironmentVariable(string key)
        {
            var variable = Environment.GetEnvironmentVariable(key);

            if (string.IsNullOrWhiteSpace(variable))
            {
                throw new ArgumentException($"Environment variable \"{key}\" is not set.", key);
            }

            return variable;
        }

        private static async Task<SpotifyClient> GetSpotifyClient(string spotifyToken)
        {
            var config = SpotifyClientConfig.CreateDefault().WithToken(spotifyToken, "Bearer");

            var spotify = new SpotifyClient(config);

            var me = await spotify.UserProfile.Current();

            if (string.IsNullOrWhiteSpace(me.Id))
            {
                throw new Exception("Authorization with Spotify failed.");
            }

            return spotify;
        }

        private static async Task<List<SavedTrack>> GetLibrarySongs(SpotifyClient spotifyClient)
        {
            var librarySongsRequest = await spotifyClient.Library.GetTracks();

            var items = new List<SavedTrack>();

            await foreach(var item in spotifyClient.Paginate(librarySongsRequest))
            {
                items.Add(item);

                // await Task.Delay(10);
            }

            return items;
        }

        private static async Task<List<FullTrack>> GetPlaylistSongs(SpotifyClient spotifyClient, string spotifyPlaylistId)
        {
            var playlistSongsRequest = await spotifyClient.Playlists.GetItems(spotifyPlaylistId);

            var items = new List<FullTrack>();

            await foreach(var item in spotifyClient.Paginate(playlistSongsRequest))
            {
                items.Add((FullTrack) item.Track);

                // await Task.Delay(10);
            }

            return items;
        }
    }
}

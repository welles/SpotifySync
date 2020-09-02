using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using SpotifyAPI.Web;
using SpotifySync.Models;

namespace SpotifySync
{
    public static class Program
    {
        private const string SpotifyTokenName = "SPOTIFY_TOKEN";

        private const string SpotifyClientIdName = "SPOTIFY_CLIENT_ID";

        private const string GitHubTokenName = "GH_TOKEN";

        private const string GitHubRepositoryName = "GITHUB_REPOSITORY";

        private static readonly string SpotifyClientId = Environment.GetEnvironmentVariable(Program.SpotifyClientIdName);

        private static readonly string SpotifyToken = Environment.GetEnvironmentVariable(Program.SpotifyTokenName);

        private static readonly string GitHubToken = Environment.GetEnvironmentVariable(Program.GitHubTokenName);

        private static readonly string GitHubRepository = Environment.GetEnvironmentVariable(Program.GitHubRepositoryName);

        public static async Task Main()
        {
            if (string.IsNullOrWhiteSpace(Program.SpotifyToken))
            {
                throw new InvalidOperationException("SPOTIFY_TOKEN environment variable is null or empty.");
            }

            if (string.IsNullOrWhiteSpace(Program.SpotifyClientId))
            {
                throw new InvalidOperationException("SPOTIFY_CLIENT_ID environment variable is null or empty.");
            }

            if (string.IsNullOrWhiteSpace(Program.GitHubToken))
            {
                throw new InvalidOperationException("GH_TOKEN environment variable is null or empty.");
            }

            if (string.IsNullOrWhiteSpace(Program.GitHubRepository))
            {
                throw new InvalidOperationException("GITHUB_REPOSITORY environment variable is null or empty.");
            }

            var publicKey = await Program.GetPublicKey();

            var token = Program.GetToken();

            var authenticator = new PKCEAuthenticator(Program.SpotifyClientId, token);
            authenticator.TokenRefreshed += async (sender, newToken) => await Program.UpdateSecret(publicKey, newToken);

            var config = SpotifyClientConfig.CreateDefault().WithAuthenticator(authenticator);

            var spotify = new SpotifyClient(config);

            var me = await spotify.UserProfile.Current();

            Console.WriteLine($"Authentication successful. ({me.DisplayName})");

            var librarySongsTask = spotify.PaginateAll(await spotify.Library.GetTracks());

            var savedPlaylist = (await spotify.PaginateAll(await spotify.Playlists.CurrentUsers())).SingleOrDefault(x => x.Name == "SAVED");

            if (savedPlaylist == null)
            {
                throw new InvalidOperationException("Playlist SAVED not found.");
            }

            var playlistItems = await spotify.PaginateAll(await spotify.Playlists.GetItems(savedPlaylist.Id));
            var playlistSongs = playlistItems.Select(x => x.Track).Cast<FullTrack>().ToList();

            var librarySongs = await librarySongsTask;

            Console.WriteLine($"There are {librarySongs.Count} songs in the library.");
            Console.WriteLine($"There are {playlistSongs.Count} songs in the playlist.");

            await Program.SynchronizeSongs(spotify, savedPlaylist, librarySongs, playlistSongs);
        }

        private static async Task SynchronizeSongs(SpotifyClient spotify, SimplePlaylist savedPlaylist, IList<SavedTrack> librarySongs, IList<FullTrack> playlistSongs)
        {
            var libraryIds = librarySongs.Select(x => x.Track.Id).ToList();
            var playlistIds = playlistSongs.Select(x => x.Id).ToList();

            var added = libraryIds.Except(playlistIds).ToList();
            var removed = playlistIds.Except(libraryIds).ToList();

            Console.WriteLine($"{added.Count} songs were added since last run.");
            Console.WriteLine($"{removed.Count} songs were removed since last run.");

            var addedSongs = librarySongs.Where(x => added.Contains(x.Track.Id)).OrderBy(x => x.AddedAt).ToList();
            var removedSongs = playlistSongs.Where(x => removed.Contains(x.Id)).ToList();

            for (var index = 0; index < addedSongs.Count; index += 100)
            {
                var tracks = addedSongs.Skip(index).Take(100).ToList();

                var uris = tracks.Select(x => x.Track.Uri).ToList();

                await spotify.Playlists.AddItems(savedPlaylist.Id, new PlaylistAddItemsRequest(uris));

                Console.WriteLine($"Added {tracks.Count} to playlist.");
            }

            for (var index = 0; index < removedSongs.Count; index += 100)
            {
                var tracks = removedSongs.Skip(index).Take(100).ToList();

                var uris = tracks.Select(x => new PlaylistRemoveItemsRequest.Item {Uri = x.Uri}).ToList();

                await spotify.Playlists.RemoveItems(savedPlaylist.Id, new PlaylistRemoveItemsRequest {Tracks = uris});

                Console.WriteLine($"Removed {tracks.Count} from playlist.");
            }
        }

        private static PKCETokenResponse GetToken()
        {
            var pkceToken = JsonConvert.DeserializeObject<PKCETokenResponse>(Program.SpotifyToken);

            return pkceToken;
        }

        private static async Task<GitHubPublicKey> GetPublicKey()
        {
            using (var client = new HttpClient())
            using (var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/" + Program.GitHubRepository + "/actions/secrets/public-key"))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Program.GitHubToken);
                request.Headers.UserAgent.Add(new ProductInfoHeaderValue("SpotifySync", "1.0"));

                var response = await client.SendAsync(request);

                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();

                    var publicKey = JsonConvert.DeserializeObject<GitHubPublicKey>(content);

                    return publicKey;
                }
            }

            throw new InvalidOperationException("Could not load public key from GitHub.");
        }

        private static GitHubSecretPayload GetSecretPayload(GitHubPublicKey publicKey, PKCETokenResponse token)
        {
            var tokenJson = JsonConvert.SerializeObject(token, Formatting.None);

            var secretValueBytes = Encoding.UTF8.GetBytes(tokenJson);
            var publicKeyBytes = Convert.FromBase64String(publicKey.Key);

            var sealedPublicKeyBox = Sodium.SealedPublicKeyBox.Create(secretValueBytes, publicKeyBytes);

            var keyboxString = Convert.ToBase64String(sealedPublicKeyBox);

            var payload = new GitHubSecretPayload
            {
                EncryptedValue = keyboxString,
                KeyId = publicKey.KeyId
            };

            return payload;
        }

        private static async Task UpdateSecret(GitHubPublicKey publicKey, PKCETokenResponse token)
        {
            var payload = Program.GetSecretPayload(publicKey, token);

            var payloadJson = JsonConvert.SerializeObject(payload, Formatting.None);

            using (var client = new HttpClient())
            using (var request = new HttpRequestMessage(HttpMethod.Put, "https://api.github.com/repos/" + Program.GitHubRepository + "/actions/secrets/" + Program.SpotifyTokenName))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Program.GitHubToken);
                request.Headers.UserAgent.Add(new ProductInfoHeaderValue("SpotifySync", "1.0"));

                request.Content = new StringContent(payloadJson);

                var response = await client.SendAsync(request);

                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }

            throw new InvalidOperationException("Could not update secret value on GitHub.");
        }
    }
}

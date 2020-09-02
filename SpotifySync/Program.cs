using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;
using Google.Apis.Sheets.v4;
using Google.Apis.Sheets.v4.Data;
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

        private const string GoogleTokenName = "GOOGLE_TOKEN";

        private const string GoogleSheetIdName = "GOOGLE_SHEET_ID";


        private static readonly string SpotifyClientId = Environment.GetEnvironmentVariable(Program.SpotifyClientIdName);

        private static readonly string SpotifyToken = Environment.GetEnvironmentVariable(Program.SpotifyTokenName);

        private static readonly string GitHubToken = Environment.GetEnvironmentVariable(Program.GitHubTokenName);

        private static readonly string GitHubRepository = Environment.GetEnvironmentVariable(Program.GitHubRepositoryName);

        private static readonly string GoogleToken = Environment.GetEnvironmentVariable(Program.GoogleTokenName);

        private static readonly string GoogleSheetId = Environment.GetEnvironmentVariable(Program.GoogleSheetIdName);

        public static async Task Main()
        {
            Program.CheckEnvVariable(Program.SpotifyToken, Program.SpotifyTokenName);
            Program.CheckEnvVariable(Program.SpotifyClientId, Program.SpotifyClientIdName);
            Program.CheckEnvVariable(Program.GitHubToken, Program.GitHubTokenName);
            Program.CheckEnvVariable(Program.SpotifyToken, Program.SpotifyTokenName);
            Program.CheckEnvVariable(Program.GitHubRepository, Program.GitHubRepositoryName);
            Program.CheckEnvVariable(Program.GoogleToken, Program.GoogleTokenName);
            Program.CheckEnvVariable(Program.GoogleSheetId, Program.GoogleSheetIdName);

            var publicKey = await Program.GetPublicKey();

            var token = Program.GetToken();

            var authenticator = new PKCEAuthenticator(Program.SpotifyClientId, token);
            authenticator.TokenRefreshed += async (sender, newToken) => await Program.UpdateSecret(publicKey, newToken);

            var config = SpotifyClientConfig.CreateDefault().WithAuthenticator(authenticator);

            var spotify = new SpotifyClient(config);

            var me = await spotify.UserProfile.Current();

            Console.WriteLine($"Authentication successful. ({me.DisplayName})");

            var librarySongsTask = (await spotify.Library.GetTracks()).Paginate(spotify);

            var savedPlaylist = (await (await spotify.Playlists.CurrentUsers()).Paginate(spotify)).SingleOrDefault(x => x.Name == "SAVED" && x.Owner.Id == me.Id);

            if (savedPlaylist == null)
            {
                throw new InvalidOperationException("Playlist SAVED not found.");
            }

            var playlistItems = await (await spotify.Playlists.GetItems(savedPlaylist.Id)).Paginate(spotify);
            var playlistSongs = playlistItems.Select(x => x.Track).Cast<FullTrack>().ToList();

            var librarySongs = await librarySongsTask;

            Console.WriteLine($"There are {librarySongs.Count} songs in the library.");
            Console.WriteLine($"There are {playlistSongs.Count} songs in the playlist.");

            await Program.SynchronizeSongs(spotify, savedPlaylist, librarySongs, playlistSongs);
        }

        private static async Task<List<T>> Paginate<T>(this IPaginatable<T> pages, ISpotifyClient spotify)
            where T : class
        {
            var items = new List<T>();

            await foreach(var item in spotify.Paginate(pages))
            {
                items.Add(item);

                await Task.Delay(100);
            }

            return items;
        }

        private static void CheckEnvVariable(string variable, string variableName)
        {
            if (string.IsNullOrWhiteSpace(variable))
            {
                throw new InvalidOperationException($"{variableName} environment variable is null or empty.");
            }
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

                Console.WriteLine($"Added {tracks.Count} songs to playlist.");
            }

            for (var index = 0; index < removedSongs.Count; index += 100)
            {
                var tracks = removedSongs.Skip(index).Take(100).ToList();

                var uris = tracks.Select(x => new PlaylistRemoveItemsRequest.Item {Uri = x.Uri}).ToList();

                await spotify.Playlists.RemoveItems(savedPlaylist.Id, new PlaylistRemoveItemsRequest {Tracks = uris});

                Console.WriteLine($"Removed {tracks.Count} songs from playlist.");
            }

            await Program.LogChanges(librarySongs, addedSongs, removedSongs);
        }

        private static async Task LogChanges(IList<SavedTrack> librarySongs, IList<SavedTrack> addedSongs, IList<FullTrack> removedSongs)
        {
            var service = Program.CreateSheetsService();

            var serviceValues = service.Spreadsheets.Values;

            var rows = librarySongs.Select(song => new[] {$"=IMAGE(\"{song.Track.Album.Images.OrderByDescending(x => x.Height).First().Url}\")", song.Track.Id, song.Track.Name, song.Track.Artists.First().Name, song.Track.Album.Name}).ToArray();

            var valueRange = new ValueRange {Values = rows };

            var update = serviceValues.Update(valueRange, Program.GoogleSheetId, "Current!A:Z");

            update.ValueInputOption = SpreadsheetsResource.ValuesResource.UpdateRequest.ValueInputOptionEnum.RAW;

            var response = await update.ExecuteAsync();

            Console.WriteLine($"Saved current songs list to Google Sheet.");
        }

        private static SheetsService CreateSheetsService()
        {
            using (var stream = Program.GoogleToken.ToStream())
            {
                var serviceInitializer = new BaseClientService.Initializer
                {
                    HttpClientInitializer = GoogleCredential.FromStream(stream).CreateScoped(SheetsService.Scope.Spreadsheets)
                };

                return new SheetsService(serviceInitializer);
            }
        }

        private static Stream ToStream(this string s)
        {
            var stream = new MemoryStream();
            var writer = new StreamWriter(stream);
            writer.Write(s);
            writer.Flush();
            stream.Position = 0;
            return stream;
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

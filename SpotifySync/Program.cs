﻿using System;
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
            var googleToken = Program.GetEnvironmentVariable("GOOGLE_TOKEN");
            var googleSheetId = Program.GetEnvironmentVariable("GOOGLE_SHEET_ID");

            Console.WriteLine("[Ok]");

            Console.Write("Loading Spotify token... ");

            var spotifyToken = await Program.GetSpotifyToken(spotifyClientId, spotifyClientSecret, spotifyRefreshToken);

            Console.WriteLine("[Ok]");

            Console.Write("Authenticating with Spotify... ");

            var spotifyClient = await Program.GetSpotifyClient(spotifyToken);

            Console.WriteLine("[Ok]");

            Console.Write("Authenticating with Google Sheets... ");

            var sheetsService = Program.GetSheetsService(googleToken);

            Console.WriteLine("[Ok]");

            Console.Write("Loading library songs list... ");

            var librarySongs = await Program.GetLibrarySongs(spotifyClient);

            Console.WriteLine($"[Ok: {librarySongs.Count} songs]");

            Console.Write("Loading playlist songs list... ");

            var playlistSongs = await Program.GetPlaylistSongs(spotifyClient, spotifyPlaylistId);

            Console.WriteLine($"[Ok: {playlistSongs.Count} songs]");

            Console.Write("Checking for added and removed songs... ");

            Program.CheckAddedRemovedSongs(librarySongs, playlistSongs, out var addedSongs, out var removedSongs);

            Console.WriteLine($"[Ok: {addedSongs.Count} added, {removedSongs.Count} removed]");

            Console.Write("Synchronizing library and playlist... ");

            await Program.SynchronizePlaylist(spotifyClient, spotifyPlaylistId, addedSongs, removedSongs);

            Console.WriteLine("[Ok]");

            Console.Write("Write added songs to spreadsheet... ");

            await Program.AppendAddedLog(sheetsService, addedSongs, googleSheetId);

            Console.WriteLine("[Ok]");

            Console.Write("Write removed songs to spreadsheet... ");

            await Program.AppendRemovedLog(sheetsService, removedSongs, googleSheetId);

            Console.WriteLine("[Ok]");

            Console.Write("Write all saved songs to spreadsheet... ");

            await Program.UpdateCurrentSheet(sheetsService, librarySongs, googleSheetId);

            Console.WriteLine("[Ok]");
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

        private static void CheckAddedRemovedSongs(List<SavedTrack> librarySongs, List<FullTrack> playlistSongs,
            out List<SavedTrack> addedSongs, out List<FullTrack> removedSongs)
        {
            var libraryIds = librarySongs.Select(x => x.Track.Id).ToList();
            var playlistIds = playlistSongs.Select(x => x.Id).ToList();

            var added = libraryIds.Except(playlistIds).ToList();
            var removed = playlistIds.Except(libraryIds).ToList();

            addedSongs = librarySongs.Where(x => added.Contains(x.Track.Id)).OrderBy(x => x.AddedAt).ToList();
            removedSongs = playlistSongs.Where(x => removed.Contains(x.Id)).ToList();
        }

        private static async Task SynchronizePlaylist(SpotifyClient spotifyClient, string spotifyPlaylistId, List<SavedTrack> addedSongs, List<FullTrack> removedSongs)
        {
            for (var index = 0; index < addedSongs.Count; index += 100)
            {
                var tracks = addedSongs.Skip(index).Take(100).ToList();

                var uris = tracks.Select(x => x.Track.Uri).ToList();

                await spotifyClient.Playlists.AddItems(spotifyPlaylistId, new PlaylistAddItemsRequest(uris));
            }

            for (var index = 0; index < removedSongs.Count; index += 100)
            {
                var tracks = removedSongs.Skip(index).Take(100).ToList();

                var uris = tracks.Select(x => new PlaylistRemoveItemsRequest.Item {Uri = x.Uri}).ToList();

                await spotifyClient.Playlists.RemoveItems(spotifyPlaylistId, new PlaylistRemoveItemsRequest {Tracks = uris});
            }
        }

        private static SheetsService GetSheetsService(string googleToken)
        {
            using (var stream = googleToken.ToStream())
            {
                var serviceInitializer = new BaseClientService.Initializer
                {
                    HttpClientInitializer = GoogleCredential.FromStream(stream).CreateScoped(SheetsService.Scope.Spreadsheets)
                };

                var service = new SheetsService(serviceInitializer);

                if (string.IsNullOrWhiteSpace(service.Name))
                {
                    throw new Exception("Authorization with Google failed.");
                }

                return service;
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

        private static async Task UpdateCurrentSheet(SheetsService sheetsService, List<SavedTrack> librarySongs, string googleSheetId)
        {
            var serviceValues = sheetsService.Spreadsheets.Values;

            var clear = serviceValues.Clear(new ClearValuesRequest(), googleSheetId, "Current!A:E");

            await clear.ExecuteAsync();

            var rows = librarySongs.Select(song => new[]
            {
                $"=IMAGE(\"{song.Track.Album.Images.OrderByDescending(x => x.Height).First().Url}\")",
                song.Track.Name,
                song.Track.Artists.First().Name,
                song.Track.Album.Name,
                song.Track.Id
            }).ToArray();

            var valueRange = new ValueRange {Values = rows };

            var update = serviceValues.Update(valueRange, googleSheetId, "Current!A:E");
            update.ValueInputOption = SpreadsheetsResource.ValuesResource.UpdateRequest.ValueInputOptionEnum.USERENTERED;

            await update.ExecuteAsync();
        }

        private static async Task AppendAddedLog(SheetsService sheetsService, List<SavedTrack> addedSongs, string googleSheetId)
        {
            if (!addedSongs.Any()) { return; }

            var serviceValues = sheetsService.Spreadsheets.Values;

            var addedRows = addedSongs.Select(song => new[]
            {
                "Added",
                $"=IMAGE(\"{song.Track.Album.Images.OrderByDescending(x => x.Height).First().Url}\")",
                song.Track.Name,
                song.Track.Artists.First().Name,
                song.Track.Album.Name,
                song.Track.Id
            }).ToArray();

            var addedValueRange = new ValueRange {Values = addedRows };

            var update = serviceValues.Append(addedValueRange, googleSheetId, "Log!A:F");
            update.ValueInputOption = SpreadsheetsResource.ValuesResource.AppendRequest.ValueInputOptionEnum.USERENTERED;

            await update.ExecuteAsync();
        }

        private static async Task AppendRemovedLog(SheetsService sheetsService, List<FullTrack> removedSongs, string googleSheetId)
        {
            if (!removedSongs.Any()) { return; }

            var serviceValues = sheetsService.Spreadsheets.Values;

            var removedRows = removedSongs.Select(song => new[]
            {
                "Removed",
                $"=IMAGE(\"{song.Album.Images.OrderByDescending(x => x.Height).First().Url}\")",
                song.Name,
                song.Artists.First().Name,
                song.Album.Name,
                song.Id
            }).ToArray();

            var removedValueRange = new ValueRange {Values = removedRows };

            var update = serviceValues.Append(removedValueRange, googleSheetId, "Log!A:F");
            update.ValueInputOption = SpreadsheetsResource.ValuesResource.AppendRequest.ValueInputOptionEnum.USERENTERED;

            await update.ExecuteAsync();
        }
    }
}

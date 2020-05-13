﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;
using MomentumDiscordBot.Models;
using MomentumDiscordBot.Utilities;
using TwitchLib.Api.Helix.Models.Streams;

namespace MomentumDiscordBot.Services
{
    /// <summary>
    ///     Service to provide a list of current streamers playing Momentum Mod.
    /// </summary>
    public class StreamMonitorService
    {
        private ulong _channelId;
        private TimeSpan _updateInterval;
        private Dictionary<string, ulong> _cachedStreamsIds;
        private readonly Config _config;
        private readonly DiscordSocketClient _discordClient;
        private SocketTextChannel _textChannel;
        public readonly TwitchApiService TwitchApiService;
        private Timer _intervalFunctionTimer;
        private List<string> _streamSoftBanList = new List<string>();
        private List<Stream> _previousStreams;

        public StreamMonitorService(DiscordSocketClient discordClient, Config config)
        {
            _config = config;
            _discordClient = discordClient;
            TwitchApiService = new TwitchApiService();

            _channelId = _config.MomentumModStreamerChannelId;
            _updateInterval = TimeSpan.FromMinutes(_config.StreamUpdateInterval);
        }

        public void Start()
        {
            _textChannel = _discordClient.GetChannel(_channelId) as SocketTextChannel;

            _cachedStreamsIds = new Dictionary<string, ulong>();

            TryParseExistingEmbedsAsync().GetAwaiter().GetResult();

            _intervalFunctionTimer = new Timer(UpdateCurrentStreamersAsync, null, TimeSpan.Zero, _updateInterval);
        }

        public async void UpdateCurrentStreamersAsync(object state)
        {
            var streams = await TwitchApiService.GetLiveMomentumModStreamersAsync();
            var streamIds = streams.Select(x => x.Id);

            // Get streams from banned users
            var bannedStreams = streams.Where(x => (_config.TwitchUserBans ?? new string[0]).Contains(x.UserId));
            foreach (var bannedStream in bannedStreams)
            {
                if (_cachedStreamsIds.TryGetValue(bannedStream.Id, out var messageId))
                {
                    await _textChannel.DeleteMessageAsync(messageId);
                }
            }

            // If the cached stream id's isn't in the fetched stream id, it is an ended stream
            var endedStreams = _cachedStreamsIds.Where(x => !streamIds.Contains(x.Key));
            
            foreach (var (endedStreamId, messageId) in endedStreams)
            {
                // If the stream was soft banned, remove it
                if (_streamSoftBanList.Contains(endedStreamId))
                {
                    _streamSoftBanList.Remove(endedStreamId);
                }

                await _textChannel.DeleteMessageAsync(messageId);
                _cachedStreamsIds.Remove(endedStreamId);
            }

            // Check for soft-banned stream, when a mod deletes the message
            var existingSelfMessages = (await _textChannel.GetMessagesAsync(limit: 200).FlattenAsync()).FromSelf(_discordClient);
            var softBannedMessages = _cachedStreamsIds.Where(x => existingSelfMessages.All(y => y.Id != x.Value));
            _streamSoftBanList.AddRange(softBannedMessages.Select(x => x.Key));
            _previousStreams = streams;

            // Filter out soft banned streams
            var filteredStreams = streams.Where(x => !_streamSoftBanList.Contains(x.Id) && !(_config.TwitchUserBans ?? new string[0]).Contains(x.UserId));

            // Reload embeds
            await TryParseExistingEmbedsAsync();

            foreach (var stream in filteredStreams)
            {
                var messageText =
                    $"{stream.UserName.EscapeDiscordChars()} has gone live! {MentionUtils.MentionRole(_config.LivestreamMentionRoleId)}";

                var embed = new EmbedBuilder
                {
                    Title = stream.Title.EscapeDiscordChars(),
                    Color = Color.Purple,
                    Author = new EmbedAuthorBuilder
                    {
                        Name = stream.UserName,
                        IconUrl = await TwitchApiService.GetStreamerIconUrlAsync(stream.UserId),
                        Url = $"https://twitch.tv/{stream.UserName}"
                    },
                    ImageUrl = stream.ThumbnailUrl.Replace("{width}", "1280").Replace("{height}", "720") + "?q=" + Environment.TickCount,
                    Description = stream.ViewerCount + " viewers",
                    Url = $"https://twitch.tv/{stream.UserName}",
                    Timestamp = DateTimeOffset.Now
                }.Build();

                // New streams are not in the cache
                if (!_cachedStreamsIds.ContainsKey(stream.Id))
                {
                    // New stream, send a new message
                    var message =
                        await _textChannel.SendMessageAsync(messageText, embed: embed);

                    _cachedStreamsIds.Add(stream.Id, message.Id);
                }
                else
                {
                    // Existing stream, update message with new information
                    if (_cachedStreamsIds.TryGetValue(stream.Id, out var messageId))
                    {
                        var oldMessage = await _textChannel.GetMessageAsync(messageId);
                        if (oldMessage is IUserMessage oldRestMessage)
                        {
                            await oldRestMessage.ModifyAsync(x =>
                            {
                                x.Content = messageText;
                                x.Embed = embed;
                            });
                        }
                    }
                }
            }
        }

        private async Task TryParseExistingEmbedsAsync()
        {
            // Reset cache
            _cachedStreamsIds = new Dictionary<string, ulong>();

            // Get all messages
            var messages = (await _textChannel.GetMessagesAsync().FlattenAsync()).FromSelf(_discordClient).ToList();

            if (!messages.Any()) return;

            // Get current streams
            var streams = await TwitchApiService.GetLiveMomentumModStreamersAsync();

            // Delete existing bot messages simultaneously
            var deleteTasks = messages
                .Select(async x =>
                {
                    if (x.Embeds.Count == 1)
                    {
                        var matchingStream = streams.FirstOrDefault(y => y.UserName == x.Embeds.First().Author?.Name);
                        if (matchingStream == null)
                        {
                            // No matching stream
                            await x.DeleteAsync();
                        }
                        else
                        {
                            // Found the matching stream
                            _cachedStreamsIds.Add(matchingStream.Id, x.Id);
                        }
                    }
                    else
                    {
                        // Stream has ended, or failed to parse
                        await x.DeleteAsync();
                    }
                });
            await Task.WhenAll(deleteTasks);
        }

        public async Task<string> GetTwitchIDAsync(string username)
        { 
            if (ulong.TryParse(username, out _))
            {
                // Input is a explicit Twitch ID
                return username;
            }
            else
            {
                // Input is the Twitch username
                var cachedUser = _previousStreams.FirstOrDefault(x =>
                    string.Equals(username, x.UserName, StringComparison.InvariantCultureIgnoreCase));

                if (cachedUser != null)
                {
                    // User is in the cache
                    return cachedUser.UserId;
                }
                else
                {
                    // Search the API, throws exception if not found
                    return await TwitchApiService.GetStreamerIDAsync(username);
                }
            }
        }
    }
}

using System;
using System.Reflection;
using System.Threading.Tasks;
using DSharpPlus;
using DSharpPlus.SlashCommands;
using DSharpPlus.SlashCommands.EventArgs;
using DSharpPlus.Entities;
using DSharpPlus.EventArgs;
using Microsoft.Extensions.Logging;
using MomentumDiscordBot.Constants;
using MomentumDiscordBot.Models;
using MomentumDiscordBot.Utilities;
using MomentumDiscordBot.Commands.Admin;

namespace MomentumDiscordBot.Services
{
    [Microservice(MicroserviceType.InjectAndInitialize)]
    public class SlashCommandService
    {
        private readonly Configuration _config;
        private readonly DiscordClient _discordClient;
        private DiscordChannel _textChannel;

        public SlashCommandService(Configuration config, DiscordClient discordClient, IServiceProvider services)
        {
            var commands = discordClient.UseSlashCommands(new SlashCommandsConfiguration
            {
                Services = services
            });

            _config = config;
            _discordClient = discordClient;

            commands.RegisterCommands(Assembly.GetEntryAssembly(), config.GuildID);

            commands.SlashCommandErrored += _commands_SlashCommandErrored;
            commands.ContextMenuErrored += _commands_ContextMenuErrored;
            discordClient.GuildDownloadCompleted += _discordClient_GuildsDownloaded;
        }

        private Task _commands_SlashCommandErrored(SlashCommandsExtension sender, SlashCommandErrorEventArgs e)
        {
            return HandleSlashCommandException(e.Exception, e.Context);
        }

        private Task _commands_ContextMenuErrored(SlashCommandsExtension sender, ContextMenuErrorEventArgs e)
        {
            return HandleSlashCommandException(e.Exception, e.Context);
        }

        private Task HandleSlashCommandException(Exception exception, BaseContext context)
        {
            _ = Task.Run(async () =>
            {
                var embedBuilder = new DiscordEmbedBuilder
                {
                    Color = MomentumColor.Red
                };

                string response = null;

                var isChecksFailedException = true;
                switch (exception)
                {
                    case SlashExecutionChecksFailedException e:
                        response = e.FailedChecks.ToCleanResponse();
                        break;
                    case ContextMenuExecutionChecksFailedException e:
                        response = e.FailedChecks.ToCleanResponse();
                        break;
                    default:
                        isChecksFailedException = false;
                        break;
                }

                if (isChecksFailedException)
                {
                    embedBuilder
                        .WithTitle("Access Denied")
                        .WithDescription(response);

                    await context.CreateResponseAsync(embedBuilder.Build());
                }

                else
                {
                    var message =
                        $"{context.User.Username}#{context.User.Discriminator} tried executing '{context.CommandName ?? "<unknown command>"}' but it errored:\n\n{exception.GetType()}: {exception.Message}";

                    embedBuilder
                        .WithTitle("Bot Error")
                        .WithDescription(message);

                    context.Client.Logger.LogError(
                        message,
                        DateTime.Now);

                    var botChannel = _discordClient.FindChannel(_config.AdminBotChannel);

                    if (botChannel is null) return;

                    try
                    {
                        await botChannel.SendMessageAsync(embedBuilder.Build());
                    }
                    catch (Exception)
                    {
                        context.Client.Logger.LogError(
                            "Tried posting an message error in admin channel, but it errored!");
                    }
                }
            });

            return Task.CompletedTask;
        }

        private Task _discordClient_GuildsDownloaded(DiscordClient sender, GuildDownloadCompletedEventArgs e)
        {
            _ = Task.Run(async () =>
            {
                _textChannel = await _discordClient.GetChannelAsync(_config.AdminBotChannel);
                await FindRestartMessageAsync();
            });

            return Task.CompletedTask;
        }

        private async Task FindRestartMessageAsync()
        {
            // Filter only messages from this bot
            var existingMessages = (await _textChannel.GetMessagesAsync(50)).FromSelf(_discordClient);

            foreach (var message in existingMessages)
            {
                var (result, restartMessage) = await TryFindRestartMessageAsync(message);
                if (!result) continue;

                // restart message found
                if (restartMessage != null)
                {
                    // restartMessage is null when we already responded

                    var diff = DateTimeOffset.Now - restartMessage.Timestamp;
                    var embed = new DiscordEmbedBuilder
                    {
                        Description = $"Restart complete! Took {diff.TotalSeconds:N2} seconds.",
                        Color = MomentumColor.Blue
                    };
                    await restartMessage.RespondAsync(embed: embed);
                }

                break;
            }
        }

        private async Task<(bool result, DiscordMessage restartMessage)> TryFindRestartMessageAsync(
            DiscordMessage input)
        {
            var message = await input.Channel.GetMessageAsync(input.Id);

            bool isReply = false;
            if (message.ReferencedMessage != null)
            {
                message = message.ReferencedMessage;
                isReply = true;
            }

            if (message.Interaction is not { Name: AdminModule.ForcerestartCommandName }) return (false, null);

            return (true, isReply ? null : input);
        }
    }
}

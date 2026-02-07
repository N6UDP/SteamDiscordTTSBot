using NetCord.Gateway;
using NetCord.Rest;
using System.Threading.Tasks;
using System.Linq;
using NetCord;
using System;

namespace DiscordBotTTS
{
    public class CommandHandler
    {
        private readonly GatewayClient _client;
        private readonly RestClient _restClient;
        private readonly TTSModule _ttsModule;

        // Retrieve client and RestClient instance via ctor
        public CommandHandler(GatewayClient client, RestClient restClient)
        {
            _restClient = restClient;
            _client = client;
            _ttsModule = new TTSModule();
            _ttsModule.SetRestClient(restClient);
        }

        public async Task InstallCommandsAsync()
        {
            // Hook the MessageReceived event into our command handler
            _client.MessageCreate += HandleCommandAsync;
        }

        private async ValueTask HandleCommandAsync(NetCord.Gateway.Message message)
        {
            Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - Message received from {message.Author.Username}: '{message.Content}'");
            
            // Don't process the command if it was a system message or bot message
            if (message.Author.IsBot)
            {
                Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - Ignoring bot message");
                return;
            }

            // Create a number to track where the prefix ends and the command begins
            int argPos = 0;
            var content = message.Content;

            // Determine if the message is a command based on the prefix
            if (!content.StartsWith('!'))
            {
                Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - Message doesn't start with '!' prefix");
                return;
            }

            Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - Processing command: {content}");

            argPos = 1; // Skip the '!' prefix

            // Simple command parsing - split by spaces
            var args = content.Substring(argPos).Split(' ');
            if (args.Length == 0) return;

            var command = args[0].ToLower();
            Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - Command parsed: {command}");

            // Handle basic commands here for now
            // This is simplified compared to Discord.Net's command framework
            switch (command)
            {
                case "tts":
                    Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - TTS command recognized, processing subcommand");
                    // Handle TTS commands - delegate to TTSModule methods directly
                    await HandleTTSCommand(message, args);
                    break;
                case "say":
                    Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - Say command recognized");
                    // Handle say command - delegate to InfoModule functionality
                    if (args.Length > 1)
                    {
                        var response = string.Join(" ", args.Skip(1));
                        Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - Sending response: {response}");
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await _restClient.SendMessageAsync(message.ChannelId, new MessageProperties { Content = response });
                                Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - Response sent successfully");
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - Error sending response: {ex.Message}");
                            }
                        });
                    }
                    break;
                default:
                    Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - Unknown command: {command}");
                    break;
            }

            return;
        }

        // Public method for handling TTS commands that can be called from both text and slash commands
        public async Task HandleTTSCommandAsync(string subCommand, TextChannel textChannel, ulong guildId, ulong userId, string username,
            string[] args = null, ulong? voiceChannelId = null)
        {
            Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - TTS subcommand: {subCommand}");

            try
            {
                switch (subCommand)
                {
                    case "join":
                        Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - Processing TTS join command");
                        await HandleJoinCommandAsync(textChannel, guildId, userId, username, voiceChannelId);
                        break;

                    case "leave":
                        Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - Processing TTS leave command");
                        await _ttsModule.LeaveChannel(null, textChannel, guildId);
                        break;

                    case "link":
                        Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - Processing TTS link command");
                        await HandleLinkCommandAsync(args, textChannel, userId, guildId, username);
                        break;

                    case "unlink":
                        Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - Processing TTS unlink command");
                        await _ttsModule.UnlinkChannel(userId, textChannel, username);
                        break;

                    case "verify":
                        Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - Processing TTS verify command");
                        await _ttsModule.VerifyLink(userId, textChannel, username);
                        break;

                    case "changevoice":
                        Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - Processing TTS changevoice command");
                        await HandleChangeVoiceCommandAsync(args, textChannel, userId, username);
                        break;

                    case "changerate":
                        Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - Processing TTS changerate command");
                        await HandleChangeRateCommandAsync(args, textChannel, userId, username);
                        break;

                    case "changeserver":
                        Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - Processing TTS changeserver command");
                        await _ttsModule.ChangeServer(userId, guildId, textChannel, username);
                        break;

                    case "voices":
                        Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - Processing TTS voices command");
                        await _ttsModule.ListVoices(textChannel);
                        break;

                    case "say":
                        Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - Processing TTS say command");
                        await HandleSayCommandAsync(args, textChannel, guildId, userId, username);
                        break;

                    case "help":
                        Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - Processing TTS help command");
                        await _ttsModule.Help(textChannel);
                        break;

                    default:
                        Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - Unknown TTS subcommand: {subCommand}");
                        await textChannel.SendMessageAsync(new MessageProperties { Content = $"Unknown TTS command: {subCommand}. Try !tts help" });
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - Error handling TTS command: {ex.Message}");
                await textChannel.SendMessageAsync(new MessageProperties { Content = "An error occurred while processing the TTS command." });
            }
        }

        private async Task HandleTTSCommand(NetCord.Gateway.Message message, string[] args)
        {
            if (args.Length < 2)
            {
                Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - TTS command missing subcommand");
                return;
            }

            var subCommand = args[1].ToLower();
            var textChannel = message.Channel;
            if (textChannel == null)
            {
                Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - Unable to get text channel from message");
                return;
            }

            var guildId = message.GuildId ?? 0;
            var userId = message.Author.Id;
            var username = message.Author.Username;

            // Delegate to the shared handler
            await HandleTTSCommandAsync(subCommand, textChannel, guildId, userId, username, args);
        }

        private async Task HandleJoinCommandAsync(TextChannel textChannel, ulong guildId, ulong userId, string username, ulong? voiceChannelId = null)
        {
            object targetVoiceChannel = null;
            string channelInfo = "unknown";

            try
            {
                // Check if a voice channel ID was provided
                if (voiceChannelId.HasValue)
                {
                    Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - Voice channel ID provided: {voiceChannelId.Value}");
                    
                    // Try to get the voice channel from the guild cache
                    if (_client.Cache.Guilds.TryGetValue(guildId, out var guild))
                    {
                        if (guild.Channels.TryGetValue(voiceChannelId.Value, out var channel))
                        {
                            if (channel is VoiceGuildChannel voiceChannel)
                            {
                                targetVoiceChannel = voiceChannel;
                                channelInfo = voiceChannel.Name;
                                Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - Found voice channel: {channelInfo}");
                            }
                            else
                            {
                                await textChannel.SendMessageAsync(new MessageProperties { Content = $"Channel {voiceChannelId.Value} is not a voice channel." });
                                return;
                            }
                        }
                        else
                        {
                            await textChannel.SendMessageAsync(new MessageProperties { Content = $"Voice channel {voiceChannelId.Value} not found." });
                            return;
                        }
                    }
                    else
                    {
                        await textChannel.SendMessageAsync(new MessageProperties { Content = "Guild not found in cache." });
                        return;
                    }
                }
                else
                {
                    Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - No voice channel ID provided, checking user's current voice state");
                    
                    // Try to get the user's current voice channel from voice states
                    if (_client.Cache.Guilds.TryGetValue(guildId, out var guild))
                    {
                        var voiceStates = guild.VoiceStates;
                        if (voiceStates != null && voiceStates.TryGetValue(userId, out var voiceState) && voiceState.ChannelId.HasValue)
                        {
                            var userVoiceChannelId = voiceState.ChannelId.Value;
                            if (guild.Channels.TryGetValue(userVoiceChannelId, out var userChannel) && userChannel is VoiceGuildChannel userVoiceChannel)
                            {
                                targetVoiceChannel = userVoiceChannel;
                                channelInfo = userVoiceChannel.Name;
                                Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - User is in voice channel: {channelInfo}");
                            }
                        }
                        else
                        {
                            await textChannel.SendMessageAsync(new MessageProperties { Content = "You must be in a voice channel or specify a voice channel ID. Usage: `!tts join [channel_id]`" });
                            return;
                        }
                    }
                    else
                    {
                        await textChannel.SendMessageAsync(new MessageProperties { Content = "Guild not found in cache." });
                        return;
                    }
                }

                // Call the TTS module join method
                await _ttsModule.JoinChannel(targetVoiceChannel, textChannel, guildId, _client);
                
                // Send success message
                string message_text = targetVoiceChannel != null
                    ? $"Joined voice channel: {channelInfo}"
                    : "Attempted to join voice channel";
                    
                await textChannel.SendMessageAsync(new MessageProperties { Content = message_text });
                
                Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - Join command completed for channel: {channelInfo}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - Error in HandleJoinCommandAsync: {ex.Message}");
                await textChannel.SendMessageAsync(new MessageProperties { Content = "An error occurred while trying to join the voice channel." });
            }
        }

        private async Task HandleLinkCommandAsync(string[] args, TextChannel textChannel, ulong userId, ulong guildId, string username)
        {
            if (args != null && args.Length > 2 && ulong.TryParse(args[2], out ulong steamId))
            {
                var voice = args.Length > 3 ? args[3] : "Microsoft David";
                var linkRate = args.Length > 4 && int.TryParse(args[4], out int r) ? r : 0;
                await _ttsModule.LinkChannel(steamId, null, voice, linkRate, userId, guildId, textChannel, username);
            }
            else
            {
                await textChannel.SendMessageAsync(new MessageProperties { Content = "Usage: !tts link <steamid> [voice] [rate]" });
            }
        }

        private async Task HandleChangeVoiceCommandAsync(string[] args, TextChannel textChannel, ulong userId, string username)
        {
            if (args != null && args.Length > 2)
            {
                var voice = string.Join(" ", args.Skip(2));
                await _ttsModule.ChangeVoice(voice, userId, textChannel, username);
            }
            else
            {
                await textChannel.SendMessageAsync(new MessageProperties { Content = "Usage: !tts changevoice <voice name>" });
            }
        }

        private async Task HandleChangeRateCommandAsync(string[] args, TextChannel textChannel, ulong userId, string username)
        {
            if (args != null && args.Length > 2 && int.TryParse(args[2], out int rate))
            {
                await _ttsModule.ChangeRate(rate, userId, textChannel, username);
            }
            else
            {
                await textChannel.SendMessageAsync(new MessageProperties { Content = "Usage: !tts changerate <-10 to 10>" });
            }
        }

        private async Task HandleSayCommandAsync(string[] args, TextChannel textChannel, ulong guildId, ulong userId, string username)
        {
            if (args != null && args.Length > 2)
            {
                var message = string.Join(" ", args.Skip(2));
                await _ttsModule.SayTTS(message, userId, guildId, textChannel, username);
            }
            else
            {
                await textChannel.SendMessageAsync(new MessageProperties { Content = "Usage: !tts say <message>" });
            }
        }
    }
}

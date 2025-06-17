using NetCord;
using NetCord.Gateway;
using NetCord.Rest;
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Speech;
using System.Collections;
using System.Configuration;
using System.Collections.Specialized;
using SteamKit2;
using System.Collections.Generic;
using System.Linq;

namespace DiscordBotTTS
{
    public class Program
    {
        private GatewayClient _client;
        private RestClient _restClient;

        public static void Main(string[] args)
            => new Program().MainAsync().GetAwaiter().GetResult();

        public async Task MainAsync()
        {
            try
            {
                Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - Starting bot initialization...");
                
                _ = Steam.RunSteamTask();

                _ = bool.TryParse(ConfigurationManager.AppSettings.Get("EnableMessageContent"), out bool useMessageContent);

                var intents = useMessageContent ? GatewayIntents.AllNonPrivileged | GatewayIntents.MessageContent : GatewayIntents.AllNonPrivileged;

                var botToken = ConfigurationManager.AppSettings.Get("BotToken");
                
                if (string.IsNullOrEmpty(botToken))
                {
                    Console.WriteLine("ERROR: Bot token is null or empty. Check your App.config file.");
                    return;
                }

                Console.WriteLine($"Bot token found, length: {botToken.Length}");
                Console.WriteLine($"Intents: {intents}");
                Console.WriteLine($"Message content enabled: {useMessageContent}");
                
                // Initialize the TTS module
                slashtts = new TTSModule();

                var token = new BotToken(botToken);
                _restClient = new RestClient(token);
                
                // Set the RestClient for the slash command TTS module
                slashtts.SetRestClient(_restClient);
                _client = new GatewayClient(token, new GatewayClientConfiguration { Intents = intents });
                
                _client.Ready += Client_Ready;
                
                _client.InteractionCreate += async interaction =>
                {
                    Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - Interaction received from {interaction.User.Username}");
                    if (interaction is SlashCommandInteraction slashCommand)
                    {
                        await SlashCommandHandler(slashCommand);
                    }
                };

                var _ch = new CommandHandler(_client, _restClient);
                await _ch.InstallCommandsAsync();

                Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - Starting NetCord client...");
                await _client.StartAsync();
                Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - NetCord client started successfully!");

                // Block this task until the program is closed.
                await Task.Delay(-1);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - FATAL ERROR: {ex.Message}");
                Console.WriteLine($"Stack trace: {ex.StackTrace}");
            }
        }

        public async ValueTask Client_Ready(ReadyEventArgs eventArgs)
        {
            Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - Bot is READY!");
            Console.WriteLine($"Logged in as: {eventArgs.User.Username} (ID: {eventArgs.User.Id})");
            Console.WriteLine($"Connected to {_client.Cache.Guilds.Count} guilds");
            
            foreach (var guild in _client.Cache.Guilds.Values)
            {
                Console.WriteLine($"  - {guild.Name} (ID: {guild.Id})");
            }

            Dictionary<string, (string description, List<ApplicationCommandOptionProperties> options)> commands = new Dictionary<string, (string, List<ApplicationCommandOptionProperties>)>()
            {
                {"help", ("Gets command help", new List<ApplicationCommandOptionProperties>())},
                {"link", ("Link Steam account to Discord", new List<ApplicationCommandOptionProperties>
                {
                    new ApplicationCommandOptionProperties(ApplicationCommandOptionType.String, "steamid", "Your Steam ID") { Required = true },
                    new ApplicationCommandOptionProperties(ApplicationCommandOptionType.String, "voice", "TTS Voice (optional)") { Required = false },
                    new ApplicationCommandOptionProperties(ApplicationCommandOptionType.Integer, "rate", "Speech rate (-10 to 10)") { Required = false }
                })},
                {"unlink", ("Unlink Steam account from Discord", new List<ApplicationCommandOptionProperties>())},
                {"verify", ("Verify your current link status", new List<ApplicationCommandOptionProperties>())},
                {"join", ("Join voice channel", new List<ApplicationCommandOptionProperties>
                {
                    new ApplicationCommandOptionProperties(ApplicationCommandOptionType.Channel, "channel", "Voice channel to join (optional)") { Required = false }
                })},
                {"leave", ("Leave voice channel", new List<ApplicationCommandOptionProperties>())},
                {"voices", ("List available TTS voices", new List<ApplicationCommandOptionProperties>())},
                {"changevoice", ("Change TTS voice", new List<ApplicationCommandOptionProperties>
                {
                    new ApplicationCommandOptionProperties(ApplicationCommandOptionType.String, "voice", "Voice name") { Required = true }
                })},
                {"changerate", ("Change speech rate", new List<ApplicationCommandOptionProperties>
                {
                    new ApplicationCommandOptionProperties(ApplicationCommandOptionType.Integer, "rate", "Speech rate (-10 to 10)") { Required = true }
                })},
                {"changeserver", ("Change server", new List<ApplicationCommandOptionProperties>())}
            };
            
            List<SlashCommandProperties> builtCommands = new List<SlashCommandProperties>();
            foreach ((var command, (var description, var options)) in commands)
            {
                var commandData = new SlashCommandProperties(command, description)
                {
                    Options = options
                };
                builtCommands.Add(commandData);
            }
            
            Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - Registering {builtCommands.Count} slash commands...");
            
            foreach (var guild in _client.Cache.Guilds.Values)
            {
                try
                {
                    foreach (var prop in builtCommands)
                    {
                        await _restClient.CreateGuildApplicationCommandAsync(guild.Id, guild.Id, prop);
                        Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - Registered /{prop.Name} command for guild {guild.Name}");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - Error registering commands for guild {guild.Name}: {ex.Message}");
                }
            }
        }

        private TTSModule slashtts;

        private async Task SlashCommandHandler(SlashCommandInteraction command)
        {
            Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - Slash command received: {command.Data.Name} from {command.User.Username}");
            
            try
            {
                var textChannel = command.Channel as TextChannel;
                var userId = command.User.Id;
                var username = command.User.Username;
                var guildId = command.GuildId.Value;
                
                switch (command.Data.Name)
                {
                    case "help":
                        await command.SendResponseAsync(InteractionCallback.Message("TTS Bot Help:\n" +
                            "/link <steamid> [voice] [rate] - Link Steam account\n" +
                            "/unlink - Unlink Steam account\n" +
                            "/verify - Check link status\n" +
                            "/join [channel] - Join voice channel\n" +
                            "/leave - Leave voice channel\n" +
                            "/changevoice <voice> - Change TTS voice\n" +
                            "/changerate <rate> - Change speech rate\n" +
                            "/changeserver - Change server"));
                        break;
                        
                    case "link":
                        var steamIdStr = command.Data.Options?.FirstOrDefault(o => o.Name == "steamid")?.Value?.ToString();
                        var voice = command.Data.Options?.FirstOrDefault(o => o.Name == "voice")?.Value?.ToString() ?? "Microsoft David";
                        var rateObj = command.Data.Options?.FirstOrDefault(o => o.Name == "rate")?.Value;
                        var rate = rateObj != null ? Convert.ToInt32(rateObj) : 0;
                        
                        if (steamIdStr != null && ulong.TryParse(steamIdStr, out ulong steamId))
                        {
                            await command.SendResponseAsync(InteractionCallback.Message("Linking account..."));
                            await slashtts.LinkChannel(steamId, null, voice, rate, userId, guildId, textChannel, username);
                        }
                        else
                        {
                            await command.SendResponseAsync(InteractionCallback.Message("Invalid Steam ID provided."));
                        }
                        break;
                        
                    case "unlink":
                        await command.SendResponseAsync(InteractionCallback.Message("Unlinking account..."));
                        await slashtts.UnlinkChannel(userId, textChannel, username);
                        break;
                        
                    case "verify":
                        await command.SendResponseAsync(InteractionCallback.Message("Checking link status..."));
                        await slashtts.VerifyLink(userId, textChannel, username);
                        break;
                        
                    case "join":
                        var channelOption = command.Data.Options?.FirstOrDefault(o => o.Name == "channel")?.Value;
                        object targetChannel = null;
                        
                        if (channelOption != null && ulong.TryParse(channelOption.ToString(), out ulong channelId))
                        {
                            if (_client.Cache.Guilds.TryGetValue(guildId, out var guild))
                            {
                                if (guild.Channels.TryGetValue(channelId, out var channel) && channel is VoiceGuildChannel voiceChannel)
                                {
                                    targetChannel = voiceChannel;
                                }
                            }
                        }
                        else
                        {
                            // Try to get user's current voice channel
                            if (_client.Cache.Guilds.TryGetValue(guildId, out var guild))
                            {
                                var voiceStates = guild.VoiceStates;
                                if (voiceStates != null && voiceStates.TryGetValue(userId, out var voiceState) && voiceState.ChannelId.HasValue)
                                {
                                    var userVoiceChannelId = voiceState.ChannelId.Value;
                                    if (guild.Channels.TryGetValue(userVoiceChannelId, out var userChannel) && userChannel is VoiceGuildChannel userVoiceChannel)
                                    {
                                        targetChannel = userVoiceChannel;
                                    }
                                }
                            }
                        }
                        
                        await command.SendResponseAsync(InteractionCallback.Message("Joining voice channel..."));
                        await slashtts.JoinChannel(targetChannel, textChannel, guildId, _client);
                        break;
                        
                    case "leave":
                        await command.SendResponseAsync(InteractionCallback.Message("Leaving voice channel..."));
                        await slashtts.LeaveChannel(null, textChannel, guildId);
                        break;
                        
                    case "voices":
                        await command.SendResponseAsync(InteractionCallback.Message("Listing available voices..."));
                        await slashtts.ListVoices(textChannel);
                        break;
                        
                    case "changevoice":
                        var newVoice = command.Data.Options?.FirstOrDefault(o => o.Name == "voice")?.Value?.ToString();
                        if (newVoice != null)
                        {
                            await command.SendResponseAsync(InteractionCallback.Message("Changing voice..."));
                            await slashtts.ChangeVoice(newVoice, userId, textChannel, username);
                        }
                        else
                        {
                            await command.SendResponseAsync(InteractionCallback.Message("Voice name is required."));
                        }
                        break;
                        
                    case "changerate":
                        var newRateObj = command.Data.Options?.FirstOrDefault(o => o.Name == "rate")?.Value;
                        if (newRateObj != null)
                        {
                            var newRate = Convert.ToInt32(newRateObj);
                            await command.SendResponseAsync(InteractionCallback.Message("Changing rate..."));
                            await slashtts.ChangeRate(newRate, userId, textChannel, username);
                        }
                        else
                        {
                            await command.SendResponseAsync(InteractionCallback.Message("Rate value is required."));
                        }
                        break;
                        
                    case "changeserver":
                        await command.SendResponseAsync(InteractionCallback.Message("Changing server..."));
                        await slashtts.ChangeServer(userId, guildId, textChannel, username);
                        break;
                        
                    default:
                        await command.SendResponseAsync(InteractionCallback.Message($"Unknown command: {command.Data.Name}"));
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - Error handling slash command: {ex.Message}");
                try
                {
                    await command.SendResponseAsync(InteractionCallback.Message("An error occurred while processing the command."));
                }
                catch { }
            }
        }


    }

}

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
        private CommandHandler _ch;

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

                _ch = new CommandHandler(_client, _restClient);
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
                var commandName = command.Data.Name;
                
                // Send immediate response to acknowledge the interaction
                await command.SendResponseAsync(InteractionCallback.Message($"Processing {commandName} command..."));
                
                // Handle special case commands that need slash-specific logic
                if (commandName == "help")
                {
                    await slashtts.Help(textChannel);
                    return;
                }
                
                // Handle join command with special voice channel parsing
                if (commandName == "join")
                {
                    var channelOption = command.Data.Options?.FirstOrDefault(o => o.Name == "channel")?.Value;
                    ulong? voiceChannelId = null;
                    
                    if (channelOption != null && ulong.TryParse(channelOption.ToString(), out ulong channelId))
                    {
                        voiceChannelId = channelId;
                    }
                    
                    await _ch.HandleTTSCommandAsync(commandName, textChannel, guildId, userId, username, null, voiceChannelId);
                    return;
                }
                
                // Handle link command with special parameter parsing
                if (commandName == "link")
                {
                    var steamIdStr = command.Data.Options?.FirstOrDefault(o => o.Name == "steamid")?.Value?.ToString();
                    var voice = command.Data.Options?.FirstOrDefault(o => o.Name == "voice")?.Value?.ToString() ?? "Microsoft David";
                    var rateObj = command.Data.Options?.FirstOrDefault(o => o.Name == "rate")?.Value;
                    var rate = rateObj != null ? Convert.ToInt32(rateObj) : 0;
                    
                    if (steamIdStr != null && ulong.TryParse(steamIdStr, out ulong steamId))
                    {
                        // Create fake args array for the shared handler
                        string[] args = { "tts", "link", steamIdStr, voice, rate.ToString() };
                        await _ch.HandleTTSCommandAsync(commandName, textChannel, guildId, userId, username, args);
                    }
                    else
                    {
                        await textChannel.SendMessageAsync(new MessageProperties { Content = "Invalid Steam ID provided." });
                    }
                    return;
                }
                
                // Handle changevoice command with special parameter parsing
                if (commandName == "changevoice")
                {
                    var voice = command.Data.Options?.FirstOrDefault(o => o.Name == "voice")?.Value?.ToString();
                    if (voice != null)
                    {
                        // Create fake args array for the shared handler
                        string[] args = { "tts", "changevoice", voice };
                        await _ch.HandleTTSCommandAsync(commandName, textChannel, guildId, userId, username, args);
                    }
                    else
                    {
                        await textChannel.SendMessageAsync(new MessageProperties { Content = "Voice name is required." });
                    }
                    return;
                }
                
                // Handle changerate command with special parameter parsing
                if (commandName == "changerate")
                {
                    var rateObj = command.Data.Options?.FirstOrDefault(o => o.Name == "rate")?.Value;
                    if (rateObj != null)
                    {
                        var rate = Convert.ToInt32(rateObj);
                        // Create fake args array for the shared handler
                        string[] args = { "tts", "changerate", rate.ToString() };
                        await _ch.HandleTTSCommandAsync(commandName, textChannel, guildId, userId, username, args);
                    }
                    else
                    {
                        await textChannel.SendMessageAsync(new MessageProperties { Content = "Rate value is required." });
                    }
                    return;
                }
                
                // Handle commands that don't need special parameter parsing
                switch (commandName)
                {
                    case "voices":
                    case "unlink":
                    case "verify":
                    case "leave":
                    case "changeserver":
                        await _ch.HandleTTSCommandAsync(commandName, textChannel, guildId, userId, username);
                        break;
                    default:
                        await textChannel.SendMessageAsync(new MessageProperties { Content = $"Unknown command: {commandName}" });
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - Error handling slash command: {ex.Message}");
                try
                {
                    if (command.Channel is TextChannel errorTextChannel)
                    {
                        await errorTextChannel.SendMessageAsync(new MessageProperties { Content = "An error occurred while processing the command." });
                    }
                }
                catch { }
            }
        }


    }

}

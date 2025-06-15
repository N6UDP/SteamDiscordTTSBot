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
            _ = Steam.RunSteamTask();

            _ = bool.TryParse(ConfigurationManager.AppSettings.Get("EnableMessageContent"), out bool useMessageContent);

            var intents = useMessageContent ? GatewayIntents.AllNonPrivileged | GatewayIntents.MessageContent : GatewayIntents.AllNonPrivileged;

            var botToken = ConfigurationManager.AppSettings.Get("BotToken");
            
            // Initialize the TTS module
            slashtts = new TTSModule();

            var token = new BotToken(botToken);
            _restClient = new RestClient(token);
            _client = new GatewayClient(token, new GatewayClientConfiguration { Intents = intents });
            _client.Ready += Client_Ready;
            
            _client.InteractionCreate += async interaction =>
            {
                if (interaction is SlashCommandInteraction slashCommand)
                {
                    await SlashCommandHandler(slashCommand);
                }
            };

            var _ch = new CommandHandler(_client, _restClient);
            await _ch.InstallCommandsAsync();

            await _client.StartAsync();

            // Block this task until the program is closed.
            await Task.Delay(-1);
        }

        public async ValueTask Client_Ready(ReadyEventArgs eventArgs)
        {
            Dictionary<string, string> commands = new Dictionary<string, string>()
            {
                {"help", "Gets command help" },
                {"link", "<steamid> [<channel> <voice> <rate>]" },
                {"join", "<channel>" },
                {"leave", "<channel>" },
                {"changevoice", "<voice>" },
                {"changerate", "<-10 .. 10> where 10 is fastest" },
                {"changeserver", "Changes server" }
            };
            
            List<SlashCommandProperties> builtCommands = new List<SlashCommandProperties>();
            foreach ((var command, var desc) in commands)
            {
                var commandData = new SlashCommandProperties(command, desc)
                {
                    Options = new List<ApplicationCommandOptionProperties>
                    {
                        new ApplicationCommandOptionProperties(ApplicationCommandOptionType.String, "args", "Arguments")
                        {
                            Required = false
                        }
                    }
                };
                builtCommands.Add(commandData);
            }
            // TODO: This could be parallel...

            /*
            foreach (var guild in _client.Cache.Guilds.Values)
            {
                foreach (var prop in builtCommands)
                {
                    await _restClient.CreateGuildApplicationCommandAsync(guild.Id, prop);
                }
            }
            */
        }

        private TTSModule slashtts;

        private async Task SlashCommandHandler(SlashCommandInteraction command)
        {
            switch (command.Data.Name)
            {
                case "join":
                    await command.SendResponseAsync(InteractionCallback.Message("Done"));
                    // For now, we'll pass null for the voice channel - will need to implement voice state tracking
                    if (command.Channel is TextChannel textChannel)
                    {
                        await slashtts.JoinChannel(null, textChannel, command.GuildId.Value);
                    }
                    break;
                default:
                    var optionValue = command.Data.Options?.FirstOrDefault()?.Value?.ToString() ?? "no args";
                    await command.SendResponseAsync(InteractionCallback.Message($"You executed {command.Data.Name} with {optionValue} which isn't yet implemented."));
                    break;
            }
        }


    }

}

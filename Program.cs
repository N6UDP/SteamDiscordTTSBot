using Discord;
using Discord.Commands;
using Discord.WebSocket;
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
        private DiscordSocketClient _client;

        private CommandService _cs;

        public static void Main(string[] args)
            => new Program().MainAsync().GetAwaiter().GetResult();

        public async Task MainAsync()
        {
            _ = Steam.RunSteamTask();

            _ = bool.TryParse(ConfigurationManager.AppSettings.Get("EnableMessageContent"), out bool useMessageContent);

            var intents = useMessageContent ? GatewayIntents.AllUnprivileged | GatewayIntents.MessageContent : GatewayIntents.AllUnprivileged;

            var config = new DiscordSocketConfig
            {
                GatewayIntents = intents
            };
            // TODO: make one of these or similar without breaking stuff
            //slashtts = new TTSModule();

            _client = new DiscordSocketClient(config);
            _client.Log += Log;
            _client.Ready += Client_Ready;
            _client.SlashCommandExecuted += SlashCommandHandler;
            await _client.LoginAsync(TokenType.Bot, ConfigurationManager.AppSettings.Get("BotToken"));
            await _client.StartAsync();
            _cs = new CommandService();
            _cs.Log += Log;
            var _ch = new CommandHandler(_client, _cs);
            await _ch.InstallCommandsAsync();

            // Block this task until the program is closed.
            await Task.Delay(-1);
        }

        public async Task Client_Ready()
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
                var builder = new SlashCommandBuilder();
                builder.WithName(command).WithDescription(desc).AddOption("args", ApplicationCommandOptionType.String, "Arguments");
                builtCommands.Add(builder.Build());
            }
            // TODO: This could be parallel...

            /*
            foreach (var guild in _client.Guilds)
            {
                foreach (var prop in builtCommands)
                {
                    await guild.CreateApplicationCommandAsync(prop);
                }
            }
            */
        }

        private TTSModule slashtts;

        private async Task SlashCommandHandler(SocketSlashCommand command)
        {
            switch (command.CommandName)
            {
                case "join":
                    await command.RespondAsync("Done");
                    await slashtts.JoinChannel((command.User as IGuildUser)?.VoiceChannel, command.Channel, command.GuildId.Value);
                    break;
                default:
                    await command.RespondAsync($"You executed {command.Data.Name} with {command.Data.Options.FirstOrDefault().Value} which isn't yet implemented.");
                    break;
            }
            
        }

        private Task Log(LogMessage msg)
        {
            Console.WriteLine($"{DateTime.Now.ToString("s")}:{msg.Source}:{msg.Severity}: {msg.ToString()}{(msg.Exception != null ? msg.Exception : string.Empty)}");
            return Task.CompletedTask;
        }

    }

}

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

namespace DiscordBotTTS
{
    public class Program
    {
        private DiscordSocketClient _client;

        public static void Main(string[] args)
            => new Program().MainAsync().GetAwaiter().GetResult();

        public async Task MainAsync()
        {
            _ = Steam.RunSteamTask();

            _client = new DiscordSocketClient();
            _client.Log += Log;
            await _client.LoginAsync(TokenType.Bot, ConfigurationManager.AppSettings.Get("BotToken"));
            await _client.StartAsync();
            var _cs = new CommandService();
            var _ch = new CommandHandler(_client,_cs);
            await _ch.InstallCommandsAsync();

            // Block this task until the program is closed.
            await Task.Delay(-1);
        }
        private Task Log(LogMessage msg)
        {
            Console.WriteLine(msg.ToString());
            return Task.CompletedTask;
        }

    }

}

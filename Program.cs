using Discord;
using Discord.Audio;
using Discord.Commands;
using Discord.WebSocket;
using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading.Tasks;
using System.Speech;
using System.Speech.Synthesis;
using System.IO;
using System.Speech.AudioFormat;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Configuration;
using System.Collections.Specialized;
using System.Threading;
using System.Linq;

namespace DiscordBotTTS
{
    public class Program
    {
        private DiscordSocketClient _client;

        public static void Main(string[] args)
            => new Program().MainAsync().GetAwaiter().GetResult();

        public async Task MainAsync()
        {
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
    public class CommandHandler
    {
        private readonly DiscordSocketClient _client;
        private readonly CommandService _commands;

        // Retrieve client and CommandService instance via ctor
        public CommandHandler(DiscordSocketClient client, CommandService commands)
        {
            _commands = commands;
            _client = client;
        }

        public async Task InstallCommandsAsync()
        {
            // Hook the MessageReceived event into our command handler
            _client.MessageReceived += HandleCommandAsync;

            // Here we discover all of the command modules in the entry 
            // assembly and load them. Starting from Discord.NET 2.0, a
            // service provider is required to be passed into the
            // module registration method to inject the 
            // required dependencies.
            //
            // If you do not use Dependency Injection, pass null.
            // See Dependency Injection guide for more information.
            await _commands.AddModulesAsync(assembly: Assembly.GetEntryAssembly(),
                                            services: null);
        }

        private async Task HandleCommandAsync(SocketMessage messageParam)
        {
            // Don't process the command if it was a system message
            var message = messageParam as SocketUserMessage;
            if (message == null) return;

            // Create a number to track where the prefix ends and the command begins
            int argPos = 0;

            // Determine if the message is a command based on the prefix and make sure no bots trigger commands
            if (!(message.HasCharPrefix('!', ref argPos) ||
                message.HasMentionPrefix(_client.CurrentUser, ref argPos)) ||
                message.Author.IsBot)
                return;

            // Create a WebSocket-based command context based on the message
            var context = new SocketCommandContext(_client, message);

            // Execute the command with the command context we just
            // created, along with the service provider for precondition checks.
            await _commands.ExecuteAsync(
                context: context,
                argPos: argPos,
                services: null);
        }


    }

    public class InfoModule : ModuleBase<SocketCommandContext>
    {
        // ~say hello world -> hello world
        [Command("say")]
        [Summary("Echoes a message.")]
        public Task SayAsync([Remainder] [Summary("The text to echo")] string echo)
            => ReplyAsync(echo);

        // ReplyAsync is a method on ModuleBase 
    }

    // Create a module with the 'sample' prefix
    [Group("tts")]
    public class SampleModule : ModuleBase<SocketCommandContext>
    {
        static ConcurrentDictionary<ulong, ValueTuple<IAudioClient, IVoiceChannel, AudioOutStream, SemaphoreSlim>> map = new ConcurrentDictionary<ulong, (IAudioClient, IVoiceChannel, AudioOutStream, SemaphoreSlim)>();
        private static Mutex voicewriting = new Mutex();
        /*
        // ~sample square 20 -> 400
        [Command("square")]
        [Summary("Squares a number.")]
        public async Task SquareAsync(
            [Summary("The number to square.")]
        int num)
        {
            // We can also access the channel from the Command Context.
            await Context.Channel.SendMessageAsync($"{num}^2 = {Math.Pow(num, 2)}");
        }

        // ~sample userinfo --> foxbot#0282
        // ~sample userinfo @Khionu --> Khionu#8708
        // ~sample userinfo Khionu#8708 --> Khionu#8708
        // ~sample userinfo Khionu --> Khionu#8708
        // ~sample userinfo 96642168176807936 --> Khionu#8708
        // ~sample whois 96642168176807936 --> Khionu#8708
        */
        [Command("userinfo")]
        [Summary
        ("Returns info about the current user, or the user parameter, if one passed.")]
        [Alias("user", "whois")]
        public async Task UserInfoAsync(
            [Summary("The (optional) user to get info from")]
        SocketUser user = null)
        {
            var userInfo = user ?? Context.Client.CurrentUser;
            await ReplyAsync($"{userInfo.Username}#{userInfo.Discriminator}");
        }

        // The command's Run Mode MUST be set to RunMode.Async, otherwise, being connected to a voice channel will block the gateway thread.
        [Command("join", RunMode = RunMode.Async)]
        public async Task JoinChannel(IVoiceChannel channel = null)
        {
            // Get the audio channel
            channel = channel ?? (Context.User as IGuildUser)?.VoiceChannel;
            if (channel == null) { await Context.Channel.SendMessageAsync("User must be in a voice channel, or a voice channel must be passed as an argument."); return; }

            // For the next step with transmitting audio, you would want to pass this Audio Client in to a service.
            var audioClient = await channel.ConnectAsync();
            await Context.Channel.SendMessageAsync($"Connected to {channel.Name} ({channel.Id})!");
            map[channel.Id] = (audioClient, channel, audioClient.CreatePCMStream(AudioApplication.Mixed), new SemaphoreSlim(1,1));

            //await SendAsync(channel.Id,"Hello world");
            //await SendAsync(channel.Id,"Testing second");
        }
        private async Task SendAsync(ulong channelId, string msg, string voice= "Microsoft David Desktop", string user="", int rate = 0)
        {
            (var audioClient, var channel, var audiostream, var sem) = map[channelId];
            // Create FFmpeg using the previous example
            //using (var ffmpeg = CreateStream(path))
            //using (var output = ffmpeg.StandardOutput.BaseStream)
            var synth = new SpeechSynthesizer();
            var _ms = new MemoryStream();
            try
            {
                synth.SelectVoice(voice);
                synth.Rate = rate;
            } catch
            {

            }
            synth.SetOutputToAudioStream(_ms, new System.Speech.AudioFormat.SpeechAudioFormatInfo(EncodingFormat.Pcm, 48000, 16, 2, 1536000, 2, null));
            synth.Speak(msg);
            synth.SetOutputToNull();
            synth.Dispose();
            _ms.Seek(0, SeekOrigin.Begin);
            sem.Wait();
            await _ms.CopyToAsync(audiostream);
            await audiostream.FlushAsync();
            sem.Release(1);
            await Context.Channel.SendMessageAsync($"{user}:{channel.Name}:{voice}:{msg}");
        }

        [Command("leave", RunMode = RunMode.Async)]
        public async Task LeaveChannel(IVoiceChannel channel = null)
        {
            // Get the audio channel
            channel = channel ?? (Context.User as IGuildUser)?.VoiceChannel;
            if (channel == null) { await Context.Channel.SendMessageAsync("User must be in a voice channel, or a voice channel must be passed as an argument."); return; }

            (var audioClient, var channelvar, var audiostream, var sem) = map[channel.Id];
            await audiostream.DisposeAsync();
            await audioClient.StopAsync();
            audioClient.Dispose();
            await channelvar.DisconnectAsync();
        }
        [Command("link", RunMode = RunMode.Async)]
        public async Task LinkChannel(string user, string steamchatid, string voice = "Microsoft David Desktop", int rate = 0, IVoiceChannel channel = null)
        {
            // Get the audio channel
            channel = channel ?? (Context.User as IGuildUser)?.VoiceChannel;
            if (channel == null) { await Context.Channel.SendMessageAsync("User must be in a voice channel, or a voice channel must be passed as an argument."); return; }

            (var audioClient, var channelvar, var audiostream, var sem) = map[channel.Id];
            await Context.Channel.SendMessageAsync($"Linking with {steamchatid} for user {user} to {channel.Name} with voice {voice}!");
            var wh = new AutoResetEvent(false);
            var fsw = new FileSystemWatcher(ConfigurationManager.AppSettings.Get("LogPath"));
            fsw.Filter = steamchatid;
            fsw.EnableRaisingEvents = true;
            fsw.Changed += (s, e) => wh.Set();

            var fs = new FileStream(ConfigurationManager.AppSettings.Get("LogPath") + steamchatid, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using (var sr = new StreamReader(fs))
            {
                sr.ReadToEnd();
                var s = "";
                while (audiostream.CanWrite)
                {
                    s = sr.ReadLine();
                    if (s != null)
                    {
                        Console.WriteLine(s);
                        if (s.Length < 1000)
                        {
                            if (s.Contains($" {user}: "))
                            {
                                string thisvoice = voice;
                                var array = s.Split(":");
                                int skip = 3;
                                if (array.Length > 4)
                                {
                                    if (array[3].Trim().Equals("change",StringComparison.OrdinalIgnoreCase))
                                    {
                                        string tmpvoice = CheckVoice(array[4]);
                                        if (!string.IsNullOrWhiteSpace(tmpvoice))
                                        {
                                            voice = tmpvoice;
                                            thisvoice = tmpvoice;
                                            skip = 4;
                                        }
                                    } else if (array[3].Trim().Equals("rate",StringComparison.OrdinalIgnoreCase))
                                    {
                                        if(int.TryParse(array[4].Trim(), out rate))
                                        {
                                            if(rate > 10 || rate < -10)
                                            {
                                                await Context.Channel.SendMessageAsync($"{user}:{channel.Name}:{voice}:rate {rate} was invalid rate range (-10 to 10)");
                                                rate = 0;
                                            } else
                                            {
                                                await Context.Channel.SendMessageAsync($"{user}:{channel.Name}:{voice}:rate {rate} was set");
                                            }
                                        } else
                                        {
                                            await Context.Channel.SendMessageAsync($"{user}:{channel.Name}:{voice}:{array[4].Trim()} was invalid rate");
                                        }
                                    }
                                    else
                                    {
                                        string tmpvoice = CheckVoice(array[3]);
                                        if (!string.IsNullOrWhiteSpace(tmpvoice))
                                        {
                                            skip = 4;
                                            thisvoice = tmpvoice;
                                        }
                                    }
                                }

                                await SendAsync(channel.Id, string.Join(":", new ArraySegment<string>(array, skip, array.Length - skip)), thisvoice, user, rate);
                            }
                        } else
                        {
                            await Context.Channel.SendMessageAsync($"{user}:{channel.Name}:{voice}:{s} was TOO LONG!");
                        }
                    }
                    else
                    {
                        wh.WaitOne(1000);
                    }
                }
            }

            wh.Close();
        }
        static List<string> voices = new List<string>();
        public string CheckVoice(string voice)
        {
            voice = voice.Trim();
            if(voices.Count == 0)
            {
                var synth = new SpeechSynthesizer();
                voices = synth.GetInstalledVoices().Select(x => x.VoiceInfo.Name).ToList<string>(); ;
                synth.Dispose();
            }
            if (voices.Contains(voice, StringComparer.OrdinalIgnoreCase))
            {
                return voices.Where(x => x.Equals(voice, StringComparison.OrdinalIgnoreCase)).First<string>();
            }
            else if (voices.Contains("Microsoft " + voice, StringComparer.OrdinalIgnoreCase))
            {
                return voices.Where(x => x.Equals("Microsoft " + voice, StringComparison.OrdinalIgnoreCase)).First<string>();
            } 
            else if (voices.Contains("Microsoft " + voice + " Desktop", StringComparer.OrdinalIgnoreCase))
            {
                return voices.Where(x => x.Equals("Microsoft " + voice + " Desktop", StringComparison.OrdinalIgnoreCase)).First<string>();
            } else
            {
                return "";
            }
        }
    }

}

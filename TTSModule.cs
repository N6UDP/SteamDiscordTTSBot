using Discord;
using Discord.Audio;
using Discord.Commands;
using Discord.WebSocket;
using System;
using System.Threading.Tasks;
using System.Speech.Synthesis;
using System.IO;
using System.Speech.AudioFormat;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Configuration;
using System.Threading;
using System.Linq;
using System.Text.Json;
using Newtonsoft.Json;
using static DiscordBotTTS.TTSModule;
using System.Text.RegularExpressions;
using System.Diagnostics;
using SteamKit2.CDN;

namespace DiscordBotTTS
{
    [Group("tts")]
    public class TTSModule : ModuleBase<SocketCommandContext>
    {
        static ConcurrentDictionary<ulong, ValueTuple<IAudioClient, IVoiceChannel, AudioOutStream, SemaphoreSlim>> map = new ConcurrentDictionary<ulong, (IAudioClient, IVoiceChannel, AudioOutStream, SemaphoreSlim)>();

        static Task dq;

        static Task saver;

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

        private static void Log(string msg, string level = "Info")
        {
            Console.WriteLine($"{DateTime.Now.ToString("s")}:TTS:{level}: {msg}");
        }

        // The command's Run Mode MUST be set to RunMode.Async, otherwise, being connected to a voice channel will block the gateway thread.
        [Command("join", RunMode = RunMode.Async)]
        public async Task JoinChannel(IVoiceChannel channel = null, ISocketMessageChannel textChannel = null, ulong guildId = 0)
        {
            // Get the audio channel
            channel = channel ?? (Context.User as IGuildUser)?.VoiceChannel;
            textChannel = textChannel ?? Context.Channel;
            guildId = guildId != 0 ? guildId : Context.Guild.Id;

            if (channel == null) { await textChannel.SendMessageAsync("User must be in a voice channel, or a voice channel must be passed as an argument."); return; }

            // For the next step with transmitting audio, you would want to pass this Audio Client in to a service.
            if (map.ContainsKey(guildId))
            {
                await LeaveChannel(channel);
            }

            var audioClient = await channel.ConnectAsync();
            await textChannel.SendMessageAsync($"Connected to {channel.Name} ({channel.Id}) for Guild {guildId}!");
            map[guildId] = (audioClient, channel, audioClient.CreatePCMStream(AudioApplication.Mixed), new SemaphoreSlim(1, 1));

            if (dq == null)
            {
                dq = Dequeuer();
            }

            if (saver == null)
            {
                try
                {
                    var userdict = JsonConvert.DeserializeObject<ConcurrentDictionary<ulong, UserPrefs>>(File.ReadAllText("userprefs.json"));
                    foreach (var u in userdict)
                    {
                        userPrefsDict[u.Key] = u.Value;
                    }
                }
                catch
                {
                    Log("Failed to read userprefs.json");
                }

                try
                {
                    var steamdict = JsonConvert.DeserializeObject<ConcurrentDictionary<ulong, ulong>>(File.ReadAllText("steamid.json"));
                    foreach (var s in steamdict)
                    {
                        steamIdtoDiscordId[s.Key] = s.Value;
                    }
                }
                catch
                {
                    Log("Failed to read steamid.json");
                }
                saver = Saver();
            }

            IdtoChannel.TryAdd(guildId, textChannel);
            //await SendAsync(channel.Id,"Hello world");
            //await SendAsync(channel.Id,"Testing second");
        }

        private Process CreateStream(string path)
        {
            return Process.Start(new ProcessStartInfo
            {
                FileName = ConfigurationManager.AppSettings.Get("ffmpeg"),
                Arguments = $"-hide_banner -loglevel panic -i \"{path}\" -ac 2 -f s16le -ar 48000 pipe:1",
                UseShellExecute = false,
                RedirectStandardOutput = true,
            });
        }

        private Process CreateTTSFile(string cleanmsg, out string path)
        {
            path = Path.GetTempFileName();
            return Process.Start(new ProcessStartInfo
            {
                FileName = ConfigurationManager.AppSettings.Get("coquitts"),
                Arguments = $"--text \"{cleanmsg}\" --out_path \"{path}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
            });
        }
        private async Task SendAsync(ulong guildId, string msg, string voice = "Microsoft David Desktop", string user = "", int rate = 0)
        {
            (var audioClient, var channel, var audiostream, var sem) = map[guildId];

            // Shamelessly lifted from https://stackoverflow.com/a/37960256
            var cleanmsg = Regex.Replace(msg, @"(ftp:\/\/|www\.|https?:\/\/){1}[a-zA-Z0-9u00a1-\uffff0-]{2,}\.[a-zA-Z0-9u00a1-\uffff0-]{2,}(\S*)", "URL replaced");
            if (msg != cleanmsg)
            {
                // So when we modified the message we log that we did so in the discord log.. this could probably be less ugly
                msg = msg + "; cleaned: " + cleanmsg;
            }

            var textMsg = await IdtoChannel[guildId].SendMessageAsync($"{user}:{channel.Name}:{voice}:{msg}");

            using (var _ms = new MemoryStream())
            {
                if (voice.StartsWith("CoQui"))
                {
                    using (var coquitts = CreateTTSFile(cleanmsg, out string coquittspath))
                    {
                        await coquitts.WaitForExitAsync();
                        using (var ffmpeg = CreateStream(coquittspath))
                        {
                            using (var output = ffmpeg.StandardOutput.BaseStream)
                            {
                                await output.CopyToAsync(_ms);
                            }
                        }
                        if (File.Exists(coquittspath))
                        {
                            File.Delete(coquittspath);
                        }
                    }
                }
                else
                {
                    using (var synth = new SpeechSynthesizer())
                    {
                        try
                        {
                            synth.SelectVoice(voice);
                            synth.Rate = rate;
                        }
                        catch
                        {
                            Log("Failed to set voice and rate", "Warning");
                        }
                        synth.SetOutputToAudioStream(_ms, new System.Speech.AudioFormat.SpeechAudioFormatInfo(EncodingFormat.Pcm, 48000, 16, 2, 1536000, 2, null));
                        synth.Speak(cleanmsg);
                        synth.SetOutputToNull();
                    }
                }
                _ms.Seek(0, SeekOrigin.Begin);
                sem.Wait();
                try
                {
                    await _ms.CopyToAsync(audiostream, new CancellationTokenSource(40000).Token);
                    await audiostream.FlushAsync(new CancellationTokenSource(40000).Token);
                    await textMsg.AddReactionAsync(new Emoji("\U00002705"));
                }
                catch (Exception e)
                {
                    await IdtoChannel[guildId].SendMessageAsync($"{user}:{channel.Name}:{voice}:{msg} FAILED TO SEND");
                    Log($"{user}:{channel.Name}:{voice}:{msg} FAILED TO SEND", "Error");
                    Log(e.ToString(), "Error");
                    try
                    {
                        await IdtoChannel[guildId].SendMessageAsync($"Leaving {channel.Name} due to failure.");
                        await LeaveChannel(channel);
                    }
                    catch
                    {
                        await IdtoChannel[guildId].SendMessageAsync($"Failed to leave {channel.Name}.");
                    }
                }
                finally
                {
                    sem.Release(1);
                }
                try
                {
                    await audiostream.FlushAsync(new CancellationTokenSource(40000).Token);
                }
                catch
                {
                    await IdtoChannel[guildId].SendMessageAsync($"Failed to second flush {channel.Name}.");
                }
            }
        }

        [Command("leave", RunMode = RunMode.Async)]
        public async Task LeaveChannel(IVoiceChannel channel = null)
        {
            // Get the audio channel
            channel = channel ?? (Context.User as IGuildUser)?.VoiceChannel;
            if (channel == null) { await Context.Channel.SendMessageAsync("User must be in a voice channel, or a voice channel must be passed as an argument."); return; }
            if (map.ContainsKey(Context.Guild.Id))
            {
                (var audioClient, var channelvar, var audiostream, var sem) = map[Context.Guild.Id];
                try
                {
                    await audiostream.DisposeAsync();
                    await audioClient.StopAsync();
                    audioClient.Dispose();
                    await channelvar.DisconnectAsync();
                }
                catch
                {
                    await Context.Channel.SendMessageAsync($"Issues leaving channel {channelvar.Name}.");
                }
                finally
                {
                    map.Remove(Context.Guild.Id, out _);
                }
            }
        }

        static ConcurrentDictionary<ulong, UserPrefs> userPrefsDict = new ConcurrentDictionary<ulong, UserPrefs>();
        static ConcurrentDictionary<ulong, ulong> steamIdtoDiscordId = new ConcurrentDictionary<ulong, ulong>();
        static ConcurrentDictionary<ulong, ISocketMessageChannel> IdtoChannel = new ConcurrentDictionary<ulong, ISocketMessageChannel>();

        public class UserPrefs
        {
            public string Voice { get; set; }
            public int Rate { get; set; }

            public ulong UserId { get; set; }

            public ulong SteamId { get; set; }

            public ulong GuildId { get; set; }

            public string Name { get; set; }
        }

        [Command("changeserver", RunMode = RunMode.Async)]
        public async Task ChangeServer()
        {
            var userPrefs = userPrefsDict.GetValueOrDefault(Context.User.Id, null);

            if (userPrefs != null)
            {
                userPrefs.GuildId = Context.Guild.Id;
                await Context.Channel.SendMessageAsync($"{Context.User.Username} server changed.");
            }
        }

        [Command("changerate", RunMode = RunMode.Async)]
        public async Task ChangeRate(int rate = 0)
        {
            var userPrefs = userPrefsDict.GetValueOrDefault(Context.User.Id, null);

            if (userPrefs != null)
            {
                if (rate > 10 || rate < -10)
                {
                    await Context.Channel.SendMessageAsync($"{Context.User.Username} rate {rate} was invalid rate range (-10 to 10)");
                }
                else
                {
                    userPrefs.Rate = rate;
                    await Context.Channel.SendMessageAsync($"{Context.User.Username} rate {rate} changed.");
                }
            }
        }

        [Command("changevoice", RunMode = RunMode.Async)]
        public async Task ChangeVoice(string voice = "Microsoft David Desktop")
        {
            var userPrefs = userPrefsDict.GetValueOrDefault(Context.User.Id, null);

            if (userPrefs != null)
            {
                string tmpvoice = CheckVoice(voice);
                if (!string.IsNullOrWhiteSpace(tmpvoice))
                {
                    userPrefs.Voice = tmpvoice;
                    await Context.Channel.SendMessageAsync($"{Context.User.Username}:voice {tmpvoice} changed.");
                }
                else
                {
                    await Context.Channel.SendMessageAsync($"{Context.User.Username}:voice {voice} invalid.");
                }
            }
            else
            {
                await Context.Channel.SendMessageAsync($"{Context.User.Username}:No user prefs, please use link first.");
            }
        }


        [Command("link", RunMode = RunMode.Async)]
        public async Task LinkChannel(ulong steamchatid = 0, IVoiceChannel channel = null, string voice = "No Voice", int rate = -11)
        {
            channel = channel ?? (Context.User as IGuildUser)?.VoiceChannel;
            if (channel == null) { await Context.Channel.SendMessageAsync("User must be in a voice channel, or a voice channel must be passed as an argument."); return; }

            var userPrefs = userPrefsDict.GetOrAdd(Context.User.Id, new UserPrefs { UserId = Context.User.Id, Rate = rate == -11 ? 0 : rate, Voice = voice == "No Voice" ? "Microsoft David" : voice, GuildId = Context.Guild.Id, SteamId = steamchatid, Name = Context.User.Username });

            if (steamchatid != 0)
            {
                steamIdtoDiscordId.GetOrAdd(steamchatid, Context.User.Id);
                userPrefs.SteamId = steamchatid;
            }

            if (userPrefs.SteamId == 0)
            {
                await Context.Channel.SendMessageAsync($"WARNING {Context.User.Username} has missing steam ID!!");
            }

            if (rate != -11)
            {
                if (rate > 10 || rate < -10)
                {
                    await Context.Channel.SendMessageAsync($"{Context.User.Username} rate {rate} was invalid rate range (-10 to 10)");
                }
                else
                {
                    userPrefs.Rate = rate;
                    await Context.Channel.SendMessageAsync($"{Context.User.Username} rate {rate} changed.");
                }
            }

            if (voice != "No Voice")
            {
                string tmpvoice = CheckVoice(voice);
                if (!string.IsNullOrWhiteSpace(tmpvoice))
                {
                    userPrefs.Voice = tmpvoice;
                    await Context.Channel.SendMessageAsync($"{Context.User.Username}:voice {tmpvoice} changed.");
                }
                else
                {
                    await Context.Channel.SendMessageAsync($"{Context.User.Username}:voice {voice} invalid.");
                }
            }

            userPrefs.GuildId = Context.Guild.Id;

            IdtoChannel.TryAdd(Context.Guild.Id, Context.Channel);

            await Context.Channel.SendMessageAsync($"Linking with {userPrefs.SteamId} for user {Context.User.Username} to {channel.Name} with voice {userPrefs.Voice}!");
        }

        [Command("help", RunMode = RunMode.Async)]
        public async Task Help()
        {
            await Context.Channel.SendMessageAsync("\nHelp:\n" +
                "!tts link <steamid> [<channel> <voice> <rate>]\n" +
                "!tts join <channel>\n" +
                "!tts leave <channel>\n" +
                "!tts changevoice <voice>\n" +
                "!tts changerate <-10 .. 10> where 10 is fastest\n" +
                "!tts changeserver\n" +
                "Or use @botname tts <command>");
        }

        public async Task Dequeuer()
        {
            while (true)
            {
                Message result;
                if (Steam.Queue.TryDequeue(out result))
                {
                    _ = Dequeue(result);
                }
                else
                {
                    await Task.Delay(100);
                }
            }
        }

        public void WriteAllTextAtomicWithBackup(string path, string data)
        {
            File.WriteAllText(path + ".tmp", data);
            if (File.Exists(path))
            {
                File.Move(path, path + ".bak", true);
            }
            File.Move(path + ".tmp", path);
        }

        public async Task Saver()
        {
            string prevuserprefs = string.Empty;
            string prevsteamid = string.Empty;

            while (true)
            {
                var userprefs = JsonConvert.SerializeObject(userPrefsDict);
                var steamid = JsonConvert.SerializeObject(steamIdtoDiscordId);

                if (userprefs != prevuserprefs)
                {
                    WriteAllTextAtomicWithBackup("userprefs.json", userprefs);
                }
                if (steamid != prevsteamid)
                {
                    WriteAllTextAtomicWithBackup("steamid.json", steamid);
                }
                await Task.Delay(1000 * 10);
            }
        }

        public async Task Dequeue(Message message)
        {
            ulong discord = 0;
            steamIdtoDiscordId.TryGetValue(message.UserId, out discord);
            if (discord == 0) { return; }
            UserPrefs userPrefs;
            userPrefsDict.TryGetValue(discord, out userPrefs);
            if (userPrefs == null) { return; }
            if (map.ContainsKey(userPrefs.GuildId))
            {
                string voice = userPrefs.Voice;
                string msg = message.Msg;
                var array = msg.Split(":");
                var possiblevoice = CheckVoice(array[0]);
                if (!string.IsNullOrWhiteSpace(possiblevoice))
                {
                    voice = possiblevoice;
                    msg = string.Join(":", new ArraySegment<string>(array, 1, array.Length - 1));
                }
                await SendAsync(userPrefs.GuildId, msg, voice, userPrefs.Name, userPrefs.Rate);
            }
        }

        static List<string> voices = new List<string>();
        public string CheckVoice(string voice)
        {
            voice = voice.Trim();
            if (voice == "CoQui")
            {
                return "CoQui";
            }
            if (voices.Count == 0)
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
            }
            else
            {
                return "";
            }
        }
    }

}

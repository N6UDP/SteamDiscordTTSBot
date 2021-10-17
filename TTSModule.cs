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

        // The command's Run Mode MUST be set to RunMode.Async, otherwise, being connected to a voice channel will block the gateway thread.
        [Command("join", RunMode = RunMode.Async)]
        public async Task JoinChannel(IVoiceChannel channel = null)
        {
            // Get the audio channel
            channel = channel ?? (Context.User as IGuildUser)?.VoiceChannel;
            if (channel == null) { await Context.Channel.SendMessageAsync("User must be in a voice channel, or a voice channel must be passed as an argument."); return; }

            // For the next step with transmitting audio, you would want to pass this Audio Client in to a service.
            if (map.ContainsKey(channel.Id))
            {
                await LeaveChannel(channel);
            }
            var audioClient = await channel.ConnectAsync();
            await Context.Channel.SendMessageAsync($"Connected to {channel.Name} ({channel.Id})!");
            map[channel.Id] = (audioClient, channel, audioClient.CreatePCMStream(AudioApplication.Mixed), new SemaphoreSlim(1,1));

            if(dq == null)
            {
                dq = Dequeuer();
            }

            if(saver == null)
            {
                try
                {
                    var steamdict = JsonConvert.DeserializeObject<ConcurrentDictionary<ulong, ulong>>(File.ReadAllText("steamid.json"));
                    var userdict = JsonConvert.DeserializeObject<ConcurrentDictionary<ulong, UserPrefs>>(File.ReadAllText("userprefs.json"));

                    foreach (var u in userdict)
                    {
                        userPrefsDict[u.Key] = u.Value;
                    }
                    foreach (var s in steamdict)
                    {
                        steamIdtoDiscordId[s.Key] = s.Value;
                    }
                }
                catch
                {
                    Console.WriteLine("Failed to read persisted data");
                }
                saver = Saver();
            }

            IdtoChannel.TryAdd(Context.Channel.Id, Context.Channel);
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
            try
            {
                await _ms.CopyToAsync(audiostream);
                await audiostream.FlushAsync();
            }
            catch {
                await Context.Channel.SendMessageAsync($"{user}:{channel.Name}:{voice}:{msg} FAILED TO SEND");
            }
            finally
            {
                sem.Release(1);
            }
            await Context.Channel.SendMessageAsync($"{user}:{channel.Name}:{voice}:{msg}");
        }

        [Command("leave", RunMode = RunMode.Async)]
        public async Task LeaveChannel(IVoiceChannel channel = null)
        {
            // Get the audio channel
            channel = channel ?? (Context.User as IGuildUser)?.VoiceChannel;
            if (channel == null) { await Context.Channel.SendMessageAsync("User must be in a voice channel, or a voice channel must be passed as an argument."); return; }
            if (map.ContainsKey(channel.Id))
            {
                (var audioClient, var channelvar, var audiostream, var sem) = map[channel.Id];
                await audiostream.DisposeAsync();
                await audioClient.StopAsync();
                audioClient.Dispose();
                await channelvar.DisconnectAsync();
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

            public ulong VoiceChannelId { get; set; }

            public ulong TextChannelId { get; set; }

            public string Name { get; set; }
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

            var userPrefs = userPrefsDict.GetOrAdd(Context.User.Id, new UserPrefs { UserId = Context.User.Id, Rate = rate == -11 ? 0 : rate, Voice = voice == "No Voice" ? "Microsoft David" : voice, TextChannelId = Context.Channel.Id, VoiceChannelId = channel.Id, SteamId = steamchatid, Name = Context.User.Username });

            if (steamchatid != 0) {
                steamIdtoDiscordId.GetOrAdd(steamchatid, Context.User.Id);
                userPrefs.SteamId = steamchatid;
            }

            if (userPrefs.SteamId == 0)
            {
                await Context.Channel.SendMessageAsync($"WARNING {Context.User.Username} has missing steam ID!!");
            }

            if (rate != -11) {
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

            if (voice != "No Voice") {
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

            userPrefs.VoiceChannelId = channel.Id;
            userPrefs.TextChannelId = Context.Channel.Id;

            IdtoChannel.TryAdd(Context.Channel.Id, Context.Channel);

            await Context.Channel.SendMessageAsync($"Linking with {userPrefs.SteamId} for user {Context.User.Username} to {channel.Name} with voice {userPrefs.Voice}!");
        }

        public async Task Dequeuer()
        {
            while (true)
            {
                Message result;
                if (Steam.Queue.TryDequeue(out result)) {
                    _ = Dequeue(result);
                } else
                {
                    await Task.Delay(100);
                }
            }
        }

        public async Task Saver()
        {
            while (true)
            {
                File.WriteAllText("userprefs.json", JsonConvert.SerializeObject(userPrefsDict));
                File.WriteAllText("steamid.json", JsonConvert.SerializeObject(steamIdtoDiscordId));
                await Task.Delay(1000*60);
            }
        }

        public async Task Dequeue(Message message)
        {
            ulong discord = 0;
            steamIdtoDiscordId.TryGetValue(message.UserId, out discord);
            if(discord == 0) { return; }
            UserPrefs userPrefs;
            userPrefsDict.TryGetValue(discord, out userPrefs);
            if(userPrefs == null) { return; }

            if (map.ContainsKey(userPrefs.VoiceChannelId)) {
                string voice = userPrefs.Voice;
                string msg = message.Msg;
                var array = msg.Split(":");
                var possiblevoice = CheckVoice(array[0]);
                if (!string.IsNullOrWhiteSpace(possiblevoice)){
                    voice = possiblevoice;
                    msg = string.Join(":", new ArraySegment<string>(array, 1, array.Length - 1));
                }
                await IdtoChannel[userPrefs.TextChannelId].SendMessageAsync($"{userPrefs.Name}: {message.Msg}");
                await SendAsync(userPrefs.VoiceChannelId, message.Msg , voice, userPrefs.Name, userPrefs.Rate);
            }
            
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

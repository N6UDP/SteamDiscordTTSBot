using NetCord;
using NetCord.Gateway;
using NetCord.Gateway.Voice;
using NetCord.Rest;
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
    public class TTSModule
    {
        static ConcurrentDictionary<ulong, ValueTuple<VoiceClient, VoiceGuildChannel, Stream, SemaphoreSlim>> map = new ConcurrentDictionary<ulong, (VoiceClient, VoiceGuildChannel, Stream, SemaphoreSlim)>();

        static Task dq;

        static Task saver;

        public async Task UserInfoAsync(User user = null, TextChannel channel = null)
        {
            // This will need to be adapted for NetCord when we have proper context
            // var userInfo = user ?? /* current user */;
            // await channel.SendMessageAsync($"{userInfo.Username}");
        }

        private static void Log(string msg, string level = "Info")
        {
            Console.WriteLine($"{DateTime.Now.ToString("s")}:TTS:{level}: {msg}");
        }

        // The command's Run Mode MUST be set to RunMode.Async, otherwise, being connected to a voice channel will block the gateway thread.
        public async Task JoinChannel(object channel = null, TextChannel textChannel = null, ulong guildId = 0, GatewayClient gatewayClient = null)
        {
            // Validate inputs
            if (channel == null && textChannel != null)
            {
                await textChannel.SendMessageAsync(new MessageProperties { Content = "User must be in a voice channel, or a voice channel must be passed as an argument." });
                return;
            }

            if (gatewayClient == null)
            {
                if (textChannel != null)
                    await textChannel.SendMessageAsync(new MessageProperties { Content = "Gateway client is required for voice connection." });
                return;
            }

            // Cast to proper voice channel type
            if (channel is not VoiceGuildChannel voiceChannel)
            {
                if (textChannel != null)
                    await textChannel.SendMessageAsync(new MessageProperties { Content = "Invalid voice channel provided." });
                return;
            }

            // For the next step with transmitting audio, you would want to pass this Audio Client in to a service.
            if (map.ContainsKey(guildId))
            {
                await LeaveChannel(channel, textChannel, guildId);
            }

            try
            {
                // Create NetCord voice connection
                Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - Attempting to join voice channel: {voiceChannel.Name} (ID: {voiceChannel.Id})");
                
                var voiceClient = await gatewayClient.JoinVoiceChannelAsync(guildId, voiceChannel.Id);
                
                Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - Voice client created, starting connection...");
                
                // Connect to voice
                await voiceClient.StartAsync();
                
                Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - Voice client connected, entering speaking state...");
                
                // Enter speaking state to be able to send voice
                await voiceClient.EnterSpeakingStateAsync(new SpeakingProperties(SpeakingFlags.Microphone));
                
                // Create output stream for sending voice
                var outputStream = voiceClient.CreateOutputStream();
                
                Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - Voice connection established successfully");
                
                if (textChannel != null)
                {
                    await textChannel.SendMessageAsync(new MessageProperties { Content = $"Connected to voice channel: {voiceChannel.Name}" });
                }
                
                // Store the voice connection
                map[guildId] = (voiceClient, voiceChannel, outputStream, new SemaphoreSlim(1, 1));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - Error connecting to voice channel: {ex.Message}");
                if (textChannel != null)
                {
                    await textChannel.SendMessageAsync(new MessageProperties { Content = $"Failed to connect to voice channel: {ex.Message}" });
                }
                return;
            }

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
            if (!map.TryGetValue(guildId, out var mapData))
                return;

            (var voiceClient, var voiceChannel, var outputStream, var sem) = mapData;

            // Shamelessly lifted from https://stackoverflow.com/a/37960256
            var cleanmsg = Regex.Replace(msg, @"(ftp:\/\/|www\.|https?:\/\/){1}[a-zA-Z0-9u00a1-\uffff0-]{2,}\.[a-zA-Z0-9u00a1-\uffff0-]{2,}(\S*)", "URL replaced");
            if (msg != cleanmsg)
            {
                // So when we modified the message we log that we did so in the discord log.. this could probably be less ugly
                msg = msg + "; cleaned: " + cleanmsg;
            }

            if (!IdtoChannel.TryGetValue(guildId, out var textChannel))
                return;

            var textMsg = await textChannel.SendMessageAsync(new MessageProperties { Content = $"{user}:{voiceChannel?.Name ?? "voice_channel"}:{voice}:{msg}" });

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
                    Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - Streaming TTS audio to voice channel: {cleanmsg}");
                    
                    // Create an Opus encoding stream to convert PCM to Opus format
                    // NetCord expects Opus-encoded frames, not raw PCM
                    using (var opusStream = new OpusEncodeStream(outputStream, PcmFormat.Short, VoiceChannels.Stereo, OpusApplication.Audio))
                    {
                        await _ms.CopyToAsync(opusStream);
                        await opusStream.FlushAsync();
                    }
                    
                    Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - TTS audio streaming completed successfully");
                    
                    Log($"TTS sent to voice channel: {user}:{voiceChannel?.Name}:{voice}:{msg}", "Info");
                }
                catch (Exception e)
                {
                    Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - Error streaming TTS audio: {e.Message}");
                    await textChannel.SendMessageAsync(new MessageProperties { Content = $"{user}:{voiceChannel?.Name}:{voice}:{msg} FAILED TO SEND" });
                    Log($"{user}:{voiceChannel?.Name}:{voice}:{msg} FAILED TO SEND", "Error");
                    Log(e.ToString(), "Error");
                    try
                    {
                        await textChannel.SendMessageAsync(new MessageProperties { Content = "Leaving voice channel due to failure." });
                        await LeaveChannel(voiceChannel, textChannel, guildId);
                    }
                    catch
                    {
                        await textChannel.SendMessageAsync(new MessageProperties { Content = "Failed to leave voice channel." });
                    }
                }
                finally
                {
                    sem.Release(1);
                }
            }
        }

        public async Task LeaveChannel(object channel = null, TextChannel textChannel = null, ulong guildId = 0)
        {
            if (map.ContainsKey(guildId))
            {
                (var voiceClient, var voiceChannel, var outputStream, var sem) = map[guildId];
                try
                {
                    Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - Disconnecting from voice channel: {voiceChannel?.Name}");
                    
                    // Dispose the output stream first
                    if (outputStream != null)
                    {
                        await outputStream.FlushAsync();
                        outputStream.Dispose();
                    }
                    
                    // Dispose the voice client (it will automatically stop)
                    if (voiceClient != null)
                    {
                        voiceClient.Dispose();
                    }
                    
                    // Dispose the semaphore
                    sem?.Dispose();
                    
                    Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - Successfully disconnected from voice channel");
                    
                    if (textChannel != null)
                    {
                        await textChannel.SendMessageAsync(new MessageProperties { Content = $"Left voice channel: {voiceChannel?.Name ?? "Unknown"}" });
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - Error leaving voice channel: {ex.Message}");
                    if (textChannel != null)
                    {
                        await textChannel.SendMessageAsync(new MessageProperties { Content = "Issues leaving voice channel." });
                    }
                }
                finally
                {
                    map.Remove(guildId, out _);
                }
            }
        }

        static ConcurrentDictionary<ulong, UserPrefs> userPrefsDict = new ConcurrentDictionary<ulong, UserPrefs>();
        static ConcurrentDictionary<ulong, ulong> steamIdtoDiscordId = new ConcurrentDictionary<ulong, ulong>();
        static ConcurrentDictionary<ulong, TextChannel> IdtoChannel = new ConcurrentDictionary<ulong, TextChannel>();

        public class UserPrefs
        {
            public string Voice { get; set; }
            public int Rate { get; set; }

            public ulong UserId { get; set; }

            public ulong SteamId { get; set; }

            public ulong GuildId { get; set; }

            public string Name { get; set; }
        }

        public async Task ChangeServer(ulong userId, ulong guildId, TextChannel channel, string username)
        {
            var userPrefs = userPrefsDict.GetValueOrDefault(userId, null);

            if (userPrefs != null)
            {
                userPrefs.GuildId = guildId;
                await channel.SendMessageAsync(new MessageProperties { Content = $"{username} server changed." });
            }
        }

        public async Task ChangeRate(int rate, ulong userId, TextChannel channel, string username)
        {
            var userPrefs = userPrefsDict.GetValueOrDefault(userId, null);

            if (userPrefs != null)
            {
                if (rate > 10 || rate < -10)
                {
                    await channel.SendMessageAsync(new MessageProperties { Content = $"{username} rate {rate} was invalid rate range (-10 to 10)" });
                }
                else
                {
                    userPrefs.Rate = rate;
                    await channel.SendMessageAsync(new MessageProperties { Content = $"{username} rate {rate} changed." });
                }
            }
        }

        public async Task ChangeVoice(string voice, ulong userId, TextChannel channel, string username)
        {
            var userPrefs = userPrefsDict.GetValueOrDefault(userId, null);

            if (userPrefs != null)
            {
                string tmpvoice = CheckVoice(voice);
                if (!string.IsNullOrWhiteSpace(tmpvoice))
                {
                    userPrefs.Voice = tmpvoice;
                    await channel.SendMessageAsync(new MessageProperties { Content = $"{username}:voice {tmpvoice} changed." });
                }
                else
                {
                    await channel.SendMessageAsync(new MessageProperties { Content = $"{username}:voice {voice} invalid." });
                }
            }
            else
            {
                await channel.SendMessageAsync(new MessageProperties { Content = $"{username}:No user prefs, please use link first." });
            }
        }


        public async Task LinkChannel(ulong steamchatid, object voiceChannel, string voice, int rate, ulong userId, ulong guildId, TextChannel textChannel, string username)
        {
            if (voiceChannel == null && textChannel != null)
            {
                await textChannel.SendMessageAsync(new MessageProperties { Content = "User must be in a voice channel, or a voice channel must be passed as an argument." });
                return;
            }

            var userPrefs = userPrefsDict.GetOrAdd(userId, new UserPrefs { UserId = userId, Rate = rate == -11 ? 0 : rate, Voice = voice == "No Voice" ? "Microsoft David" : voice, GuildId = guildId, SteamId = steamchatid, Name = username });

            if (steamchatid != 0)
            {
                steamIdtoDiscordId.GetOrAdd(steamchatid, userId);
                userPrefs.SteamId = steamchatid;
            }

            if (userPrefs.SteamId == 0 && textChannel != null)
            {
                await textChannel.SendMessageAsync(new MessageProperties { Content = $"WARNING {username} has missing steam ID!!" });
            }

            if (rate != -11)
            {
                if (rate > 10 || rate < -10)
                {
                    if (textChannel != null)
                        await textChannel.SendMessageAsync(new MessageProperties { Content = $"{username} rate {rate} was invalid rate range (-10 to 10)" });
                }
                else
                {
                    userPrefs.Rate = rate;
                    if (textChannel != null)
                        await textChannel.SendMessageAsync(new MessageProperties { Content = $"{username} rate {rate} changed." });
                }
            }

            if (voice != "No Voice")
            {
                string tmpvoice = CheckVoice(voice);
                if (!string.IsNullOrWhiteSpace(tmpvoice))
                {
                    userPrefs.Voice = tmpvoice;
                    if (textChannel != null)
                        await textChannel.SendMessageAsync(new MessageProperties { Content = $"{username}:voice {tmpvoice} changed." });
                }
                else
                {
                    if (textChannel != null)
                        await textChannel.SendMessageAsync(new MessageProperties { Content = $"{username}:voice {voice} invalid." });
                }
            }

            userPrefs.GuildId = guildId;

            if (textChannel != null)
            {
                IdtoChannel.TryAdd(guildId, textChannel);
                await textChannel.SendMessageAsync(new MessageProperties { Content = $"Linking with {userPrefs.SteamId} for user {username} to voice channel with voice {userPrefs.Voice}!" });
            }
        }

        public async Task Help(TextChannel channel)
        {
            await channel.SendMessageAsync(new MessageProperties
            {
                Content = "\nHelp:\n" +
                    "!tts link <steamid> [<channel> <voice> <rate>]\n" +
                    "!tts join <channel>\n" +
                    "!tts leave <channel>\n" +
                    "!tts changevoice <voice>\n" +
                    "!tts changerate <-10 .. 10> where 10 is fastest\n" +
                    "!tts changeserver\n" +
                    "Or use @botname tts <command>"
            });
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
                    msg = string.Join(":", (IEnumerable<string>)new ArraySegment<string>(array, 1, array.Length - 1));
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

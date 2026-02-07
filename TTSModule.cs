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
using System.Net.Http;
using System.Text;

namespace DiscordBotTTS
{
    public class TTSModule
    {
        static ConcurrentDictionary<ulong, ValueTuple<VoiceClient, VoiceGuildChannel, Stream, SemaphoreSlim>> map = new ConcurrentDictionary<ulong, (VoiceClient, VoiceGuildChannel, Stream, SemaphoreSlim)>();

        static Task dq;

        static Task saver;
        
        private RestClient _restClient;

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

        public void SetRestClient(RestClient restClient)
        {
            _restClient = restClient;
        }

        private static GatewayClient _gatewayClient; // Store gateway client for proper disconnection
        private static HttpClient _httpClient = new HttpClient() { Timeout = TimeSpan.FromSeconds(30) };
        private static Process _coquiServerProcess;
        private static bool _serverStarted = false;
        private static readonly object _serverLock = new object();
        
        // Load Coqui voice configuration from app.config
        private static void LoadCoquiVoiceConfiguration()
        {
            if (coquiVoiceModels.Count == 0) // Only load once
            {
                try
                {
                    var coquiMode = ConfigurationManager.AppSettings.Get("CoquiMode")?.ToLower() ?? "exe";
                    
                    if (coquiMode == "exe")
                    {
                        // Load voice model mappings for exe mode (legacy)
                        var modelsConfig = ConfigurationManager.AppSettings.Get("CoquiVoiceModels");
                        if (!string.IsNullOrEmpty(modelsConfig))
                        {
                            var pairs = modelsConfig.Split(';');
                            foreach (var pair in pairs)
                            {
                                var parts = pair.Split(':');
                                if (parts.Length == 2)
                                {
                                    coquiVoiceModels[parts[0].Trim()] = parts[1].Trim();
                                }
                            }
                        }
                        
                        Log($"Loaded {coquiVoiceModels.Count} Coqui voice models for exe mode");
                    }
                    // Server mode is now handled in InitializeCoquiServerAsync with parallel discovery
                }
                catch (Exception ex)
                {
                    Log($"Error loading Coqui voice configuration: {ex.Message}", "Warning");
                }
            }
        }
        
        // Initialize Coqui TTS server on application startup
        public static async Task InitializeCoquiServerAsync()
        {
            var coquiMode = ConfigurationManager.AppSettings.Get("CoquiMode")?.ToLower() ?? "exe";
            if (coquiMode == "server")
            {
                Log("Initializing Coqui TTS server at startup...");
                await EnsureCoquiServerStarted();
                
                // Start speaker discovery in parallel with server startup
                var speakerDiscoveryTask = Task.Run(async () => await DiscoverCoquiSpeakers());
                
                // Wait for server to be ready with retry mechanism
                await WaitForServerReady();
                
                // Wait for speaker discovery to complete and load configuration
                Log("Waiting for speaker discovery to complete...");
                var discoveredSpeakers = await speakerDiscoveryTask;
                
                // Load discovered speakers into voice models
                foreach (var speaker in discoveredSpeakers)
                {
                    coquiVoiceModels[$"CoQui_{speaker}"] = speaker;
                }
                
                Log($"Loaded {coquiVoiceModels.Count} Coqui speakers for server mode (discovered in parallel)");
            }
            else
            {
                Log("Coqui TTS server mode disabled - using exe mode");
            }
        }
        
        // Discover available speakers for the loaded model
        private static async Task<List<string>> DiscoverCoquiSpeakers()
        {
            var speakers = new List<string>();
            
            try
            {
                var coquiPath = ConfigurationManager.AppSettings.Get("coquitts") ?? "tts";
                var model = ConfigurationManager.AppSettings.Get("coqui_server_model") ?? "tts_models/en/ljspeech/tacotron2-DDC";
                
                Log($"Discovering speakers for model: {model}");
                
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = coquiPath,
                        Arguments = $"--model_name {model} --list_speaker_idx",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };
                
                process.Start();
                var output = await process.StandardOutput.ReadToEndAsync();
                var error = await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();
                
                if (!string.IsNullOrEmpty(error))
                {
                    Log($"Speaker discovery stderr: {error}", "Warning");
                    
                    // Extract speaker list from stderr (due to logging issue)
                    var match = System.Text.RegularExpressions.Regex.Match(error, @"Message: \[([^\]]+)\]");
                    if (match.Success)
                    {
                        var speakerListText = match.Groups[1].Value;
                        var speakerMatches = System.Text.RegularExpressions.Regex.Matches(speakerListText, @"'([^']+)'");
                        
                        foreach (System.Text.RegularExpressions.Match speakerMatch in speakerMatches)
                        {
                            var speakerName = speakerMatch.Groups[1].Value;
                            if (!string.IsNullOrEmpty(speakerName))
                            {
                                speakers.Add(speakerName);
                            }
                        }
                    }
                }
                
                if (!string.IsNullOrEmpty(output))
                {
                    Log($"Speaker discovery output: {output}");
                    
                    // Parse speaker list from output (fallback)
                    var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    foreach (var line in lines)
                    {
                        var trimmed = line.Trim();
                        if (!string.IsNullOrEmpty(trimmed) &&
                            !trimmed.Contains("INFO") &&
                            !trimmed.Contains("WARNING") &&
                            !trimmed.Contains("ERROR") &&
                            !trimmed.Contains("Loading") &&
                            !trimmed.Contains("Setting up") &&
                            !trimmed.Contains("downloaded") &&
                            !trimmed.Contains("Using model"))
                        {
                            speakers.Add(trimmed);
                        }
                    }
                }
                
                Log($"Discovered {speakers.Count} speakers: {string.Join(", ", speakers)}");
            }
            catch (Exception ex)
            {
                Log($"Error discovering speakers: {ex.Message}", "Warning");
                
                // Fallback to config if discovery fails
                var fallbackConfig = ConfigurationManager.AppSettings.Get("CoquiSpeakers");
                if (!string.IsNullOrEmpty(fallbackConfig))
                {
                    speakers.AddRange(fallbackConfig.Split(',').Select(s => s.Trim()));
                    Log($"Using fallback speakers from config: {string.Join(", ", speakers)}");
                }
            }
            
            return speakers;
        }
        
        // Wait for Coqui TTS server to be ready with retry mechanism
        private static async Task WaitForServerReady()
        {
            var port = ConfigurationManager.AppSettings.Get("coqui_server_port") ?? "5002";
            var startupTimeout = int.Parse(ConfigurationManager.AppSettings.Get("coqui_server_startup_timeout") ?? "60");
            var retryInterval = int.Parse(ConfigurationManager.AppSettings.Get("coqui_server_retry_interval") ?? "2");
            
            var maxRetries = startupTimeout / retryInterval;
            var attempt = 0;
            
            Log($"Waiting for Coqui TTS server to be ready (timeout: {startupTimeout}s, retry interval: {retryInterval}s)...");
            
            while (attempt < maxRetries)
            {
                try
                {
                    var response = await _httpClient.GetAsync($"http://localhost:{port}/");
                    if (response.IsSuccessStatusCode)
                    {
                        Log($"Coqui TTS server is ready! (took {attempt * retryInterval}s)");
                        return;
                    }
                    else
                    {
                        Log($"Server responded with status: {response.StatusCode}, retrying in {retryInterval}s... (attempt {attempt + 1}/{maxRetries})");
                    }
                }
                catch (Exception ex)
                {
                    Log($"Server not ready yet: {ex.Message}, retrying in {retryInterval}s... (attempt {attempt + 1}/{maxRetries})");
                }
                
                attempt++;
                if (attempt < maxRetries)
                {
                    await Task.Delay(retryInterval * 1000);
                }
            }
            
            Log($"Coqui TTS server did not become ready within {startupTimeout}s timeout", "Warning");
        }
        
        // Cleanup Coqui TTS server on application exit
        public static void CleanupCoquiServer()
        {
            lock (_serverLock)
            {
                if (_coquiServerProcess != null && !_coquiServerProcess.HasExited)
                {
                    try
                    {
                        Log("Shutting down Coqui TTS server...");
                        _coquiServerProcess.Kill();
                        _coquiServerProcess.WaitForExit(5000); // Wait up to 5 seconds
                        _coquiServerProcess.Dispose();
                        _coquiServerProcess = null;
                        _serverStarted = false;
                        Log("Coqui TTS server shutdown completed");
                    }
                    catch (Exception ex)
                    {
                        Log($"Error shutting down Coqui TTS server: {ex.Message}", "Warning");
                    }
                }
            }
        }
        
        // Start Coqui TTS Server if configured
        private static async Task<bool> EnsureCoquiServerStarted()
        {
            var coquiMode = ConfigurationManager.AppSettings.Get("CoquiMode")?.ToLower() ?? "exe";
            if (coquiMode != "server")
                return false; // Not using server mode
                
            lock (_serverLock)
            {
                if (_serverStarted && _coquiServerProcess != null && !_coquiServerProcess.HasExited)
                    return true; // Already running
                    
                try
                {
                    var serverPath = ConfigurationManager.AppSettings.Get("coqui_server");
                    var port = ConfigurationManager.AppSettings.Get("coqui_server_port") ?? "5002";
                    var useCuda = bool.Parse(ConfigurationManager.AppSettings.Get("coqui_server_use_cuda") ?? "false");
                    var debug = bool.Parse(ConfigurationManager.AppSettings.Get("coqui_server_debug") ?? "false");
                    var model = ConfigurationManager.AppSettings.Get("coqui_server_model") ?? "tts_models/en/ljspeech/tacotron2-DDC";
                    
                    if (string.IsNullOrEmpty(serverPath))
                    {
                        Log("Coqui server path not configured", "Warning");
                        return false;
                    }
                    
                    var args = new List<string>
                    {
                        "--port", port,
                        "--model_name", model
                    };
                    
                    if (useCuda)
                        args.Add("--use_cuda");
                        
                    if (debug)
                        args.Add("--debug");
                    
                    Log($"Starting Coqui TTS server: {serverPath} {string.Join(" ", args)}");
                    
                    _coquiServerProcess = new Process
                    {
                        StartInfo = new ProcessStartInfo
                        {
                            FileName = serverPath,
                            Arguments = string.Join(" ", args),
                            UseShellExecute = false,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            CreateNoWindow = true
                        }
                    };
                    
                    // Log server output
                    _coquiServerProcess.OutputDataReceived += (sender, e) =>
                    {
                        if (!string.IsNullOrEmpty(e.Data))
                            Log($"CoquiServer: {e.Data}");
                    };
                    
                    _coquiServerProcess.ErrorDataReceived += (sender, e) =>
                    {
                        if (!string.IsNullOrEmpty(e.Data))
                            Log($"CoquiServer ERROR: {e.Data}", "Warning");
                    };
                    
                    _coquiServerProcess.Start();
                    _coquiServerProcess.BeginOutputReadLine();
                    _coquiServerProcess.BeginErrorReadLine();
                    
                    _serverStarted = true;
                    Log($"Coqui TTS server started on port {port}");
                    
                    return true;
                }
                catch (Exception ex)
                {
                    Log($"Failed to start Coqui TTS server: {ex.Message}", "Error");
                    return false;
                }
            }
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
                
                // Store the gateway client for proper disconnection
                _gatewayClient = gatewayClient;
                
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

        private async Task<(Process process, string path)> CreateTTSFile(string cleanmsg, string voice)
        {
            var path = Path.GetTempFileName() + ".wav"; // Ensure wav extension for Coqui TTS
            
            // Load Coqui configuration if not already loaded
            LoadCoquiVoiceConfiguration();
            
            var coquiMode = ConfigurationManager.AppSettings.Get("CoquiMode")?.ToLower() ?? "exe";
            
            if (coquiMode == "server")
            {
                // Use server API mode
                var process = await CreateTTSFileViaServer(cleanmsg, voice, path);
                return (process, path);
            }
            else
            {
                // Use legacy exe mode
                var process = CreateTTSFileViaExe(cleanmsg, voice, path);
                return (process, path);
            }
        }
        
        private Process CreateTTSFileViaExe(string cleanmsg, string voice, string path)
        {
            // Get model for the specified voice from configuration
            string modelName = "tts_models/en/ljspeech/tacotron2-DDC"; // Default
            if (coquiVoiceModels.TryGetValue(voice, out var configuredModel))
            {
                modelName = configuredModel;
            }
            
            var modelArgs = $"--model_name {modelName}";
            
            return Process.Start(new ProcessStartInfo
            {
                FileName = ConfigurationManager.AppSettings.Get("coquitts") ?? "tts", // Use tts.exe as fallback
                Arguments = $"{modelArgs} --text \"{cleanmsg}\" --out_path \"{path}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
        }
        
        private async Task<Process> CreateTTSFileViaServer(string cleanmsg, string voice, string path)
        {
            try
            {
                // Ensure server is started
                if (!await EnsureCoquiServerStarted())
                {
                    Log("Coqui server not available, falling back to exe mode", "Warning");
                    return CreateTTSFileViaExe(cleanmsg, voice, path);
                }
                
                var port = ConfigurationManager.AppSettings.Get("coqui_server_port") ?? "5002";
                var timeout = int.Parse(ConfigurationManager.AppSettings.Get("coqui_server_timeout") ?? "30");
                
                // Get speaker name from voice mapping
                string speakerName = null;
                if (coquiVoiceModels.TryGetValue(voice, out var configuredSpeaker))
                {
                    speakerName = configuredSpeaker;
                }
                
                // Get default language from config
                var defaultLanguage = ConfigurationManager.AppSettings.Get("coqui_server_default_language") ?? "en";
                
                // Coqui TTS server API expects form POST data
                var formData = new List<KeyValuePair<string, string>>
                {
                    new KeyValuePair<string, string>("text", cleanmsg),
                    new KeyValuePair<string, string>("language_id", defaultLanguage)
                };
                
                if (!string.IsNullOrEmpty(speakerName))
                {
                    formData.Add(new KeyValuePair<string, string>("speaker_id", speakerName));
                    Log($"Using speaker: '{speakerName}' for voice: '{voice}'");
                }
                else
                {
                    Log($"No speaker mapping found for voice: '{voice}', using default");
                }
                
                var content = new FormUrlEncodedContent(formData);
                
                Log($"Calling Coqui TTS server API: text='{cleanmsg}', speaker_id='{speakerName}'");
                
                var response = await _httpClient.PostAsync($"http://localhost:{port}/api/tts", content);
                
                if (response.IsSuccessStatusCode)
                {
                    var audioData = await response.Content.ReadAsByteArrayAsync();
                    await File.WriteAllBytesAsync(path, audioData);
                    
                    Log($"Successfully generated TTS via server API: {path}");
                    
                    // Return a dummy completed process since we already have the file
                    return Process.Start(new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = "/c echo TTS completed via server API",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        CreateNoWindow = true
                    });
                }
                else
                {
                    Log($"Coqui server API failed: {response.StatusCode}", "Warning");
                    return CreateTTSFileViaExe(cleanmsg, voice, path);
                }
            }
            catch (Exception ex)
            {
                Log($"Error calling Coqui server API: {ex.Message}", "Warning");
                return CreateTTSFileViaExe(cleanmsg, voice, path);
            }
        }
        private async Task SendAsync(ulong guildId, string msg, string voice = "Microsoft David Desktop", string user = "", int rate = 0, RestClient restClient = null)
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
                    var (coquitts, coquittspath) = await CreateTTSFile(cleanmsg, voice);
                    using (coquitts)
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
                    
                    // Add checkmark reaction to indicate successful TTS completion
                    if (restClient != null)
                    {
                        try
                        {
                            await restClient.AddMessageReactionAsync(textMsg.ChannelId, textMsg.Id, "✅");
                            Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - Added checkmark reaction to TTS message");
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - Failed to add reaction: {ex.Message}");
                        }
                    }
                    
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
                    
                    // Wait for any pending operations to complete
                    await sem.WaitAsync();
                    
                    // Use gateway client to properly disconnect from voice channel
                    if (_gatewayClient != null)
                    {
                        Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - Disconnecting from voice channel via gateway client...");
                        try
                        {
                            // Use UpdateVoiceStateAsync with null channelId to disconnect
                            await _gatewayClient.UpdateVoiceStateAsync(new VoiceStateProperties(guildId, null));
                            Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - Voice state updated to disconnect");
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - Error updating voice state: {ex.Message}");
                        }
                    }
                    
                    // Dispose the output stream BEFORE disposing the voice client
                    if (outputStream != null)
                    {
                        Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - Disposing output stream...");
                        try
                        {
                            await outputStream.FlushAsync();
                            outputStream.Dispose();
                            Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - Output stream disposed successfully");
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - Error disposing output stream: {ex.Message}");
                        }
                    }
                    
                    // Dispose the voice client AFTER disposing the output stream
                    if (voiceClient != null)
                    {
                        Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - Disposing voice client...");
                        try
                        {
                            voiceClient.Dispose();
                            Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - Voice client disposed successfully");
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - Error disposing voice client: {ex.Message}");
                        }
                    }
                    
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
                        await textChannel.SendMessageAsync(new MessageProperties { Content = $"Error leaving voice channel: {ex.Message}" });
                    }
                }
                finally
                {
                    // Always dispose the semaphore and remove from map
                    sem?.Release();
                    sem?.Dispose();
                    map.Remove(guildId, out _);
                    Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - Voice channel mapping removed for guild {guildId}");
                }
            }
            else
            {
                if (textChannel != null)
                {
                    await textChannel.SendMessageAsync(new MessageProperties { Content = "Bot is not currently connected to any voice channel." });
                }
                Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - No voice connection found for guild {guildId}");
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

        public async Task UnlinkChannel(ulong userId, TextChannel textChannel, string username)
        {
            if (userPrefsDict.TryRemove(userId, out var userPrefs))
            {
                // Also remove from steam ID mapping if it exists
                if (userPrefs.SteamId != 0)
                {
                    steamIdtoDiscordId.TryRemove(userPrefs.SteamId, out _);
                }
                
                await textChannel.SendMessageAsync(new MessageProperties { Content = $"{username} has been unlinked from Steam ID {userPrefs.SteamId}." });
                Log($"User {username} ({userId}) unlinked from Steam ID {userPrefs.SteamId}");
            }
            else
            {
                await textChannel.SendMessageAsync(new MessageProperties { Content = $"{username} is not currently linked to any Steam account." });
            }
        }

        public async Task VerifyLink(ulong userId, TextChannel textChannel, string username)
        {
            if (userPrefsDict.TryGetValue(userId, out var userPrefs))
            {
                var steamStatus = userPrefs.SteamId != 0 ? "Linked" : "Not linked";
                var voiceInfo = !string.IsNullOrEmpty(userPrefs.Voice) ? userPrefs.Voice : "Default";
                
                await textChannel.SendMessageAsync(new MessageProperties
                {
                    Content = $"**Link Verification for {username}:**\n" +
                             $"Discord ID: {userId}\n" +
                             $"Steam ID: {userPrefs.SteamId} ({steamStatus})\n" +
                             $"Voice: {voiceInfo}\n" +
                             $"Rate: {userPrefs.Rate}\n" +
                             $"Guild ID: {userPrefs.GuildId}"
                });
                
                Log($"Link verification requested for user {username} ({userId})");
            }
            else
            {
                await textChannel.SendMessageAsync(new MessageProperties
                {
                    Content = $"{username} is not linked to any Steam account. Use `!tts link <steamid>` to link your account."
                });
            }
        }

        public async Task Help(TextChannel channel)
        {
            await channel.SendMessageAsync(new MessageProperties
            {
                Content = "**TTS Bot Help:**\n" +
                    "`!tts link <steamid> [voice] [rate]` - Link Steam account to Discord\n" +
                    "`!tts unlink` - Unlink Steam account\n" +
                    "`!tts verify` - Check current link status\n" +
                    "`!tts join [channel_id]` - Join voice channel (auto-detects if no ID provided)\n" +
                    "`!tts leave` - Leave voice channel\n" +
                    "`!tts changevoice <voice>` - Change TTS voice\n" +
                    "`!tts changerate <-10 to 10>` - Change speech rate (10 is fastest)\n" +
                    "`!tts changeserver` - Change server\n" +
                    "`!tts voices` - List all available voices\n" +
                    "`!tts help` - Show this help\n\n" +
                    "**Available Voice Types:**\n" +
                    "• System voices (e.g., Microsoft David Desktop)\n" +
                    "• Coqui TTS voices (CoQui_female_1, CoQui_male_1, etc.)\n\n" +
                    "Slash commands (/) are also supported!"
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
                await SendAsync(userPrefs.GuildId, msg, voice, userPrefs.Name, userPrefs.Rate, _restClient);
            }
        }

        static List<string> voices = new List<string>();
        static Dictionary<string, string> coquiVoiceModels = new Dictionary<string, string>();
        
        public string CheckVoice(string voice)
        {
            voice = voice.Trim();
            
            // Check for Coqui TTS voices - any voice starting with "CoQui" prefix
            if (voice.StartsWith("CoQui", StringComparison.OrdinalIgnoreCase))
            {
                // Load Coqui configuration if not already loaded
                LoadCoquiVoiceConfiguration();
                
                // Allow generic "CoQui" for default
                if (voice.Equals("CoQui", StringComparison.OrdinalIgnoreCase))
                {
                    return coquiVoiceModels.Keys.FirstOrDefault() ?? "CoQui_female_1";  // Use first configured voice or default
                }
                
                // Check if the voice is in our configured list
                if (coquiVoiceModels.ContainsKey(voice))
                {
                    return voice;
                }
                
                // If not found in configured list but starts with CoQui, allow it anyway for flexibility
                return voice;
            }
            
            // Check for System.Speech voices
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
        
        public async Task ListVoices(TextChannel channel)
        {
            // Get System.Speech voices
            if (voices.Count == 0)
            {
                var synth = new SpeechSynthesizer();
                voices = synth.GetInstalledVoices().Select(x => x.VoiceInfo.Name).ToList<string>();
                synth.Dispose();
            }
            
            // Load Coqui configuration if not already loaded
            LoadCoquiVoiceConfiguration();
            
            var systemVoices = string.Join("\n", voices.Select(v => $"• {v}"));
            var coquiVoiceList = coquiVoiceModels.Count > 0
                ? string.Join("\n", coquiVoiceModels.Keys.Select(v => $"• {v}"))
                : "• No Coqui voices configured";
            
            await channel.SendMessageAsync(new MessageProperties
            {
                Content = $"**Available TTS Voices:**\n\n" +
                         $"**System Voices:**\n{systemVoices}\n\n" +
                         $"**Coqui TTS Voices:**\n{coquiVoiceList}\n\n" +
                         $"Use `!tts changevoice <voice_name>` to change your voice."
            });
        }
    }

}

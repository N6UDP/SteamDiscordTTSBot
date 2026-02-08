using NetCord;
using NetCord.Gateway;
using NetCord.Gateway.Voice;
using NetCord.Rest;
using System;
using System.Threading.Tasks;
using System.Speech.Synthesis;
using System.Speech.AudioFormat;
using System.IO;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Configuration;
using System.Threading;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;

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

        // PocketTTS fields
        private static Process _pocketServerProcess;
        private static bool _pocketServerStarted = false;
        private static readonly object _pocketServerLock = new object();
        private static Dictionary<string, string> pocketVoiceModels = new Dictionary<string, string>();
        private static readonly string[] PocketPredefinedVoices = { "alba", "marius", "javert", "jean", "fantine", "cosette", "eponine", "azelma" };

        // Engine enable/disable switches
        private static bool _enableSystemSpeech = true;
        private static bool _enableCoquiTTS = true;
        private static bool _enablePocketTTS = true;
        private static bool _pocketLocalPaths = false;

        public static void LoadEngineSwitch()
        {
            _enableSystemSpeech = bool.TryParse(ConfigurationManager.AppSettings.Get("EnableSystemSpeech"), out var es) ? es : true;
            _enableCoquiTTS = bool.TryParse(ConfigurationManager.AppSettings.Get("EnableCoquiTTS"), out var ec) ? ec : true;
            _enablePocketTTS = bool.TryParse(ConfigurationManager.AppSettings.Get("EnablePocketTTS"), out var ep) ? ep : true;
            _pocketLocalPaths = bool.TryParse(ConfigurationManager.AppSettings.Get("PocketTTS_LocalPaths"), out var lp) ? lp : false;
            Log($"Engine switches — SystemSpeech={_enableSystemSpeech}, CoquiTTS={_enableCoquiTTS}, PocketTTS={_enablePocketTTS}, PocketLocalPaths={_pocketLocalPaths}");
        }
        
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
            if (!_enableCoquiTTS)
            {
                Log("Coqui TTS engine disabled via config — skipping server startup");
                return;
            }

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
                    var match = Regex.Match(error, @"Message: \[([^\]]+)\]");
                    if (match.Success)
                    {
                        var speakerListText = match.Groups[1].Value;
                        var speakerMatches = Regex.Matches(speakerListText, @"'([^']+)'");
                        
                        foreach (Match speakerMatch in speakerMatches)
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
                        Log("Shutting down Coqui TTS server (killing process tree)...");
                        _coquiServerProcess.Kill(entireProcessTree: true);
                        _coquiServerProcess.WaitForExit(5000);
                        Log("Coqui TTS server shutdown completed");
                    }
                    catch (Exception ex)
                    {
                        Log($"Error shutting down Coqui TTS server: {ex.Message}", "Warning");
                    }
                    finally
                    {
                        try { _coquiServerProcess.Dispose(); } catch { }
                        _coquiServerProcess = null;
                        _serverStarted = false;
                    }
                }
            }
        }

        // ==================== PocketTTS Engine ====================

        // Initialize PocketTTS server on application startup
        public static async Task InitializePocketTTSServerAsync()
        {
            if (!_enablePocketTTS)
            {
                Log("PocketTTS engine disabled via config — skipping server startup");
                return;
            }

            Log("Initializing PocketTTS server at startup...");

            // Ensure voice directory exists
            var voiceDir = ConfigurationManager.AppSettings.Get("PocketTTS_VoiceDirectory") ?? "";
            if (!string.IsNullOrEmpty(voiceDir))
            {
                try
                {
                    Directory.CreateDirectory(voiceDir);
                    Log($"PocketTTS voice directory ensured: {voiceDir}");
                }
                catch (Exception ex)
                {
                    Log($"Failed to create PocketTTS voice directory '{voiceDir}': {ex.Message}", "Warning");
                }
            }

            await EnsurePocketServerStarted();
            await WaitForPocketServerReady();

            // Discover voices
            DiscoverPocketVoices();
            Log($"PocketTTS initialized with {pocketVoiceModels.Count} voices");
        }

        private static void DiscoverPocketVoices()
        {
            pocketVoiceModels.Clear();

            // Register predefined voices
            foreach (var v in PocketPredefinedVoices)
            {
                pocketVoiceModels[$"Pocket_{v}"] = v;
            }

            // Auto-discover custom voices from voice directory (.safetensors files)
            var voiceDir = ConfigurationManager.AppSettings.Get("PocketTTS_VoiceDirectory") ?? "";
            if (!string.IsNullOrEmpty(voiceDir) && Directory.Exists(voiceDir))
            {
                foreach (var file in Directory.GetFiles(voiceDir, "*.safetensors"))
                {
                    var name = Path.GetFileNameWithoutExtension(file);
                    var key = $"Pocket_{name}";
                    if (!pocketVoiceModels.ContainsKey(key))
                    {
                        pocketVoiceModels[key] = file;
                        Log($"Auto-discovered PocketTTS custom voice: {key} → {file}");
                    }
                }
            }

            // Load additional custom voice mappings from pocketvoices.json (HF URLs, non-standard paths, etc.)
            LoadPocketVoicesJson();

            Log($"PocketTTS voice discovery complete: {pocketVoiceModels.Count} voices ({PocketPredefinedVoices.Length} predefined + {pocketVoiceModels.Count - PocketPredefinedVoices.Length} custom)");
        }

        private static readonly string PocketVoicesJsonPath = "pocketvoices.json";

        private static void LoadPocketVoicesJson()
        {
            try
            {
                if (File.Exists(PocketVoicesJsonPath))
                {
                    var json = File.ReadAllText(PocketVoicesJsonPath);
                    if (!string.IsNullOrWhiteSpace(json) && json != "{}")
                    {
                        var entries = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                        if (entries != null)
                        {
                            foreach (var kv in entries)
                            {
                                var key = kv.Key.StartsWith("Pocket_", StringComparison.OrdinalIgnoreCase)
                                    ? kv.Key
                                    : $"Pocket_{kv.Key}";
                                if (!pocketVoiceModels.ContainsKey(key))
                                {
                                    pocketVoiceModels[key] = kv.Value;
                                    Log($"Loaded PocketTTS voice from pocketvoices.json: {key} → {kv.Value}");
                                }
                            }
                            Log($"Loaded {entries.Count} custom voice mappings from {PocketVoicesJsonPath}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"Error loading {PocketVoicesJsonPath}: {ex.Message}", "Warning");
            }
        }

        private static void SavePocketVoicesJson()
        {
            try
            {
                // Build dict of only custom (non-predefined) voices
                var customEntries = new Dictionary<string, string>();
                foreach (var kv in pocketVoiceModels)
                {
                    // Skip predefined voices — they're always auto-registered
                    if (PocketPredefinedVoices.Contains(kv.Value, StringComparer.OrdinalIgnoreCase))
                        continue;
                    customEntries[kv.Key] = kv.Value;
                }

                var json = JsonSerializer.Serialize(customEntries, new JsonSerializerOptions { WriteIndented = true });

                // Guard: never write empty data
                if (string.IsNullOrWhiteSpace(json) || json == "{}")
                {
                    // If there are no custom voices, delete the file if it exists
                    if (File.Exists(PocketVoicesJsonPath))
                    {
                        File.Delete(PocketVoicesJsonPath);
                        Log("Removed empty pocketvoices.json (no custom voices)");
                    }
                    return;
                }

                // Atomic write with backup (same pattern as userprefs.json)
                var tmpPath = PocketVoicesJsonPath + ".tmp";
                File.WriteAllText(tmpPath, json);
                if (File.Exists(PocketVoicesJsonPath))
                {
                    var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
                    var backupPath = $"{PocketVoicesJsonPath}.{timestamp}.bak";
                    try { File.Copy(PocketVoicesJsonPath, backupPath, true); }
                    catch (Exception ex) { Log($"Failed to create pocketvoices backup: {ex.Message}", "Warning"); }
                }
                File.Move(tmpPath, PocketVoicesJsonPath, true);
                Log($"Saved {customEntries.Count} custom voice mappings to {PocketVoicesJsonPath}");
            }
            catch (Exception ex)
            {
                Log($"Error saving {PocketVoicesJsonPath}: {ex.Message}", "Error");
            }
        }

        private static async Task<bool> EnsurePocketServerStarted()
        {
            lock (_pocketServerLock)
            {
                if (_pocketServerStarted && _pocketServerProcess != null && !_pocketServerProcess.HasExited)
                    return true;

                try
                {
                    var executable = ConfigurationManager.AppSettings.Get("PocketTTS_Executable") ?? "uvx";
                    var uvxPath = ConfigurationManager.AppSettings.Get("PocketTTS_UvxPath") ?? "uvx";
                    var pocketTtsPath = ConfigurationManager.AppSettings.Get("PocketTTS_PocketTTSPath") ?? "pocket-tts";
                    var gitUrl = ConfigurationManager.AppSettings.Get("PocketTTS_GitUrl") ?? "git+https://github.com/N6UDP/pocket-tts.git";
                    var host = ConfigurationManager.AppSettings.Get("PocketTTS_ServerHost") ?? "localhost";
                    var port = ConfigurationManager.AppSettings.Get("PocketTTS_ServerPort") ?? "8000";
                    var defaultVoice = ConfigurationManager.AppSettings.Get("PocketTTS_DefaultVoice") ?? "alba";
                    var hfToken = ConfigurationManager.AppSettings.Get("HuggingFace_Token") ?? "";
                    var localPathsFlag = _pocketLocalPaths ? " --local-paths" : "";

                    string fileName;
                    string arguments;

                    if (executable.Equals("uvx", StringComparison.OrdinalIgnoreCase))
                    {
                        fileName = uvxPath;
                        arguments = $"--with soundfile --from {gitUrl} pocket-tts serve --host {host} --port {port} --voice {defaultVoice}{localPathsFlag}";
                    }
                    else if (executable.Equals("pocket-tts", StringComparison.OrdinalIgnoreCase))
                    {
                        fileName = pocketTtsPath;
                        arguments = $"serve --host {host} --port {port} --voice {defaultVoice}{localPathsFlag}";
                    }
                    else
                    {
                        // Custom command — split first token as filename, rest as prefix
                        var parts = executable.Split(' ', 2);
                        fileName = parts[0];
                        var prefix = parts.Length > 1 ? parts[1] + " " : "";
                        arguments = $"{prefix}serve --host {host} --port {port} --voice {defaultVoice}{localPathsFlag}";
                    }

                    Log($"Starting PocketTTS server: {fileName} {arguments}");

                    var startInfo = new ProcessStartInfo
                    {
                        FileName = fileName,
                        Arguments = arguments,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };

                    // Pass HuggingFace token as env var if configured
                    if (!string.IsNullOrEmpty(hfToken))
                    {
                        startInfo.EnvironmentVariables["HF_TOKEN"] = hfToken;
                        Log("HuggingFace token set for PocketTTS server process");
                    }

                    _pocketServerProcess = new Process { StartInfo = startInfo };

                    _pocketServerProcess.OutputDataReceived += (sender, e) =>
                    {
                        if (!string.IsNullOrEmpty(e.Data))
                            Log($"PocketTTS: {e.Data}");
                    };

                    _pocketServerProcess.ErrorDataReceived += (sender, e) =>
                    {
                        if (!string.IsNullOrEmpty(e.Data))
                            Log($"PocketTTS STDERR: {e.Data}", "Warning");
                    };

                    _pocketServerProcess.Start();
                    _pocketServerProcess.BeginOutputReadLine();
                    _pocketServerProcess.BeginErrorReadLine();

                    _pocketServerStarted = true;
                    Log($"PocketTTS server started on {host}:{port}");
                    return true;
                }
                catch (Exception ex)
                {
                    Log($"Failed to start PocketTTS server: {ex.Message}", "Error");
                    return false;
                }
            }
        }

        private static async Task WaitForPocketServerReady()
        {
            var host = ConfigurationManager.AppSettings.Get("PocketTTS_ServerHost") ?? "localhost";
            var port = ConfigurationManager.AppSettings.Get("PocketTTS_ServerPort") ?? "8000";
            var startupTimeout = int.Parse(ConfigurationManager.AppSettings.Get("PocketTTS_StartupTimeout") ?? "120");
            var retryInterval = int.Parse(ConfigurationManager.AppSettings.Get("PocketTTS_RetryInterval") ?? "3");
            var maxRetries = startupTimeout / retryInterval;

            Log($"Waiting for PocketTTS server to be ready (timeout: {startupTimeout}s, retry interval: {retryInterval}s)...");

            for (int attempt = 0; attempt < maxRetries; attempt++)
            {
                try
                {
                    var response = await _httpClient.GetAsync($"http://{host}:{port}/health");
                    if (response.IsSuccessStatusCode)
                    {
                        Log($"PocketTTS server is ready! (took {attempt * retryInterval}s)");
                        return;
                    }
                    else
                    {
                        Log($"PocketTTS health check returned {response.StatusCode}, retrying in {retryInterval}s... (attempt {attempt + 1}/{maxRetries})");
                    }
                }
                catch (Exception ex)
                {
                    Log($"PocketTTS not ready yet: {ex.Message}, retrying in {retryInterval}s... (attempt {attempt + 1}/{maxRetries})");
                }

                await Task.Delay(retryInterval * 1000);
            }

            Log($"PocketTTS server did not become ready within {startupTimeout}s timeout", "Warning");
        }

        public static void CleanupPocketTTSServer()
        {
            lock (_pocketServerLock)
            {
                if (_pocketServerProcess != null && !_pocketServerProcess.HasExited)
                {
                    try
                    {
                        Log("Shutting down PocketTTS server (killing process tree)...");
                        // Kill the entire process tree — uvx spawns a child Python process
                        // that would otherwise remain running as an orphan
                        _pocketServerProcess.Kill(entireProcessTree: true);
                        _pocketServerProcess.WaitForExit(5000);
                        Log("PocketTTS process tree killed");
                    }
                    catch (Exception ex)
                    {
                        Log($"Error killing PocketTTS process tree: {ex.Message}", "Warning");
                    }
                    finally
                    {
                        try { _pocketServerProcess.Dispose(); } catch { }
                        _pocketServerProcess = null;
                        _pocketServerStarted = false;
                    }
                }
                else
                {
                    // Process already exited or was null — clean up reference
                    if (_pocketServerProcess != null)
                    {
                        try { _pocketServerProcess.Dispose(); } catch { }
                        _pocketServerProcess = null;
                    }
                    _pocketServerStarted = false;
                }

                // Fallback: kill any process still listening on the PocketTTS port
                KillProcessOnPort(ConfigurationManager.AppSettings.Get("PocketTTS_ServerPort") ?? "8000");
            }
        }

        private static void KillProcessOnPort(string port)
        {
            try
            {
                // Use netstat to find PID listening on the port
                var netstat = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = $"/c netstat -ano | findstr \"LISTENING\" | findstr \":{port} \"",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        CreateNoWindow = true
                    }
                };
                netstat.Start();
                var output = netstat.StandardOutput.ReadToEnd();
                netstat.WaitForExit(3000);

                if (string.IsNullOrWhiteSpace(output)) return;

                // Parse PIDs from netstat output (last column)
                var pids = new HashSet<int>();
                foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length > 0 && int.TryParse(parts[^1], out var pid) && pid > 0)
                    {
                        pids.Add(pid);
                    }
                }

                foreach (var pid in pids)
                {
                    try
                    {
                        var proc = Process.GetProcessById(pid);
                        Log($"Killing orphaned process on port {port}: PID {pid} ({proc.ProcessName})");
                        proc.Kill(entireProcessTree: true);
                        proc.WaitForExit(3000);
                    }
                    catch (ArgumentException)
                    {
                        // Process already exited
                    }
                    catch (Exception ex)
                    {
                        Log($"Failed to kill PID {pid}: {ex.Message}", "Warning");
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"Port cleanup fallback failed: {ex.Message}", "Warning");
            }
        }

        private async Task<(bool success, string tempPath)> CreatePocketTTSFile(string text, string voice)
        {
            var host = ConfigurationManager.AppSettings.Get("PocketTTS_ServerHost") ?? "localhost";
            var port = ConfigurationManager.AppSettings.Get("PocketTTS_ServerPort") ?? "8000";
            var requestTimeout = int.Parse(ConfigurationManager.AppSettings.Get("PocketTTS_RequestTimeout") ?? "60");

            // Resolve voice: strip Pocket_ prefix
            string resolvedVoice;
            if (pocketVoiceModels.TryGetValue(voice, out var mapped))
            {
                resolvedVoice = mapped;
            }
            else
            {
                // Strip prefix and use as-is
                resolvedVoice = voice.StartsWith("Pocket_", StringComparison.OrdinalIgnoreCase)
                    ? voice.Substring(7)
                    : voice;
            }

            var tempPath = Path.GetTempFileName() + ".wav";

            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(requestTimeout) };
                using var formContent = new MultipartFormDataContent();
                formContent.Add(new StringContent(text), "text");

                // Check if resolvedVoice is a local file path (custom .safetensors or .wav)
                // When PocketTTS_LocalPaths is true, the server was started with --local-paths
                // and can accept local file paths directly via voice_url (avoids uploading).
                // When false (default), local files must be uploaded via voice_wav.
                if (File.Exists(resolvedVoice))
                {
                    if (_pocketLocalPaths)
                    {
                        // Server has --local-paths enabled — pass the absolute path as voice_url
                        var absolutePath = Path.GetFullPath(resolvedVoice);
                        formContent.Add(new StringContent(absolutePath), "voice_url");
                    }
                    else
                    {
                        // Upload local file as voice_wav — the server will detect format by extension
                        var fileBytes = await File.ReadAllBytesAsync(resolvedVoice);
                        var fileContent = new ByteArrayContent(fileBytes);
                        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                        formContent.Add(fileContent, "voice_wav", Path.GetFileName(resolvedVoice));
                    }
                }
                else if (resolvedVoice.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                      || resolvedVoice.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                      || resolvedVoice.StartsWith("hf://", StringComparison.OrdinalIgnoreCase))
                {
                    // Remote URL — pass as voice_url
                    formContent.Add(new StringContent(resolvedVoice), "voice_url");
                }
                else
                {
                    // Predefined voice name (alba, marius, etc.) — pass as voice_url
                    formContent.Add(new StringContent(resolvedVoice), "voice_url");
                }

                Log($"PocketTTS request: text='{text}', voice='{resolvedVoice}'");

                var response = await client.PostAsync($"http://{host}:{port}/tts", formContent);

                if (response.IsSuccessStatusCode)
                {
                    var audioData = await response.Content.ReadAsByteArrayAsync();
                    await File.WriteAllBytesAsync(tempPath, audioData);
                    Log($"PocketTTS generated audio: {tempPath} ({audioData.Length} bytes)");
                    return (true, tempPath);
                }
                else
                {
                    var errorBody = await response.Content.ReadAsStringAsync();
                    Log($"PocketTTS API failed: {response.StatusCode} — {errorBody}", "Error");
                    return (false, null);
                }
            }
            catch (Exception ex)
            {
                Log($"PocketTTS request error: {ex.Message}", "Error");
                if (File.Exists(tempPath)) File.Delete(tempPath);
                return (false, null);
            }
        }

        // ==================== Voice Upload/Rename/Delete Commands ====================

        private static bool IsUserAllowedToUpload(ulong userId)
        {
            var allowlist = ConfigurationManager.AppSettings.Get("PocketTTS_VoiceUploadAllowlist") ?? "";
            if (string.IsNullOrWhiteSpace(allowlist)) return false;
            if (allowlist.Trim() == "*") return true;
            return allowlist.Split(',').Select(s => s.Trim()).Any(id => id == userId.ToString());
        }

        private static string BuildPocketTTSCommand(string subCommand, string extraArgs)
        {
            var executable = ConfigurationManager.AppSettings.Get("PocketTTS_Executable") ?? "uvx";

            if (executable.Equals("uvx", StringComparison.OrdinalIgnoreCase))
            {
                return $"uvx --from \"git+https://github.com/kyutai-labs/pocket-tts.git\" pocket-tts {subCommand} {extraArgs}";
            }
            else if (executable.Equals("pocket-tts", StringComparison.OrdinalIgnoreCase))
            {
                return $"pocket-tts {subCommand} {extraArgs}";
            }
            else
            {
                // Custom command prefix
                var parts = executable.Split(' ', 2);
                var prefix = parts.Length > 1 ? parts[1] + " " : "";
                return $"{parts[0]} {prefix}{subCommand} {extraArgs}";
            }
        }

        public async Task UploadVoice(string name, ulong userId, TextChannel textChannel, string attachmentUrl, string fileName)
        {
            if (!_enablePocketTTS)
            {
                await textChannel.SendMessageAsync(new MessageProperties { Content = "PocketTTS engine is disabled." });
                return;
            }

            if (!IsUserAllowedToUpload(userId))
            {
                await textChannel.SendMessageAsync(new MessageProperties { Content = "You don't have permission to upload custom voices." });
                return;
            }

            var voiceDir = ConfigurationManager.AppSettings.Get("PocketTTS_VoiceDirectory") ?? "";
            if (string.IsNullOrEmpty(voiceDir))
            {
                await textChannel.SendMessageAsync(new MessageProperties { Content = "Custom voice storage is not configured. Set PocketTTS_VoiceDirectory in App.config." });
                return;
            }

            // Check name doesn't conflict with predefined voices
            if (PocketPredefinedVoices.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                await textChannel.SendMessageAsync(new MessageProperties { Content = $"Cannot use that name — it conflicts with a built-in voice: {name}" });
                return;
            }

            // Validate file extension
            var ext = Path.GetExtension(fileName).ToLowerInvariant();
            if (ext != ".wav" && ext != ".mp3" && ext != ".safetensors")
            {
                await textChannel.SendMessageAsync(new MessageProperties { Content = "Unsupported file type. Please upload a .wav, .mp3, or .safetensors file." });
                return;
            }

            // Validate file size
            var maxSizeMB = int.Parse(ConfigurationManager.AppSettings.Get("PocketTTS_MaxUploadSizeMB") ?? "10");

            try
            {
                await textChannel.SendMessageAsync(new MessageProperties { Content = $"Processing voice upload '{name}'..." });

                // Download the attachment
                using var client = new HttpClient();
                var data = await client.GetByteArrayAsync(attachmentUrl);

                if (data.Length > maxSizeMB * 1024 * 1024)
                {
                    await textChannel.SendMessageAsync(new MessageProperties { Content = $"File exceeds maximum size of {maxSizeMB}MB." });
                    return;
                }

                var outputPath = Path.Combine(voiceDir, $"{name}.safetensors");

                if (ext == ".safetensors")
                {
                    // Copy directly
                    await File.WriteAllBytesAsync(outputPath, data);
                    Log($"Voice upload: copied .safetensors directly to {outputPath}");
                }
                else
                {
                    // Save temp file and run export-voice
                    var tempInput = Path.GetTempFileName() + ext;
                    await File.WriteAllBytesAsync(tempInput, data);

                    try
                    {
                        var fullCmd = BuildPocketTTSCommand("export-voice", $"\"{tempInput}\" \"{outputPath}\"");
                        // Parse the command
                        var cmdParts = fullCmd.Split(' ', 2);

                        var startInfo = new ProcessStartInfo
                        {
                            FileName = cmdParts[0],
                            Arguments = cmdParts.Length > 1 ? cmdParts[1] : "",
                            UseShellExecute = false,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            CreateNoWindow = true
                        };

                        var hfToken = ConfigurationManager.AppSettings.Get("HuggingFace_Token") ?? "";
                        if (!string.IsNullOrEmpty(hfToken))
                        {
                            startInfo.EnvironmentVariables["HF_TOKEN"] = hfToken;
                        }

                        Log($"Running export-voice: {fullCmd}");
                        var process = new Process { StartInfo = startInfo };
                        process.Start();

                        var stdout = await process.StandardOutput.ReadToEndAsync();
                        var stderr = await process.StandardError.ReadToEndAsync();
                        await process.WaitForExitAsync();

                        if (!string.IsNullOrEmpty(stdout)) Log($"export-voice stdout: {stdout}");
                        if (!string.IsNullOrEmpty(stderr)) Log($"export-voice stderr: {stderr}", "Warning");

                        if (process.ExitCode != 0 || !File.Exists(outputPath))
                        {
                            await textChannel.SendMessageAsync(new MessageProperties { Content = "Failed to process voice sample. The audio file may be corrupted or too short." });

                            if (!string.IsNullOrEmpty(hfToken))
                            {
                                // Token was set, maybe other issue
                            }
                            else
                            {
                                await textChannel.SendMessageAsync(new MessageProperties { Content = "Note: HuggingFace token may be required for voice processing. Set HuggingFace_Token in App.config and accept terms at <https://huggingface.co/kyutai/pocket-tts>" });
                            }
                            return;
                        }
                    }
                    finally
                    {
                        if (File.Exists(tempInput)) File.Delete(tempInput);
                    }
                }

                // Register the new voice and persist to JSON
                var voiceKey = $"Pocket_{name}";
                pocketVoiceModels[voiceKey] = outputPath;
                SavePocketVoicesJson();
                Log($"Voice uploaded and registered: {voiceKey} → {outputPath}");

                await textChannel.SendMessageAsync(new MessageProperties { Content = $"✅ Voice uploaded! Use: `!tts changevoice {voiceKey}`" });
            }
            catch (Exception ex)
            {
                Log($"Voice upload error: {ex.Message}", "Error");
                await textChannel.SendMessageAsync(new MessageProperties { Content = $"Error uploading voice: {ex.Message}" });
            }
        }

        public async Task RenameVoice(string oldName, string newName, ulong userId, TextChannel textChannel)
        {
            if (!_enablePocketTTS)
            {
                await textChannel.SendMessageAsync(new MessageProperties { Content = "PocketTTS engine is disabled." });
                return;
            }

            if (!IsUserAllowedToUpload(userId))
            {
                await textChannel.SendMessageAsync(new MessageProperties { Content = "You don't have permission to manage custom voices." });
                return;
            }

            var voiceDir = ConfigurationManager.AppSettings.Get("PocketTTS_VoiceDirectory") ?? "";
            if (string.IsNullOrEmpty(voiceDir))
            {
                await textChannel.SendMessageAsync(new MessageProperties { Content = "Custom voice storage is not configured." });
                return;
            }

            // Check new name doesn't conflict with predefined voices
            if (PocketPredefinedVoices.Contains(newName, StringComparer.OrdinalIgnoreCase))
            {
                await textChannel.SendMessageAsync(new MessageProperties { Content = $"Cannot use that name — it conflicts with a built-in voice: {newName}" });
                return;
            }

            var oldPath = Path.Combine(voiceDir, $"{oldName}.safetensors");
            var newPath = Path.Combine(voiceDir, $"{newName}.safetensors");

            if (!File.Exists(oldPath))
            {
                await textChannel.SendMessageAsync(new MessageProperties { Content = $"Voice '{oldName}' not found in voice directory." });
                return;
            }

            if (File.Exists(newPath))
            {
                await textChannel.SendMessageAsync(new MessageProperties { Content = $"A voice named '{newName}' already exists." });
                return;
            }

            try
            {
                File.Move(oldPath, newPath);
                pocketVoiceModels.Remove($"Pocket_{oldName}");
                pocketVoiceModels[$"Pocket_{newName}"] = newPath;
                SavePocketVoicesJson();
                Log($"Voice renamed: Pocket_{oldName} → Pocket_{newName}");

                await textChannel.SendMessageAsync(new MessageProperties
                {
                    Content = $"✅ Voice renamed from `Pocket_{oldName}` to `Pocket_{newName}`.\n" +
                              $"⚠️ Users currently using `Pocket_{oldName}` will need to run `!tts changevoice Pocket_{newName}`."
                });
            }
            catch (Exception ex)
            {
                Log($"Voice rename error: {ex.Message}", "Error");
                await textChannel.SendMessageAsync(new MessageProperties { Content = $"Error renaming voice: {ex.Message}" });
            }
        }

        public async Task DeleteVoice(string name, ulong userId, TextChannel textChannel)
        {
            if (!_enablePocketTTS)
            {
                await textChannel.SendMessageAsync(new MessageProperties { Content = "PocketTTS engine is disabled." });
                return;
            }

            if (!IsUserAllowedToUpload(userId))
            {
                await textChannel.SendMessageAsync(new MessageProperties { Content = "You don't have permission to manage custom voices." });
                return;
            }

            // Prevent deleting predefined voices
            if (PocketPredefinedVoices.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                await textChannel.SendMessageAsync(new MessageProperties { Content = $"Cannot delete built-in voice: {name}" });
                return;
            }

            var voiceDir = ConfigurationManager.AppSettings.Get("PocketTTS_VoiceDirectory") ?? "";
            if (string.IsNullOrEmpty(voiceDir))
            {
                await textChannel.SendMessageAsync(new MessageProperties { Content = "Custom voice storage is not configured." });
                return;
            }

            var filePath = Path.Combine(voiceDir, $"{name}.safetensors");

            if (!File.Exists(filePath))
            {
                await textChannel.SendMessageAsync(new MessageProperties { Content = $"Voice '{name}' not found in voice directory." });
                return;
            }

            try
            {
                File.Delete(filePath);
                pocketVoiceModels.Remove($"Pocket_{name}");
                SavePocketVoicesJson();
                Log($"Voice deleted: Pocket_{name} ({filePath})");

                await textChannel.SendMessageAsync(new MessageProperties
                {
                    Content = $"✅ Voice `Pocket_{name}` deleted.\n" +
                              $"⚠️ Users currently using this voice will need to change to a different voice."
                });
            }
            catch (Exception ex)
            {
                Log($"Voice delete error: {ex.Message}", "Error");
                await textChannel.SendMessageAsync(new MessageProperties { Content = $"Error deleting voice: {ex.Message}" });
            }
        }

        public async Task ListCustomVoices(TextChannel textChannel)
        {
            if (!_enablePocketTTS)
            {
                await textChannel.SendMessageAsync(new MessageProperties { Content = "PocketTTS engine is disabled." });
                return;
            }

            var voiceDir = ConfigurationManager.AppSettings.Get("PocketTTS_VoiceDirectory") ?? "";
            var customVoices = new List<string>();

            if (!string.IsNullOrEmpty(voiceDir) && Directory.Exists(voiceDir))
            {
                foreach (var file in Directory.GetFiles(voiceDir, "*.safetensors"))
                {
                    customVoices.Add(Path.GetFileNameWithoutExtension(file));
                }
            }

            if (customVoices.Count == 0)
            {
                await textChannel.SendMessageAsync(new MessageProperties { Content = "No custom PocketTTS voices found.\nUse `!tts uploadvoice <name>` with an attached .wav/.mp3/.safetensors file to add one." });
                return;
            }

            var list = string.Join("\n", customVoices.Select(v => $"• Pocket_{v}"));
            await textChannel.SendMessageAsync(new MessageProperties
            {
                Content = $"**Custom PocketTTS Voices ({customVoices.Count}):**\n{list}\n\n" +
                          $"Voice directory: `{voiceDir}`"
            });
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
                    var fileContent = File.ReadAllText("userprefs.json");
                    if (!string.IsNullOrWhiteSpace(fileContent) && fileContent != "{}")
                    {
                        var userdict = JsonSerializer.Deserialize<Dictionary<ulong, UserPrefs>>(fileContent);
                        if (userdict != null)
                        {
                            foreach (var u in userdict)
                            {
                                userPrefsDict[u.Key] = u.Value;
                            }
                            Log($"Loaded {userdict.Count} user preferences from userprefs.json");
                        }
                    }
                    else
                    {
                        Log("userprefs.json is empty or contains no data, skipping load", "Warning");
                    }
                }
                catch
                {
                    Log("Failed to read userprefs.json");
                }

                try
                {
                    var fileContent = File.ReadAllText("steamid.json");
                    if (!string.IsNullOrWhiteSpace(fileContent) && fileContent != "{}")
                    {
                        var steamdict = JsonSerializer.Deserialize<Dictionary<ulong, ulong>>(fileContent);
                        if (steamdict != null)
                        {
                            foreach (var s in steamdict)
                            {
                                steamIdtoDiscordId[s.Key] = s.Value;
                            }
                            Log($"Loaded {steamdict.Count} Steam ID mappings from steamid.json");
                        }
                    }
                    else
                    {
                        Log("steamid.json is empty or contains no data, skipping load", "Warning");
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
                if (voice.StartsWith("Pocket", StringComparison.OrdinalIgnoreCase))
                {
                    if (!_enablePocketTTS)
                    {
                        Log($"PocketTTS engine disabled — cannot use voice '{voice}'", "Warning");
                        await textChannel.SendMessageAsync(new MessageProperties { Content = $"PocketTTS engine is disabled. Change your voice with `!tts changevoice <voice>`." });
                        return;
                    }
                    var (success, pocketPath) = await CreatePocketTTSFile(cleanmsg, voice);
                    if (!success || pocketPath == null)
                    {
                        await textChannel.SendMessageAsync(new MessageProperties { Content = $"PocketTTS failed to generate audio for: {cleanmsg}" });
                        return;
                    }
                    try
                    {
                        using (var ffmpeg = CreateStream(pocketPath))
                        {
                            using (var output = ffmpeg.StandardOutput.BaseStream)
                            {
                                await output.CopyToAsync(_ms);
                            }
                        }
                    }
                    finally
                    {
                        if (File.Exists(pocketPath)) File.Delete(pocketPath);
                    }
                }
                else if (voice.StartsWith("CoQui"))
                {
                    if (!_enableCoquiTTS)
                    {
                        Log($"CoquiTTS engine disabled — cannot use voice '{voice}'", "Warning");
                        await textChannel.SendMessageAsync(new MessageProperties { Content = $"Coqui TTS engine is disabled. Change your voice with `!tts changevoice <voice>`." });
                        return;
                    }
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
                    if (!_enableSystemSpeech)
                    {
                        Log($"System.Speech engine disabled — cannot use voice '{voice}'", "Warning");
                        await textChannel.SendMessageAsync(new MessageProperties { Content = $"System.Speech engine is disabled. Change your voice with `!tts changevoice <voice>`." });
                        return;
                    }
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
                        synth.SetOutputToAudioStream(_ms, new SpeechAudioFormatInfo(EncodingFormat.Pcm, 48000, 16, 2, 1536000, 2, null));
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
                    var hint = "";
                    if (voice.StartsWith("Pocket", StringComparison.OrdinalIgnoreCase))
                    {
                        var available = string.Join(", ", pocketVoiceModels.Keys.OrderBy(k => k));
                        hint = $"\nAvailable PocketTTS voices: {available}";
                    }
                    await channel.SendMessageAsync(new MessageProperties { Content = $"{username}:voice {voice} invalid.{hint}" });
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
                    "`!tts say <message>` - Send a TTS message directly from Discord\n" +
                    "`!tts changevoice <voice>` - Change TTS voice\n" +
                    "`!tts changerate <-10 to 10>` - Change speech rate (10 is fastest)\n" +
                    "`!tts changeserver` - Change server\n" +
                    "`!tts voices` - List all available voices\n" +
                    "`!tts uploadvoice <name>` - Upload a custom PocketTTS voice (attach .wav/.mp3/.safetensors)\n" +
                    "`!tts renamevoice <old> <new>` - Rename a custom PocketTTS voice\n" +
                    "`!tts deletevoice <name>` - Delete a custom PocketTTS voice\n" +
                    "`!tts customvoices` - List custom PocketTTS voices\n" +
                    "`!tts help` - Show this help\n\n" +
                    "**Available Voice Types:**\n" +
                    "• System voices (e.g., Microsoft David Desktop)\n" +
                    "• Coqui TTS voices (prefix `CoQui`, e.g., CoQui_female_1)\n" +
                    "• PocketTTS voices (prefix `Pocket`, e.g., Pocket_alba, Pocket_marius)\n\n" +
                    "Slash commands (/) are also supported!"
            });
        }

        public async Task SayTTS(string message, ulong userId, ulong guildId, TextChannel textChannel, string username)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                await textChannel.SendMessageAsync(new MessageProperties { Content = "Usage: `!tts say <message>` or `/say <message>`" });
                return;
            }

            if (!map.ContainsKey(guildId))
            {
                await textChannel.SendMessageAsync(new MessageProperties { Content = "Bot is not connected to a voice channel. Use `!tts join` first." });
                return;
            }

            // Look up user preferences for voice and rate; fall back to defaults
            var userPrefs = userPrefsDict.GetValueOrDefault(userId, null);
            var voice = userPrefs?.Voice ?? "Microsoft David Desktop";
            var rate = userPrefs?.Rate ?? 0;

            Log($"SayTTS from {username}: voice={voice}, rate={rate}, msg={message}");
            await SendAsync(guildId, message, voice, username, rate, _restClient);
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
            // Guard: never write empty or whitespace-only data
            if (string.IsNullOrWhiteSpace(data))
            {
                Log($"Refusing to write empty data to {path}", "Warning");
                return;
            }

            // Guard: never write empty JSON objects/arrays for files that should have content
            if (data == "{}" || data == "[]")
            {
                Log($"Refusing to write empty JSON ({data}) to {path}", "Warning");
                return;
            }

            File.WriteAllText(path + ".tmp", data);
            if (File.Exists(path))
            {
                // Use timestamped backup so we never lose history
                var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
                var backupPath = $"{path}.{timestamp}.bak";
                try
                {
                    File.Copy(path, backupPath, true);
                }
                catch (Exception ex)
                {
                    Log($"Failed to create timestamped backup {backupPath}: {ex.Message}", "Warning");
                }
            }
            File.Move(path + ".tmp", path, true);
        }

        public async Task Saver()
        {
            string prevuserprefs = string.Empty;
            string prevsteamid = string.Empty;

            while (true)
            {
                try
                {
                    // Take a snapshot of the dictionaries to avoid race conditions during serialization
                    var userPrefsSnapshot = new Dictionary<ulong, UserPrefs>(userPrefsDict);
                    var steamIdSnapshot = new Dictionary<ulong, ulong>(steamIdtoDiscordId);

                    var userprefs = JsonSerializer.Serialize(userPrefsSnapshot);
                    var steamid = JsonSerializer.Serialize(steamIdSnapshot);

                    if (userprefs != prevuserprefs)
                    {
                        WriteAllTextAtomicWithBackup("userprefs.json", userprefs);
                        prevuserprefs = userprefs;
                    }
                    if (steamid != prevsteamid)
                    {
                        WriteAllTextAtomicWithBackup("steamid.json", steamid);
                        prevsteamid = steamid;
                    }
                }
                catch (Exception ex)
                {
                    Log($"Error in Saver: {ex.Message}", "Error");
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
            
            // Check for PocketTTS voices — any voice starting with "Pocket" prefix
            if (voice.StartsWith("Pocket", StringComparison.OrdinalIgnoreCase))
            {
                if (!_enablePocketTTS)
                    return "";

                // Allow generic "Pocket" for default voice
                if (voice.Equals("Pocket", StringComparison.OrdinalIgnoreCase))
                {
                    var defaultVoice = ConfigurationManager.AppSettings.Get("PocketTTS_DefaultVoice") ?? "alba";
                    return $"Pocket_{defaultVoice}";
                }

                // Check if the voice is in our discovered list
                if (pocketVoiceModels.ContainsKey(voice))
                    return voice;

                // Try with Pocket_ prefix if not already present
                if (!voice.StartsWith("Pocket_", StringComparison.OrdinalIgnoreCase))
                    return "";

                // Strip Pocket_ prefix and check if it's a known predefined voice
                var stripped = voice.Substring(7);
                if (PocketPredefinedVoices.Contains(stripped, StringComparer.OrdinalIgnoreCase))
                    return voice;

                // Not a recognized voice
                return "";
            }

            // Check for Coqui TTS voices - any voice starting with "CoQui" prefix
            if (voice.StartsWith("CoQui", StringComparison.OrdinalIgnoreCase))
            {
                if (!_enableCoquiTTS)
                    return "";

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
            if (!_enableSystemSpeech)
                return "";

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
            var sections = new List<string>();

            // System.Speech voices
            if (_enableSystemSpeech)
            {
                if (voices.Count == 0)
                {
                    var synth = new SpeechSynthesizer();
                    voices = synth.GetInstalledVoices().Select(x => x.VoiceInfo.Name).ToList<string>();
                    synth.Dispose();
                }
                var systemVoices = string.Join("\n", voices.Select(v => $"• {v}"));
                sections.Add($"**System Voices:**\n{systemVoices}");
            }

            // Coqui TTS voices
            if (_enableCoquiTTS)
            {
                LoadCoquiVoiceConfiguration();
                var coquiVoiceList = coquiVoiceModels.Count > 0
                    ? string.Join("\n", coquiVoiceModels.Keys.Select(v => $"• {v}"))
                    : "• No Coqui voices configured";
                sections.Add($"**Coqui TTS Voices:**\n{coquiVoiceList}");
            }

            // PocketTTS voices
            if (_enablePocketTTS)
            {
                var predefined = pocketVoiceModels
                    .Where(kv => PocketPredefinedVoices.Contains(kv.Value, StringComparer.OrdinalIgnoreCase))
                    .Select(kv => $"• {kv.Key}")
                    .ToList();

                var custom = pocketVoiceModels
                    .Where(kv => !PocketPredefinedVoices.Contains(kv.Value, StringComparer.OrdinalIgnoreCase))
                    .Select(kv => $"• {kv.Key}")
                    .ToList();

                var pocketSection = "**PocketTTS Voices:**\n";
                if (predefined.Count > 0)
                    pocketSection += $"_Predefined:_\n{string.Join("\n", predefined)}\n";
                if (custom.Count > 0)
                    pocketSection += $"_Custom:_\n{string.Join("\n", custom)}\n";
                if (predefined.Count == 0 && custom.Count == 0)
                    pocketSection += "• No PocketTTS voices available\n";
                pocketSection += "\nUpload custom voices with `!tts uploadvoice <name>` (attach .wav/.mp3/.safetensors)";

                sections.Add(pocketSection);
            }

            if (sections.Count == 0)
            {
                await channel.SendMessageAsync(new MessageProperties { Content = "No TTS engines are enabled." });
                return;
            }
            
            await channel.SendMessageAsync(new MessageProperties
            {
                Content = $"**Available TTS Voices:**\n\n" +
                         string.Join("\n\n", sections) + "\n\n" +
                         $"Use `!tts changevoice <voice_name>` to change your voice."
            });
        }
    }

}

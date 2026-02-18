using MumbleSharp;
using MumbleSharp.Audio;
using MumbleSharp.Model;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace DiscordBotTTS
{
    /// <summary>
    /// Manages the Mumble voice connection: auto-connect, text chat commands,
    /// channel management, and audio transmission.
    /// </summary>
    public class MumbleModule
    {
        private static MumbleConnection _connection;
        private static TTSBotMumbleProtocol _protocol;
        private static Thread _processThread;
        private static bool _enabled;
        private static bool _connected;
        private static readonly object _lock = new object();
        private static CancellationTokenSource _cts;

        // Config values cached at startup
        private static string _serverHost;
        private static int _serverPort;
        private static string _username;
        private static string _password;
        private static string _defaultChannel;
        private static int _reconnectInterval;

        /// <summary>True when Mumble is enabled in config and currently connected.</summary>
        public static bool IsConnected => _connected && _connection?.State == ConnectionStates.Connected;

        /// <summary>True when Mumble support is enabled in config (regardless of connection state).</summary>
        public static bool IsEnabled => _enabled;

        /// <summary>Current Mumble channel the bot resides in, or null.</summary>
        public static string CurrentChannelName => _protocol?.LocalUser?.Channel?.Name;

        private static void Log(string msg, string level = "Info")
        {
            Console.WriteLine($"{DateTime.Now:s}:Mumble:{level}: {msg}");
        }

        // ───────────────────── Lifecycle ─────────────────────

        /// <summary>
        /// Pre-load the native opus.dll that MumbleSharp requires.
        /// MumbleSharp's NativeMethods static ctor loads it via kernel32 LoadLibrary
        /// from AppDomain.BaseDirectory\Audio\Codecs\Opus\Libs\64bit\opus.dll.
        /// If LoadLibrary fails silently, all P/Invoke delegates stay null and the
        /// encoding thread crashes with NullReferenceException. We pre-load here
        /// with NativeLibrary so we get a proper error message.
        /// </summary>
        private static bool PreLoadOpusNative()
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            // This is the EXACT path MumbleSharp's NativeMethods static ctor hardcodes
            // via LibraryLoader.Load(kernel32.LoadLibrary). If the file isn't here,
            // LoadLibrary fails silently and all opus delegates stay null.
            var mumbleExpectedPath = Path.Combine(baseDir, "Audio", "Codecs", "Opus", "Libs", "64bit", "opus.dll");

            Log($"MumbleSharp expects opus.dll at: {mumbleExpectedPath}");

            if (File.Exists(mumbleExpectedPath))
            {
                Log("opus.dll found at expected MumbleSharp path");
                return true; // MumbleSharp will load it itself — no intervention needed
            }

            // opus.dll isn't where MumbleSharp expects it — find it and put it there
            Log("opus.dll NOT at expected MumbleSharp path — searching...", "Warning");
            string foundPath = null;

            // Check directly in the app directory (common for flat publish layouts)
            var flatPath = Path.Combine(baseDir, "opus.dll");
            if (File.Exists(flatPath))
            {
                foundPath = flatPath;
                Log($"Found opus.dll in app directory: {foundPath}");
            }
            else
            {
                // Search recursively as last resort
                var altSearch = Directory.GetFiles(baseDir, "opus.dll", SearchOption.AllDirectories);
                if (altSearch.Length > 0)
                {
                    foundPath = altSearch.FirstOrDefault(f => f.Contains("64bit", StringComparison.OrdinalIgnoreCase))
                                ?? altSearch[0];
                    Log($"Found opus.dll at alternate path: {foundPath}");
                }
            }

            if (foundPath == null)
            {
                Log("No opus.dll found anywhere in application directory!", "Error");
                return false;
            }

            // Create the directory structure MumbleSharp expects and copy opus.dll there.
            // MumbleSharp uses kernel32.LoadLibrary with this exact absolute path —
            // pre-loading via NativeLibrary.Load does NOT help because LoadLibrary
            // with a full path that doesn't exist still returns NULL.
            try
            {
                var targetDir = Path.GetDirectoryName(mumbleExpectedPath);
                Directory.CreateDirectory(targetDir);
                File.Copy(foundPath, mumbleExpectedPath, overwrite: true);
                Log($"Copied opus.dll to MumbleSharp expected path: {mumbleExpectedPath}");
                return true;
            }
            catch (Exception ex)
            {
                Log($"Failed to copy opus.dll to expected path: {ex.Message}", "Error");
            }

            // Fallback: at least try NativeLibrary.Load so it's in the process
            try
            {
                var handle = NativeLibrary.Load(foundPath);
                if (handle != IntPtr.Zero)
                {
                    Log($"Pre-loaded opus.dll via NativeLibrary (handle=0x{handle:X}) — MumbleSharp may still fail", "Warning");
                    return true;
                }
                else
                {
                    Log("NativeLibrary.Load returned zero handle for opus.dll", "Error");
                    return false;
                }
            }
            catch (DllNotFoundException ex)
            {
                Log($"Failed to load opus.dll — DLL not found: {ex.Message}", "Error");
                Log("This may mean a dependency of opus.dll (e.g. vcruntime140.dll) is missing.", "Error");
                Log("Install the Visual C++ Redistributable: https://aka.ms/vs/17/release/vc_redist.x64.exe", "Error");
                return false;
            }
            catch (Exception ex)
            {
                Log($"Failed to pre-load opus.dll: {ex.GetType().Name}: {ex.Message}", "Error");
                return false;
            }
        }

        /// <summary>Load config and auto-connect if enabled.</summary>
        public static async Task InitializeAsync()
        {
            _enabled = bool.TryParse(ConfigurationManager.AppSettings.Get("EnableMumble"), out var em) && em;
            if (!_enabled)
            {
                Log("Mumble disabled via config — skipping initialization");
                return;
            }

            _serverHost = ConfigurationManager.AppSettings.Get("Mumble_ServerHost") ?? "localhost";
            _serverPort = int.TryParse(ConfigurationManager.AppSettings.Get("Mumble_ServerPort"), out var p) ? p : 64738;
            _username = ConfigurationManager.AppSettings.Get("Mumble_Username") ?? "TTSBot";
            _password = ConfigurationManager.AppSettings.Get("Mumble_Password") ?? "";
            _defaultChannel = ConfigurationManager.AppSettings.Get("Mumble_DefaultChannel") ?? "";
            _reconnectInterval = int.TryParse(ConfigurationManager.AppSettings.Get("Mumble_ReconnectInterval"), out var ri) ? ri : 10;

            Log($"Mumble config — host={_serverHost}:{_serverPort}, user={_username}, defaultChannel={_defaultChannel}");

            // Pre-load opus native library before MumbleSharp tries to use it
            if (!PreLoadOpusNative())
            {
                Log("Cannot initialize Mumble without opus.dll — disabling Mumble support", "Error");
                _enabled = false;
                return;
            }

            _cts = new CancellationTokenSource();
            await ConnectAsync();
        }

        private static async Task ConnectAsync()
        {
            try
            {
                Log($"Connecting to Mumble server {_serverHost}:{_serverPort} as '{_username}'...");

                _protocol = new TTSBotMumbleProtocol();
                _connection = new MumbleConnection(_serverHost, _serverPort, _protocol);

                _connection.Connect(_username, _password, tokens: Array.Empty<string>(), serverName: _serverHost);

                // Start the mandatory network processing loop
                _processThread = new Thread(ProcessLoop) { IsBackground = true, Name = "MumbleProcess" };
                _processThread.Start();

                // Wait for server sync (up to 15 seconds)
                var timeout = DateTime.UtcNow.AddSeconds(15);
                while (!_protocol.ReceivedServerSync && DateTime.UtcNow < timeout)
                {
                    await Task.Delay(50);
                }

                if (!_protocol.ReceivedServerSync)
                {
                    Log("Mumble server sync timed out after 15s", "Warning");
                    return;
                }

                _connected = true;
                Log($"Connected to Mumble as '{_protocol.LocalUser?.Name}' (ID {_protocol.LocalUser?.Id})");
                Log($"Current channel: {_protocol.LocalUser?.Channel?.Name}");

                // Auto-join default channel if configured
                if (!string.IsNullOrEmpty(_defaultChannel))
                {
                    JoinChannel(_defaultChannel);
                }

                // List available channels
                var channels = _protocol.Channels?.ToList() ?? new List<Channel>();
                Log($"Available channels: {string.Join(", ", channels.Select(c => c.Name))}");
            }
            catch (Exception ex)
            {
                Log($"Failed to connect to Mumble: {ex.Message}", "Error");
                _connected = false;
                // Schedule reconnect
                _ = Task.Run(async () => await ReconnectLoopAsync());
            }
        }

        private static async Task ReconnectLoopAsync()
        {
            while (!_cts.IsCancellationRequested && !_connected)
            {
                Log($"Reconnecting to Mumble in {_reconnectInterval}s...");
                await Task.Delay(_reconnectInterval * 1000, _cts.Token).ConfigureAwait(false);
                if (!_cts.IsCancellationRequested)
                {
                    await ConnectAsync();
                }
            }
        }

        private static void ProcessLoop()
        {
            try
            {
                while (_connection != null && _connection.State != ConnectionStates.Disconnected)
                {
                    try
                    {
                        if (_connection.Process())
                            Thread.Yield();
                        else
                            Thread.Sleep(1);
                    }
                    catch (Exception ex)
                    {
                        Log($"Process loop error: {ex.Message}", "Warning");
                        Thread.Sleep(100);
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"Process loop fatal: {ex.Message}", "Error");
            }

            _connected = false;
            Log("Mumble process loop ended — connection lost");

            // Trigger reconnect unless shutting down
            if (_cts != null && !_cts.IsCancellationRequested)
            {
                _ = Task.Run(async () => await ReconnectLoopAsync());
            }
        }

        /// <summary>Graceful shutdown.</summary>
        public static void Cleanup()
        {
            if (!_enabled) return;
            try
            {
                Log("Shutting down Mumble connection...");
                _cts?.Cancel();
                _connected = false;
                _protocol?.Close();
                _connection?.Close();
                _processThread = null;
                Log("Mumble connection closed");
            }
            catch (Exception ex)
            {
                Log($"Error during Mumble cleanup: {ex.Message}", "Warning");
            }
        }

        // ───────────────────── Channel Management ─────────────────────

        /// <summary>Join a Mumble channel by name.</summary>
        public static bool JoinChannel(string channelName)
        {
            if (!IsConnected) { Log("Not connected to Mumble", "Warning"); return false; }

            var target = _protocol.Channels?
                .FirstOrDefault(c => c.Name.Equals(channelName, StringComparison.OrdinalIgnoreCase));

            if (target == null)
            {
                var available = string.Join(", ", _protocol.Channels?.Select(c => c.Name) ?? Array.Empty<string>());
                Log($"Channel '{channelName}' not found. Available: {available}", "Warning");
                return false;
            }

            target.Join();
            Log($"Joined Mumble channel: {target.Name}");
            return true;
        }

        /// <summary>Get list of available Mumble channels.</summary>
        public static List<string> GetChannels()
        {
            if (!IsConnected) return new List<string>();
            return _protocol.Channels?.Select(c => c.Name).ToList() ?? new List<string>();
        }

        /// <summary>Get list of users in the bot's current channel.</summary>
        public static List<string> GetCurrentChannelUsers()
        {
            if (!IsConnected) return new List<string>();
            return _protocol.LocalUser?.Channel?.Users?.Select(u => u.Name).ToList() ?? new List<string>();
        }

        // ───────────────────── Audio Transmission ─────────────────────

        /// <summary>
        /// Send PCM audio to the current Mumble channel with real-time pacing.
        /// Expects 48 kHz, 16-bit, MONO, little-endian PCM.
        ///
        /// IMPORTANT: MumbleSharp has a sequenceIndex bug — it always increments
        /// the Mumble audio sequence number by 1 per encoded frame, regardless of
        /// frame duration. In Mumble protocol, each sequence unit = 10ms (480 samples
        /// at 48kHz). So we MUST feed exactly 960 bytes (480 samples = 10ms) at a
        /// time to ensure the encoder picks 480-sample frames and sequenceIndex++
        /// is correct. Larger frames (20ms, 60ms) would play too fast because the
        /// Sends mono 48 kHz 16-bit PCM audio to Mumble with real-time pacing.
        /// Audio is fed in 20ms chunks (MumbleSharp default frame size) at wall-clock
        /// rate so the receiver's jitter buffer gets a smooth stream of UDP packets.
        /// </summary>
        public static async Task SendPcmAudioAsync(byte[] pcmMono48k)
        {
            if (!IsConnected)
            {
                Log("Cannot send audio — not connected to Mumble", "Warning");
                return;
            }

            try
            {
                // 48 kHz, 16-bit mono = 2 bytes/sample
                // 20ms frame = 960 samples = 1920 bytes (MumbleSharp default)
                const int bytesPerFrame = 960 * 2; // 1920 bytes = 20ms
                const int frameDurationMs = 20;

                int totalBytes = pcmMono48k.Length;
                int offset = 0;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                int frameIndex = 0;

                Log($"Sending {totalBytes} bytes ({totalBytes / (48000.0 * 2):F1}s) of audio to Mumble");

                while (offset < totalBytes)
                {
                    int chunkSize = Math.Min(bytesPerFrame, totalBytes - offset);
                    _protocol.SendVoice(
                        new ArraySegment<byte>(pcmMono48k, offset, chunkSize),
                        SpeechTarget.Normal,
                        0);
                    offset += chunkSize;
                    frameIndex++;

                    // Pace to real-time for smooth network delivery
                    if (offset < totalBytes)
                    {
                        long expectedElapsedMs = (long)frameIndex * frameDurationMs;
                        long actualElapsedMs = sw.ElapsedMilliseconds;
                        int sleepMs = (int)(expectedElapsedMs - actualElapsedMs);
                        if (sleepMs > 0)
                            await Task.Delay(sleepMs);
                    }
                }

                // Give the encoding thread time to flush the last frame
                await Task.Delay(60);
                _protocol.SendVoiceStop();
                Log($"Mumble audio complete: {totalBytes} bytes, {frameIndex} frames in {sw.ElapsedMilliseconds}ms");
            }
            catch (Exception ex)
            {
                Log($"Error sending audio to Mumble: {ex.Message}", "Error");
            }
        }

        /// <summary>
        /// Converts stereo 48 kHz 16-bit PCM to mono by averaging left+right channels.
        /// The existing TTS pipeline produces stereo; Mumble needs mono.
        /// </summary>
        public static byte[] StereoToMono(byte[] stereoPcm)
        {
            // Each stereo sample frame = 4 bytes (2 bytes left + 2 bytes right)
            int frameCount = stereoPcm.Length / 4;
            byte[] mono = new byte[frameCount * 2];

            for (int i = 0; i < frameCount; i++)
            {
                int srcIdx = i * 4;
                short left = (short)(stereoPcm[srcIdx] | (stereoPcm[srcIdx + 1] << 8));
                short right = (short)(stereoPcm[srcIdx + 2] | (stereoPcm[srcIdx + 3] << 8));
                short mixed = (short)((left + right) / 2);
                int dstIdx = i * 2;
                mono[dstIdx] = (byte)(mixed & 0xFF);
                mono[dstIdx + 1] = (byte)((mixed >> 8) & 0xFF);
            }

            return mono;
        }

        // ───────────────────── Text Chat ─────────────────────

        /// <summary>Send a text message to the bot's current Mumble channel.</summary>
        public static void SendChannelMessage(string text)
        {
            if (!IsConnected) return;
            try
            {
                _protocol.LocalUser?.Channel?.SendMessage(text, recursive: false);
            }
            catch (Exception ex)
            {
                Log($"Error sending channel message: {ex.Message}", "Warning");
            }
        }

        /// <summary>Get a formatted status string for display.</summary>
        public static string GetStatusText()
        {
            if (!_enabled) return "Mumble: Disabled";
            if (!IsConnected) return "Mumble: Disconnected";
            var channel = _protocol.LocalUser?.Channel?.Name ?? "unknown";
            var users = GetCurrentChannelUsers();
            return $"Mumble: Connected to {_serverHost}:{_serverPort}\n" +
                   $"Channel: {channel}\n" +
                   $"Users: {string.Join(", ", users)}";
        }

        // ───────────────────── Mumble Chat Command Handler ─────────────────────

        /// <summary>
        /// Process an incoming Mumble text message as a bot command.
        /// Supported commands:
        ///   !join &lt;channel&gt;   — move bot to a channel
        ///   !channels          — list available channels
        ///   !status            — show bot status
        ///   !help              — show help
        /// </summary>
        internal static void HandleMumbleChatCommand(string senderName, string text, bool isChannel)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            text = text.Trim();

            // Strip HTML tags (Mumble text chat sends HTML)
            text = System.Text.RegularExpressions.Regex.Replace(text, "<.*?>", "");
            text = System.Net.WebUtility.HtmlDecode(text);

            if (!text.StartsWith("!")) return;

            var parts = text.Substring(1).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return;

            var cmd = parts[0].ToLowerInvariant();
            Log($"Mumble command from {senderName}: {text}");

            switch (cmd)
            {
                case "join":
                    if (parts.Length < 2)
                    {
                        SendChannelMessage("Usage: !join <channel name>");
                        return;
                    }
                    var channelName = string.Join(" ", parts.Skip(1));
                    if (JoinChannel(channelName))
                        SendChannelMessage($"Moved to channel: {channelName}");
                    else
                        SendChannelMessage($"Channel '{channelName}' not found. Use !channels to list.");
                    break;

                case "channels":
                    var channels = GetChannels();
                    SendChannelMessage($"Available channels: {string.Join(", ", channels)}");
                    break;

                case "status":
                    SendChannelMessage(GetStatusText());
                    break;

                case "help":
                    SendChannelMessage(
                        "Bot commands:\n" +
                        "  !join <channel> — Move bot to a channel\n" +
                        "  !channels — List available channels\n" +
                        "  !status — Show bot status\n" +
                        "  !help — Show this help");
                    break;

                default:
                    SendChannelMessage($"Unknown command: {cmd}. Try !help");
                    break;
            }
        }
    }

    // ───────────────────── Mumble Protocol Implementation ─────────────────────

    /// <summary>
    /// Custom Mumble protocol handler that routes text chat messages
    /// to the bot's command handler.
    /// </summary>
    public class TTSBotMumbleProtocol : BasicMumbleProtocol
    {
        protected override void ChannelMessageReceived(ChannelMessage message)
        {
            Console.WriteLine($"{DateTime.Now:s}:Mumble:Info: [#{message.Channel?.Name}] {message.Sender?.Name}: {message.Text}");
            MumbleModule.HandleMumbleChatCommand(message.Sender?.Name ?? "unknown", message.Text, isChannel: true);
            base.ChannelMessageReceived(message);
        }

        protected override void PersonalMessageReceived(PersonalMessage message)
        {
            Console.WriteLine($"{DateTime.Now:s}:Mumble:Info: [DM] {message.Sender?.Name}: {message.Text}");
            MumbleModule.HandleMumbleChatCommand(message.Sender?.Name ?? "unknown", message.Text, isChannel: false);
            base.PersonalMessageReceived(message);
        }

        protected override void UserJoined(User user)
        {
            Console.WriteLine($"{DateTime.Now:s}:Mumble:Info: User joined: {user.Name}");
            base.UserJoined(user);
        }

        protected override void UserLeft(User user)
        {
            Console.WriteLine($"{DateTime.Now:s}:Mumble:Info: User left: {user.Name}");
            base.UserLeft(user);
        }
    }
}

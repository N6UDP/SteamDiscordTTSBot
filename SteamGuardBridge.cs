using System;
using System.Threading;
using System.Threading.Tasks;
using NetCord.Rest;
using SteamKit2.Authentication;

namespace DiscordBotTTS
{
    // Bridges Steam's IAuthenticator (which needs a Steam Guard code during the new
    // token-based login flow) to Discord: when Steam asks for a code, we post a prompt
    // to a configured Discord channel and wait for an operator to reply with the code
    // in that same channel. The code is also logged to the console as a fallback.
    public static class SteamGuardBridge
    {
        private static readonly object _lock = new object();
        private static TaskCompletionSource<string> _pending;

        // Wired up from Program.cs once the Discord client is available.
        public static RestClient Rest;
        public static ulong PromptChannelId;
        public static volatile bool DiscordReady;

        private static void Log(string msg, string level = "Info")
            => Console.WriteLine($"{DateTime.Now:s}:SteamGuard:{level}: {msg}");

        // Called by the Discord message handler. Returns true if a code request was
        // pending in this channel and the message was consumed as the code.
        public static bool TrySubmitCode(ulong channelId, string content)
        {
            lock (_lock)
            {
                if (_pending == null || channelId != PromptChannelId)
                    return false;

                var code = (content ?? string.Empty).Trim();
                if (string.IsNullOrEmpty(code))
                    return false;

                var tcs = _pending;
                _pending = null;
                tcs.TrySetResult(code);
                return true;
            }
        }

        // Called by the authenticator. Posts the prompt to Discord (and console) and
        // waits up to <paramref name="timeout"/> for a reply. Throws TimeoutException
        // if no code arrives in time.
        public static async Task<string> RequestCodeAsync(string prompt, TimeSpan timeout)
        {
            TaskCompletionSource<string> tcs;
            lock (_lock)
            {
                // Abandon any stale request before starting a new one.
                _pending?.TrySetCanceled();
                _pending = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                tcs = _pending;
            }

            await PostPromptAsync(prompt, timeout);

            using var cts = new CancellationTokenSource(timeout);
            using var reg = cts.Token.Register(() => tcs.TrySetCanceled());
            try
            {
                return await tcs.Task;
            }
            catch (OperationCanceledException)
            {
                lock (_lock)
                {
                    if (_pending == tcs)
                        _pending = null;
                }
                throw new TimeoutException("Timed out waiting for a Steam Guard code from Discord.");
            }
        }

        private static async Task PostPromptAsync(string prompt, TimeSpan timeout)
        {
            // Wait (up to the timeout) for the Discord gateway to be ready so the prompt
            // actually lands. On first-ever login Steam may connect before Discord does.
            var waitUntil = DateTime.UtcNow + timeout;
            while (!DiscordReady && DateTime.UtcNow < waitUntil)
                await Task.Delay(500);

            // Always log to console as a fallback so the operator can see the request
            // even if Discord is unavailable.
            Log(prompt, "Action");

            if (Rest == null || PromptChannelId == 0)
            {
                Log("No Discord prompt channel configured (set SteamGuard_DiscordChannelId in App.config).", "Warn");
                return;
            }

            if (!DiscordReady)
            {
                Log("Discord gateway not ready; could not post the Steam Guard prompt to Discord.", "Warn");
                return;
            }

            try
            {
                await Rest.SendMessageAsync(PromptChannelId, new MessageProperties { Content = "🔐 " + prompt });
            }
            catch (Exception ex)
            {
                Log($"Failed to post the Steam Guard prompt to Discord: {ex.Message}", "Error");
            }
        }
    }

    // IAuthenticator implementation that sources Steam Guard codes from Discord.
    public class DiscordAuthenticator : IAuthenticator
    {
        private static readonly TimeSpan CodeTimeout = TimeSpan.FromMinutes(5);

        public Task<string> GetEmailCodeAsync(string email, bool previousCodeWasIncorrect)
        {
            var prompt = previousCodeWasIncorrect
                ? $"That Steam Guard code was incorrect. Reply in this channel with the new code emailed to {email}."
                : $"Steam needs a Steam Guard code to log in. Reply in this channel with the code emailed to {email}.";
            return SteamGuardBridge.RequestCodeAsync(prompt, CodeTimeout);
        }

        public Task<string> GetDeviceCodeAsync(bool previousCodeWasIncorrect)
        {
            var prompt = previousCodeWasIncorrect
                ? "That Steam Guard code was incorrect. Reply in this channel with the current code from your Steam Mobile Authenticator."
                : "Steam needs a Steam Guard code to log in. Reply in this channel with the current code from your Steam Mobile Authenticator.";
            return SteamGuardBridge.RequestCodeAsync(prompt, CodeTimeout);
        }

        public Task<bool> AcceptDeviceConfirmationAsync()
        {
            // These accounts have no mobile authenticator to approve a confirmation on,
            // so decline and fall back to entering a code instead of waiting forever.
            return Task.FromResult(false);
        }
    }
}

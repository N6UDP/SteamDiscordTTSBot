using System;

namespace DiscordBotTTS
{
    // Capped exponential backoff with jitter, shared by the Steam and Discord
    // reconnect loops. Delay grows as baseSeconds * 2^(attempt-1), capped at
    // maxSeconds, then has +/- 20% random jitter applied to avoid thundering-herd
    // reconnection storms.
    internal static class Backoff
    {
        private static readonly Random _random = new Random();
        private static readonly object _lock = new object();

        public static TimeSpan Compute(int attempt, double baseSeconds = 5, double maxSeconds = 300)
        {
            if (attempt < 1) attempt = 1;

            // Clamp the exponent so Math.Pow can't overflow on long outages.
            double seconds = baseSeconds * Math.Pow(2, Math.Min(attempt - 1, 16));
            seconds = Math.Min(seconds, maxSeconds);

            double roll;
            lock (_lock)
            {
                roll = _random.NextDouble();
            }

            double jitter = seconds * 0.2 * (roll * 2 - 1);
            return TimeSpan.FromSeconds(Math.Max(1, seconds + jitter));
        }
    }
}

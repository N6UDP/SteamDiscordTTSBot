using NetCord.Gateway;
using NetCord.Rest;
using System.Reflection;
using System.Threading.Tasks;
using System.Linq;
using NetCord;

namespace DiscordBotTTS
{
    public class CommandHandler
    {
        private readonly GatewayClient _client;
        private readonly RestClient _restClient;

        // Retrieve client and RestClient instance via ctor
        public CommandHandler(GatewayClient client, RestClient restClient)
        {
            _restClient = restClient;
            _client = client;
        }

        public async Task InstallCommandsAsync()
        {
            // Hook the MessageReceived event into our command handler
            _client.MessageCreate += HandleCommandAsync;
        }

        private ValueTask HandleCommandAsync(NetCord.Gateway.Message message)
        {
            // Don't process the command if it was a system message or bot message
            if (message.Author.IsBot) return ValueTask.CompletedTask;

            // Create a number to track where the prefix ends and the command begins
            int argPos = 0;
            var content = message.Content;

            // Determine if the message is a command based on the prefix
            if (!content.StartsWith('!'))
                return ValueTask.CompletedTask;

            argPos = 1; // Skip the '!' prefix

            // Simple command parsing - split by spaces
            var args = content.Substring(argPos).Split(' ');
            if (args.Length == 0) return ValueTask.CompletedTask;

            var command = args[0].ToLower();

            // Handle basic commands here for now
            // This is simplified compared to Discord.Net's command framework
            switch (command)
            {
                case "tts":
                    // Handle TTS commands - delegate to TTSModule methods directly
                    break;
                case "say":
                    // Handle say command - delegate to InfoModule functionality
                    if (args.Length > 1)
                    {
                        var response = string.Join(" ", args.Skip(1));
                        _ = Task.Run(async () =>
                        {
                            await _restClient.SendMessageAsync(message.ChannelId, new MessageProperties { Content = response });
                        });
                    }
                    break;
            }

            return ValueTask.CompletedTask;
        }
    }
}

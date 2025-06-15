using NetCord.Rest;
using NetCord;
using System.Threading.Tasks;

namespace DiscordBotTTS
{
    public class InfoModule
    {
        public async Task SayAsync(string echo, TextChannel channel)
        {
            await channel.SendMessageAsync(new MessageProperties { Content = echo });
        }
    }
}

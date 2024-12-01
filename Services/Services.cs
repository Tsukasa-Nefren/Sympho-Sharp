using CounterStrikeSharp.API.Core;
using Microsoft.Extensions.DependencyInjection;
using Sympho.Functions;

namespace Sympho.Services
{
    public class SymphoInjection : IPluginServiceCollection<Sympho>
    {
        public void ConfigureServices(IServiceCollection service)
        {
            service.AddSingleton<Sympho>();
            service.AddSingleton<Youtube>();
            service.AddSingleton<AudioHandler>();
        }
    }
}

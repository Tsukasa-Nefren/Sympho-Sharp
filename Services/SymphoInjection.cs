using CounterStrikeSharp.API.Core;
using Microsoft.Extensions.DependencyInjection;
using Sympho.Functions;
using Sympho.Models;

namespace Sympho.Services
{
    public class SymphoInjection : IPluginServiceCollection<Sympho>
    {
        public void ConfigureServices(IServiceCollection service)
        {
            // 모든 주요 서비스를 싱글톤으로 등록하여 앱 전체에서 단 하나의 인스턴스만 사용하도록 합니다.
            service.AddSingleton<Sympho>();
            service.AddSingleton<Youtube>();
            service.AddSingleton<AudioHandler>();
            service.AddSingleton<Subtitles>();
            service.AddSingleton<CacheManager>();
        }
    }
}
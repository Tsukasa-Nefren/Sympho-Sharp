using System;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Events;

namespace Sympho
{
    public sealed class Event
    {
        private readonly Sympho _plugin;

        public Event(Sympho plugin)
        {
            _plugin = plugin;
        }

        [GameEventHandler]
        public HookResult OnPlayerChat(EventPlayerChat @event, GameEventInfo info)
        {
            var text = (@event.Text ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(text))
                return HookResult.Continue;
            
            var player = Utilities.GetPlayerFromUserid(@event.Userid);
            if (player == null || !player.IsValid)
                return HookResult.Continue;
            
            if (text.StartsWith("!yt ", StringComparison.OrdinalIgnoreCase))
            {
                var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2)
                {
                    var url = parts[1];
                    var startSeconds = 0;

                    if (parts.Length >= 3 && int.TryParse(parts[2], out var parsed))
                        startSeconds = parsed;
                    
                    Action<string> replyToChat = msg => player.PrintToChat(msg);
                    _plugin.EnqueueYoutubeFromChat(player, url, startSeconds, replyToChat);
                }
                
                return HookResult.Stop;
            }
            
            if (text.Equals("!stopall", StringComparison.OrdinalIgnoreCase))
            {
                if (AdminManager.PlayerHasPermissions(player, "@css/kick"))
                {
                    _plugin.StopAllSound(player);
                }
                return HookResult.Stop;
            }

            return HookResult.Continue;
        }
    }
}
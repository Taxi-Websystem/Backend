using System.Collections.Concurrent;
using System.Security.Claims;
using Backend.Data;
using Backend.Models.Enums;
using Backend.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Backend.Hubs;

[Authorize]
public class PresenceHub : Hub
{
    private static readonly ConcurrentDictionary<int, int> _activeConnections = new();
    public static bool HasActiveConnections(int userId)
        => _activeConnections.TryGetValue(userId, out var count) && count > 0;

    private readonly ApplicationDbContext _context;
    private readonly IUserSettingsService _userSettingsService;

    public PresenceHub(ApplicationDbContext context, IUserSettingsService userSettingsService)
    {
        _context = context;
        _userSettingsService = userSettingsService;
    }

    public override async Task OnConnectedAsync()
    {
        var userId = GetUserId();
        if (userId is not null)
        {
            _activeConnections.AddOrUpdate(userId.Value, 1, static (_, count) => count + 1);

            var settings = await _userSettingsService.GetOrCreateAsync(userId.Value);
            if (settings.IsAutoStatusEnabled)
            {
                var profile = await _context.UserProfiles
                    .FirstOrDefaultAsync(p => p.UserId == userId.Value);
                if (profile is not null && profile.UserStatus == UserStatus.Offline)
                {
                    profile.UserStatus = UserStatus.Online;
                    await _context.SaveChangesAsync();
                    await BroadcastStatusChanged(userId.Value, UserStatus.Online);
                }
            }
        }

        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var userId = GetUserId();
        if (userId is not null)
        {
            var count = _activeConnections.AddOrUpdate(userId.Value, 0, static (_, current) => Math.Max(0, current - 1));
            if (count <= 0)
            {
                _activeConnections.TryRemove(userId.Value, out _);
            }

            if (count == 0)
            {
                var profile = await _context.UserProfiles
                    .FirstOrDefaultAsync(p => p.UserId == userId.Value);

                if (profile is not null && profile.UserStatus != UserStatus.InRide)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5));

                    if (!_activeConnections.TryGetValue(userId.Value, out var activeAfterDelay) || activeAfterDelay <= 0)
                    {
                        var settings = await _userSettingsService.GetOrCreateAsync(userId.Value);
                        if (settings.IsAutoStatusEnabled)
                        {
                            profile = await _context.UserProfiles
                                .FirstOrDefaultAsync(p => p.UserId == userId.Value);

                            if (profile is not null && profile.UserStatus != UserStatus.InRide)
                            {
                                profile.UserStatus = UserStatus.Offline;
                                await _context.SaveChangesAsync();
                                await BroadcastStatusChanged(userId.Value, UserStatus.Offline);
                            }
                        }
                    }
                }
            }
        }

        await base.OnDisconnectedAsync(exception);
    }

    private int? GetUserId()
    {
        var idClaim = Context.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        return int.TryParse(idClaim, out var userId) ? userId : null;
    }

    private Task BroadcastStatusChanged(int userId, UserStatus status)
        => Task.WhenAll(
            Clients.All.SendAsync("PresenceChanged", new { userId, status = status.ToString() }),
            Clients.All.SendAsync("DashboardDataChanged", new { entity = "presence", action = "status", userId }));
}

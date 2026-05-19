using System.Collections.Concurrent;
using System.Security.Claims;
using Backend.Data;
using Backend.Models;
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

    public static bool HasActiveConnections(int userId) =>
        _activeConnections.TryGetValue(userId, out var connectionCount) && connectionCount > 0;

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
            RegisterConnection(userId.Value);
            await TryPromoteToOnlineAsync(userId.Value);
        }

        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var userId = GetUserId();
        if (userId is not null)
        {
            var remainingConnections = UnregisterConnection(userId.Value);
            if (remainingConnections == 0)
            {
                await TryPromoteToOfflineAsync(userId.Value);
            }
        }

        await base.OnDisconnectedAsync(exception);
    }

    private static void RegisterConnection(int userId) =>
        _activeConnections.AddOrUpdate(userId, 1, static (_, count) => count + 1);

    private static int UnregisterConnection(int userId)
    {
        var remainingConnections = _activeConnections.AddOrUpdate(userId, 0, static (_, current) => Math.Max(0, current - 1));
        if (remainingConnections <= 0)
        {
            _activeConnections.TryRemove(userId, out _);
        }

        return remainingConnections;
    }

    private async Task TryPromoteToOnlineAsync(int userId)
    {
        var userSettings = await _userSettingsService.GetOrCreateAsync(userId);
        if (!userSettings.IsAutoStatusEnabled)
            return;

        var profile = await _context.UserProfiles.FirstOrDefaultAsync(p => p.UserId == userId);
        if (profile is null || profile.UserStatus != UserStatus.Offline)
            return;

        profile.UserStatus = UserStatus.Online;
        await _context.SaveChangesAsync();
        await BroadcastStatusChanged(userId, UserStatus.Online);
    }

    private async Task TryPromoteToOfflineAsync(int userId)
    {
        var profile = await _context.UserProfiles.FirstOrDefaultAsync(p => p.UserId == userId);
        if (profile is null || profile.UserStatus == UserStatus.InRide)
            return;

        await Task.Delay(TimeSpan.FromSeconds(5));

        if (HasActiveConnections(userId))
            return;

        var userSettings = await _userSettingsService.GetOrCreateAsync(userId);
        if (!userSettings.IsAutoStatusEnabled)
            return;

        profile = await _context.UserProfiles.FirstOrDefaultAsync(p => p.UserId == userId);
        if (profile is null || profile.UserStatus == UserStatus.InRide)
            return;

        profile.UserStatus = UserStatus.Offline;
        await _context.SaveChangesAsync();
        await BroadcastStatusChanged(userId, UserStatus.Offline);
    }

    private int? GetUserId()
    {
        var idClaim = Context.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        return int.TryParse(idClaim, out var userId) ? userId : null;
    }

    private Task BroadcastStatusChanged(int userId, UserStatus status) =>
        Task.WhenAll(
            Clients.All.SendAsync("PresenceChanged", new { userId, status = status.ToString() }),
            Clients.All.SendAsync("DashboardDataChanged", new { entity = "presence", action = "status", userId }));
}

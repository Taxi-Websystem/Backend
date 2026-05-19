using Backend.Data;
using Backend.Models;
using Backend.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Backend.Services;

public interface IUserSettingsService
{
    Task<UserSettings> GetOrCreateAsync(int userId, CancellationToken cancellationToken = default);
}

public class UserSettingsService : IUserSettingsService
{
    private readonly ApplicationDbContext _context;

    public UserSettingsService(ApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<UserSettings> GetOrCreateAsync(int userId, CancellationToken cancellationToken = default)
    {
        var userRole = await _context.UserProfiles
            .Where(profile => profile.UserId == userId)
            .Select(profile => profile.Role)
            .FirstOrDefaultAsync(cancellationToken);

        var userSettings = await _context.UserSettings
            .FirstOrDefaultAsync(settings => settings.UserId == userId, cancellationToken);

        if (userSettings is not null)
        {
            await EnsureAutoStatusForManagersAsync(userSettings, userRole, cancellationToken);
            return userSettings;
        }

        var createdSettings = new UserSettings
        {
            UserId = userId,
            IsAutoStatusEnabled = true
        };

        _context.UserSettings.Add(createdSettings);
        await _context.SaveChangesAsync(cancellationToken);
        return createdSettings;
    }

    private async Task EnsureAutoStatusForManagersAsync(
        UserSettings userSettings,
        UserRole userRole,
        CancellationToken cancellationToken)
    {
        if (userRole is not (UserRole.Manager or UserRole.SuperAdmin) || userSettings.IsAutoStatusEnabled)
            return;

        userSettings.IsAutoStatusEnabled = true;
        await _context.SaveChangesAsync(cancellationToken);
    }
}

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
        var role = await _context.UserProfiles
            .Where(p => p.UserId == userId)
            .Select(p => p.Role)
            .FirstOrDefaultAsync(cancellationToken);

        var existing = await _context.UserSettings
            .FirstOrDefaultAsync(s => s.UserId == userId, cancellationToken);
        if (existing is not null)
        {
            if (role is UserRole.Manager or UserRole.SuperAdmin && !existing.IsAutoStatusEnabled)
            {
                // Для менеджерів і адмінів автостатус завжди увімкнений.
                existing.IsAutoStatusEnabled = true;
                await _context.SaveChangesAsync(cancellationToken);
            }
            return existing;
        }

        var created = new UserSettings
        {
            UserId = userId,
            IsAutoStatusEnabled = true
        };

        _context.UserSettings.Add(created);
        await _context.SaveChangesAsync(cancellationToken);
        return created;
    }
}

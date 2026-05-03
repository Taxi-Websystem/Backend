using Backend.Data;
using Backend.Models;
using Backend.Models.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Backend.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = "ManagerOrSuperAdmin")]
public class DriversController : ControllerBase
{
    private readonly ApplicationDbContext _context;

    public DriversController(ApplicationDbContext context)
    {
        _context = context;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<UserProfile>>> GetAll()
    {
        var drivers = await (from profile in _context.UserProfiles
                             join whitelist in _context.UserWhitelists
                                 on profile.UserId equals whitelist.Id
                             where profile.Role == UserRole.Driver && whitelist.IsActive
                             select profile)
            .ToListAsync();

        return drivers;
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<UserProfile>> GetById(int id)
    {
        var driver = await _context.UserProfiles.FindAsync(id);
        if (driver is null || driver.Role != UserRole.Driver)
            return NotFound();

        if (!await IsActiveWhitelistEntry(driver.UserId))
            return NotFound();

        return driver;
    }

    [HttpPost]
    public async Task<ActionResult<UserProfile>> Create(UserProfile driver)
    {
        driver.Role = UserRole.Driver;
        if (!await IsActiveWhitelistEntry(driver.UserId))
            return BadRequest(new { message = "Користувач має бути активним у whitelist." });

        _context.UserProfiles.Add(driver);
        await _context.SaveChangesAsync();
        return CreatedAtAction(nameof(GetById), new { id = driver.Id }, driver);
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(int id, UserProfile driver)
    {
        if (id != driver.Id)
            return BadRequest();

        if (!await IsActiveWhitelistEntry(driver.UserId))
            return BadRequest(new { message = "Користувач має бути активним у whitelist." });

        driver.Role = UserRole.Driver;
        _context.Entry(driver).State = EntityState.Modified;

        try
        {
            await _context.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            if (!await _context.UserProfiles.AnyAsync(d => d.Id == id && d.Role == UserRole.Driver))
                return NotFound();
            throw;
        }

        return NoContent();
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(int id)
    {
        var driver = await _context.UserProfiles.FindAsync(id);
        if (driver is null || driver.Role != UserRole.Driver)
            return NotFound();

        _context.UserProfiles.Remove(driver);
        await _context.SaveChangesAsync();
        return NoContent();
    }

    private Task<bool> IsActiveWhitelistEntry(int userId)
    {
        return _context.UserWhitelists.AnyAsync(w => w.Id == userId && w.IsActive);
    }
}

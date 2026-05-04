using Backend.Data;
using Backend.Models;
using Backend.Models.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Backend.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class RidesController : ControllerBase
{
    private readonly ApplicationDbContext _context;

    public RidesController(ApplicationDbContext context)
    {
        _context = context;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<Ride>>> GetAll()
    {
        return await _context.Rides.ToListAsync();
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<Ride>> GetById(int id)
    {
        var ride = await _context.Rides.FindAsync(id);
        if (ride is null) return NotFound();
        return ride;
    }

    [HttpPost]
    public async Task<ActionResult<Ride>> Create(Ride ride)
    {
        var validationError = await ValidateRideDriverAndRatingMessageAsync(ride);
        if (validationError is not null)
            return BadRequest(new { message = validationError });

        ride.StartTime = DateTime.UtcNow;
        _context.Rides.Add(ride);
        await _context.SaveChangesAsync();
        return CreatedAtAction(nameof(GetById), new { id = ride.Id }, ride);
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(int id, Ride ride)
    {
        if (id != ride.Id) return BadRequest();

        var validationError = await ValidateRideDriverAndRatingMessageAsync(ride);
        if (validationError is not null)
            return BadRequest(new { message = validationError });

        _context.Entry(ride).State = EntityState.Modified;
        try
        {
            await _context.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            if (!await _context.Rides.AnyAsync(r => r.Id == id))
                return NotFound();
            throw;
        }

        return NoContent();
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(int id)
    {
        var ride = await _context.Rides.FindAsync(id);
        if (ride is null) return NotFound();

        _context.Rides.Remove(ride);
        await _context.SaveChangesAsync();
        return NoContent();
    }

    private async Task<string?> ValidateRideDriverAndRatingMessageAsync(Ride ride)
    {
        if (ride.DriverProfileId.HasValue)
        {
            var profile = await _context.UserProfiles.FindAsync(ride.DriverProfileId.Value);
            if (profile is null || profile.Role != UserRole.Driver)
                return "Профіль водія для поїздки не знайдено або не є водієм.";
        }

        if (ride.Rating.HasValue && (ride.Rating.Value < 1m || ride.Rating.Value > 5m))
            return "Оцінка поїздки має бути від 1 до 5.";

        return null;
    }
}

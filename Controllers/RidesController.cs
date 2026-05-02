using Backend.Data;
using Backend.Models;
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
        ride.StartTime = DateTime.UtcNow;
        _context.Rides.Add(ride);
        await _context.SaveChangesAsync();
        return CreatedAtAction(nameof(GetById), new { id = ride.Id }, ride);
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(int id, Ride ride)
    {
        if (id != ride.Id) return BadRequest();

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
}

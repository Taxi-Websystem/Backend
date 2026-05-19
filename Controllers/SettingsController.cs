using Backend.Data;
using Backend.Models;
using Backend.Validation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Backend.Controllers;

[ApiController]
[Route("api/settings")]
public class SettingsController : ControllerBase
{
    private const int SystemSettingsId = 1;
    private const string SettingsNotFoundMessage = "Системні тарифи не знайдено.";

    private readonly ApplicationDbContext _context;

    public SettingsController(ApplicationDbContext context)
    {
        _context = context;
    }

    [HttpGet]
    [Authorize(Policy = "ManagerOrSuperAdmin")]
    public async Task<ActionResult<FinancialSettingsDto>> Get()
    {
        var row = await FindSystemSettingsAsync(asNoTracking: true);
        if (row is null)
            return NotFound(new { message = SettingsNotFoundMessage });

        return Ok(MapToDto(row));
    }

    [HttpPut]
    [Authorize(Roles = "SuperAdmin")]
    public async Task<IActionResult> Put([FromBody] UpdateFinancialSettingsDto dto)
    {
        var validationError = SystemSettingsValidation.ValidateUpdate(dto);
        if (validationError is not null)
            return BadRequest(new { message = validationError });

        var row = await FindSystemSettingsAsync(asNoTracking: false);
        if (row is null)
            return NotFound(new { message = SettingsNotFoundMessage });

        SystemSettingsValidation.ApplyRoundedValues(row, dto);
        await _context.SaveChangesAsync();
        return NoContent();
    }

    private static FinancialSettingsDto MapToDto(SystemSettings row) => new()
    {
        BaseFare = row.BaseFare,
        CostPerKm = row.CostPerKm,
        PlatformFixedFee = row.PlatformFixedFee,
        PlatformFeePercentage = row.PlatformFeePercentage
    };

    private Task<SystemSettings?> FindSystemSettingsAsync(bool asNoTracking)
    {
        var query = asNoTracking
            ? _context.SystemSettings.AsNoTracking()
            : _context.SystemSettings.AsQueryable();

        return query.FirstOrDefaultAsync(s => s.Id == SystemSettingsId);
    }
}

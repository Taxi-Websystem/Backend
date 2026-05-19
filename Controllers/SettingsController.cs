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
    private readonly ApplicationDbContext _context;

    public SettingsController(ApplicationDbContext context)
    {
        _context = context;
    }

    [HttpGet]
    [Authorize(Policy = "ManagerOrSuperAdmin")]
    public async Task<ActionResult<FinancialSettingsDto>> Get()
    {
        var row = await _context.SystemSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1);
        if (row is null)
            return NotFound(new { message = "Системні тарифи не знайдено." });

        return Ok(MapToDto(row));
    }

    [HttpPut]
    [Authorize(Roles = "SuperAdmin")]
    public async Task<IActionResult> Put([FromBody] UpdateFinancialSettingsDto dto)
    {
        var validationError = SystemSettingsValidation.ValidateUpdate(dto);
        if (validationError is not null)
            return BadRequest(new { message = validationError });

        var row = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Id == 1);
        if (row is null)
            return NotFound(new { message = "Системні тарифи не знайдено." });

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
}

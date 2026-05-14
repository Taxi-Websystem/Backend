using Backend.Data;
using Backend.Models;
using Backend.Models.Enums;
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

        return Ok(new FinancialSettingsDto
        {
            BaseFare = row.BaseFare,
            CostPerKm = row.CostPerKm,
            PlatformFixedFee = row.PlatformFixedFee,
            PlatformFeePercentage = row.PlatformFeePercentage
        });
    }

    [HttpPut]
    [Authorize(Roles = "SuperAdmin")]
    public async Task<IActionResult> Put([FromBody] UpdateFinancialSettingsDto dto)
    {
        if (dto.BaseFare < 0 || dto.CostPerKm < 0 || dto.PlatformFixedFee < 0)
            return BadRequest(new { message = "Тарифи не можуть бути від’ємними." });

        if (dto.PlatformFeePercentage < 0 || dto.PlatformFeePercentage > 1)
            return BadRequest(new { message = "Комісія має бути від 0 до 1 (наприклад 0.10 для 10%)." });

        var row = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Id == 1);
        if (row is null)
            return NotFound(new { message = "Системні тарифи не знайдено." });

        row.BaseFare = decimal.Round(dto.BaseFare, 2, MidpointRounding.AwayFromZero);
        row.CostPerKm = decimal.Round(dto.CostPerKm, 2, MidpointRounding.AwayFromZero);
        row.PlatformFixedFee = decimal.Round(dto.PlatformFixedFee, 2, MidpointRounding.AwayFromZero);
        row.PlatformFeePercentage = decimal.Round(dto.PlatformFeePercentage, 4, MidpointRounding.AwayFromZero);

        await _context.SaveChangesAsync();
        return NoContent();
    }
}

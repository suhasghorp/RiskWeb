using Microsoft.EntityFrameworkCore;
using RiskWeb.Data;

namespace RiskWeb.Services;

public class UserRoleService : IUserRoleService
{
    private readonly ApplicationDbContext _dbContext;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public UserRoleService(ApplicationDbContext dbContext, IHttpContextAccessor httpContextAccessor)
    {
        _dbContext = dbContext;
        _httpContextAccessor = httpContextAccessor;
    }

    public string? GetCurrentUsername()
    {
        return _httpContextAccessor.HttpContext?.User?.Identity?.Name;
    }

    public async Task<bool> IsUserInRoleAsync(string windowsUsername, string roleName)
    {
        if (string.IsNullOrWhiteSpace(windowsUsername) || string.IsNullOrWhiteSpace(roleName))
        {
            return false;
        }

        return await _dbContext.UserInRoles
            .Include(ur => ur.User)
            .Include(ur => ur.Role)
            .AnyAsync(ur =>
                ur.User.WindowsUsername.ToLower() == windowsUsername.ToLower() &&
                ur.User.IsActive &&
                ur.Role.RoleName.ToLower() == roleName.ToLower());
    }

    public async Task<bool> IsCurrentUserLiqAdminAsync()
    {
        var username = GetCurrentUsername();
        if (string.IsNullOrWhiteSpace(username))
        {
            return false;
        }

        return await IsUserInRoleAsync(username, "LiqAdmin");
    }
}

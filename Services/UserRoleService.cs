using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using RiskWeb.Data;

namespace RiskWeb.Services;

public class UserRoleService : IUserRoleService
{
    private readonly ApplicationDbContext _dbContext;
    private readonly AuthenticationStateProvider _authStateProvider;
    private readonly ILogger<UserRoleService> _logger;

    public UserRoleService(ApplicationDbContext dbContext, AuthenticationStateProvider authStateProvider, ILogger<UserRoleService> logger)
    {
        _dbContext = dbContext;
        _authStateProvider = authStateProvider;
        _logger = logger;
    }

    public async Task<string?> GetCurrentUsernameAsync()
    {
        _logger.LogInformation("=== GetCurrentUsernameAsync Debug ===");

        var authState = await _authStateProvider.GetAuthenticationStateAsync();
        var user = authState.User;
        var identity = user.Identity;

        _logger.LogInformation("AuthState User is null: {IsNull}", user == null);
        _logger.LogInformation("User Identity is null: {IsNull}", identity == null);
        _logger.LogInformation("Identity.IsAuthenticated: {IsAuth}", identity?.IsAuthenticated);
        _logger.LogInformation("Identity.AuthenticationType: {AuthType}", identity?.AuthenticationType);
        _logger.LogInformation("Identity.Name: {Name}", identity?.Name);

        return identity?.Name;
    }

    public async Task<bool> IsUserInRoleAsync(string windowsUsername, string roleName)
    {
        _logger.LogInformation("=== IsUserInRoleAsync Debug ===");
        _logger.LogInformation("Checking role for WindowsUsername: '{Username}'", windowsUsername);
        _logger.LogInformation("Looking for RoleName: '{RoleName}'", roleName);

        if (string.IsNullOrWhiteSpace(windowsUsername) || string.IsNullOrWhiteSpace(roleName))
        {
            _logger.LogWarning("Username or RoleName is null/empty. Returning false.");
            return false;
        }

        // Log all users in the database for comparison
        var allUsers = await _dbContext.Users.ToListAsync();
        _logger.LogInformation("=== All Users in Database ===");
        foreach (var user in allUsers)
        {
            _logger.LogInformation("UserId: {Id}, WindowsUsername: '{Username}', IsActive: {IsActive}",
                user.UserId, user.WindowsUsername, user.IsActive);
        }

        // Log all roles in the database
        var allRoles = await _dbContext.Roles.ToListAsync();
        _logger.LogInformation("=== All Roles in Database ===");
        foreach (var role in allRoles)
        {
            _logger.LogInformation("RoleId: {Id}, RoleName: '{RoleName}'", role.RoleId, role.RoleName);
        }

        // Log all user-role assignments
        var allUserRoles = await _dbContext.UserInRoles
            .Include(ur => ur.User)
            .Include(ur => ur.Role)
            .ToListAsync();
        _logger.LogInformation("=== All UserInRoles Assignments ===");
        foreach (var ur in allUserRoles)
        {
            _logger.LogInformation("UserRoleId: {Id}, Username: '{Username}', RoleName: '{RoleName}'",
                ur.UserRoleId, ur.User.WindowsUsername, ur.Role.RoleName);
        }

        // Check for exact match
        var result = await _dbContext.UserInRoles
            .Include(ur => ur.User)
            .Include(ur => ur.Role)
            .AnyAsync(ur =>
                ur.User.WindowsUsername.ToLower() == windowsUsername.ToLower() &&
                ur.User.IsActive &&
                ur.Role.RoleName.ToLower() == roleName.ToLower());

        _logger.LogInformation("=== Role Check Result ===");
        _logger.LogInformation("User '{Username}' in role '{RoleName}': {Result}", windowsUsername, roleName, result);

        return result;
    }

    public async Task<bool> IsCurrentUserLiqAdminAsync()
    {
        _logger.LogInformation("=== IsCurrentUserLiqAdminAsync Called ===");

        var username = await GetCurrentUsernameAsync();

        if (string.IsNullOrWhiteSpace(username))
        {
            _logger.LogWarning("Current username is null or empty. Returning false.");
            return false;
        }

        var result = await IsUserInRoleAsync(username, "LiqAdmin");
        _logger.LogInformation("Final result for IsCurrentUserLiqAdminAsync: {Result}", result);

        return result;
    }
}

namespace RiskWeb.Services;

public interface IUserRoleService
{
    Task<bool> IsUserInRoleAsync(string windowsUsername, string roleName);
    Task<bool> IsCurrentUserLiqAdminAsync();
    Task<string?> GetCurrentUsernameAsync();
}

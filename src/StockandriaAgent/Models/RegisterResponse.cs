namespace StockandriaAgent.Models;

public class RegisterResponse
{
    public string AgentId { get; set; } = string.Empty;
    public string Token { get; set; } = string.Empty;
    public string BranchId { get; set; } = string.Empty;
    public string OrganizationId { get; set; } = string.Empty;
}

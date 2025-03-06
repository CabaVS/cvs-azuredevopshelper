namespace CabaVS.AzureDevOpsHelper.Web.Models;

internal sealed record RemainingByTeamLine(string Team, double Remaining, RemainingByTeamLine.TaskDetails[]? Tasks = null)
{
    internal sealed record TaskDetails(int Id, string Title, double Remaining);
}
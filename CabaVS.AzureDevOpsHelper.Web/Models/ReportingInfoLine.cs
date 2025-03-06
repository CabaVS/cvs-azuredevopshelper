namespace CabaVS.AzureDevOpsHelper.Web.Models;

internal sealed record ReportingInfoLine(DateOnly Date, string Alias, decimal Hours, string Text);
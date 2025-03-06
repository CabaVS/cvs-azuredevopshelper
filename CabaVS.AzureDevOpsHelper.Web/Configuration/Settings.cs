namespace CabaVS.AzureDevOpsHelper.Web.Configuration;

internal static class Settings
{
    internal const int MaxWorkItemsLimit = 200;

    public static class FieldNames
    {
        public const string Title = "System.Title";
        public const string WorkItemType = "System.WorkItemType";
        public const string AssignedTo = "System.AssignedTo";
        public const string Remaining = "Microsoft.VSTS.Scheduling.RemainingWork";
        public const string ReportingInfo = "Custom.ReportingInfo";
    }

    public static class Relations
    {
        public const string Children = "System.LinkTypes.Hierarchy-Forward";
    }
}
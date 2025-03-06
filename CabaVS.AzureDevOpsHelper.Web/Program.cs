using System.Globalization;
using CabaVS.AzureDevOpsHelper.Web.Configuration;
using CabaVS.AzureDevOpsHelper.Web.Models;
using HtmlAgilityPack;
using Microsoft.AspNetCore.Mvc;
using Microsoft.TeamFoundation.WorkItemTracking.WebApi;
using Microsoft.TeamFoundation.WorkItemTracking.WebApi.Models;
using Microsoft.VisualStudio.Services.Common;
using Microsoft.VisualStudio.Services.WebApi;
using TeamMember = CabaVS.AzureDevOpsHelper.Web.Models.TeamMember;

var builder = WebApplication.CreateBuilder(args);
var configuration = builder.Configuration;

var organization = new Uri(configuration["OrganizationUrl"]
                           ?? throw new InvalidOperationException("OrganizationUrl is not configured."));
var token = configuration["PersonalAccessToken"] 
            ?? throw new InvalidOperationException("PersonalAccessToken is not configured.");
var teams = (configuration.GetSection("Teams").Get<Dictionary<string, string[]>>()
            ?? throw new InvalidOperationException("Teams are not configured."))
    .Select(kvp => new Team(kvp.Key, kvp.Value.Select(alias => new TeamMember(alias)).ToArray()))
    .ToArray();

builder.Services.AddSingleton(
    new VssConnection(organization, new VssBasicCredential(string.Empty, token)));

var app = builder.Build();

app.MapGet("/api/work-items/{workItemId:int}/reporting-info", async (int workItemId, VssConnection connection) =>
{
    var workItemTrackingClient = connection.GetClient<WorkItemTrackingHttpClient>();

    var workItem = await workItemTrackingClient.GetWorkItemAsync(workItemId, [Settings.FieldNames.Title, Settings.FieldNames.ReportingInfo]);
    if (workItem is null)
    {
        return Results.BadRequest($"Work Item '{workItemId}' not found.");
    }

    var fieldValue = (string)workItem.Fields[Settings.FieldNames.ReportingInfo];

    var parsed = ParseHtmlToReportingInfoLines(fieldValue)
        .GroupBy(x => x.Alias)
        .Select(g => new
        {
            Alias = g.Key,
            TotalHours = g.Select(x => x.Hours).Sum(),
            TeamLabel = teams.SingleOrDefault(x => x.Members.Contains(new TeamMember(g.Key)))?.Name ?? g.Key
        })
        .GroupBy(x => x.TeamLabel)
        .Select(g => new
        {
            TeamLabel = g.Key,
            TotalHours = g.Select(x => x.TotalHours).Sum()
        })
        .OrderByDescending(x => x.TotalHours)
        .ThenBy(x => x.TeamLabel)
        .ToArray();
    return Results.Ok(new
    {
        Id = workItemId,
        Title = (string)workItem.Fields[Settings.FieldNames.Title],
        ReportingInfo = parsed
    });

    static IEnumerable<ReportingInfoLine> ParseHtmlToReportingInfoLines(string htmlContent)
    {
        var doc = new HtmlDocument();
        
        doc.LoadHtml(htmlContent.Replace("&nbsp;", " "));

        return from row in doc.DocumentNode.SelectNodes("//tr") 
            select row.SelectNodes("td") 
            into cells 
            where cells is { Count: 4 }
            select new ReportingInfoLine(
                DateOnly.ParseExact(cells[0].InnerText.Trim(), "dd.MM.yyyy", CultureInfo.InvariantCulture), 
                cells[1].InnerText.Trim(), 
                decimal.Parse(cells[2].InnerText.Trim().Replace(',', '.'), CultureInfo.InvariantCulture), 
                cells[3].InnerText.Trim());
    }
});

app.MapGet("/api/work-items/{workItemId:int}/remaining-by-team", async (int workItemId, VssConnection connection,
    [FromQuery] bool includeTasksInResponse = false) =>
{
    var workItemTrackingClient = connection.GetClient<WorkItemTrackingHttpClient>();

    var rootWorkItem = await workItemTrackingClient.GetWorkItemAsync(
        workItemId,
        expand: WorkItemExpand.Relations);
    if (rootWorkItem is null)
    {
        return Results.BadRequest(new { Error = $"Work Item '{workItemId}' not found." });
    }

    var childrenTasksOrBugs = await TraverseWorkItemsTree(rootWorkItem);

    return Results.Ok(new
    {
        Id = workItemId,
        Title = (string)rootWorkItem.Fields[Settings.FieldNames.Title],
        RemainingByTeam = CalculateRemainingByTeamLines(teams, childrenTasksOrBugs, includeTasksInResponse)
    });

    bool IsTaskOrBug(WorkItem workItem) => workItem.Fields[Settings.FieldNames.WorkItemType].ToString() is "Task" or "Bug";

    async Task<List<WorkItem>> TraverseWorkItemsTree(WorkItem parent)
    {
        var (processing, children) = IsTaskOrBug(parent)
            ? ([], [parent])
            : (new List<WorkItem> { parent }, new List<WorkItem>(0));

        while (processing is { Count: > 0 })
        {
            var loaded = (await Task.WhenAll(
                    processing
                        .SelectMany(
                            wi => wi.Relations
                                .Where(relation => relation.Rel == Settings.Relations.Children)
                                .Select(relation => int.Parse(relation.Url.Split('/').Last())))
                        .Chunk(Settings.MaxWorkItemsLimit)
                        .Select(ids => workItemTrackingClient.GetWorkItemsAsync(
                            ids,
                            expand: WorkItemExpand.Relations,
                            errorPolicy: WorkItemErrorPolicy.Omit))))
                .SelectMany(x => x)
                .ToLookup(IsTaskOrBug);
            
            processing = loaded[false].ToList();
            children.AddRange(loaded[true]);
        }
        
        return children;
    }

    static IEnumerable<RemainingByTeamLine> CalculateRemainingByTeamLines(
        Team[] teams,
        IEnumerable<WorkItem> workItems,
        bool includeTasksInResponse)
    {
        return workItems
            .Select(wi =>
            {
                var alias = wi.Fields.TryGetValue(Settings.FieldNames.AssignedTo, out var assignedToFieldValue)
                    ? ((IdentityRef)assignedToFieldValue).UniqueName.Split('@').First().ToUpper()
                    : string.Empty;

                return new
                {
                    Id = wi.Id.GetValueOrDefault(),
                    Title = wi.Fields[Settings.FieldNames.Title].ToString(),
                    Team = teams.FirstOrDefault(x => x.Members.Select(y => y.Alias).Contains(alias))?.Name ?? alias,
                    Remaining = wi.Fields.TryGetValue(Settings.FieldNames.Remaining, out var remainingFieldValue)
                        ? (double)remainingFieldValue
                        : 0
                };
            })
            .GroupBy(x => x.Team)
            .Select(g => new RemainingByTeamLine(
                g.Key,
                g.Select(x => x.Remaining).Sum(),
                includeTasksInResponse
                    ? g.Select(x => new RemainingByTeamLine.TaskDetails(x.Id, x.Title ?? string.Empty, x.Remaining))
                        .OrderByDescending(x => x.Remaining)
                        .ToArray()
                    : null))
            .OrderByDescending(x => x.Remaining)
            .ThenBy(x => x.Team);
    }
});

app.Run();
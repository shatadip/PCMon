namespace PCMon.Services;

public sealed record ScheduledTaskInfo(string Name, string Folder, int State, DateTime? LastRun, DateTime? NextRun, int LastResult);

/// <summary>Read-only view of Task Scheduler via its COM API (SRS FR-14). No NuGet needed.</summary>
static class ScheduledTaskService
{
    const int TASK_ENUM_HIDDEN = 1;

    public static IReadOnlyList<ScheduledTaskInfo> Load()
    {
        var list = new List<ScheduledTaskInfo>();
        var type = Type.GetTypeFromProgID("Schedule.Service")
                   ?? throw new InvalidOperationException("Task Scheduler COM service not available.");
        dynamic service = Activator.CreateInstance(type)!;
        service.Connect();
        Walk(service.GetFolder("\\"), list);
        return list;
    }

    static void Walk(dynamic folder, List<ScheduledTaskInfo> list)
    {
        string path = "\\";
        try { path = (string)folder.Path; } catch { }

        try
        {
            foreach (dynamic task in folder.GetTasks(TASK_ENUM_HIDDEN))
            {
                try
                {
                    list.Add(new ScheduledTaskInfo(
                        (string)task.Name, path, (int)task.State,
                        RealDate((DateTime)task.LastRunTime), RealDate((DateTime)task.NextRunTime),
                        (int)task.LastTaskResult));
                }
                catch (Exception ex)
                {
                    Log.Error("scheduled task in " + path, ex);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("scheduled tasks in " + path, ex);
        }

        try
        {
            foreach (dynamic sub in folder.GetFolders(0))
                Walk(sub, list);
        }
        catch (Exception ex)
        {
            Log.Error("scheduled task folders in " + path, ex);
        }
    }

    /// <summary>Task Scheduler reports "never" as 1899-12-30 / 1999-11-30.</summary>
    static DateTime? RealDate(DateTime d) => d.Year < 2000 ? null : d;
}

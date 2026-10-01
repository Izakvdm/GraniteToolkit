using GraniteBiDeployWizard.Models;
using Microsoft.Win32.TaskScheduler;

namespace GraniteBiDeployWizard.Core;

/// <summary>
/// Registers the recurring background sync as a native Windows Scheduled
/// Task using the TaskScheduler wrapper library (Microsoft.Win32.TaskScheduler,
/// by David Hall) instead of SQL Server Agent, so it works the same way on
/// SQL Server Express (which has no Agent) as on Standard/Enterprise.
/// </summary>
public static class ScheduledTaskService
{
    /// <summary>
    /// Creates (or replaces) a task that runs the generated .bat wrapper on a
    /// fixed interval, indefinitely, under the Windows account supplied on
    /// Panel 5.
    /// </summary>
    /// <remarks>
    /// The trigger is a single "one time" TimeTrigger, not a Daily/Weekly
    /// trigger -- exactly what Task Scheduler's UI produces when you tick
    /// "Repeat task every ... for a duration of Indefinitely" on a one-time
    /// trigger. Repetition.Duration = TimeSpan.Zero is what makes the
    /// repetition indefinite rather than bounded.
    /// </remarks>
    public static void RegisterOrUpdateSyncTask(DeploymentContext context, string batchFilePath)
    {
        using var taskService = new TaskService();

        TaskDefinition taskDefinition = taskService.NewTask();
        taskDefinition.RegistrationInfo.Description =
            $"Runs {context.BatchFileName} every {context.ScheduleIntervalMinutes} minute(s) to refresh {context.BiDb} " +
            "from the live GraniteWMS database. Created by the GraniteWMS BI Deployment Wizard.";
        taskDefinition.RegistrationInfo.Author = "GraniteWMS BI Deployment Wizard";

        // Run whether or not the account is logged on, using the stored password.
        taskDefinition.Principal.LogonType = TaskLogonType.Password;
        taskDefinition.Principal.RunLevel = TaskRunLevel.Highest;

        // One-time trigger, starting a minute from now, repeating on the
        // configured interval indefinitely (Duration = TimeSpan.Zero).
        var timeTrigger = new TimeTrigger(DateTime.Now.AddMinutes(1))
        {
            Repetition =
            {
                Interval = TimeSpan.FromMinutes(context.ScheduleIntervalMinutes),
                Duration = TimeSpan.Zero // indefinite
            }
        };
        taskDefinition.Triggers.Add(timeTrigger);

        // Run in the wrapper's own admin-only folder (TaskFolder), not System32
        // (.bat's default) and not the script folder: cmd looks for sqlcmd in
        // the working directory first, so that folder must be one only
        // administrators can write to.
        var execAction = new ExecAction(batchFilePath, arguments: null, workingDirectory: Path.GetDirectoryName(batchFilePath));
        taskDefinition.Actions.Add(execAction);

        taskDefinition.Settings.Enabled = true;
        taskDefinition.Settings.StartWhenAvailable = true;
        taskDefinition.Settings.DisallowStartIfOnBatteries = false;
        taskDefinition.Settings.StopIfGoingOnBatteries = false;
        taskDefinition.Settings.ExecutionTimeLimit = TimeSpan.Zero; // no limit
        taskDefinition.Settings.MultipleInstances = TaskInstancesPolicy.IgnoreNew;

        // Task Scheduler's task-definition XML rejects a ".\" prefixed
        // UserId at schema-validation time -- it needs the literal computer
        // name, exactly like SQL Server's CREATE LOGIN ... FROM WINDOWS
        // (see WindowsAccountNameResolver). Left unresolved, this surfaces
        // as a cryptic "(21,8):UserId:" exception right here rather than a
        // readable "account not found" message.
        string resolvedAccountName = WindowsAccountNameResolver.ResolveLocalShorthand(context.WindowsAccountName);

        taskService.RootFolder.RegisterTaskDefinition(
            context.TaskName,
            taskDefinition,
            TaskCreation.CreateOrUpdate,
            resolvedAccountName,
            context.WindowsAccountPassword,
            TaskLogonType.Password);
    }

    public static bool TaskExists(string taskName)
    {
        using var taskService = new TaskService();
        return taskService.FindTask(taskName) is not null;
    }
}

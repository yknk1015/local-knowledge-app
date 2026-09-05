using System.IO;

namespace KnowledgeApp.CSharp;

public enum HostCloseDecision { BeginShutdown, RejectCriticalOperation, IgnoreDuplicate, AllowClose }
public enum HostWaitDecision { Completed, TimedOut, Pending }

// State decisions are independent of WPF, processes, files, and wall-clock time.
public sealed class HostShutdownPolicy
{
    public bool IsClosing { get; private set; }
    public bool CanClose { get; private set; }

    public HostCloseDecision RequestClose(int activeCriticalOperations)
    {
        if (CanClose) return HostCloseDecision.AllowClose;
        if (IsClosing) return HostCloseDecision.IgnoreDuplicate;
        if (activeCriticalOperations > 0) return HostCloseDecision.RejectCriticalOperation;
        IsClosing = true;
        return HostCloseDecision.BeginShutdown;
    }

    public void CompleteShutdown()
    {
        if (!IsClosing) throw new InvalidOperationException("Shutdown has not started.");
        CanClose = true;
    }

    public static bool BrowserResourcesReleased(
        bool initializationCompleted, bool browserCreationStarted,
        uint? expectedBrowserProcessId, IReadOnlySet<uint> exitedBrowserProcessIds) =>
        initializationCompleted && (!browserCreationStarted ||
            (expectedBrowserProcessId is { } processId
                ? exitedBrowserProcessIds.Contains(processId)
                : exitedBrowserProcessIds.Count > 0));

    public static bool MayDeleteTemporaryData(
        bool initializationCompleted, bool browserResourcesReleased, bool databaseDisposed) =>
        initializationCompleted && browserResourcesReleased && databaseDisposed;

    public static bool NeedsResidualDataNotice(bool hasTemporaryRoot, bool cleanupSucceeded) =>
        hasTemporaryRoot && !cleanupSucceeded;

    public static HostWaitDecision DecideWait(bool operationCompleted, bool deadlineCompleted) =>
        operationCompleted ? HostWaitDecision.Completed :
        deadlineCompleted ? HostWaitDecision.TimedOut : HostWaitDecision.Pending;

    // Compare only strings; never probe an unexpected profile folder chosen by an
    // environment variable or registry override.
    public static bool IsExpectedProfileDirectory(string? actual, string? expected)
    {
        if (string.IsNullOrWhiteSpace(actual) || string.IsNullOrWhiteSpace(expected) ||
            !Path.IsPathFullyQualified(actual) || !Path.IsPathFullyQualified(expected) ||
            actual.Any(char.IsControl) || expected.Any(char.IsControl))
        {
            return false;
        }
        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(actual)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(expected)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException) { return false; }
        catch (NotSupportedException) { return false; }
        catch (PathTooLongException) { return false; }
    }
}

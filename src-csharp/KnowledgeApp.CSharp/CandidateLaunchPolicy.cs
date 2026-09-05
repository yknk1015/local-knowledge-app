namespace KnowledgeApp.CSharp;

public static class CandidateLaunchPolicy
{
    public const string RehearsalArgument = "--rehearsal";

    // Deliberately no path, SQL, file, URL or mode from a renderer/environment.
    public static bool TrySelect(string[] args, out bool production)
    {
        production = args.Length == 0;
        return production || (args.Length == 1 && args[0] == RehearsalArgument);
    }
}

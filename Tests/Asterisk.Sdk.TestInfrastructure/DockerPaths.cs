namespace Asterisk.Sdk.TestInfrastructure;

/// <summary>Resolves Docker-related paths relative to the solution root.</summary>
public static class DockerPaths
{
    private static readonly Lazy<string> _solutionRoot = new(FindSolutionRoot);

    public static string SolutionRoot => _solutionRoot.Value;
    public static string DockerDir => Path.Combine(SolutionRoot, "docker");
    public static string FunctionalDir => Path.Combine(DockerDir, "functional");
    public static string AsteriskConfig => Path.Combine(FunctionalDir, "asterisk-config");
    public static string PstnEmulatorConfig => Path.Combine(FunctionalDir, "pstn-emulator-config");
    public static string AsteriskDockerfile => Path.Combine(DockerDir, "Dockerfile.asterisk");
    public static string FunctionalSqlDir => Path.Combine(FunctionalDir, "sql");
    public static string SippScenariosDir => Path.Combine(FunctionalDir, "sipp-scenarios");

    private static string FindSolutionRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (dir.GetFiles("Asterisk.Sdk.slnx").Length > 0)
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not find solution root (Asterisk.Sdk.slnx)");
    }
}

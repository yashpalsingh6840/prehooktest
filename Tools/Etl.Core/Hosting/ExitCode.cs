namespace Etl.Core.Hosting;

/// <summary>Mirrors SSIS's DTSExecResult exit codes.</summary>
public static class ExitCode
{
    public const int Success = 0;
    public const int LoadFailed = 1;
    public const int ConfigurationInvalid = 2;
}

namespace DotnetIntelligence.Cli;

/// <summary>Exit codes (4.1). 2 is the usage error code, reserved alongside the documented set.</summary>
public static class ExitCodes
{
    public const int Complete = 0;
    public const int InternalError = 1;
    public const int Usage = 2;
    public const int Partial = 10;
    public const int PreflightBlocked = 20;
    public const int NothingLoaded = 30;
    public const int Cancelled = 40;
    public const int IntegrityFailed = 50;
}

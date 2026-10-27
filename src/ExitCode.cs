namespace Mod2Xgm;

/// <summary>Process exit codes. See docs/cli.md#exit-codes.</summary>
internal enum ExitCode
{
    Success = 0,
    UsageError = 1,
    InputNotFound = 2,
    InvalidInput = 3,
    OutputNotWritable = 4,
    InternalError = 5,
    DoesNotFit = 6,
}

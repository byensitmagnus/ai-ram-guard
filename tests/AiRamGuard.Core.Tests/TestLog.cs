using System.Runtime.CompilerServices;
using AiRamGuard.Core;

static class TestLog
{
    /// <summary>Tests log to a temp file: the real guard.log is what the Activity page shows.</summary>
#pragma warning disable CA2255 // a module initializer is the one hook xUnit v2 runs before every test class
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Init() => Paths.LogFile = Path.Combine(Path.GetTempPath(), "airg-tests.log");
}

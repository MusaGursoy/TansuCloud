// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Runtime.CompilerServices;

namespace TansuCloud.Database.UnitTests;

internal static class TestInitialization
{
    [ModuleInitializer]
    public static void Initialize()
    {
        TestEnvironment.EnsureInitialized();
    }
} // End of Class TestInitialization

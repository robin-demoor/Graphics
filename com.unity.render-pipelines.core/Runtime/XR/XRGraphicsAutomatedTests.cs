using System;

namespace UnityEngine.Rendering
{
    /// <summary>
    /// Utility class to connect SRP to automated test framework.
    /// </summary>
    public static class XRGraphicsAutomatedTests
    {
        // XR tests can be enabled from the command line. Cache result to avoid GC.
        static bool activatedFromCommandLine { get => Array.Exists(Environment.GetCommandLineArgs(), arg => arg == "-xr-tests"); }

        /// <summary>
        /// Used by render pipelines to initialize XR tests.
        /// </summary>
#if XR_STANDALONE_TESTS
        public static bool enabled { get; } = true;
#else
        public static bool enabled { get; } = activatedFromCommandLine;
#endif

        /// <summary>
        /// Set by automated test framework and read by render pipelines.
        /// </summary>
        public static bool running = false;
    }
}

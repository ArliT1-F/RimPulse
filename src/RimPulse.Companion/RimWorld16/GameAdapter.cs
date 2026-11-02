namespace RimPulse.Companion
{
    // Compiled only against the selected 1.6 installation's Unity/Mono assemblies.
    internal static class GameAdapter
    {
        public static string Version { get { return RimWorld.VersionControl.CurrentVersionStringWithRev; } }
    }
}

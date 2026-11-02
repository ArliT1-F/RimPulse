namespace RimPulse.Companion
{
    // Compiled only against the selected 1.7 installation's Waylith/.NET assemblies.
    internal static class GameAdapter
    {
        public static string Version { get { return RimWorld.VersionControl.CurrentVersionStringWithRev; } }
    }
}

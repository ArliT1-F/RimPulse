namespace RimPulse.Protocol
{
    // This file is compiled into both Core and each companion. Keep it compatible with Mono's net472 profile.
    public static class WireProtocol
    {
        public const int Version = 1;
        public const string StartupPhase = "startup_snapshot";
        public const string PortEnvironmentVariable = "RIMPULSE_IPC_PORT";
        public const string TokenEnvironmentVariable = "RIMPULSE_IPC_TOKEN";
        public const string CompanionPackageId = "rimpulse.companion";
        public const int HeartbeatMilliseconds = 5000;
        public const int MaximumFrameBytes = 524288;
    }
}

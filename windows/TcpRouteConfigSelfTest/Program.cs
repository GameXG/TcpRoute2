namespace TcpRouteTray;

internal static class Program
{
    private static void Main(string[] args)
    {
        ConfigCodecSelfTest.Run();
        SubscriptionSelfTest.Run();
        GeoDataManagerSelfTest.Run();
        WintunDependenciesSelfTest.Run();
        TrayConfigurationSelfTest.Run();
        PowerShellOutputSelfTest.Run();
        SocksListenerProbeSelfTest.Run();
        WintunNetworkSelfTest.Run();
        foreach (var artifact in args) ArtifactSelfTest.Run(artifact);
        Console.WriteLine("All Windows config self-tests passed.");
    }
}

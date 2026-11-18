using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using RoundsP2P.Network.Tests;
using Steamworks;
using Xunit;
using Xunit.Abstractions;

namespace RoundsP2P.Steam.Tests;

// Explicit remote-service diagnostic, separate from local adapter tests.
[Collection("Native Steam")]
[Trait("Category", "NativeSteamRelay")]
public sealed class NativeSteamRelayDiagnosticTests
{
    private readonly ITestOutputHelper output;
    public NativeSteamRelayDiagnosticTests(SteamRuntime runtime, ITestOutputHelper output)
    {
        _ = runtime;
        this.output = output;
    }

    [Fact]
    public void ROUNDS_app_can_obtain_Steam_authentication_certificate_and_relay_access()
    {
        string directory = Path.Combine(PhotonSession.FindRepositoryRoot(), "artifacts", "steam-tests", "native-relay-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var log = new List<string> { "AppID=1557740;real SDK;normal Steam session;no invitations" };
        var timer = Stopwatch.StartNew();
        var authentication = new SteamNetAuthenticationStatus_t();
        var relay = new SteamRelayNetworkStatus_t();
        ESteamNetworkingAvailability authAvailability = ESteamNetworkingAvailability.k_ESteamNetworkingAvailability_Unknown;
        ESteamNetworkingAvailability relayAvailability = ESteamNetworkingAvailability.k_ESteamNetworkingAvailability_Unknown;
        try
        {
            log.Add("InitAuthentication=" + SteamNetworkingSockets.InitAuthentication());
            SteamNetworkingUtils.InitRelayNetworkAccess();
            string? previous = null;
            while (timer.ElapsedMilliseconds < 30000)
            {
                SteamAPI.RunCallbacks();
                authAvailability = SteamNetworkingSockets.GetAuthenticationStatus(out authentication);
                relayAvailability = SteamNetworkingUtils.GetRelayNetworkStatus(out relay);
                string status = "auth=" + authAvailability + ";auth_debug=" + authentication.m_debugMsg +
                    ";relay=" + relayAvailability + ";relay_debug=" + relay.m_debugMsg +
                    ";network_config=" + relay.m_eAvailNetworkConfig + ";any_relay=" + relay.m_eAvailAnyRelay;
                if (status != previous)
                {
                    log.Add(timer.ElapsedMilliseconds + "ms " + status);
                    previous = status;
                }
                if (authAvailability == ESteamNetworkingAvailability.k_ESteamNetworkingAvailability_Current &&
                    relayAvailability == ESteamNetworkingAvailability.k_ESteamNetworkingAvailability_Current)
                    break;
                if ((int)authAvailability <= -101 || (int)relayAvailability <= -101)
                    break;
                Thread.Sleep(5); // Bounded callback/service cadence while real SDK initializes.
            }
            Assert.True(authAvailability == ESteamNetworkingAvailability.k_ESteamNetworkingAvailability_Current,
                "Steam authentication certificate unavailable after " + timer.ElapsedMilliseconds + "ms: " + authAvailability + "; " + authentication.m_debugMsg);
            Assert.True(relayAvailability == ESteamNetworkingAvailability.k_ESteamNetworkingAvailability_Current,
                "Steam relay access unavailable after " + timer.ElapsedMilliseconds + "ms: " + relayAvailability + "; " + relay.m_debugMsg);
        }
        finally
        {
            log.Add("elapsed_ms=" + timer.ElapsedMilliseconds);
            string report = string.Join(Environment.NewLine, log);
            File.WriteAllText(Path.Combine(directory, "relay-and-auth.log"), report);
            output.WriteLine(report);
            output.WriteLine("Diagnostic artifact: " + directory);
        }
    }
}

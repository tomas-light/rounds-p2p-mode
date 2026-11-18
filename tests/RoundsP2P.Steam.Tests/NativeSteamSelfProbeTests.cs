using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using RoundsP2P.Network.Tests;
using Steamworks;
using Xunit;
using Xunit.Abstractions;

namespace RoundsP2P.Steam.Tests;

// A separate, explicitly selected diagnostic: this uses Steam signaling and a
// real P2P listener, not CreateSocketPair. It still does not prove a WAN session.
[Collection("Native Steam")]
[Trait("Category", "NativeSteamP2P")]
public sealed class NativeSteamSelfProbeTests
{
    private readonly ITestOutputHelper output;
    public NativeSteamSelfProbeTests(SteamRuntime runtime, ITestOutputHelper output)
    {
        _ = runtime; // Collection fixture owns genuine Steam initialization.
        this.output = output;
    }

    [Fact]
    public void Native_P2P_self_probe_connects_via_Steam_signaling_for_ROUNDS_appid()
    {
        const int probePort = 94; // Separate from the production listener's 93.
        var log = new List<string>();
        string directory = Path.Combine(PhotonSession.FindRepositoryRoot(), "artifacts", "steam-tests", "native-p2p-self-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        HSteamListenSocket listener = default;
        HSteamNetConnection outgoing = default;
        HSteamNetConnection incoming = default;
        string? failure = null;
        CSteamID self = SteamUser.GetSteamID();
        var connected = new HashSet<HSteamNetConnection>();
        using var callback = Callback<SteamNetConnectionStatusChangedCallback_t>.Create(change =>
        {
            // Assertions stay outside native callback dispatch: SDK callback
            // exception logging relies on Unity, unavailable in a .NET test host.
            log.Add("connection=" + change.m_hConn + ";state=" + change.m_info.m_eState + ";reason=" + change.m_info.m_eEndReason + ";debug=" + change.m_info.m_szEndDebug);
            if (change.m_info.m_eState == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connecting && change.m_info.m_hListenSocket.Equals(listener))
            {
                if (!change.m_info.m_identityRemote.GetSteamID().Equals(self) || !incoming.Equals(default(HSteamNetConnection)))
                {
                    SteamNetworkingSockets.CloseConnection(change.m_hConn, 1000, "Self probe accepts only its own identity", false);
                    failure = "Unexpected identity or extra incoming P2P connection.";
                    return;
                }
                incoming = change.m_hConn;
                EResult accepted = SteamNetworkingSockets.AcceptConnection(incoming);
                if (accepted != EResult.k_EResultOK) failure = "AcceptConnection returned " + accepted;
            }
            if (change.m_hConn.Equals(outgoing) || change.m_hConn.Equals(incoming))
            {
                if (change.m_info.m_eState == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected)
                    connected.Add(change.m_hConn);
                else if (change.m_info.m_eState == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer ||
                    change.m_info.m_eState == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ProblemDetectedLocally)
                    failure = "P2P closed: " + change.m_info.m_eEndReason + " " + change.m_info.m_szEndDebug;
            }
        });
        try
        {
            SteamNetworkingUtils.InitRelayNetworkAccess();
            log.Add("initial " + Availability());
            listener = SteamNetworkingSockets.CreateListenSocketP2P(probePort, 0, Array.Empty<SteamNetworkingConfigValue_t>());
            Assert.False(listener.Equals(default(HSteamListenSocket)), "Native Steam P2P listener creation failed. " + Availability());
            var identity = new SteamNetworkingIdentity();
            identity.SetSteamID(self);
            outgoing = SteamNetworkingSockets.ConnectP2P(ref identity, probePort, 0, Array.Empty<SteamNetworkingConfigValue_t>());
            Assert.False(outgoing.Equals(default(HSteamNetConnection)), "Native Steam self P2P connection creation failed. " + Availability());
            var timer = Stopwatch.StartNew();
            while (connected.Count < 2 && failure == null && timer.ElapsedMilliseconds < 30000)
            {
                SteamAPI.RunCallbacks();
                Thread.Sleep(5); // Bounded native callback pump, no blind startup delay.
            }
            log.Add("final " + Availability());
            Assert.Null(failure);
            Assert.True(connected.Count == 2, "Both real P2P handles did not reach Connected within 30 seconds. " + Availability());
            foreach (HSteamNetConnection handle in new[] { outgoing, incoming })
            {
                Assert.True(SteamNetworkingSockets.GetConnectionInfo(handle, out SteamNetConnectionInfo_t info));
                Assert.Equal(ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected, info.m_eState);
                Assert.Equal(self, info.m_identityRemote.GetSteamID());
                log.Add("verified self identity;flags=" + info.m_nFlags + ";relayed=" + ((info.m_nFlags & 16) != 0));
            }
        }
        finally
        {
            if (!outgoing.Equals(default(HSteamNetConnection))) SteamNetworkingSockets.CloseConnection(outgoing, 1000, "Self probe complete", false);
            if (!incoming.Equals(default(HSteamNetConnection))) SteamNetworkingSockets.CloseConnection(incoming, 1000, "Self probe complete", false);
            if (!listener.Equals(default(HSteamListenSocket))) SteamNetworkingSockets.CloseListenSocket(listener);
            string report = string.Join(Environment.NewLine, log);
            File.WriteAllText(Path.Combine(directory, "probe.log"), report);
            output.WriteLine(report);
            output.WriteLine("Diagnostic artifact: " + directory);
        }
    }

    private static string Availability()
    {
        var auth = new SteamNetAuthenticationStatus_t();
        var relay = new SteamRelayNetworkStatus_t();
        ESteamNetworkingAvailability authAvailability = SteamNetworkingSockets.GetAuthenticationStatus(out auth);
        ESteamNetworkingAvailability relayAvailability = SteamNetworkingUtils.GetRelayNetworkStatus(out relay);
        return "auth=" + authAvailability + ";auth_debug=" + auth.m_debugMsg +
            ";relay=" + relayAvailability + ";relay_debug=" + relay.m_debugMsg +
            ";network_config=" + relay.m_eAvailNetworkConfig + ";any_relay=" + relay.m_eAvailAnyRelay;
    }
}

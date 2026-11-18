using System;
using System.Runtime.InteropServices;
using Steamworks;
using Xunit;

namespace RoundsP2P.Steam.Tests;

[Collection("Native Steam")]
[Trait("Category", "NativeSteam")]
public sealed class NativeSteamSocketTests
{
    private readonly SteamRuntime runtime;
    public NativeSteamSocketTests(SteamRuntime runtime) => this.runtime = runtime;

    [Fact]
    public void Packetized_native_socket_pair_preserves_message_payload_and_boundary()
    {
        var firstIdentity = new SteamNetworkingIdentity();
        var secondIdentity = new SteamNetworkingIdentity();
        HSteamNetConnection first = default;
        HSteamNetConnection second = default;
        try
        {
            Assert.True(SteamNetworkingSockets.CreateSocketPair(out first, out second, true, ref firstIdentity, ref secondIdentity));
            Assert.True(SteamNetworkingSockets.GetConnectionInfo(first, out SteamNetConnectionInfo_t info));
            Assert.Equal(ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected, info.m_eState);
            byte[] payload = { 0, 255, 82, 80, 1, 2, 3, 4, 0, 128 };
            IntPtr outbound = Marshal.AllocHGlobal(payload.Length);
            try
            {
                Marshal.Copy(payload, 0, outbound, payload.Length);
                Assert.Equal(EResult.k_EResultOK, SteamNetworkingSockets.SendMessageToConnection(first, outbound, (uint)payload.Length, Constants.k_nSteamNetworkingSend_UnreliableNoNagle, out _));
            }
            finally { Marshal.FreeHGlobal(outbound); }
            var incoming = new IntPtr[1];
            try
            {
                runtime.WaitUntil(() => SteamNetworkingSockets.ReceiveMessagesOnConnection(second, incoming, 1) == 1,
                    "native packetized Steam message");
                SteamNetworkingMessage_t message = Marshal.PtrToStructure<SteamNetworkingMessage_t>(incoming[0]);
                Assert.Equal(payload.Length, message.m_cbSize);
                var received = new byte[message.m_cbSize];
                Marshal.Copy(message.m_pData, received, 0, received.Length);
                Assert.Equal(payload, received);
            }
            finally
            {
                if (incoming[0] != IntPtr.Zero)
                    SteamNetworkingMessage_t.Release(incoming[0]);
            }
        }
        finally
        {
            if (!first.Equals(default(HSteamNetConnection))) SteamNetworkingSockets.CloseConnection(first, 0, "test complete", false);
            if (!second.Equals(default(HSteamNetConnection))) SteamNetworkingSockets.CloseConnection(second, 0, "test complete", false);
        }
    }
}

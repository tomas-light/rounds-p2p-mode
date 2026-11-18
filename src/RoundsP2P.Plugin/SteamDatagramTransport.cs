using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using RoundsP2P.Core.Tunnel;
using Steamworks;

namespace RoundsP2P.Plugin;

// Photon keeps its UDP ordering/reliability. Steam transports each original
// datagram as one unreliable message; Unity/Steam calls stay on the main thread.
internal sealed class SteamDatagramTransport : IDisposable
{
    internal const int VirtualPort = 93;
    private const int UnreliableNoNagle = 1; // k_nSteamNetworkingSend_UnreliableNoNagle
    private const int MaximumBufferedBytes = DatagramFrame.MaxFrameLength * 128;
    private readonly UdpTunnel _tunnel;
    private readonly Action<string> _log;
    private readonly Action _onConnected;
    private readonly Action<string> _onClosed;
    private readonly Func<ulong, bool>? _allowPeer;
    private readonly Callback<SteamNetConnectionStatusChangedCallback_t>? _callback;
    private readonly IntPtr[] _messages = new IntPtr[64];
    private readonly IntPtr _sendBuffer;
    private readonly byte[] _hello;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private HSteamListenSocket _listen;
    private HSteamNetConnection _connection;
    private bool _connected;
    private bool _nativeConnected;
    private double _handshakeStarted;
    private bool _closed;
    private bool _disposed;
    private bool _congested;

    private SteamDatagramTransport(bool host, int master, int game, Func<ulong, bool>? allowPeer,
        Action onConnected, Action<string> onClosed, Action<string> log, bool callbacks, string token)
    {
        _allowPeer = allowPeer;
        _onConnected = onConnected;
        _onClosed = onClosed;
        _log = log;
        _hello = Encoding.ASCII.GetBytes("ROUNDS-P2P/1\n" + token);
        if (_hello.Length > DatagramFrame.MaxFrameLength) throw new ArgumentException("Steam session handshake is too large.", nameof(token));
        _sendBuffer = Marshal.AllocHGlobal(DatagramFrame.MaxFrameLength);
        UdpTunnel? createdTunnel = null;
        try
        {
            createdTunnel = host ? UdpTunnel.CreateHost(master, game, SendFrame) : UdpTunnel.CreateClient(SendFrame);
            _tunnel = createdTunnel;
            if (callbacks) _callback = Callback<SteamNetConnectionStatusChangedCallback_t>.Create(OnStatusChanged);
        }
        catch { createdTunnel?.Dispose(); Marshal.FreeHGlobal(_sendBuffer); throw; }
    }

    internal int MasterPort => _tunnel.MasterPort;
    internal int GamePort => _tunnel.GamePort;
    internal bool IsConnected => _connected && !_closed && !_disposed;
    internal bool IsClosed => _closed || _disposed;
    internal int? PeerPingMilliseconds { get; private set; }
    internal string Route { get; private set; } = "unknown";

    internal static SteamDatagramTransport CreateHost(int master, int game, Func<ulong, bool> allowPeer,
        Action connected, Action<string> closed, Action<string> log, string token)
    {
        var transport = new SteamDatagramTransport(true, master, game, allowPeer, connected, closed, log, true, token);
        try
        {
            transport._listen = SteamNetworkingSockets.CreateListenSocketP2P(VirtualPort, 0, Array.Empty<SteamNetworkingConfigValue_t>());
            if (transport._listen.Equals(default(HSteamListenSocket))) throw new InvalidOperationException("Steam could not create a P2P listener.");
            return transport;
        }
        catch { transport.Dispose(); throw; }
    }

    internal static SteamDatagramTransport CreateClient(ulong hostId, Action connected, Action<string> closed, Action<string> log, string token)
    {
        var transport = new SteamDatagramTransport(false, 0, 0, null, connected, closed, log, true, token);
        try
        {
            var identity = new SteamNetworkingIdentity();
            identity.SetSteamID64(hostId);
            transport._connection = SteamNetworkingSockets.ConnectP2P(ref identity, VirtualPort, 0, Array.Empty<SteamNetworkingConfigValue_t>());
            if (transport._connection.Equals(default(HSteamNetConnection))) throw new InvalidOperationException("Steam could not start a P2P connection.");
            return transport;
        }
        catch { transport.Dispose(); throw; }
    }

    private void OnStatusChanged(SteamNetConnectionStatusChangedCallback_t change)
    {
        if (_disposed || _closed) return;
        HSteamNetConnection incoming = change.m_hConn;
        ESteamNetworkingConnectionState state = change.m_info.m_eState;
        if (state == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connecting &&
            !_listen.Equals(default(HSteamListenSocket)) && change.m_info.m_hListenSocket.Equals(_listen))
        {
            ulong peer = change.m_info.m_identityRemote.GetSteamID64();
            if (!_connection.Equals(default(HSteamNetConnection)) || _allowPeer == null || !_allowPeer(peer))
            {
                SteamNetworkingSockets.CloseConnection(incoming, 1000, "Not an admitted lobby member", false);
                return;
            }
            _connection = incoming;
            if (SteamNetworkingSockets.AcceptConnection(incoming) != EResult.k_EResultOK)
                SignalClosed("Steam rejected the incoming connection.");
        }
        if (!incoming.Equals(_connection)) return;
        if (state == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected && !_nativeConnected)
        {
            NativeConnected();
        }
        else if (state == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer ||
                 state == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ProblemDetectedLocally)
            SignalClosed("Steam connection closed (" + change.m_info.m_eEndReason + ").");
    }

    internal void Pump()
    {
        if (_disposed || _closed || !_nativeConnected) return;
        if (!_connected && _clock.Elapsed.TotalSeconds - _handshakeStarted > 10)
        { SignalClosed("Steam session handshake timed out."); return; }
        var status = new SteamNetConnectionRealTimeStatus_t();
        var lanes = new SteamNetConnectionRealTimeLaneStatus_t();
        if (SteamNetworkingSockets.GetConnectionRealTimeStatus(_connection, ref status, 0, ref lanes) == EResult.k_EResultOK)
        {
            _congested = status.m_cbPendingUnreliable > MaximumBufferedBytes;
            PeerPingMilliseconds = status.m_eState == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected && status.m_nPing >= 0
                ? status.m_nPing : (int?)null;
        }
        else PeerPingMilliseconds = null;
        if (SteamNetworkingSockets.GetConnectionInfo(_connection, out SteamNetConnectionInfo_t info))
            Route = (info.m_nFlags & 16) != 0 ? "relay" : "direct";
        int count = SteamNetworkingSockets.ReceiveMessagesOnConnection(_connection, _messages, _messages.Length);
        if (count < 0) { SignalClosed("Steam receive handle became invalid."); return; }
        try
        {
            for (int index = 0; index < count && !_disposed; index++)
            {
                var message = Marshal.PtrToStructure<SteamNetworkingMessage_t>(_messages[index]);
                if (!message.m_conn.Equals(_connection) || message.m_cbSize < DatagramFrame.HeaderLength || message.m_cbSize > DatagramFrame.MaxFrameLength)
                    continue;
                var frame = new byte[message.m_cbSize];
                Marshal.Copy(message.m_pData, frame, 0, frame.Length);
                if (IsHello(frame))
                {
                    if (!EqualBytes(frame, _hello)) { SignalClosed("Steam session protocol/build does not match."); break; }
                    if (!_connected)
                    {
                        _connected = true;
                        _log("steam_handshake_complete");
                        _onConnected();
                    }
                }
                else if (_connected) _tunnel.ReceiveFrame(frame);
            }
        }
        finally
        {
            // Release every SDK allocation, including malformed/unprocessed messages.
            for (int index = 0; index < count; index++)
            {
                SteamNetworkingMessage_t.Release(_messages[index]);
                _messages[index] = IntPtr.Zero;
            }
        }
        if (IsConnected) _tunnel.Pump();
    }

    private void NativeConnected()
    {
        _nativeConnected = true;
        _handshakeStarted = _clock.Elapsed.TotalSeconds;
        Marshal.Copy(_hello, 0, _sendBuffer, _hello.Length);
        if (SteamNetworkingSockets.SendMessageToConnection(_connection, _sendBuffer, (uint)_hello.Length, 9, out _) != EResult.k_EResultOK)
            SignalClosed("Steam could not send the session handshake.");
    }

    private static bool IsHello(byte[] message) => message.Length >= 7 && message[0] == 'R' && message[1] == 'O' && message[2] == 'U';
    private static bool EqualBytes(byte[] left, byte[] right)
    {
        if (left.Length != right.Length) return false;
        for (int index = 0; index < left.Length; index++) if (left[index] != right[index]) return false;
        return true;
    }

    private void SendFrame(byte[] frame)
    {
        if (!IsConnected || _congested) return; // Photon retransmits its own reliable traffic.
        Marshal.Copy(frame, 0, _sendBuffer, frame.Length);
        EResult result = SteamNetworkingSockets.SendMessageToConnection(_connection, _sendBuffer, (uint)frame.Length, UnreliableNoNagle, out _);
        if (result != EResult.k_EResultOK && result != EResult.k_EResultIgnored && result != EResult.k_EResultLimitExceeded)
            SignalClosed("Steam send failed (" + result + ").");
    }

    private void SignalClosed(string reason)
    {
        if (_closed || _disposed) return;
        _closed = true;
        _connected = false;
        PeerPingMilliseconds = null;
        _onClosed(reason);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _connected = false;
        PeerPingMilliseconds = null;
        _callback?.Dispose();
        if (!_connection.Equals(default(HSteamNetConnection))) SteamNetworkingSockets.CloseConnection(_connection, 1000, "Session ended", false);
        if (!_listen.Equals(default(HSteamListenSocket))) SteamNetworkingSockets.CloseListenSocket(_listen);
        _tunnel.Dispose();
        Marshal.FreeHGlobal(_sendBuffer);
    }

    // Real SDK packetized/encrypted loopback. This tests this adapter, not WAN/NAT.
    internal static void CreateConnectedPairForTest(int backendMaster, int backendGame, Action<string> log,
        out SteamDatagramTransport host, out SteamDatagramTransport client, string hostToken = "socket-pair-test", string clientToken = "socket-pair-test")
    {
        var first = new SteamNetworkingIdentity();
        var second = new SteamNetworkingIdentity();
        if (!SteamNetworkingSockets.CreateSocketPair(out HSteamNetConnection a, out HSteamNetConnection b, true, ref first, ref second))
            throw new InvalidOperationException("Steam could not create the native loopback socket pair.");
        SteamDatagramTransport? createdHost = null;
        SteamDatagramTransport? createdClient = null;
        try
        {
            createdHost = new SteamDatagramTransport(true, backendMaster, backendGame, null, () => { }, _ => { }, log, false, hostToken) { _connection = a };
            createdClient = new SteamDatagramTransport(false, 0, 0, null, () => { }, _ => { }, log, false, clientToken) { _connection = b };
            createdHost.NativeConnected();
            createdClient.NativeConnected();
            host = createdHost;
            client = createdClient;
        }
        catch
        {
            if (createdHost != null) createdHost.Dispose(); else SteamNetworkingSockets.CloseConnection(a, 1000, "Test setup failed", false);
            if (createdClient != null) createdClient.Dispose(); else SteamNetworkingSockets.CloseConnection(b, 1000, "Test setup failed", false);
            throw;
        }
    }
}

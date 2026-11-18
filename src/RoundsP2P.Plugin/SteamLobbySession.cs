using System;
using System.Collections.Generic;
using System.Diagnostics;
using RoundsP2P.Core;
using Steamworks;

namespace RoundsP2P.Plugin;

// SteamManager owns SDK initialization and RunCallbacks. This controller owns only
// its lobby, retained callbacks, application session and tunnel lifetime.
internal sealed class SteamLobbySession : IDisposable
{
    private const double OperationTimeoutSeconds = 40;
    private static readonly string[] MetadataKeys =
    {
        SteamLobbyDescriptor.MarkerKey, "r2p_protocol", "r2p_gamefingerprint", "r2p_host", "r2p_room", "r2p_ready"
    };
    private readonly Plugin _plugin;
    private readonly string _fingerprint;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly CallResult<LobbyCreated_t> _created;
    private readonly CallResult<LobbyEnter_t> _entered;
    private readonly Callback<LobbyDataUpdate_t> _data;
    private readonly Callback<LobbyChatUpdate_t> _members;
    private CSteamID _lobby;
    private CSteamID _lookupLobby;
    private CSteamID _joinTarget;
    private Action? _stockFallback;
    private SteamLobbyDescriptor? _descriptor;
    private SteamDatagramTransport? _transport;
    private bool _lookupPending;
    private bool _creating;
    private bool _joining;
    private bool _abandonCreate;
    private bool _abandonJoin;
    private bool _host;
    private bool _hostReady;
    private bool _hadPeer;
    private bool _guestStarted;
    private bool _closing;
    private bool _disposed;
    private double _deadline;
    private string? _pendingFailure;

    internal SteamLobbySession(Plugin plugin, string fingerprint)
    {
        _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
        // Reuse the pure validator before registering any native callbacks.
        _fingerprint = new SteamLobbyDescriptor(1, fingerprint, Guid.NewGuid().ToString("N"), false).GameFingerprint;
        _created = CallResult<LobbyCreated_t>.Create(OnCreated);
        _entered = CallResult<LobbyEnter_t>.Create(OnEntered);
        _data = Callback<LobbyDataUpdate_t>.Create(OnDataUpdated);
        _members = Callback<LobbyChatUpdate_t>.Create(OnMembersUpdated);
        try { SteamNetworkingUtils.InitRelayNetworkAccess(); }
        catch
        {
            _created.Dispose();
            _entered.Dispose();
            _data.Dispose();
            _members.Dispose();
            throw;
        }
    }

    internal string Status { get; private set; } = "Создайте лобби и пригласите друга через Steam.";
    internal bool HasLobby => !_disposed && _lobby.IsValid() && _lobby.IsLobby();
    internal int? PeerPingMilliseconds => _transport?.IsConnected == true ? _transport.PeerPingMilliseconds : null;
    internal bool CanInvite => HasLobby && _host && _hostReady && !_hadPeer && _transport != null && !_closing;

    internal void Create()
    {
        if (_disposed) return;
        if (_creating || _joining) { RejectAttempt("Steam завершает предыдущее подключение. Подождите немного."); return; }
        if (HasLobby) { _plugin.ShowSteamLobbyPage(); return; }
        if (_plugin.Active) { RejectAttempt("Сначала завершите текущую игру."); return; }
        ClearLookup();
        _creating = true;
        _abandonCreate = false;
        Status = "Создаём лобби…";
        ArmDeadline();
        try
        {
            SteamAPICall_t operation = SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypeFriendsOnly, 2);
            if (operation.Equals(default(SteamAPICall_t))) throw new InvalidOperationException("CreateLobby returned an invalid handle.");
            _created.Set(operation);
            _plugin.WriteTrace("steam_lobby_create_requested", "friends_only;max_members=2");
        }
        catch (Exception exception)
        {
            _creating = false;
            Fault("Не удалось создать лобби Steam.", exception);
        }
    }

    internal void Invite()
    {
        if (!CanInvite) { RejectAttempt("Лобби ещё не готово к приглашению друга."); return; }
        if (!SteamUtils.IsOverlayEnabled()) { RejectAttempt("Включите оверлей Steam для приглашения друга."); return; }
        SteamFriends.ActivateGameOverlayInviteDialog(_lobby);
    }

    internal void ReceiveInvite(CSteamID lobby, Action? fallbackToStock)
    {
        if (_disposed) return;
        if (!lobby.IsValid() || !lobby.IsLobby()) { RejectAttempt("Приглашение Steam недействительно."); return; }
        if ((_lookupPending && lobby.Equals(_lookupLobby)) || (_joining && lobby.Equals(_joinTarget))) return;
        if (HasLobby && lobby.Equals(_lobby)) { _plugin.ShowSteamLobbyPage(); return; }
        if (_creating || _joining) { RejectAttempt("Дождитесь завершения текущего подключения."); return; }
        if (HasLobby || _plugin.Active) { RejectAttempt("Сначала выйдите из текущего лобби."); return; }
        _lookupLobby = lobby;
        _lookupPending = true;
        _stockFallback = fallbackToStock;
        Status = "Проверяем приглашение…";
        ArmDeadline();
        try
        {
            if (!SteamMatchmaking.RequestLobbyData(lobby)) throw new InvalidOperationException("RequestLobbyData failed.");
            _plugin.WriteTrace("steam_lobby_data_requested", "invited_lobby");
        }
        catch (Exception exception) { Fault("Не удалось проверить приглашение Steam.", exception); }
    }

    private void OnCreated(LobbyCreated_t result, bool ioFailure)
    {
        _creating = false;
        bool success = !ioFailure && result.m_eResult == EResult.k_EResultOK;
        var lobby = new CSteamID(result.m_ulSteamIDLobby);
        if (_disposed || _abandonCreate)
        {
            _abandonCreate = false;
            if (success && lobby.IsValid()) Cleanup(() => SteamMatchmaking.LeaveLobby(lobby));
            return;
        }
        if (!success || !lobby.IsValid() || !lobby.IsLobby()) { Fail("Не удалось создать лобби Steam. Попробуйте ещё раз."); return; }
        _lobby = lobby;
        _host = true;
        _hostReady = false;
        _hadPeer = false;
        _deadline = 0;
        try
        {
            CSteamID self = SteamUser.GetSteamID();
            if (!self.IsValid() || !SteamMatchmaking.GetLobbyOwner(lobby).Equals(self))
                throw new InvalidOperationException("Created lobby owner does not match the local Steam user.");
            _descriptor = new SteamLobbyDescriptor(self.m_SteamID, _fingerprint, Guid.NewGuid().ToString("N"), false);
            Publish(_descriptor);
            PrototypeOptions options = _plugin.StartSteamHost(_descriptor.RoomName);
            if (!_lobby.Equals(lobby) || !_plugin.Active) return;
            _transport = SteamDatagramTransport.CreateHost(options.MasterPort, options.GamePort, AdmitPeer,
                () => { _hadPeer = true; Status = "Друг подключён. Подготавливаем матч…"; },
                _ => QueueFailure("Соединение с другом потеряно."),
                detail => _plugin.WriteTrace("steam_transport", detail), Token(_descriptor));
            Status = "Подготавливаем лобби…";
            ArmDeadline();
            _plugin.ShowSteamLobbyPage();
            _plugin.WriteTrace("steam_lobby_created", "host_server_starting");
        }
        catch (Exception exception) { Fault("Не удалось подготовить игру в лобби Steam.", exception); }
    }

    private void OnDataUpdated(LobbyDataUpdate_t update)
    {
        if (_disposed || _closing) return;
        var lobby = new CSteamID(update.m_ulSteamIDLobby);
        if (_lookupPending && lobby.Equals(_lookupLobby))
        {
            if (update.m_bSuccess == 0) { Fail("Не удалось получить данные приглашённого лобби."); return; }
            try
            {
                Dictionary<string, string> metadata = ReadMetadata(lobby);
                if (metadata[SteamLobbyDescriptor.MarkerKey] != SteamLobbyDescriptor.MarkerValue)
                {
                    Action? fallback = _stockFallback;
                    ClearLookup();
                    _deadline = 0;
                    if (fallback == null) Fail("В этом лобби не используется совместимая версия мода.");
                    else fallback();
                    return;
                }
                if (!TryReadDescriptor(lobby, metadata, out SteamLobbyDescriptor? descriptor)) return;
                _descriptor = descriptor;
                ClearLookup();
                _joinTarget = lobby;
                _joining = true;
                _abandonJoin = false;
                Status = "Подключаемся к лобби…";
                ArmDeadline();
                SteamAPICall_t operation = SteamMatchmaking.JoinLobby(lobby);
                if (operation.Equals(default(SteamAPICall_t))) throw new InvalidOperationException("JoinLobby returned an invalid handle.");
                _entered.Set(operation);
            }
            catch (Exception exception)
            {
                // No pending SDK handle was retained if JoinLobby/Set threw.
                _joining = false;
                Fault("Не удалось подключиться к лобби Steam.", exception);
            }
            return;
        }
        if (!HasLobby || !lobby.Equals(_lobby)) return;
        if (update.m_bSuccess == 0) { QueueFailure("Не удалось обновить состояние лобби Steam."); return; }
        try
        {
            if (!TryReadDescriptor(lobby, ReadMetadata(lobby), out SteamLobbyDescriptor? current)) return;
            if (_descriptor != null && current!.RoomName != _descriptor.RoomName)
            { Fail("Игра в этом лобби изменилась. Подключитесь заново."); return; }
            _descriptor = current;
            if (!_host && _transport == null) StartGuestWhenReady();
        }
        catch (Exception exception) { Fault("Не удалось проверить состояние лобби Steam.", exception); }
    }

    private void OnEntered(LobbyEnter_t result, bool ioFailure)
    {
        _joining = false;
        var lobby = new CSteamID(result.m_ulSteamIDLobby);
        bool success = !ioFailure && result.m_EChatRoomEnterResponse == (uint)EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess;
        CSteamID expected = _joinTarget;
        _joinTarget = default;
        if (_disposed || _abandonJoin)
        {
            _abandonJoin = false;
            if (success && lobby.IsValid()) Cleanup(() => SteamMatchmaking.LeaveLobby(lobby));
            return;
        }
        if (!success || !lobby.Equals(expected))
        {
            if (success && lobby.IsValid()) Cleanup(() => SteamMatchmaking.LeaveLobby(lobby));
            Fail("Лобби недоступно или уже заполнено.");
            return;
        }
        _lobby = lobby;
        _host = false;
        _hostReady = false;
        _hadPeer = false;
        _guestStarted = false;
        try
        {
            if (!TryReadDescriptor(lobby, ReadMetadata(lobby), out SteamLobbyDescriptor? descriptor)) return;
            if (_descriptor != null && _descriptor.RoomName != descriptor!.RoomName)
            { Fail("Игра в этом лобби изменилась. Подключитесь заново."); return; }
            _descriptor = descriptor;
            if (!CheckMembership()) return;
            Status = "Ожидаем готовности хоста…";
            ArmDeadline();
            _plugin.ShowSteamLobbyPage();
            StartGuestWhenReady();
        }
        catch (Exception exception) { Fault("Не удалось подготовить подключение к другу.", exception); }
    }

    private void StartGuestWhenReady()
    {
        if (_host || _transport != null || _descriptor == null || !_descriptor.IsReady || !HasLobby) return;
        if (!CheckMembership()) return;
        Status = "Устанавливаем соединение с другом…";
        ArmDeadline();
        _transport = SteamDatagramTransport.CreateClient(_descriptor.HostId, OnGuestConnected,
            _ => QueueFailure("Не удалось сохранить соединение с другом."),
            detail => _plugin.WriteTrace("steam_transport", detail), Token(_descriptor));
    }

    private void OnGuestConnected()
    {
        if (_closing || _disposed || _guestStarted || _transport == null || _descriptor == null) return;
        try
        {
            if (!CheckMembership()) return;
            _guestStarted = true;
            Status = "Соединение готово. Подготавливаем матч…";
            ArmDeadline();
            _plugin.StartSteamClient(_transport.MasterPort, _transport.GamePort, _descriptor.RoomName);
        }
        catch (Exception exception) { QueueFailure("Не удалось начать игру с другом."); _plugin.WriteTrace("steam_client_start_error", exception.Message); }
    }

    private bool AdmitPeer(ulong peer)
    {
        if (_closing || _disposed || !_host || !_hostReady || !HasLobby || _descriptor == null) return false;
        var identity = new CSteamID(peer);
        return identity.IsValid() && SteamLobbyDescriptor.CanAdmitPeer(peer, _descriptor.HostId,
            SteamMatchmaking.GetLobbyOwner(_lobby).m_SteamID, GetMembers());
    }

    private void OnMembersUpdated(LobbyChatUpdate_t update)
    {
        if (_disposed || _closing || !HasLobby || update.m_ulSteamIDLobby != _lobby.m_SteamID) return;
        try { CheckMembership(); }
        catch (Exception exception) { Fault("Не удалось обновить участников лобби.", exception); }
    }

    private bool CheckMembership()
    {
        if (_descriptor == null || !HasLobby) return false;
        ulong owner = SteamMatchmaking.GetLobbyOwner(_lobby).m_SteamID;
        if (owner != _descriptor.HostId) { Fail("Хост покинул лобби. Создайте новое лобби для игры."); return false; }
        List<ulong> members = GetMembers();
        ulong local = SteamUser.GetSteamID().m_SteamID;
        if (members.Count < 1 || members.Count > 2 || members.Contains(0) ||
            new HashSet<ulong>(members).Count != members.Count || !members.Contains(local) || !members.Contains(owner))
        { Fail("Состав участников лобби изменился. Подключитесь заново."); return false; }
        if (_host)
        {
            SteamMatchmaking.SetLobbyJoinable(_lobby, members.Count < 2);
            if (members.Count == 2) _hadPeer = true;
            else if (_hadPeer) { Fail("Друг вышел из лобби."); return false; }
        }
        else if (!SteamLobbyDescriptor.CanAdmitPeer(local, owner, owner, members))
        { Fail("Хост покинул лобби."); return false; }
        return true;
    }

    private List<ulong> GetMembers()
    {
        int count = SteamMatchmaking.GetNumLobbyMembers(_lobby);
        var members = new List<ulong>(Math.Min(Math.Max(count, 0), 3));
        if (count < 0 || count > 2) return new List<ulong> { 0, 0, 0 };
        for (int index = 0; index < count; index++)
        {
            CSteamID member = SteamMatchmaking.GetLobbyMemberByIndex(_lobby, index);
            members.Add(member.IsValid() ? member.m_SteamID : 0);
        }
        return members;
    }

    internal void OnPhotonRoomReady()
    {
        if (_disposed || _closing || !HasLobby || _descriptor == null) return;
        if (!_host)
        {
            _deadline = 0;
            Status = "Соединение готово. Начинаем матч…";
            return;
        }
        try
        {
            _hostReady = true;
            _descriptor = new SteamLobbyDescriptor(_descriptor.HostId, _fingerprint, _descriptor.RoomName, true);
            Publish(_descriptor);
            _deadline = 0; // A prepared host may wait for its friend indefinitely.
            Status = "Лобби готово. Пригласите друга через Steam.";
            _plugin.ShowSteamLobbyPage();
            _plugin.WriteTrace("steam_lobby_ready", "photon_room_ready;invite_available");
        }
        catch (Exception exception) { Fault("Не удалось открыть готовое лобби для друга.", exception); }
    }

    internal void OnPhotonStopped()
    {
        if (!_closing && !_disposed && (HasLobby || _transport != null)) Leave();
    }

    internal void Pump()
    {
        if (_disposed || _closing) return;
        if (_pendingFailure != null)
        {
            string failure = _pendingFailure;
            _pendingFailure = null;
            Fail(failure);
            return;
        }
        if (_deadline > 0 && _clock.Elapsed.TotalSeconds >= _deadline)
        { Fail("Steam не завершил подключение вовремя. Попробуйте ещё раз."); return; }
        _transport?.Pump();
    }

    internal void Leave()
    {
        if (_disposed || _closing) return;
        _closing = true;
        try
        {
            if (_creating) _abandonCreate = true;
            if (_joining) _abandonJoin = true;
            ClearLookup();
            _deadline = 0;
            _pendingFailure = null;
            SteamDatagramTransport? transport = _transport;
            _transport = null;
            if (transport != null) Cleanup(transport.Dispose);
            CSteamID lobby = _lobby;
            bool wasHost = _host;
            _lobby = default;
            _descriptor = null;
            _host = false;
            _hostReady = false;
            _hadPeer = false;
            _guestStarted = false;
            if (lobby.IsValid())
            {
                if (wasHost)
                {
                    Cleanup(() => SteamMatchmaking.SetLobbyJoinable(lobby, false));
                    Cleanup(() => SteamMatchmaking.SetLobbyData(lobby, "r2p_ready", "0"));
                }
                Cleanup(() => SteamMatchmaking.LeaveLobby(lobby));
            }
            Cleanup(_plugin.StopSteamPhoton);
            Status = "Вы вышли из лобби.";
            _plugin.WriteTrace("steam_lobby_left", "owned_lobby_and_transport_cleaned");
        }
        finally { _closing = false; }
    }

    internal void Fail(string message)
    {
        if (_disposed || _closing) return;
        Leave();
        Status = message;
        _plugin.ShowSteamFailure(message);
    }

    public void Dispose()
    {
        if (_disposed) return;
        Leave();
        _disposed = true;
        _created.Dispose();
        _entered.Dispose();
        _data.Dispose();
        _members.Dispose();
    }

    private bool TryReadDescriptor(CSteamID lobby, IReadOnlyDictionary<string, string> metadata, out SteamLobbyDescriptor? descriptor)
    {
        if (SteamLobbyDescriptor.TryParse(metadata, SteamMatchmaking.GetLobbyOwner(lobby).m_SteamID, _fingerprint, out descriptor, out string error))
            return true;
        _plugin.WriteTrace("steam_lobby_metadata_rejected", error);
        Fail("Версии игры или параметры лобби несовместимы. Проверьте одинаковую версию игры и мода.");
        return false;
    }

    private static Dictionary<string, string> ReadMetadata(CSteamID lobby)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string key in MetadataKeys) metadata[key] = SteamMatchmaking.GetLobbyData(lobby, key);
        return metadata;
    }

    private void Publish(SteamLobbyDescriptor descriptor)
    {
        foreach (KeyValuePair<string, string> pair in descriptor.ToMetadata())
            if (!SteamMatchmaking.SetLobbyData(_lobby, pair.Key, pair.Value))
                throw new InvalidOperationException("Steam refused lobby metadata: " + pair.Key);
    }

    private static string Token(SteamLobbyDescriptor descriptor) =>
        SteamLobbyDescriptor.Protocol + ";" + descriptor.RoomName + ";" + descriptor.GameFingerprint;
    private void ArmDeadline() => _deadline = _clock.Elapsed.TotalSeconds + OperationTimeoutSeconds;
    private void QueueFailure(string message) { if (!_closing && !_disposed) _pendingFailure = message; }
    private void ClearLookup() { _lookupPending = false; _lookupLobby = default; _stockFallback = null; }
    private void RejectAttempt(string message) { Status = message; _plugin.ShowSteamFailure(message); }
    private void Fault(string message, Exception exception) { _plugin.WriteTrace("steam_lobby_exception", exception.Message); Fail(message); }
    private void Cleanup(Action action)
    {
        try { action(); }
        catch (Exception exception) { _plugin.WriteTrace("steam_lobby_cleanup_error", exception.Message); }
    }
}

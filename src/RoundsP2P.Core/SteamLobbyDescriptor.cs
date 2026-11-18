using System;
using System.Collections.Generic;
using System.Globalization;

namespace RoundsP2P.Core
{
    /// <summary>Validated metadata for one two-player Steam lobby; no Steam SDK dependency.</summary>
    public sealed class SteamLobbyDescriptor
    {
        public const string MarkerKey = "r2p_mod";
        public const string MarkerValue = "rounds-p2p";
        public const string Protocol = "2";

        public SteamLobbyDescriptor(ulong hostId, string gameFingerprint, string roomName, bool isReady)
        {
            if (hostId == 0)
                throw new ArgumentOutOfRangeException(nameof(hostId));
            if (gameFingerprint == null)
                throw new ArgumentNullException(nameof(gameFingerprint));
            if (!IsHex(gameFingerprint, 64, allowUppercase: true))
                throw new ArgumentException("The game fingerprint must contain exactly 64 hexadecimal characters.", nameof(gameFingerprint));
            if (roomName == null)
                throw new ArgumentNullException(nameof(roomName));
            if (!IsHex(roomName, 32, allowUppercase: false))
                throw new ArgumentException("The room name must contain exactly 32 lowercase hexadecimal characters.", nameof(roomName));

            HostId = hostId;
            GameFingerprint = gameFingerprint.ToLowerInvariant();
            RoomName = roomName;
            IsReady = isReady;
        }

        public ulong HostId { get; }
        public string GameFingerprint { get; }
        public string RoomName { get; }
        public bool IsReady { get; }

        public Dictionary<string, string> ToMetadata()
        {
            return new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { MarkerKey, MarkerValue },
                { "r2p_protocol", Protocol },
                { "r2p_gamefingerprint", GameFingerprint },
                { "r2p_host", HostId.ToString(CultureInfo.InvariantCulture) },
                { "r2p_room", RoomName },
                { "r2p_ready", IsReady ? "1" : "0" }
            };
        }

        public static bool TryParse(
            IReadOnlyDictionary<string, string> metadata,
            ulong actualOwner,
            string expectedFingerprint,
            out SteamLobbyDescriptor? descriptor,
            out string error)
        {
            descriptor = null;
            error = string.Empty;
            if (metadata == null || !metadata.TryGetValue(MarkerKey, out string marker) || marker != MarkerValue)
                return Reject("This is not a ROUNDS P2P lobby.", out error);
            if (!metadata.TryGetValue("r2p_protocol", out string protocol) || protocol != Protocol)
                return Reject("The lobby uses an unsupported protocol version.", out error);
            if (actualOwner == 0)
                return Reject("The lobby has no valid Steam owner.", out error);
            if (!metadata.TryGetValue("r2p_host", out string hostText) ||
                !ulong.TryParse(hostText, NumberStyles.None, CultureInfo.InvariantCulture, out ulong host) ||
                host == 0 || hostText != host.ToString(CultureInfo.InvariantCulture))
                return Reject("The lobby host identifier is invalid.", out error);
            if (host != actualOwner)
                return Reject("The lobby owner changed or does not match its declared host.", out error);
            if (!metadata.TryGetValue("r2p_gamefingerprint", out string fingerprint) || !IsHex(fingerprint, 64, allowUppercase: true))
                return Reject("The lobby game fingerprint is invalid.", out error);
            if (!IsHex(expectedFingerprint, 64, allowUppercase: true))
                return Reject("The local game fingerprint is invalid.", out error);
            if (!string.Equals(fingerprint, expectedFingerprint, StringComparison.OrdinalIgnoreCase))
                return Reject("The lobby uses a different game build.", out error);
            if (!metadata.TryGetValue("r2p_room", out string room) || !IsHex(room, 32, allowUppercase: false))
                return Reject("The lobby room identifier is invalid.", out error);
            if (!metadata.TryGetValue("r2p_ready", out string ready) || (ready != "0" && ready != "1"))
                return Reject("The lobby readiness flag is invalid.", out error);

            descriptor = new SteamLobbyDescriptor(host, fingerprint, room, ready == "1");
            return true;
        }

        /// <summary>
        /// Requires the remote peer and host to be the lobby's only two distinct nonzero members.
        /// Steam-specific identity validity and authenticated connection identity are checked by the caller.
        /// Ownership transfer fails closed rather than silently migrating the game server.
        /// </summary>
        public static bool CanAdmitPeer(ulong peerId, ulong localHostId, ulong actualOwner, IEnumerable<ulong> members)
        {
            if (peerId == 0 || localHostId == 0 || peerId == localHostId || actualOwner != localHostId || members == null)
                return false;
            var distinct = new HashSet<ulong>();
            foreach (ulong member in members)
            {
                if (member == 0)
                    return false;
                distinct.Add(member);
                if (distinct.Count > 2)
                    return false;
            }
            return distinct.Count == 2 && distinct.Contains(peerId) && distinct.Contains(localHostId);
        }

        private static bool IsHex(string value, int requiredLength, bool allowUppercase)
        {
            if (value == null || value.Length != requiredLength)
                return false;
            foreach (char character in value)
            {
                bool valid = character >= '0' && character <= '9' || character >= 'a' && character <= 'f' ||
                    allowUppercase && character >= 'A' && character <= 'F';
                if (!valid)
                    return false;
            }
            return true;
        }

        private static bool Reject(string message, out string error)
        {
            error = message;
            return false;
        }
    }
}

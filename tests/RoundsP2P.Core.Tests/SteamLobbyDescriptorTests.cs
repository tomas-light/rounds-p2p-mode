using System;
using System.Collections.Generic;
using System.Globalization;
using RoundsP2P.Core;
using Xunit;

namespace RoundsP2P.Core.Tests
{
    public sealed class SteamLobbyDescriptorTests
    {
        private const ulong Host = 76561198000000001;
        private const ulong Peer = 76561198000000002;
        private const string Fingerprint = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        private const string Room = "0123456789abcdef0123456789abcdef";

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void MetadataRoundTripPreservesOwnerRoomAndReadiness(bool isReady)
        {
            var original = new SteamLobbyDescriptor(Host, Fingerprint, Room, isReady);
            Dictionary<string, string> metadata = original.ToMetadata();
            Assert.Equal(6, metadata.Count);
            Assert.Equal(SteamLobbyDescriptor.MarkerValue, metadata[SteamLobbyDescriptor.MarkerKey]);
            Assert.Equal(Host.ToString(CultureInfo.InvariantCulture), metadata["r2p_host"]);
            Assert.Equal(isReady ? "1" : "0", metadata["r2p_ready"]);

            Assert.True(SteamLobbyDescriptor.TryParse(metadata, Host, Fingerprint, out SteamLobbyDescriptor? parsed, out string error));
            Assert.NotNull(parsed);
            Assert.Equal(string.Empty, error);
            Assert.Equal(original.HostId, parsed.HostId);
            Assert.Equal(original.GameFingerprint, parsed.GameFingerprint);
            Assert.Equal(original.RoomName, parsed.RoomName);
            Assert.Equal(isReady, parsed.IsReady);
        }

        [Fact]
        public void FingerprintIsCanonicalizedAndComparedWithoutHexCaseDifferences()
        {
            var original = new SteamLobbyDescriptor(Host, Fingerprint.ToUpperInvariant(), Room, true);
            Assert.Equal(Fingerprint, original.GameFingerprint);
            Dictionary<string, string> metadata = original.ToMetadata();
            metadata["r2p_gamefingerprint"] = Fingerprint.ToUpperInvariant();
            Assert.True(SteamLobbyDescriptor.TryParse(metadata, Host, Fingerprint.ToUpperInvariant(), out SteamLobbyDescriptor? parsed, out _));
            Assert.Equal(Fingerprint, parsed!.GameFingerprint);
        }

        [Fact]
        public void MetadataDictionaryIsAnIndependentSnapshot()
        {
            var descriptor = new SteamLobbyDescriptor(Host, Fingerprint, Room, true);
            Dictionary<string, string> changed = descriptor.ToMetadata();
            changed["r2p_host"] = Peer.ToString(CultureInfo.InvariantCulture);
            changed.Clear();
            Assert.Equal(6, descriptor.ToMetadata().Count);
            Assert.Equal(Host, descriptor.HostId);
        }

        [Fact]
        public void EveryMissingRequiredFieldFailsClosed()
        {
            foreach (string key in Valid().Keys)
            {
                Dictionary<string, string> metadata = Valid();
                metadata.Remove(key);
                AssertRejected(metadata, Host, Fingerprint);
            }
        }

        [Theory]
        [InlineData("r2p_mod", "another-mod")]
        [InlineData("r2p_mod", "ROUNDS-P2P")]
        [InlineData("r2p_protocol", "0")]
        [InlineData("r2p_protocol", "1")]
        [InlineData("r2p_protocol", "01")]
        [InlineData("r2p_ready", "true")]
        [InlineData("r2p_ready", "false")]
        [InlineData("r2p_ready", "2")]
        [InlineData("r2p_ready", " 1")]
        [InlineData("r2p_host", "0")]
        [InlineData("r2p_host", "-1")]
        [InlineData("r2p_host", "+76561198000000001")]
        [InlineData("r2p_host", "076561198000000001")]
        [InlineData("r2p_host", "76561198000000001 ")]
        [InlineData("r2p_host", "18446744073709551616")]
        [InlineData("r2p_host", "76561198000000001\n")]
        [InlineData("r2p_host", "76561198000000001\0")]
        [InlineData("r2p_gamefingerprint", "abc")]
        [InlineData("r2p_room", "01234567-89ab-cdef-0123-456789abcdef")]
        [InlineData("r2p_room", "0123456789ABCDEF0123456789ABCDEF")]
        public void InvalidFieldValuesAreRejected(string key, string value)
        {
            Dictionary<string, string> metadata = Valid();
            metadata[key] = value;
            AssertRejected(metadata, Host, Fingerprint);
        }

        [Fact]
        public void OwnerTransferCannotSilentlyMigrateTheExistingServer()
        {
            AssertRejected(Valid(), Peer, Fingerprint);
            AssertRejected(Valid(), 0, Fingerprint);
        }

        [Fact]
        public void DifferentGameBuildAndInvalidLocalBuildAreRejected()
        {
            AssertRejected(Valid(), Host, new string('f', 64));
            AssertRejected(Valid(), Host, string.Empty);
            AssertRejected(Valid(), Host, null!);
        }

        [Fact]
        public void NullMetadataAndNullFieldsAreRejectedWithoutThrowing()
        {
            AssertRejected(null!, Host, Fingerprint);
            foreach (string key in Valid().Keys)
            {
                Dictionary<string, string> metadata = Valid();
                metadata[key] = null!;
                AssertRejected(metadata, Host, Fingerprint);
            }
        }

        [Fact]
        public void ConstructorValidatesBeforeCreatingMetadata()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new SteamLobbyDescriptor(0, Fingerprint, Room, true));
            Assert.Throws<ArgumentNullException>(() => new SteamLobbyDescriptor(Host, null!, Room, true));
            Assert.Throws<ArgumentNullException>(() => new SteamLobbyDescriptor(Host, Fingerprint, null!, true));
            foreach (string invalid in new[] { string.Empty, "f", new string('a', 63), new string('a', 65), new string('g', 64) })
                Assert.Throws<ArgumentException>(() => new SteamLobbyDescriptor(Host, invalid, Room, true));
            foreach (string invalid in new[] { string.Empty, new string('f', 31), new string('f', 33), Room.ToUpperInvariant(), new string('z', 32) })
                Assert.Throws<ArgumentException>(() => new SteamLobbyDescriptor(Host, Fingerprint, invalid, true));
        }

        [Fact]
        public void AsciiMutationsAndOversizedMetadataFailStrictFieldValidation()
        {
            foreach (string key in new[] { "r2p_gamefingerprint", "r2p_room" })
            {
                string valid = Valid()[key];
                for (int value = 0; value < 128; value++)
                {
                    char replacement = (char)value;
                    bool allowed = replacement >= '0' && replacement <= '9' || replacement >= 'a' && replacement <= 'f' ||
                        key == "r2p_gamefingerprint" && replacement >= 'A' && replacement <= 'F';
                    var chars = valid.ToCharArray();
                    chars[0] = replacement;
                    Dictionary<string, string> metadata = Valid();
                    metadata[key] = new string(chars);
                    // A valid changed hash is still a different build; supply the
                    // same expected hash to isolate character-shape validation.
                    string expected = key == "r2p_gamefingerprint" ? metadata[key] : Fingerprint;
                    bool parsed = SteamLobbyDescriptor.TryParse(metadata, Host, expected, out _, out _);
                    Assert.Equal(allowed, parsed);
                }
                Dictionary<string, string> oversized = Valid();
                oversized[key] = new string('a', 100000);
                AssertRejected(oversized, Host, Fingerprint);
            }
        }

        [Fact]
        public void RandomMalformedMetadataNeverCreatesAnUnvalidatedDescriptor()
        {
            var random = new Random(83117);
            string[] keys = { SteamLobbyDescriptor.MarkerKey, "r2p_protocol", "r2p_host", "r2p_gamefingerprint", "r2p_room", "r2p_ready" };
            for (int iteration = 0; iteration < 2000; iteration++)
            {
                Dictionary<string, string> metadata = Valid();
                string key = keys[random.Next(keys.Length)];
                var chars = new char[random.Next(0, 100)];
                for (int index = 0; index < chars.Length; index++)
                    chars[index] = (char)random.Next(0, 128);
                metadata[key] = new string(chars);
                bool success = SteamLobbyDescriptor.TryParse(metadata, Host, Fingerprint, out SteamLobbyDescriptor? descriptor, out string error);
                if (success)
                {
                    Assert.NotNull(descriptor);
                    Assert.Equal(Fingerprint, descriptor.GameFingerprint);
                    Assert.Equal(Host, descriptor.HostId);
                    Assert.Equal(string.Empty, error);
                }
                else
                {
                    Assert.Null(descriptor);
                    Assert.NotEmpty(error);
                }
            }
        }

        [Fact]
        public void OnlyTheHostAndTheAuthenticatedPeerMayBePresent()
        {
            Assert.True(SteamLobbyDescriptor.CanAdmitPeer(Peer, Host, Host, new[] { Host, Peer }));
            Assert.True(SteamLobbyDescriptor.CanAdmitPeer(Peer, Host, Host, new[] { Peer, Host }));
            Assert.True(SteamLobbyDescriptor.CanAdmitPeer(Peer, Host, Host, new[] { Peer, Host, Peer }));
            Assert.False(SteamLobbyDescriptor.CanAdmitPeer(Peer, Host, Host, Array.Empty<ulong>()));
            Assert.False(SteamLobbyDescriptor.CanAdmitPeer(Peer, Host, Host, new[] { Host }));
            Assert.False(SteamLobbyDescriptor.CanAdmitPeer(Peer, Host, Host, new[] { Peer }));
            Assert.False(SteamLobbyDescriptor.CanAdmitPeer(Peer, Host, Host, new[] { Host, Host }));
            Assert.False(SteamLobbyDescriptor.CanAdmitPeer(Peer, Host, Host, new[] { Host, Peer, 123UL }));
            Assert.False(SteamLobbyDescriptor.CanAdmitPeer(Peer, Host, Host, new[] { Host, 123UL }));
            Assert.False(SteamLobbyDescriptor.CanAdmitPeer(Peer, Host, Host, new[] { Host, Peer, 0UL }));
            Assert.False(SteamLobbyDescriptor.CanAdmitPeer(Peer, Host, Host, null!));
        }

        [Fact]
        public void PeerAndOwnerIdentityChecksFailClosed()
        {
            Assert.False(SteamLobbyDescriptor.CanAdmitPeer(0, Host, Host, new[] { Host, Peer }));
            Assert.False(SteamLobbyDescriptor.CanAdmitPeer(Host, Host, Host, new[] { Host, Peer }));
            Assert.False(SteamLobbyDescriptor.CanAdmitPeer(Peer, 0, 0, new[] { Host, Peer }));
            Assert.False(SteamLobbyDescriptor.CanAdmitPeer(Peer, Host, Peer, new[] { Host, Peer }));
            Assert.False(SteamLobbyDescriptor.CanAdmitPeer(Peer, Host, 0, new[] { Host, Peer }));
        }

        private static Dictionary<string, string> Valid() => new SteamLobbyDescriptor(Host, Fingerprint, Room, true).ToMetadata();

        private static void AssertRejected(IReadOnlyDictionary<string, string> metadata, ulong owner, string fingerprint)
        {
            Assert.False(SteamLobbyDescriptor.TryParse(metadata, owner, fingerprint, out SteamLobbyDescriptor? descriptor, out string error));
            Assert.Null(descriptor);
            Assert.NotEmpty(error);
        }
    }
}

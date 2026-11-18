using System;
using RoundsP2P.Core;
using Xunit;

namespace RoundsP2P.Core.Tests
{
    public sealed class PrototypeOptionsTests
    {
        [Theory]
        [InlineData(PrototypeRole.Disabled)]
        [InlineData(PrototypeRole.Host)]
        [InlineData(PrototypeRole.Client)]
        public void KeepsValidatedSettings(PrototypeRole role)
        {
            var options = new PrototypeOptions(role, "127.0.0.1", "192.168.1.10", "test-ROOM_42", 31000, 31001, 12.5);

            Assert.Equal(role, options.Role);
            Assert.Equal("127.0.0.1", options.HostAddress);
            Assert.Equal("192.168.1.10", options.AdvertisedAddress);
            Assert.Equal("test-ROOM_42", options.RoomName);
            Assert.Equal(31000, options.MasterPort);
            Assert.Equal(31001, options.GamePort);
            Assert.Equal(12.5, options.TimeoutSeconds);
        }

        [Theory]
        [InlineData("1.0.0.1")]
        [InlineData("10.0.0.1")]
        [InlineData("127.0.0.1")]
        [InlineData("172.16.0.1")]
        [InlineData("192.168.1.10")]
        [InlineData("223.255.255.254")]
        public void AcceptsCanonicalUnicastAddresses(string address)
        {
            _ = Create(address: address, advertisedAddress: address);
        }

        [Theory]
        [InlineData("")]
        [InlineData("localhost")]
        [InlineData("127.1")]
        [InlineData("2130706433")]
        [InlineData("0x7f000001")]
        [InlineData("127.000.0.1")]
        [InlineData("127.0.0.256")]
        [InlineData("127.0.0.-1")]
        [InlineData("127.0.0.+1")]
        [InlineData("127.0.0.1 ")]
        [InlineData(" 127.0.0.1")]
        [InlineData("127.0.0.1:5055")]
        [InlineData("127.0.0.1\nHTTP:\n  enabled: true")]
        [InlineData("0.0.0.0")]
        [InlineData("0.1.2.3")]
        [InlineData("224.0.0.1")]
        [InlineData("239.255.255.255")]
        [InlineData("240.0.0.1")]
        [InlineData("255.255.255.255")]
        [InlineData("::1")]
        [InlineData("::ffff:127.0.0.1")]
        [InlineData("１２７.０.０.１")]
        public void RejectsUnsafeOrAmbiguousAddressesInBothFields(string address)
        {
            Assert.Equal("hostAddress", Assert.Throws<ArgumentException>(() => Create(address: address)).ParamName);
            Assert.Equal("advertisedAddress", Assert.Throws<ArgumentException>(() => Create(advertisedAddress: address)).ParamName);
        }

        [Fact]
        public void RejectsNullAddressesWithParameterName()
        {
            Assert.Equal("hostAddress", Assert.Throws<ArgumentNullException>(() => Create(address: null!)).ParamName);
            Assert.Equal("advertisedAddress", Assert.Throws<ArgumentNullException>(() => Create(advertisedAddress: null!)).ParamName);
        }

        [Fact]
        public void RejectsOversizedAddressBeforeParsingOctets()
        {
            Assert.Throws<ArgumentException>(() => Create(address: new string('1', 10000)));
        }

        [Fact]
        public void ExhaustivelyChecksFirstOctetUnicastBoundary()
        {
            for (int first = 0; first <= 255; first++)
            {
                string address = first + ".1.2.3";
                if (first == 0 || first >= 224)
                    Assert.Throws<ArgumentException>(() => Create(address: address));
                else
                    Assert.Equal(address, Create(address: address).HostAddress);
            }
        }

        [Fact]
        public void ChecksEveryOctetValueAndRejectsOverflow()
        {
            for (int value = 0; value <= 256; value++)
            {
                for (int octet = 1; octet <= 3; octet++)
                {
                    string[] parts = { "10", "0", "0", "1" };
                    parts[octet] = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    string address = string.Join(".", parts);
                    if (value == 256)
                        Assert.Throws<ArgumentException>(() => Create(address: address));
                    else
                        Assert.Equal(address, Create(address: address).HostAddress);
                }
            }
        }

        [Theory]
        [InlineData("")]
        [InlineData("room name")]
        [InlineData("room/name")]
        [InlineData("room:name")]
        [InlineData("room\nname")]
        [InlineData("room'name")]
        [InlineData("комната")]
        [InlineData("room😀")]
        public void RejectsUnsafeRoomNames(string roomName)
        {
            Assert.Equal("roomName", Assert.Throws<ArgumentException>(() => Create(roomName: roomName)).ParamName);
        }

        [Fact]
        public void ChecksRoomLengthBoundariesAndNull()
        {
            Assert.Equal("a", Create(roomName: "a").RoomName);
            Assert.Equal(new string('a', 48), Create(roomName: new string('a', 48)).RoomName);
            Assert.Throws<ArgumentException>(() => Create(roomName: new string('a', 49)));
            Assert.Throws<ArgumentNullException>(() => Create(roomName: null!));
        }

        [Fact]
        public void FuzzesEveryAsciiCharacterInRoomName()
        {
            for (int code = 0; code < 128; code++)
            {
                char character = (char)code;
                string room = "a" + character + "b";
                bool allowed = char.IsAsciiLetterOrDigit(character) || character == '-' || character == '_';
                if (allowed)
                    Assert.Equal(room, Create(roomName: room).RoomName);
                else
                    Assert.Throws<ArgumentException>(() => Create(roomName: room));
            }
        }

        [Theory]
        [InlineData(int.MinValue)]
        [InlineData(-1)]
        [InlineData(0)]
        [InlineData(1023)]
        [InlineData(65536)]
        [InlineData(int.MaxValue)]
        public void RejectsPortsOutsideUnprivilegedUdpRange(int port)
        {
            Assert.Equal("masterPort", Assert.Throws<ArgumentOutOfRangeException>(() => Create(masterPort: port)).ParamName);
            Assert.Equal("gamePort", Assert.Throws<ArgumentOutOfRangeException>(() => Create(gamePort: port)).ParamName);
        }

        [Fact]
        public void ChecksPortBoundsAndCollision()
        {
            _ = Create(masterPort: 1024, gamePort: 65535);
            _ = Create(masterPort: 65535, gamePort: 1024);
            Assert.Equal("gamePort", Assert.Throws<ArgumentException>(() => Create(masterPort: 5055, gamePort: 5055)).ParamName);
        }

        [Theory]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        [InlineData(double.NegativeInfinity)]
        [InlineData(-1)]
        [InlineData(0)]
        [InlineData(0.999)]
        [InlineData(120.001)]
        public void RejectsNonFiniteAndOutOfRangeTimeouts(double timeout)
        {
            Assert.Equal("timeoutSeconds", Assert.Throws<ArgumentOutOfRangeException>(() => Create(timeoutSeconds: timeout)).ParamName);
        }

        [Fact]
        public void AcceptsTimeoutBounds()
        {
            Assert.Equal(1, Create(timeoutSeconds: 1).TimeoutSeconds);
            Assert.Equal(120, Create(timeoutSeconds: 120).TimeoutSeconds);
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(3)]
        [InlineData(int.MaxValue)]
        public void RejectsUndefinedRoles(int role)
        {
            Assert.Equal("role", Assert.Throws<ArgumentOutOfRangeException>(() => new PrototypeOptions((PrototypeRole)role, "127.0.0.1", "127.0.0.1", "test")).ParamName);
        }

        private static PrototypeOptions Create(
            string address = "127.0.0.1",
            string advertisedAddress = "192.168.1.10",
            string roomName = "test-room",
            int masterPort = 5055,
            int gamePort = 5056,
            double timeoutSeconds = 20)
        {
            return new PrototypeOptions(PrototypeRole.Host, address, advertisedAddress, roomName, masterPort, gamePort, timeoutSeconds);
        }
    }
}

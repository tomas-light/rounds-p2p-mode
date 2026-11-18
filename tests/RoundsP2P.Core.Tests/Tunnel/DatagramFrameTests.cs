using System;
using RoundsP2P.Core.Tunnel;
using Xunit;

namespace RoundsP2P.Core.Tests.Tunnel
{
    public sealed class DatagramFrameTests
    {
        [Theory]
        [InlineData(DatagramRoute.Master)]
        [InlineData(DatagramRoute.Game)]
        public void RoundTripsEveryAllowedPayloadLength(DatagramRoute route)
        {
            for (int length = 1; length <= DatagramFrame.MaxPayloadLength; length++)
            {
                var payload = new byte[length];
                for (int index = 0; index < payload.Length; index++)
                    payload[index] = (byte)(index * 37);
                byte[] frame = DatagramFrame.Encode(route, payload);

                Assert.True(DatagramFrame.TryDecode(frame, out DatagramRoute decodedRoute, out byte[] decoded));
                Assert.Equal(route, decodedRoute);
                Assert.Equal(payload, decoded);
                Assert.Equal(length + DatagramFrame.HeaderLength, frame.Length);
            }
        }

        [Fact]
        public void HeaderRejectsEveryInvalidMagicVersionAndRouteByte()
        {
            byte[] valid = DatagramFrame.Encode(DatagramRoute.Master, new byte[] { 42 });
            for (int offset = 0; offset < 4; offset++)
            {
                for (int value = 0; value <= byte.MaxValue; value++)
                {
                    if (value == valid[offset] || (offset == 3 && value == (byte)DatagramRoute.Game))
                        continue;
                    byte[] invalid = (byte[])valid.Clone();
                    invalid[offset] = (byte)value;
                    Assert.False(DatagramFrame.TryDecode(invalid, out _, out _));
                }
            }
        }

        [Fact]
        public void RejectsTruncationTrailingBytesAndUntrustedLength()
        {
            byte[] valid = DatagramFrame.Encode(DatagramRoute.Game, new byte[] { 1, 2, 3 });
            for (int length = 0; length < valid.Length; length++)
            {
                var shortened = new byte[length];
                Buffer.BlockCopy(valid, 0, shortened, 0, length);
                Assert.False(DatagramFrame.TryDecode(shortened, out _, out _));
            }
            var appended = new byte[valid.Length + 1];
            Buffer.BlockCopy(valid, 0, appended, 0, valid.Length);
            Assert.False(DatagramFrame.TryDecode(appended, out _, out _));

            foreach (int length in new[] { 0, 1, 2, 4, 1201, 65535 })
            {
                byte[] invalid = (byte[])valid.Clone();
                invalid[4] = (byte)(length >> 8);
                invalid[5] = (byte)length;
                Assert.False(DatagramFrame.TryDecode(invalid, out _, out _));
            }
        }

        [Fact]
        public void RejectsNullAndOversizedFramesWithoutPayloadAllocation()
        {
            Assert.False(DatagramFrame.TryDecode(null!, out DatagramRoute route, out byte[] payload));
            Assert.Equal(default, route);
            Assert.Empty(payload);
            Assert.False(DatagramFrame.TryDecode(new byte[65507], out _, out payload));
            Assert.Empty(payload);
        }

        [Fact]
        public void EncoderRejectsUndefinedRoutesAndInvalidPayloads()
        {
            Assert.Throws<ArgumentNullException>(() => DatagramFrame.Encode(DatagramRoute.Master, null!));
            Assert.Throws<ArgumentException>(() => DatagramFrame.Encode(DatagramRoute.Master, Array.Empty<byte>()));
            Assert.Throws<ArgumentException>(() => DatagramFrame.Encode(DatagramRoute.Master, new byte[1201]));
            for (int value = 0; value <= byte.MaxValue; value++)
            {
                if (value == 1 || value == 2)
                    continue;
                Assert.Throws<ArgumentOutOfRangeException>(() => DatagramFrame.Encode((DatagramRoute)value, new byte[] { 1 }));
            }
        }

        [Fact]
        public void FrameAndDecodedPayloadOwnTheirStorage()
        {
            var source = new byte[] { 10, 20 };
            byte[] frame = DatagramFrame.Encode(DatagramRoute.Game, source);
            source[0] = 99;
            Assert.True(DatagramFrame.TryDecode(frame, out _, out byte[] decoded));
            Assert.Equal(10, decoded[0]);
            frame[DatagramFrame.HeaderLength] = 88;
            Assert.Equal(10, decoded[0]);
            decoded[1] = 77;
            Assert.Equal(20, frame[DatagramFrame.HeaderLength + 1]);
        }

        [Fact]
        public void DeterministicRandomFramesNeverEscapeStrictBounds()
        {
            var random = new Random(73127);
            for (int iteration = 0; iteration < 10000; iteration++)
            {
                var bytes = new byte[random.Next(0, 1400)];
                random.NextBytes(bytes);
                if (DatagramFrame.TryDecode(bytes, out DatagramRoute route, out byte[] payload))
                {
                    Assert.InRange(payload.Length, 1, DatagramFrame.MaxPayloadLength);
                    Assert.Equal(bytes, DatagramFrame.Encode(route, payload));
                }
                else
                    Assert.Empty(payload);
            }
        }
    }
}

using System;

namespace RoundsP2P.Core.Tunnel
{
    /// <summary>One bounded UDP datagram per Steam message, without changing Photon delivery semantics.</summary>
    public static class DatagramFrame
    {
        public const int HeaderLength = 6;
        public const int MaxPayloadLength = 1200;
        public const int MaxFrameLength = HeaderLength + MaxPayloadLength;

        public static byte[] Encode(DatagramRoute route, byte[] payload)
        {
            if (route != DatagramRoute.Master && route != DatagramRoute.Game)
                throw new ArgumentOutOfRangeException(nameof(route));
            if (payload == null)
                throw new ArgumentNullException(nameof(payload));
            if (payload.Length == 0 || payload.Length > MaxPayloadLength)
                throw new ArgumentException("Tunnel payload must contain 1 to 1200 bytes.", nameof(payload));

            var frame = new byte[HeaderLength + payload.Length];
            frame[0] = (byte)'R';
            frame[1] = (byte)'P';
            frame[2] = 1;
            frame[3] = (byte)route;
            frame[4] = (byte)(payload.Length >> 8);
            frame[5] = (byte)payload.Length;
            Buffer.BlockCopy(payload, 0, frame, HeaderLength, payload.Length);
            return frame;
        }

        public static bool TryDecode(byte[] frame, out DatagramRoute route, out byte[] payload)
        {
            route = default;
            payload = Array.Empty<byte>();
            if (frame == null || frame.Length <= HeaderLength || frame.Length > MaxFrameLength ||
                frame[0] != (byte)'R' || frame[1] != (byte)'P' || frame[2] != 1 ||
                (frame[3] != (byte)DatagramRoute.Master && frame[3] != (byte)DatagramRoute.Game))
                return false;

            int length = (frame[4] << 8) | frame[5];
            if (length == 0 || length > MaxPayloadLength || frame.Length != HeaderLength + length)
                return false;

            route = (DatagramRoute)frame[3];
            payload = new byte[length];
            Buffer.BlockCopy(frame, HeaderLength, payload, 0, length);
            return true;
        }
    }
}

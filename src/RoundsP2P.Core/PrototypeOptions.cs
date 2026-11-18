using System;

namespace RoundsP2P.Core
{
    /// <summary>Validated settings for the direct-IPv4 network experiment.</summary>
    public sealed class PrototypeOptions
    {
        public PrototypeOptions(
            PrototypeRole role,
            string hostAddress,
            string advertisedAddress,
            string roomName,
            int masterPort = 5055,
            int gamePort = 5056,
            double timeoutSeconds = 20)
        {
            if (role != PrototypeRole.Disabled && role != PrototypeRole.Host && role != PrototypeRole.Client)
                throw new ArgumentOutOfRangeException(nameof(role));

            ValidateAddress(hostAddress, nameof(hostAddress));
            ValidateAddress(advertisedAddress, nameof(advertisedAddress));
            ValidateRoomName(roomName);
            ValidatePort(masterPort, nameof(masterPort));
            ValidatePort(gamePort, nameof(gamePort));
            if (masterPort == gamePort)
                throw new ArgumentException("Master and game servers need different UDP ports.", nameof(gamePort));
            if (double.IsNaN(timeoutSeconds) || double.IsInfinity(timeoutSeconds) || timeoutSeconds < 1 || timeoutSeconds > 120)
                throw new ArgumentOutOfRangeException(nameof(timeoutSeconds), "Timeout must be between 1 and 120 seconds.");

            Role = role;
            HostAddress = hostAddress;
            AdvertisedAddress = advertisedAddress;
            RoomName = roomName;
            MasterPort = masterPort;
            GamePort = gamePort;
            TimeoutSeconds = timeoutSeconds;
        }

        public PrototypeRole Role { get; }
        public string HostAddress { get; }
        public string AdvertisedAddress { get; }
        public string RoomName { get; }
        public int MasterPort { get; }
        public int GamePort { get; }
        public double TimeoutSeconds { get; }

        private static void ValidateAddress(string address, string parameterName)
        {
            if (address == null)
                throw new ArgumentNullException(parameterName);
            if (address.Length < 7 || address.Length > 15)
                throw InvalidAddress(parameterName);

            // Accept only canonical dotted-decimal IPv4. IPAddress.TryParse also accepts
            // shorthand and numeric forms, which are ambiguous in connection settings.
            string[] octets = address.Split('.');
            if (octets.Length != 4)
                throw InvalidAddress(parameterName);

            int firstOctet = -1;
            for (int index = 0; index < octets.Length; index++)
            {
                string octet = octets[index];
                if (octet.Length == 0 || octet.Length > 3 || (octet.Length > 1 && octet[0] == '0'))
                    throw InvalidAddress(parameterName);

                int value = 0;
                foreach (char digit in octet)
                {
                    if (digit < '0' || digit > '9')
                        throw InvalidAddress(parameterName);
                    value = value * 10 + digit - '0';
                }

                if (value > 255)
                    throw InvalidAddress(parameterName);
                if (index == 0)
                    firstOctet = value;
            }

            // Exclude this-network, multicast, reserved and limited-broadcast ranges.
            // Directed broadcasts cannot be determined without knowing the subnet mask.
            if (firstOctet == 0 || firstOctet >= 224)
                throw InvalidAddress(parameterName);
        }

        private static ArgumentException InvalidAddress(string parameterName)
        {
            return new ArgumentException("Use a canonical unicast IPv4 address, such as 127.0.0.1 or 192.168.1.10.", parameterName);
        }

        private static void ValidateRoomName(string roomName)
        {
            if (roomName == null)
                throw new ArgumentNullException(nameof(roomName));
            if (roomName.Length < 1 || roomName.Length > 48)
                throw new ArgumentException("Room names must contain 1 to 48 ASCII letters, digits, hyphens or underscores.", nameof(roomName));
            foreach (char character in roomName)
            {
                bool allowed = (character >= 'a' && character <= 'z') ||
                               (character >= 'A' && character <= 'Z') ||
                               (character >= '0' && character <= '9') || character == '-' || character == '_';
                if (!allowed)
                    throw new ArgumentException("Room names must contain 1 to 48 ASCII letters, digits, hyphens or underscores.", nameof(roomName));
            }
        }

        private static void ValidatePort(int port, string parameterName)
        {
            if (port < 1024 || port > 65535)
                throw new ArgumentOutOfRangeException(parameterName, "Use a UDP port between 1024 and 65535.");
        }
    }
}

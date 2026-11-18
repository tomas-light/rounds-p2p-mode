using System;
using System.Collections.Generic;

namespace RoundsP2P.Core
{
    public static class PhotonWireCompatibility
    {
        /// <summary>
        /// Adapts the game's integer MaxPlayers property to the byte expected by
        /// Luxon v1.5, preserving other room and operation properties.
        /// </summary>
        public static void NormalizeRoomOptions(IDictionary<byte, object> operation, bool usePropertiesKey)
        {
            if (operation == null)
                throw new ArgumentNullException(nameof(operation));

            byte propertiesKey = usePropertiesKey ? (byte)251 : (byte)248;
            if (!operation.TryGetValue(propertiesKey, out object propertiesValue) ||
                !(propertiesValue is IDictionary<object, object> properties) ||
                !properties.TryGetValue((byte)255, out object maxPlayersValue) ||
                !(maxPlayersValue is int maxPlayers))
                return;

            if (maxPlayers < byte.MinValue || maxPlayers > byte.MaxValue)
                throw new NotSupportedException("Luxon v1.5 supports MaxPlayers values between 0 and 255.");

            properties[(byte)255] = checked((byte)maxPlayers);
        }
    }
}

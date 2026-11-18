using System;
using System.Globalization;
using System.Text;

namespace RoundsP2P.Core
{
    public static class LuxonConfig
    {
        /// <summary>Renders the v1.5 server configuration for direct master-server connections.</summary>
        public static string Render(PrototypeOptions options)
        {
            if (options == null)
                throw new ArgumentNullException(nameof(options));

            var config = new StringBuilder();
            config.Append("Servers:\n");
            AppendServer(config, "MasterServer", options.AdvertisedAddress, options.MasterPort);
            AppendServer(config, "GameServer", options.AdvertisedAddress, options.GamePort);
            config.Append("EnableIPv6: false\nHTTP:\n  enabled: false\n");
            return config.ToString();
        }

        private static void AppendServer(StringBuilder config, string type, string address, int port)
        {
            string portText = port.ToString(CultureInfo.InvariantCulture);
            config.Append("  ").Append(type).Append(":\n")
                .Append("    - port: ").Append(portText).Append('\n')
                .Append("      external_address: ").Append(address).Append(':').Append(portText).Append('\n');
        }
    }
}

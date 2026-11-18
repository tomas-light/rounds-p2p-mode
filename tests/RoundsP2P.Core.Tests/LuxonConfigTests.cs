using System;
using System.Globalization;
using RoundsP2P.Core;
using Xunit;

namespace RoundsP2P.Core.Tests
{
    public sealed class LuxonConfigTests
    {
        [Fact]
        public void AdvertisesReachableHostForBothServerTransitionsAndDisablesDashboard()
        {
            var options = new PrototypeOptions(PrototypeRole.Host, "127.0.0.1", "192.168.1.20", "room", 32000, 32001);

            string config = LuxonConfig.Render(options);

            Assert.Equal(
                "Servers:\n" +
                "  MasterServer:\n" +
                "    - port: 32000\n" +
                "      external_address: 192.168.1.20:32000\n" +
                "  GameServer:\n" +
                "    - port: 32001\n" +
                "      external_address: 192.168.1.20:32001\n" +
                "EnableIPv6: false\n" +
                "HTTP:\n" +
                "  enabled: false\n", config);
            Assert.DoesNotContain("127.0.0.1", config);
            Assert.DoesNotContain("NameServer", config);
        }

        [Fact]
        public void PortFormattingIsIndependentOfCurrentCulture()
        {
            CultureInfo previous = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
                string config = LuxonConfig.Render(new PrototypeOptions(PrototypeRole.Host, "127.0.0.1", "127.0.0.1", "room", 12345, 54321));
                Assert.Contains("127.0.0.1:12345\n", config);
                Assert.Contains("127.0.0.1:54321\n", config);
            }
            finally
            {
                CultureInfo.CurrentCulture = previous;
            }
        }

        [Fact]
        public void RejectsMissingOptions()
        {
            Assert.Equal("options", Assert.Throws<ArgumentNullException>(() => LuxonConfig.Render(null!)).ParamName);
        }
    }
}

using System;
using System.Collections.Generic;
using RoundsP2P.Core;
using Xunit;

namespace RoundsP2P.Core.Tests
{
    public sealed class PhotonWireCompatibilityTests
    {
        [Theory]
        [InlineData(false, (byte)248)]
        [InlineData(true, (byte)251)]
        public void ConvertsEverySupportedIntegerWithoutChangingOtherProperties(bool usePropertiesKey, byte propertiesKey)
        {
            for (int playerCount = 0; playerCount <= 255; playerCount++)
            {
                var properties = new Dictionary<object, object>
                {
                    { (byte)255, playerCount },
                    { (byte)253, true },
                    { "custom", "keep" }
                };
                var unrelated = new object();
                var operation = new Dictionary<byte, object>
                {
                    { propertiesKey, properties },
                    { 1, unrelated }
                };

                PhotonWireCompatibility.NormalizeRoomOptions(operation, usePropertiesKey);

                Assert.Equal((byte)playerCount, Assert.IsType<byte>(properties[(byte)255]));
                Assert.Same(properties, operation[propertiesKey]);
                Assert.Same(unrelated, operation[1]);
                Assert.Equal(true, properties[(byte)253]);
                Assert.Equal("keep", properties["custom"]);
                Assert.Equal(3, properties.Count);
                Assert.Equal(2, operation.Count);
            }
        }

        [Theory]
        [InlineData(int.MinValue)]
        [InlineData(-1)]
        [InlineData(256)]
        [InlineData(int.MaxValue)]
        public void RefusesUnsupportedIntegersWithoutClampingOrMutating(int playerCount)
        {
            var properties = new Dictionary<object, object> { { (byte)255, playerCount } };
            var operation = new Dictionary<byte, object> { { 248, properties } };

            Assert.Throws<NotSupportedException>(() => PhotonWireCompatibility.NormalizeRoomOptions(operation, false));

            Assert.Equal(playerCount, Assert.IsType<int>(properties[(byte)255]));
        }

        [Fact]
        public void LeavesOtherValueTypesUnchanged()
        {
            object[] values = { (byte)2, (short)2, 2L, 2.0, "2", null! };
            foreach (object value in values)
            {
                var properties = new Dictionary<object, object> { { (byte)255, value } };
                var operation = new Dictionary<byte, object> { { 248, properties } };

                PhotonWireCompatibility.NormalizeRoomOptions(operation, false);

                Assert.Same(value, properties[(byte)255]);
            }
        }

        [Theory]
        [InlineData(false, (byte)248, (byte)251)]
        [InlineData(true, (byte)251, (byte)248)]
        public void OnlyNormalizesTheSelectedPropertyContainer(bool usePropertiesKey, byte selectedKey, byte otherKey)
        {
            var selected = new Dictionary<object, object> { { (byte)255, 2 } };
            var other = new Dictionary<object, object> { { (byte)255, 256 } };
            var operation = new Dictionary<byte, object> { { selectedKey, selected }, { otherKey, other } };

            PhotonWireCompatibility.NormalizeRoomOptions(operation, usePropertiesKey);

            Assert.IsType<byte>(selected[(byte)255]);
            Assert.Equal(256, Assert.IsType<int>(other[(byte)255]));
        }

        [Fact]
        public void DoesNotConfuseBytePropertyKeysWithIntegerOrStringKeys()
        {
            var properties = new Dictionary<object, object> { { 255, 2 }, { "255", 2 } };
            var operation = new Dictionary<byte, object> { { 248, properties } };

            PhotonWireCompatibility.NormalizeRoomOptions(operation, false);

            Assert.Equal(2, Assert.IsType<int>(properties[255]));
            Assert.Equal(2, Assert.IsType<int>(properties["255"]));
            Assert.Equal(2, properties.Count);
        }

        [Fact]
        public void PreservesMissingContainersAndProperties()
        {
            var empty = new Dictionary<byte, object>();
            PhotonWireCompatibility.NormalizeRoomOptions(empty, false);
            Assert.Empty(empty);

            var properties = new Dictionary<object, object> { { "custom", true } };
            var operation = new Dictionary<byte, object> { { 248, properties } };
            PhotonWireCompatibility.NormalizeRoomOptions(operation, false);
            Assert.Single(properties);
            Assert.Equal(true, properties["custom"]);
        }

        [Fact]
        public void LeavesUnexpectedContainerTypesAlone()
        {
            object[] values = { new Dictionary<string, object> { { "255", 2 } }, "properties", 2, null! };
            foreach (object value in values)
            {
                var operation = new Dictionary<byte, object> { { 248, value } };
                PhotonWireCompatibility.NormalizeRoomOptions(operation, false);
                Assert.Same(value, operation[248]);
            }
        }

        [Fact]
        public void NormalizationIsIdempotent()
        {
            var properties = new Dictionary<object, object> { { (byte)255, 2 } };
            var operation = new Dictionary<byte, object> { { 248, properties } };
            PhotonWireCompatibility.NormalizeRoomOptions(operation, false);
            object normalized = properties[(byte)255];

            PhotonWireCompatibility.NormalizeRoomOptions(operation, false);

            Assert.Same(normalized, properties[(byte)255]);
        }

        [Fact]
        public void RejectsMissingOperation()
        {
            Assert.Equal("operation", Assert.Throws<ArgumentNullException>(() => PhotonWireCompatibility.NormalizeRoomOptions(null!, false)).ParamName);
        }
    }
}

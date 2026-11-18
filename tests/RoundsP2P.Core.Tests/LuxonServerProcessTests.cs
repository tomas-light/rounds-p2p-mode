using System;
using System.IO;
using RoundsP2P.Core;
using Xunit;

namespace RoundsP2P.Core.Tests
{
    public sealed class LuxonServerProcessTests : IDisposable
    {
        private readonly string testDirectory = Path.Combine(Path.GetTempPath(), "RoundsP2P-CoreTests-" + Guid.NewGuid().ToString("N"));

        public LuxonServerProcessTests()
        {
            Directory.CreateDirectory(testDirectory);
        }

        [Fact]
        public void RefusesUnexpectedBinaryBeforeCreatingConfigurationOrStartingIt()
        {
            string binary = Path.Combine(testDirectory, "not-luxon.exe");
            string workingDirectory = Path.Combine(testDirectory, "server");
            File.WriteAllText(binary, "This file must never execute.");

            Assert.Throws<InvalidDataException>(() => new LuxonServerProcess(binary, workingDirectory, Options(), _ => { }));

            Assert.False(Directory.Exists(workingDirectory));
            Assert.Equal("This file must never execute.", File.ReadAllText(binary));
        }

        [Fact]
        public void MissingExecutableDoesNotCreateWorkingDirectory()
        {
            string workingDirectory = Path.Combine(testDirectory, "server");
            Assert.Throws<FileNotFoundException>(() => new LuxonServerProcess(Path.Combine(testDirectory, "missing.exe"), workingDirectory, Options(), _ => { }));
            Assert.False(Directory.Exists(workingDirectory));
        }

        [Theory]
        [InlineData(PrototypeRole.Client)]
        [InlineData(PrototypeRole.Disabled)]
        public void OnlyHostCanStartServer(PrototypeRole role)
        {
            string workingDirectory = Path.Combine(testDirectory, "server");
            Assert.Equal("options", Assert.Throws<ArgumentException>(() => new LuxonServerProcess("missing.exe", workingDirectory, Options(role), _ => { })).ParamName);
            Assert.False(Directory.Exists(workingDirectory));
        }

        [Theory]
        [InlineData("")]
        [InlineData(" ")]
        public void RejectsEmptyPaths(string path)
        {
            Assert.Equal("executablePath", Assert.Throws<ArgumentException>(() => new LuxonServerProcess(path, testDirectory, Options(), _ => { })).ParamName);
            Assert.Equal("workingDirectory", Assert.Throws<ArgumentException>(() => new LuxonServerProcess("missing.exe", path, Options(), _ => { })).ParamName);
        }

        [Fact]
        public void RejectsNullSettingsAndObserverBeforeAnyFileOperations()
        {
            Assert.Equal("options", Assert.Throws<ArgumentNullException>(() => new LuxonServerProcess("missing.exe", testDirectory, null!, _ => { })).ParamName);
            Assert.Equal("log", Assert.Throws<ArgumentNullException>(() => new LuxonServerProcess("missing.exe", testDirectory, Options(), null!)).ParamName);
        }

        [Fact]
        public void RejectsInvalidDirectoryPath()
        {
            Assert.Throws<ArgumentException>(() => new LuxonServerProcess("missing.exe", "directory\0suffix", Options(), _ => { }));
        }

        [Fact]
        public void RefusesDirectoryPathOccupiedByFileWithoutModifyingFile()
        {
            string occupiedPath = Path.Combine(testDirectory, "server");
            File.WriteAllText(occupiedPath, "Keep this file.");

            Assert.Equal("workingDirectory", Assert.Throws<ArgumentException>(() => new LuxonServerProcess("missing.exe", occupiedPath, Options(), _ => { })).ParamName);

            Assert.Equal("Keep this file.", File.ReadAllText(occupiedPath));
        }

        private static PrototypeOptions Options(PrototypeRole role = PrototypeRole.Host)
        {
            return new PrototypeOptions(role, "127.0.0.1", "127.0.0.1", "test");
        }

        public void Dispose()
        {
            // Delete only the unique directory created by this test instance.
            Directory.Delete(testDirectory, true);
        }
    }
}

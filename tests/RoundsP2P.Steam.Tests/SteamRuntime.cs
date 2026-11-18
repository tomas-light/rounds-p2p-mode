using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using Steamworks;
using Xunit;

namespace RoundsP2P.Steam.Tests;

[CollectionDefinition("Native Steam", DisableParallelization = true)]
public sealed class NativeSteamCollection : ICollectionFixture<SteamRuntime> { }

public sealed class SteamRuntime : IDisposable
{
    private readonly string? savedAppId = Environment.GetEnvironmentVariable("SteamAppId");
    private readonly string? savedGameId = Environment.GetEnvironmentVariable("SteamGameId");
    private bool initialized;

    public SteamRuntime()
    {
        string library = typeof(SteamRuntime).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "SteamNativeLibrary").Value!;
        Assert.True(File.Exists(library), "The installed game's native Steam SDK is required: " + library);
        NativeLibrary.SetDllImportResolver(typeof(SteamAPI).Assembly, (name, _, _) =>
            name.Equals("steam_api64", StringComparison.OrdinalIgnoreCase) || name.Equals("steam_api64.dll", StringComparison.OrdinalIgnoreCase)
                ? NativeLibrary.Load(library) : IntPtr.Zero);
        Environment.SetEnvironmentVariable("SteamAppId", "1557740");
        Environment.SetEnvironmentVariable("SteamGameId", "1557740");
        try
        {
            initialized = SteamAPI.Init();
            Assert.True(initialized, "SteamAPI.Init failed. Start Steam, sign in normally, and run this native test outside low-integrity sandboxing; no authentication is bypassed and no test is skipped.");
            Assert.Equal(1557740u, SteamUtils.GetAppID().m_AppId);
            Assert.True(SteamUser.BLoggedOn(), "A legitimate logged-in Steam session is required.");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void WaitUntil(Func<bool> predicate, string expectation, Action? pump = null, int timeoutMilliseconds = 10000)
    {
        var timer = Stopwatch.StartNew();
        while (!predicate())
        {
            SteamAPI.RunCallbacks();
            pump?.Invoke();
            Assert.True(timer.ElapsedMilliseconds < timeoutMilliseconds, "Timed out waiting for " + expectation);
            Thread.Sleep(1); // Cadence for actual native callback/network service, not a readiness delay.
        }
    }

    public void Dispose()
    {
        if (initialized)
        {
            SteamAPI.Shutdown();
            initialized = false;
        }
        Environment.SetEnvironmentVariable("SteamAppId", savedAppId);
        Environment.SetEnvironmentVariable("SteamGameId", savedGameId);
    }
}

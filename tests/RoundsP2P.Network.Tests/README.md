# Photon / Luxon compatibility tests

These tests use the PhotonRealtime and Photon3Unity3D assemblies from the configured, installed ROUNDS build. They start a hash-checked Luxon v1.5 process, connect real UDP clients to its master server, and follow its game-server handoff. There are no mock network clients and no cloud connection.

Prerequisites: Windows, the game assemblies in `GameDir` / `ROUNDS_GAME_DIR`, and the pinned server at `.cache/Luxon/v1.5/luxon_server.exe`. Missing prerequisites fail the tests rather than skipping them.

Run from the repository root:

```powershell
.\scripts\dotnet.ps1 test tests\RoundsP2P.Network.Tests\RoundsP2P.Network.Tests.csproj
```

Network fixtures start their own server on two dynamically selected UDP ports and wait for both actual IPv4 UDP listeners before connecting. Configurations and actual server/client logs remain under `artifacts/network-tests/`. Teardown disconnects the clients and terminates only the process created by that test.

The cases cover two-player room membership and properties, reliable payloads in both directions (including Unity Vector2 serialization), cached events for late joins, rejection of a third player, disconnect propagation, and production server launch/cleanup even when a logging observer throws. The cleanup case checks readiness before and after disposal, leaves an independent server running, and proves it remains joinable. Occupied-master and occupied-game cases assert that launch fails before creating configuration, releases its preflight socket, and leaves the external UDP listener usable.

Current ROUNDS serializes `RoomOptions.MaxPlayers` as an integer, while Luxon v1.5 expects a byte. The tests run the production `PhotonWireCompatibility` adapter at the outgoing-operation boundary using a real Photon peer subclass. The Unity plugin installs its adapter through a scoped patch instead; that patch is covered by full-game smoke validation, not by this .NET test fixture. Without the adapter, the real-server capacity test detects that a third actor can join, even with `MaxGamePeers: 2` in the server configuration.

Passing these tests demonstrates compatibility of those Photon operations with the pinned server. It does not demonstrate a complete ROUNDS match, Steam invitations, WAN reachability, a direct Steam route, or lower latency. Full-game smoke testing remains separate.

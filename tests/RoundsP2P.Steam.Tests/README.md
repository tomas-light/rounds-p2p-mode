# Native Steam transport tests

These tests use the installed ROUNDS Steamworks SDK and the production `SteamDatagramTransport` source. They require Steam to be running, a normal signed-in session owning ROUNDS, and normal-integrity execution. They fail explicitly when Steam initialization fails; nothing is silently skipped and authentication is not patched.

Run after preparing the pinned Luxon binary:

```powershell
./scripts/dotnet.ps1 test tests/RoundsP2P.Steam.Tests/RoundsP2P.Steam.Tests.csproj -c Release
```

The fixture resolves `steam_api64.dll` from the configured `GameDir`, scopes `SteamAppId`/`SteamGameId` to the test process, initializes one SDK owner, pumps its callbacks and shuts it down after the collection. It does not share the game's callback pump or modify the installed native library.

Coverage:

- A real packetized/encrypted native socket pair preserves message boundaries and bytes.
- Production UDP adapters carry requests and replies over both master and game routes.
- Session tokens must agree before the adapter becomes ready.
- Repeated disposal releases the local proxy ports.
- Actual Photon clients connect to Luxon, switch from master to game server through the production Steam adapter, exchange a fragmented reliable event in both directions and receive room property updates.
- A separately tagged `NativeSteamP2P` diagnostic creates a real P2P listener on virtual port 94, connects to the same logged-in Steam identity, accepts that connection, and checks both native handles reach Connected within 30 seconds. It retains authentication/relay status in `artifacts/steam-tests/native-p2p-self-*/probe.log`.
- A separately tagged `NativeSteamRelay` diagnostic calls `InitAuthentication` and `InitRelayNetworkAccess`, keeps the SDK alive for up to 30 seconds and requires both authentication and relay availability to reach Current. It records status transitions under `artifacts/steam-tests/native-relay-*/relay-and-auth.log`; this is an explicit live-service check rather than a local unit test.

`CreateSocketPair(true)` provides real local SDK transport with packetization. It does **not** prove Steam invitation delivery, distinct-account admission, internet NAT traversal, SDR availability for the app or WAN quality. Those require the full game and two accounts/computers. The suite sends no invitations or messages to other Steam users and does not change account statistics or achievements.

The self-P2P diagnostic uses Steam signaling APIs instead of `CreateSocketPair`, but Steam can resolve self connections locally before certificate or relay initialization completes. Passing it therefore does not establish certificate issuance or SDR availability either. To select only the eight local adapter tests, add `--filter 'Category=NativeSteam'`; to select the self-P2P diagnostic, use `--filter 'Category=NativeSteamP2P'`.

To select certificate and relay availability only, use `--filter 'Category=NativeSteamRelay'`. A successful live-service diagnostic establishes readiness for this account, app and network at the time of execution; it does not establish a match between two accounts/computers or future service availability.

Photon/Luxon integration logs are retained under `artifacts/steam-tests/`.

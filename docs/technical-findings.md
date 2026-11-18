# Технические наблюдения

Эти наблюдения описывают исходную проверку локальных файлов и выбор архитектуры. Последующие игровые прогоны и ограничения реализации описаны в [отчёте прототипа](prototype.md).

## Локальная сборка

Проверено 17 ноября 2026 года на установленной Windows-версии из `C:\Program Files (x86)\Steam\steamapps\common\ROUNDS`:

- Steam app ID: `1557740`.
- В локальном `appmanifest_1557740.acf` указаны `buildid = 21020021`, `TargetBuildID = 21020021` и ветка `public`. Это данные локального клиента; они не доказывают, что на серверах Steam сейчас нет более новой сборки.
- Последующая свежая проверка через официальный SteamCMD подтвердила build `21020021` в публичной ветке на 17 ноября 2026 года; ответ сохранён в `artifacts/verification/steam-public-build.log`.
- Версия `UnityPlayer.dll`: Unity `2022.3.34f1`.
- В `ROUNDS_Data/Managed` есть `Assembly-CSharp.dll`, Photon PUN/Realtime и `com.rlabrecque.steamworks.net.dll`.
- Метаданные и IL прочитаны через Mono.Cecil без запуска кода игровых сборок. Сетевой матч и захват трафика не выполнялись.

В `Assembly-CSharp.dll` у `NetworkConnectionHandler` есть вызовы `PhotonNetwork.CreateRoom`, `JoinRoom`, а также работа с Photon-комнатой и регионом. `Landfall.Network.ClientSteamLobby` создаёт Steam-лобби и показывает приглашение через Steam overlay; вход связан с `NetworkConnectionHandler.ForceRegionJoin`. `NetworkPhysicsObject` использует `PhotonView.RPC` и `OnPhotonSerializeView`.

В `Photon3Unity3D.dll` версии `4.1.7.0` есть `IPhotonSocket`, `PhotonPeer.SocketImplementation` и `SocketImplementationConfig`. В поставляемой Steamworks-обёртке есть `SteamNetworkingSockets.CreateListenSocketP2P`, `ConnectP2P` и методы диагностики соединения. Наличие этих API даёт точки для исследования, но не доказывает, что замена сокета обеспечит всю необходимую семантику Photon или что Steam-канал будет прямым.

Перед выпуском нужно проверить актуальную публичную Steam-сборку, зафиксировать её идентификатор и повторить проверки интеграции. Старые порты модов и успешные тесты на старой игре не заменяют эту проверку.

Дополнительные точки интеграции в прочитанной `Assembly-CSharp.dll`:

- Меню: `MainMenuHandler.PlayOnlineMultiplayer`, `ListMenu`, `ListMenuPage`, `ListMenuButton`. Приглашения: `NetworkConnectionHandler.HostPrivateAndInviteFriend` и `ClientSteamLobby.ShowInviteScreenWhenConnected`.
- `NetworkConnectionHandler.OnPlayerEnteredRoom` участвует в существующем автопереходе к игре и скрывает Steam-лобби. Согласование готовности нужно учитывать до этого перехода.
- `ClientSteamLobby.RequestStartGame` и `ReadyUp(bool)` бросают `NotImplementedException`. Их названия не означают наличия готовой реализации запуска и готовности.
- `GM_ArmsRace.DoStartGame` вызывает `WaitForSyncUp` до и после выбора карт. Эта проверка использует RPC `RPCO_RequestSyncUp` и `RPCM_ReturnSyncUp`. Ответ на RPC не доказывает полное равенство состояния клиентов; автоматически удалять эти проверки нельзя.
- Конструктор `GM_ArmsRace` задаёт двух игроков, два очка для победы в раунде и пять раундов для победы в матче. Эти настройки нужно сохранить.
- `GM_ArmsRace.GameOver(int)` сохраняет победившую команду. Для повторной игры есть `GameOverRematch`, `GetRematchYesNo` и `IDoRematch`; последний ждёт второго игрока, сбрасывает матч и запускает новую игру.
- `GM_ArmsRace.ResetMatch` обнуляет очки и раунды текущего матча. Общий счёт побед нужно хранить отдельно, на уровне лобби, и привязывать к идентичностям игроков.
- `GM_ArmsRace.DoRestart` в сетевой игре вызывает `NetworkConnectionHandler.NetworkRestart`. Возврат в сохраняемое лобби нельзя считать существующим поведением: его потребуется проверить и при необходимости реализовать отдельно.
- Во время матча используется `UIHandler.roundCounterSmall` типа `RoundCounter`. У него есть `p1Parent`, `p2Parent`, `UpdateRounds`, `UpdatePoints` и `ReDraw`. Это кандидат для размещения чисел общего счёта справа от соответствующих штатных индикаторов. Геометрию, соответствие цветов игрокам и масштабирование нужно проверить в живом UI.

## Кандидат архитектуры

Стоит сначала проверить сохранение существующей игровой синхронизации с Photon-совместимым сервером на ПК одного из игроков. Затем проверить Steam-канал к этому серверу и его фактический маршрут. Конкретный сервер, транспорт и способ встраивания пока не выбраны окончательно.

Исследованные первичные источники:

- [Photon PUN: введение](https://doc.photonengine.com/pun/current/getting-started/pun-intro/) — PUN использует выделенные серверы для коммуникации. Один Steam-сокет не заменяет работу комнат, сетевых объектов и RPC.
- [Steam Networking](https://partner.steamgames.com/doc/features/multiplayer/networking) и [Steam Datagram Relay](https://partner.steamgames.com/doc/features/multiplayer/steamdatagramrelay) — P2P API может использовать ретрансляцию. Маршрут и качество нужно измерять.
- [LuxonServer](https://github.com/niansa/LuxonServer) — кандидат Photon-совместимого сервера; совместимость с обычной установленной ROUNDS пока не проверена.
- [RoundsLanPhoton](https://thunderstore.io/c/rounds/p/tiagot/RoundsLanPhoton/) — пример использования Luxon в ROUNDS, но пакет зависит от RoundsWithFriends и UnboundLib и не доказывает работу без них.
- [BepInEx: разработка плагинов](https://docs.bepinex.dev/articles/dev_guide/plugin_tutorial/index.html) — предполагаемая основа загрузки нашего мода.

Успешное прохождение прототипа означает полный матч обычной игры через выбранную схему с сохранением правил. До этого нельзя считать сетевую архитектуру подтверждённой или обещать конкретное снижение задержки.

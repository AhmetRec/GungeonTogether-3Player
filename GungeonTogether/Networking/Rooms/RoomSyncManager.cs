using System.Collections.Generic;
using UnityEngine;
using GungeonTogether.Networking;
using GungeonTogether.Networking.Steam;
using GungeonTogether.Networking.Packets;
using GungeonTogether.Networking.Sync;
using Dungeonator;

public class RoomSyncManager : MonoBehaviour
{
    private static RoomSyncManager _instance;
    public static RoomSyncManager Instance => _instance ??= new GameObject("RoomSyncManager").AddComponent<RoomSyncManager>();

    private string _currentRoomName = "";
    private float _lastSyncTime = 0f;
    private float _lastSpawnRefreshTime = 0f;
    private bool _lastRoomSealed;
    private bool _hasRoomState;
    private string _lastLocalRoomName = "";
    private float _suppressLocalRoomEventsUntil;
    private const float SYNC_INTERVAL = 0.2f;
    private const float SPAWN_REFRESH_INTERVAL = 2f;

    private void LateUpdate()
    {
        UpdateSharedCameraBounds();
    }

    private void Awake() { DontDestroyOnLoad(gameObject); }

    private void Start()
    {
        ETGReflectionHelper.Initialise();
    }

    private void Update()
    {
        if (!NetworkManager.Instance.IsHost && !NetworkManager.Instance.IsClient) return;

        TrackLocalRoomTransition();

        // Host: sync room state
        if (NetworkManager.Instance.IsHost)
        {
            SyncHost();
        }

        // Client: handle incoming sync packets (already handled via NetworkManager)
    }

    private void SyncHost()
    {
        // Check if room changed
        var room = ETGReflectionHelper.GetCurrentRoomHandler();
        if (room == null) return;

        var roomHandler = room as RoomHandler;
        if (roomHandler == null) return;

        string roomName = roomHandler.GetRoomName() + "@" + roomHandler.area.basePosition.x + "," + roomHandler.area.basePosition.y;

        if (roomName != _currentRoomName)
        {
            _currentRoomName = roomName;

            // Spawn enemies in this room and broadcast
            SpawnAndBroadcastEnemies(roomHandler);
            BroadcastRoomState(roomHandler, true);
        }

        else if (!_hasRoomState || roomHandler.IsSealed != _lastRoomSealed)
        {
            BroadcastRoomState(roomHandler, true);
        }

        // Sync enemy states periodically
        if (Time.time - _lastSyncTime > SYNC_INTERVAL)
        {
            _lastSyncTime = Time.time;
            SyncEnemyStates();
        }
    }

    private void SpawnAndBroadcastEnemies(RoomHandler room)
    {
        var enemies = ETGReflectionHelper.GetActiveEnemies();
        foreach (var enemy in enemies)
        {
            var enemyComponent = enemy as Component;
            if (enemyComponent == null) continue;

            // Assign a network ID if not already assigned
            int id = GetOrAssignEnemyId(enemyComponent);
            Vector3 pos = enemyComponent.transform.position;
            float rot = enemyComponent.transform.eulerAngles.z;
            float health = ETGReflectionHelper.GetEnemyHealth(enemyComponent);
            AIActor actor = enemyComponent as AIActor;
            if (actor != null) EnemyProjectileSyncManager.Instance.TrackHostEnemy(id, actor);
            string prefabName = actor != null ? actor.EnemyGuid : enemyComponent.name;

            var packet = new EnemySpawnPacket
            {
                EnemyId = id,
                PrefabName = prefabName,
                Position = new Vector2(pos.x, pos.y),
                Rotation = rot,
                Health = health,
                RoomIdentifier = GetRoomIdentifier(actor != null ? actor.ParentRoom : room)
            };
            NetworkManager.Instance.Host.Broadcast(packet, reliable: true);
        }
    }

    private void SyncEnemyStates()
    {
        bool refreshSpawns = Time.time - _lastSpawnRefreshTime > SPAWN_REFRESH_INTERVAL;
        if (refreshSpawns) _lastSpawnRefreshTime = Time.time;
        var enemies = GetAllActiveEnemies();
        foreach (var enemy in enemies)
        {
            var enemyComponent = enemy as Component;
            if (enemyComponent == null) continue;

            bool isNewEnemy = !_enemyIdMap.ContainsKey(enemyComponent);
            int id = GetOrAssignEnemyId(enemyComponent);
            Vector3 pos = enemyComponent.transform.position;
            float rot = enemyComponent.transform.eulerAngles.z;
            float health = ETGReflectionHelper.GetEnemyHealth(enemyComponent);
            int aiState = ETGReflectionHelper.GetEnemyAIState(enemyComponent);

            if (isNewEnemy || refreshSpawns)
            {
                AIActor actor = enemyComponent as AIActor;
                if (actor != null) EnemyProjectileSyncManager.Instance.TrackHostEnemy(id, actor);
                NetworkManager.Instance.Host.Broadcast(new EnemySpawnPacket
                {
                    EnemyId = id,
                    PrefabName = actor != null ? actor.EnemyGuid : enemyComponent.name,
                    Position = new Vector2(pos.x, pos.y),
                    Rotation = rot,
                    Health = health,
                    RoomIdentifier = GetRoomIdentifier(actor != null ? actor.ParentRoom : null)
                }, reliable: true);
            }

            var packet = new EnemyStatePacket
            {
                EnemyId = id,
                Position = new Vector2(pos.x, pos.y),
                Rotation = rot,
                Health = health,
                AIState = aiState,
                Velocity = enemyComponent is AIActor && ((AIActor)enemyComponent).specRigidbody != null
                    ? ((AIActor)enemyComponent).specRigidbody.Velocity : Vector2.zero,
                SpriteId = enemyComponent is AIActor && ((AIActor)enemyComponent).sprite != null
                    ? ((AIActor)enemyComponent).sprite.spriteId : -1,
                FlipX = enemyComponent is AIActor && ((AIActor)enemyComponent).sprite != null &&
                    ((AIActor)enemyComponent).sprite.FlipX
            };
            NetworkManager.Instance.Host.Broadcast(packet, reliable: false);
        }

        var dead = new List<Component>();
        foreach (var pair in _enemyIdMap)
        {
            AIActor actor = pair.Key as AIActor;
            if (pair.Key == null || (actor != null && actor.healthHaver != null && actor.healthHaver.IsDead))
            {
                NetworkManager.Instance.Host.Broadcast(new EnemyDeathPacket { EnemyId = pair.Value }, reliable: true);
                dead.Add(pair.Key);
            }
        }
        foreach (Component enemy in dead) _enemyIdMap.Remove(enemy);
    }

    public void SendSnapshotTo(ulong targetId)
    {
        if (!NetworkManager.Instance.IsHost || NetworkManager.Instance.Host == null) return;
        foreach (var enemy in GetAllActiveEnemies())
        {
            Component component = enemy as Component;
            AIActor actor = component as AIActor;
            if (component == null || actor == null) continue;
            int id = GetOrAssignEnemyId(component);
            EnemyProjectileSyncManager.Instance.TrackHostEnemy(id, actor);
            NetworkManager.Instance.Host.SendPacket(targetId, new EnemySpawnPacket
            {
                EnemyId = id,
                PrefabName = actor.EnemyGuid,
                Position = component.transform.position,
                Rotation = component.transform.eulerAngles.z,
                Health = ETGReflectionHelper.GetEnemyHealth(component),
                RoomIdentifier = GetRoomIdentifier(actor.ParentRoom)
            }, reliable: true);
        }
        RoomHandler room = ETGReflectionHelper.GetCurrentRoomHandler() as RoomHandler;
        if (room != null)
        {
            NetworkManager.Instance.Host.SendPacket(targetId, CreateRoomState(room), reliable: true);
        }
    }

    private void BroadcastRoomState(RoomHandler room, bool reliable)
    {
        _lastRoomSealed = room.IsSealed;
        _hasRoomState = true;
        NetworkManager.Instance.Host.Broadcast(CreateRoomState(room), reliable: reliable);
    }

    private static RoomStatePacket CreateRoomState(RoomHandler room)
    {
        return new RoomStatePacket
        {
            RoomIdentifier = room.GetRoomName() + "@" + room.area.basePosition.x + "," + room.area.basePosition.y,
            IsSealed = room.IsSealed
        };
    }

    private void TrackLocalRoomTransition()
    {
        PlayerController local = GameManager.Instance != null ? GameManager.Instance.PrimaryPlayer : null;
        RoomHandler room = local != null ? local.CurrentRoom : null;
        if (room == null) return;
        string identifier = GetRoomIdentifier(room);
        if (identifier == _lastLocalRoomName) return;

        bool initial = string.IsNullOrEmpty(_lastLocalRoomName);
        _lastLocalRoomName = identifier;
        if (initial || Time.realtimeSinceStartup < _suppressLocalRoomEventsUntil || ETGReflectionHelper.IsLevelLoading()) return;

        var packet = new RoomChangePacket
        {
            RoomName = identifier,
            PlayerId = SteamReflectionHelper.GetLocalSteamId(),
            AnchorPosition = local.CenterPosition,
            IsRequest = NetworkManager.Instance.IsClient
        };

        if (NetworkManager.Instance.IsHost)
            BeginPartyTransition(packet);
        else if (NetworkManager.Instance.Client != null && NetworkManager.Instance.Client.IsConnected)
            NetworkManager.Instance.Client.SendPacket(0, packet, reliable: true);
    }

    public void HandleClientRoomRequest(ulong playerId, RoomChangePacket packet)
    {
        if (!NetworkManager.Instance.IsHost || packet == null) return;
        packet.PlayerId = playerId;
        packet.IsRequest = false;
        BeginPartyTransition(packet);
    }

    private void BeginPartyTransition(RoomChangePacket packet)
    {
        if (packet == null || FindRoom(packet.RoomName) == null) return;
        packet.IsRequest = false;
        ApplyPartyTransition(packet);
        NetworkManager.Instance.Host.Broadcast(packet, reliable: true);
    }

    public void ApplyPartyTransition(RoomChangePacket packet)
    {
        if (packet == null) return;
        RoomHandler targetRoom = FindRoom(packet.RoomName);
        if (targetRoom == null)
        {
            Debug.LogWarning("[RoomSync] Could not find party destination " + packet.RoomName);
            return;
        }

        _suppressLocalRoomEventsUntil = Time.realtimeSinceStartup + 1f;
        _lastLocalRoomName = packet.RoomName;
        targetRoom.SetRoomActive(true);

        GameManager gm = GameManager.Instance;
        if (gm == null) return;
        PlayerManager.Instance.RefreshGamePlayerArray();
        if (gm.AllPlayers == null) return;
        int index = 0;
        foreach (PlayerController player in gm.AllPlayers)
        {
            if (player == null) continue;
            Vector2 offset = index == 0 ? Vector2.zero : new Vector2((index % 2 == 0 ? -1f : 1f) * 0.45f, 0f);
            player.WarpToPoint(packet.AnchorPosition + offset, false, false);
            index++;
        }

        if (gm.MainCameraController != null) gm.MainCameraController.ClearPlayerCache();
        Debug.Log("[RoomSync] Moved the party together into " + packet.RoomName);
    }

    private void UpdateSharedCameraBounds()
    {
        GameManager gm = GameManager.Instance;
        if (gm == null || gm.MainCameraController == null) return;
        CameraController camera = gm.MainCameraController;
        bool active = (NetworkManager.Instance.IsHost || NetworkManager.Instance.IsClient) &&
                      !ETGReflectionHelper.IsLevelLoading() && gm.PrimaryPlayer != null;
        List<PlayerController> remotes = PlayerManager.Instance.GetRemotePlayerControllers();
        if (!active || remotes.Count == 0)
        {
            camera.UseOverridePlayerOnePosition = false;
            camera.UseOverridePlayerTwoPosition = false;
            return;
        }

        RoomHandler room = gm.PrimaryPlayer.CurrentRoom;
        Vector2 min = gm.PrimaryPlayer.CenterPosition;
        Vector2 max = min;
        int included = 1;
        foreach (PlayerController remote in remotes)
        {
            if (remote == null || room == null || remote.CurrentRoom != room) continue;
            Vector2 position = remote.CenterPosition;
            min = Vector2.Min(min, position);
            max = Vector2.Max(max, position);
            included++;
        }

        if (included < 2)
        {
            camera.UseOverridePlayerOnePosition = false;
            camera.UseOverridePlayerTwoPosition = false;
            return;
        }

        // The built-in camera only understands two players. Feeding it the
        // opposite corners of the whole party's bounds preserves its native
        // room clamping and zoom while framing all three participants.
        camera.OverridePlayerOnePosition = min;
        camera.OverridePlayerTwoPosition = max;
        camera.UseOverridePlayerOnePosition = true;
        camera.UseOverridePlayerTwoPosition = true;
    }

    public static RoomHandler FindRoom(string identifier)
    {
        GameManager gm = GameManager.Instance;
        if (gm == null || gm.Dungeon == null || gm.Dungeon.data == null || gm.Dungeon.data.rooms == null) return null;
        foreach (RoomHandler room in gm.Dungeon.data.rooms)
            if (room != null && string.Equals(GetRoomIdentifier(room), identifier, System.StringComparison.Ordinal)) return room;
        return null;
    }

    public static string GetRoomIdentifier(RoomHandler room)
    {
        return room == null || room.area == null ? "" : room.GetRoomName() + "@" + room.area.basePosition.x + "," + room.area.basePosition.y;
    }

    private static List<object> GetAllActiveEnemies()
    {
        var result = new List<object>();
        if (StaticReferenceManager.AllEnemies == null) return result;
        foreach (AIActor actor in StaticReferenceManager.AllEnemies)
            if (actor != null && actor.gameObject.activeInHierarchy && actor.healthHaver != null && !actor.healthHaver.IsDead)
                result.Add(actor);
        return result;
    }

    public void ApplyRoomState(RoomStatePacket packet)
    {
        RoomHandler room = ETGReflectionHelper.GetCurrentRoomHandler() as RoomHandler;
        if (room == null || packet == null) return;
        string current = room.GetRoomName() + "@" + room.area.basePosition.x + "," + room.area.basePosition.y;
        if (!string.Equals(current, packet.RoomIdentifier, System.StringComparison.Ordinal)) return;

        room.SetRoomActive(true);
        if (GameManager.Instance.PrimaryPlayer != null)
            room.OnBecameVisible(GameManager.Instance.PrimaryPlayer);
        if (packet.IsSealed && !room.IsSealed) room.SealRoom();
        else if (!packet.IsSealed && room.IsSealed) room.UnsealRoom();
    }

    private Dictionary<Component, int> _enemyIdMap = new Dictionary<Component, int>();

    private int GetOrAssignEnemyId(Component enemy)
    {
        if (_enemyIdMap.TryGetValue(enemy, out int id)) return id;
        id = NetworkEntityManager.Instance.AssignId(enemy);
        _enemyIdMap[enemy] = id;
        return id;
    }

    public void ResetSession()
    {
        _currentRoomName = "";
        _lastSyncTime = 0f;
        _lastSpawnRefreshTime = 0f;
        _hasRoomState = false;
        _lastLocalRoomName = "";
        _suppressLocalRoomEventsUntil = 0f;
        _enemyIdMap.Clear();
    }

    // Client side: handle packets via NetworkManager
}

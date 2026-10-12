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
    private const float SYNC_INTERVAL = 0.2f;
    private const float SPAWN_REFRESH_INTERVAL = 2f;

    private void Awake() { DontDestroyOnLoad(gameObject); }

    private void Start()
    {
        ETGReflectionHelper.Initialise();
    }

    private void Update()
    {
        if (!NetworkManager.Instance.IsHost && !NetworkManager.Instance.IsClient) return;

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
            _enemyIdMap.Clear();
            EnemyProjectileSyncManager.Instance.Clear();
            NetworkEntityManager.Instance.Clear();
            // Broadcast RoomChangePacket
            NetworkManager.Instance.Host.Broadcast(new RoomChangePacket { RoomName = roomName });

            // Spawn enemies in this room and broadcast
            SpawnAndBroadcastEnemies(roomHandler);
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
                Health = health
            };
            NetworkManager.Instance.Host.Broadcast(packet, reliable: true);
        }
    }

    private void SyncEnemyStates()
    {
        bool refreshSpawns = Time.time - _lastSpawnRefreshTime > SPAWN_REFRESH_INTERVAL;
        if (refreshSpawns) _lastSpawnRefreshTime = Time.time;
        var enemies = ETGReflectionHelper.GetActiveEnemies();
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
                    Health = health
                }, reliable: true);
            }

            var packet = new EnemyStatePacket
            {
                EnemyId = id,
                Position = new Vector2(pos.x, pos.y),
                Rotation = rot,
                Health = health,
                AIState = aiState
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
        foreach (var enemy in ETGReflectionHelper.GetActiveEnemies())
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
                Health = ETGReflectionHelper.GetEnemyHealth(component)
            }, reliable: true);
        }
    }

    private Dictionary<Component, int> _enemyIdMap = new Dictionary<Component, int>();

    private int GetOrAssignEnemyId(Component enemy)
    {
        if (_enemyIdMap.TryGetValue(enemy, out int id)) return id;
        id = NetworkEntityManager.Instance.AssignId(enemy);
        _enemyIdMap[enemy] = id;
        return id;
    }

    // Client side: handle packets via NetworkManager
}

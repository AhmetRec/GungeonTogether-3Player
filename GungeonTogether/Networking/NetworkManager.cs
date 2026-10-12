using System;
using System.Collections.Generic;
using UnityEngine;
using GungeonTogether.Core;
using GungeonTogether.Systems.Logging;
using GungeonTogether.Networking.Interfaces;
using GungeonTogether.Networking.Enums;
using GungeonTogether.Networking.Serialization;
using GungeonTogether.Networking.Steam;
using GungeonTogether.Networking.Packets;
using Debug = GungeonTogether.Systems.Logging.Debug;
using static ETGMod;
using GungeonTogether.Networking.Sync;
using Dungeonator;

namespace GungeonTogether.Networking
{
    public class NetworkManager
    {
        private static NetworkManager _instance;
        public static NetworkManager Instance => _instance ?? (_instance = new NetworkManager());

        public bool IsHost { get; private set; }
        public bool IsClient { get; private set; }
        public bool IsConnected => (CurrentRole != null);

        public INetworkRole CurrentRole { get; private set; }
        public HostController Host { get; private set; }
        public ClientController Client { get; private set; }

        private SteamP2PManager _p2p;
        private SteamLobbyManager _lobby;

        // Protocol v7 replaces visual-only avatars with real network-controlled
        // PlayerControllers and adds the character prefab identity to movement.
        public const int ProtocolVersion = 7;
        public const int MaxPlayers = 3;
        public const int MaxClients = MaxPlayers - 1;

        public void Initialise()
        {
            try
            {
                Debug.Log("NetworkManager: Initializing P2P Manager...");
                _p2p = SteamP2PManager.Instance;
                _p2p.Initialise();
                _p2p.OnPacketReceived += HandlePacket;
                Debug.Log("NetworkManager: P2P Manager initialized.");

                Debug.Log("NetworkManager: Initializing Lobby Manager...");
                _lobby = SteamLobbyManager.Instance;
                _lobby.Initialise();
                Debug.Log("NetworkManager: Lobby Manager initialized.");
                
                Debug.Log("NetworkManager Initialised.");
                PlayerManager.Instance.gameObject.SetActive(true);
                ETGReflectionHelper.Initialise();
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"NetworkManager: Exception during initialization: {ex.GetType().Name}: {ex.Message}");
                Debug.LogError($"NetworkManager: Stack trace: {ex.StackTrace}");
                throw;
            }
        }

        public void Update()
        {
            _lobby?.Update();
            _p2p?.Update();
            CurrentRole?.Update();
        }

        public void StartHosting()
        {
            if (CurrentRole != null) Shutdown();
            WorldSyncManager.Instance.ResetSession();

            IsHost = true;
            IsClient = false;
            
            Host = new HostController();
            Host.Initialise();
            Host.StartSession();
            
            CurrentRole = Host;
            Debug.Log("Started Hosting.");
        }

        public void ConnectTo(ulong hostId)
        {
            if (CurrentRole != null) Shutdown();
            WorldSyncManager.Instance.ResetSession();

            IsHost = false;
            IsClient = true;

            Client = new ClientController();
            Client.Initialise();
            Client.Connect(hostId);

            CurrentRole = Client;
            Debug.Log($"Connecting to host {hostId}...");
        }

        public void Shutdown()
        {
            CurrentRole?.Shutdown();
            EnemyProjectileSyncManager.Instance.Clear();
            GameplaySyncManager.Instance.ClearNetworkState();
            PlayerManager.Instance.ClearAll();
            WorldSyncManager.Instance.ResetSession();
            CurrentRole = null;
            Host = null;
            Client = null;
            IsHost = false;
            IsClient = false;
        }

        private void HandlePacket(ulong senderId, byte[] data)
        {
            try
            {
                INetworkPacket packet = PacketSerializer.Deserialize(data);
                if (packet == null)
                {
                    Debug.LogWarning($"[NetworkManager] Failed to deserialize packet from {senderId}");
                    return;
                }
                ProcessPacket(senderId, packet);
            }
            catch (Exception e)
            {
                Debug.LogError($"Error handling packet from {senderId}: {e}");
            }
        }

        private void ProcessPacket(ulong senderId, INetworkPacket packet)
        {
            // Route packet to appropriate controller or handle globally
            switch (packet.Type)
            {
                case PacketType.ConnectionRequest:
                    if (IsHost)
                    {
                        var req = (ConnectionRequestPacket)packet;
                        Debug.Log($"[NetworkManager] Host received ConnectionRequest from {senderId}, version={req.ProtocolVersion}");
                        Host.HandleJoinRequest(senderId, req.ClientId, req.ProtocolVersion, req.ModManifest);
                    }
                    break;
                case PacketType.ConnectionAccepted:
                    if (IsClient)
                    {
                        Client.HandleConnectionAccepted(senderId, (ConnectionAcceptedPacket)packet);
                    }
                    break;
                case PacketType.ConnectionRejected:
                    if (IsClient)
                    {
                        Client.HandleConnectionRejected(senderId, (ConnectionRejectedPacket)packet);
                    }
                    break;

                case PacketType.PlayerPosition:
                    var posPacket = (PlayerPositionPacket)packet;
                    if (IsClient && posPacket.PlayerId != SteamReflectionHelper.GetLocalSteamId())
                    {
                        PlayerManager.Instance.UpdateRemotePlayer(posPacket.PlayerId, posPacket.Position, posPacket.Velocity, posPacket.Rotation, posPacket.FlipX, posPacket.Sequence, posPacket.SpriteId, posPacket.SpriteCollectionName, posPacket.IsDodgeRolling, posPacket.CharacterPrefabKey);
                    }
                    else if (IsHost)
                    {
                        Host.HandlePlayerPosition(senderId, posPacket);
                    }
                    break;
                case PacketType.Disconnect:
                    if (IsHost)
                        Host.HandleClientDisconnect(senderId);
                    break;
                case PacketType.PlayerJoin:
                    var joinPacket = (PlayerJoinPacket)packet;
                    if (IsClient && joinPacket.PlayerId != SteamReflectionHelper.GetLocalSteamId())
                    {
                        PlayerManager.Instance.SpawnRemotePlayer(joinPacket.PlayerId, joinPacket.Position, joinPacket.Rotation);
                    }
                    break;

                case PacketType.PlayerLeave:
                    var leavePacket = (PlayerLeavePacket)packet;
                    if (IsClient)
                    {
                        PlayerManager.Instance.RemoveRemotePlayer(leavePacket.PlayerId);
                    }
                    else if (IsHost)
                    {
                        // Host should also remove from its list of connected clients
                        Host.HandleClientDisconnect(leavePacket.PlayerId);
                    }
                    break;
                case PacketType.RoomChange:
                    var roomChange = (RoomChangePacket)packet;
                    // Client: clear old remote entities and prepare for new room
                    NetworkEntityManager.Instance.Clear();
                    EnemyProjectileSyncManager.Instance.Clear();
                    // Possibly also call LoadRoom on client (but we'll rely on enemy spawns)
                    break;

                case PacketType.EnemySpawn:
                    var spawn = (EnemySpawnPacket)packet;
                    if (IsClient)
                    {
                        GameObject existingEnemy = NetworkEntityManager.Instance.GetRemote(spawn.EnemyId);
                        AIActor matched = existingEnemy != null ? existingEnemy.GetComponent<AIActor>() : null;
                        foreach (AIActor actor in UnityEngine.Object.FindObjectsOfType<AIActor>())
                        {
                            if (matched != null) break;
                            if (actor == null || actor.EnemyGuid != spawn.PrefabName || NetworkEntityManager.Instance.IsRemoteObject(actor.gameObject)) continue;
                            if (Vector2.Distance(actor.transform.position, spawn.Position) <= 4f)
                            {
                                matched = actor;
                                break;
                            }
                        }

                        if (matched == null)
                        {
                            AIActor prefab = EnemyDatabase.GetOrLoadByGuid(spawn.PrefabName);
                            RoomHandler room = ETGReflectionHelper.GetCurrentRoomHandler() as RoomHandler;
                            if (prefab != null && room != null)
                                matched = AIActor.Spawn(prefab, spawn.Position, room, true, AIActor.AwakenAnimationType.Default, true);
                        }

                        if (matched != null)
                        {
                            matched.transform.rotation = Quaternion.Euler(0f, 0f, spawn.Rotation);
                            if (matched.healthHaver != null) matched.healthHaver.ForceSetCurrentHealth(spawn.Health);
                            NetworkEntityManager.Instance.AddRemote(spawn.EnemyId, matched.gameObject);
                            EnemyProjectileSyncManager.Instance.TrackClientEnemy(spawn.EnemyId, matched);
                        }
                    }
                    break;

                case PacketType.EnemyState:
                    var state = (EnemyStatePacket)packet;
                    var remoteGO = NetworkEntityManager.Instance.GetRemote(state.EnemyId);
                    if (remoteGO != null)
                    {
                        remoteGO.transform.position = state.Position;
                        remoteGO.transform.rotation = Quaternion.Euler(0, 0, state.Rotation);
                        // Update health and AI state via reflection on the remote enemy component
                        var enemyComp = remoteGO.GetComponent<AIActor>();
                        if (enemyComp != null)
                        {
                            if (enemyComp.healthHaver != null && !enemyComp.healthHaver.IsDead)
                                enemyComp.healthHaver.ForceSetCurrentHealth(Mathf.Max(0.01f, state.Health));
                            enemyComp.State = (AIActor.ActorState)state.AIState;
                        }
                    }
                    break;

                case PacketType.EnemyDeath:
                    var death = (EnemyDeathPacket)packet;
                    NetworkEntityManager.Instance.RemoveRemote(death.EnemyId);
                    break;
                case PacketType.WorldState:
                    if (IsClient)
                    {
                        var world = (WorldStatePacket)packet;
                        Debug.Log($"[Client] Received WorldStatePacket: isFoyer={world.IsFoyer}, floor={world.FloorIndex}, pos={world.Position}");
                        WorldSyncManager.Instance.ApplyWorldState(world);
                    }
                    break;
                case PacketType.PlayerState:
                    // Protocol v3 uses PlayerVitals instead. Kept for packet-number
                    // compatibility with older source builds, but never applied.
                    break;
                case PacketType.LoadingState:
                    var load = (LoadingStatePacket)packet;
                    if (IsClient) LoadingSyncManager.Instance.ApplyLoadingState(load.IsLoading);
                    break;
                case PacketType.ProjectileFired:
                    var projectile = (ProjectileFiredPacket)packet;
                    if (IsHost)
                        Host.HandleProjectile(senderId, projectile);
                    else if (IsClient && Client.HostId == senderId)
                        GameplaySyncManager.Instance.ApplyRemoteProjectile(projectile, authoritative: false);
                    break;
                case PacketType.ItemPickup:
                    var pickup = (ItemPickupPacket)packet;
                    if (IsHost)
                        Host.HandlePickup(senderId, pickup);
                    else if (IsClient && Client.HostId == senderId)
                        GameplaySyncManager.Instance.ApplyRemotePickup(pickup);
                    break;
                case PacketType.PlayerVitals:
                    var vitals = (PlayerVitalsPacket)packet;
                    if (IsHost)
                        Host.HandleVitals(senderId, vitals);
                    else if (IsClient && Client.HostId == senderId)
                        GameplaySyncManager.Instance.RecordVitals(vitals);
                    break;
                case PacketType.PlayerRevive:
                    var revive = (PlayerRevivePacket)packet;
                    if (IsClient && Client.HostId == senderId)
                        GameplaySyncManager.Instance.ApplyRevive(revive);
                    break;
                case PacketType.EnemyProjectileSpawn:
                    if (IsClient && Client.HostId == senderId)
                        EnemyProjectileSyncManager.Instance.ApplySpawn((EnemyProjectileSpawnPacket)packet);
                    break;
                case PacketType.EnemyProjectileState:
                    if (IsClient && Client.HostId == senderId)
                        EnemyProjectileSyncManager.Instance.ApplyState((EnemyProjectileStatePacket)packet);
                    break;
                case PacketType.EnemyProjectileDestroy:
                    if (IsClient && Client.HostId == senderId)
                        EnemyProjectileSyncManager.Instance.ApplyDestroy((EnemyProjectileDestroyPacket)packet);
                    break;
                case PacketType.PlayerBeamState:
                    var beam = (PlayerBeamStatePacket)packet;
                    if (IsHost)
                        Host.HandleBeam(senderId, beam);
                    else if (IsClient && Client.HostId == senderId)
                        GameplaySyncManager.Instance.ApplyRemoteBeam(beam, authoritative: false);
                    break;
            }
        }

        public void SendPacket(ulong targetId, INetworkPacket packet, bool reliable = true)
        {
            CurrentRole?.SendPacket(targetId, packet, reliable);
        }
    }
}

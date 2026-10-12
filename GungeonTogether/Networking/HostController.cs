using System;
using System.Collections.Generic;
using UnityEngine;
using GungeonTogether.Networking.Interfaces;
using GungeonTogether.Networking.Steam;
using GungeonTogether.Networking.Packets;
using GungeonTogether.Networking.Sync;
using GungeonTogether.Systems.Logging;
using Debug = GungeonTogether.Systems.Logging.Debug;

namespace GungeonTogether.Networking
{
    public class HostController : IHost
    {
        private List<ulong> _connectedClients = new List<ulong>();
        private Dictionary<ulong, ulong> _clientPlayerIds = new Dictionary<ulong, ulong>();
        private Dictionary<ulong, PlayerPositionPacket> _lastPlayerPositions = new Dictionary<ulong, PlayerPositionPacket>();
        private readonly HashSet<ulong> _runMembers = new HashSet<ulong>();
        private readonly HashSet<ulong> _preRunMembers = new HashSet<ulong>();
        private bool _runLocked;
        private SteamP2PManager _p2p;
        private float _nextPositionSendTime;
        private const float PositionSendInterval = 0.05f;

        public void Initialise()
        {
            _p2p = SteamP2PManager.Instance;
            _p2p.OnP2PSessionFailed += HandleP2PSessionFailed;
            Debug.Log("HostController Initialised.");
        }

        public void StartSession()
        {
            // Initialise game state for hosting
            Debug.Log("Session Started.");
        }

        public void Update()
        {
            if (!_runLocked && ETGReflectionHelper.GetGameManager() != null && !Foyer.DoMainMenu &&
                !ETGReflectionHelper.IsInFoyer() && ETGReflectionHelper.GetCurrentFloorIndex() > 0)
            {
                _runLocked = true;
                foreach (ulong client in _preRunMembers)
                    _runMembers.Add(client);
                Debug.Log($"[Host] Run roster locked with {_runMembers.Count + 1} players. Only disconnected members may rejoin.");
            }

            if (_connectedClients.Count == 0) return;
            if (Time.realtimeSinceStartup < _nextPositionSendTime) return;

            _nextPositionSendTime = Time.realtimeSinceStartup + PositionSendInterval;
            ulong localId = _p2p.LocalSteamID != 0 ? _p2p.LocalSteamID : SteamReflectionHelper.GetLocalSteamId();
            var packet = PlayerManager.Instance.CreateLocalPositionPacket(localId);
            if (packet != null)
            {
                Broadcast(packet, reliable: false);
            }
        }

        public void Shutdown()
        {
            if (_p2p != null)
            {
                _p2p.OnP2PSessionFailed -= HandleP2PSessionFailed;
            }

            foreach (var client in _connectedClients)
            {
                // Send disconnect packet
            }
            _connectedClients.Clear();
            _clientPlayerIds.Clear();
            _lastPlayerPositions.Clear();
            _runMembers.Clear();
            _preRunMembers.Clear();
            _runLocked = false;
        }

        private void HandleP2PSessionFailed(ulong clientId)
        {
            HandleClientDisconnect(clientId);
        }

        private void AcceptP2PSession(ulong playerId)
        {
            try
            {
                if (SteamReflectionHelper.AcceptP2PSessionMethod != null)
                {
                    object steamIdObj = SteamReflectionHelper.CreateCSteamID(playerId);
                    SteamReflectionHelper.AcceptP2PSessionMethod.Invoke(null, new object[] { steamIdObj });
                    Debug.Log($"[Host] Accepted P2P session with {playerId}.");
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Host] Failed to accept P2P session with {playerId}: {e.Message}");
            }
        }

        public void HandleJoinRequest(ulong playerId)
        {
            HandleJoinRequest(playerId, playerId, NetworkManager.ProtocolVersion, ModCompatibility.GetManifest());
        }

        public void HandleJoinRequest(ulong playerId, int protocolVersion)
        {
            HandleJoinRequest(playerId, playerId, protocolVersion, ModCompatibility.GetManifest());
        }

        public void HandleJoinRequest(ulong transportId, ulong playerId, int protocolVersion, string modManifest)
        {
            playerId = transportId;
            Debug.Log($"[Host] HandleJoinRequest from {playerId}, version={protocolVersion}, expected={NetworkManager.ProtocolVersion}");

            if (protocolVersion != NetworkManager.ProtocolVersion)
            {
                var reject = new ConnectionRejectedPacket
                {
                    ProtocolVersion = NetworkManager.ProtocolVersion,
                    Reason = "Protocol version mismatch"
                };
                SendPacket(transportId, reject, reliable: true);
                Debug.Log($"[Host] Rejected {playerId} – protocol mismatch");
                return;
            }

            string hostManifest = ModCompatibility.GetManifest();
            if (!string.Equals(hostManifest, modManifest ?? "", StringComparison.Ordinal))
            {
                string mismatch = ModCompatibility.DescribeMismatch(modManifest);
                var reject = new ConnectionRejectedPacket
                {
                    ProtocolVersion = NetworkManager.ProtocolVersion,
                    Reason = mismatch
                };
                SendPacket(transportId, reject, reliable: true);
                Debug.Log($"[Host] Rejected {playerId} - {mismatch}");
                return;
            }

            if (_connectedClients.Contains(transportId))
            {
                // Reliable Steam packets can still be retried before the client
                // processes our acceptance. Re-acknowledge idempotently so a
                // lost first response cannot leave it retrying forever.
                SendConnectionAccepted(transportId);
                return;
            }

            if (_runLocked && !_runMembers.Contains(playerId))
            {
                var reject = new ConnectionRejectedPacket
                {
                    ProtocolVersion = NetworkManager.ProtocolVersion,
                    Reason = "Run already started; only disconnected run members may reconnect"
                };
                SendPacket(transportId, reject, reliable: true);
                Debug.Log($"[Host] Rejected {playerId} - player was not part of the locked run roster");
                return;
            }

            if (_connectedClients.Count >= NetworkManager.MaxClients)
            {
                var reject = new ConnectionRejectedPacket
                {
                    ProtocolVersion = NetworkManager.ProtocolVersion,
                    Reason = "Lobby is full (maximum 3 players)"
                };
                SendPacket(transportId, reject, reliable: true);
                Debug.Log($"[Host] Rejected {playerId} - lobby is full");
                return;
            }

            _connectedClients.Add(transportId);
            _clientPlayerIds[transportId] = transportId;
            if (!_runLocked) _preRunMembers.Add(transportId);
            Debug.Log($"[Host] Player {playerId} added to connected clients (total {_connectedClients.Count})");

            // Accept the P2P session (already accepted via callback, but ensure it)
            AcceptP2PSession(transportId);

            SendConnectionAccepted(transportId);

            // Give the newcomer an immediate roster: the host and every client
            // whose latest transform is already known.
            SendHostJoinPacket(transportId);
            SendExistingPlayersTo(transportId);

            // Now broadcast current world state to the new client
            SendCurrentWorldState(transportId);
            RoomSyncManager.Instance.SendSnapshotTo(transportId);
            EnemyProjectileSyncManager.Instance.SendSnapshotTo(transportId);
        }

        private void SendConnectionAccepted(ulong transportId)
        {
            SendPacket(transportId, new ConnectionAcceptedPacket
            {
                HostId = SteamReflectionHelper.GetLocalSteamId(),
                ProtocolVersion = NetworkManager.ProtocolVersion
            }, reliable: true);
        }

        private void SendHostJoinPacket(ulong targetId)
        {
            ulong hostId = SteamReflectionHelper.GetLocalSteamId();
            Vector3 position = ETGReflectionHelper.GetPlayerPosition();
            SendPacket(targetId, new PlayerJoinPacket
            {
                PlayerId = hostId,
                Position = new Vector2(position.x, position.y),
                Rotation = ETGReflectionHelper.GetPlayerRotation()
            }, reliable: true);
        }

        private void SendExistingPlayersTo(ulong targetId)
        {
            foreach (var pair in _lastPlayerPositions)
            {
                if (pair.Key == targetId) continue;
                PlayerPositionPacket position = pair.Value;
                SendPacket(targetId, new PlayerJoinPacket
                {
                    PlayerId = position.PlayerId,
                    Position = position.Position,
                    Rotation = position.Rotation
                }, reliable: true);
            }
        }

        private void SendCurrentWorldState(ulong targetId)
        {
            var gm = ETGReflectionHelper.GetGameManager();
            if (gm == null) return;

            bool isFoyer = ETGReflectionHelper.IsInFoyer();
            int floorIndex = ETGReflectionHelper.GetCurrentFloorIndex();
            string roomId = ETGReflectionHelper.GetCurrentRoomIdentifier();
            Vector3 pos = ETGReflectionHelper.GetPlayerPosition();
            float rot = ETGReflectionHelper.GetPlayerRotation();

            var packet = new WorldStatePacket
            {
                IsFoyer = isFoyer,
                FloorIndex = floorIndex,
                RunSeed = ETGReflectionHelper.GetCurrentRunSeed(),
                LevelName = ETGReflectionHelper.GetCurrentLevelName(),
                RoomIdentifier = roomId,
                Position = new Vector2(pos.x, pos.y),
                Rotation = rot
            };
            SendPacket(targetId, packet, reliable: true);
            Debug.Log($"[Host] Sent initial world state to {targetId}: isFoyer={isFoyer}, floor={floorIndex}");
        }

        public void HandlePlayerPosition(ulong senderId, PlayerPositionPacket packet)
        {
            ulong registeredPlayerId;
            if (!_connectedClients.Contains(senderId) ||
                !_clientPlayerIds.TryGetValue(senderId, out registeredPlayerId) ||
                registeredPlayerId == 0)
            {
                // Don't auto-join on a bare position packet - that bypasses the
                // ConnectionRequest/ConnectionAccepted handshake (and its protocol
                // version check). Drop it and wait for a real handshake instead.
                Debug.LogWarning($"[Host] Ignored position packet from unrecognised sender {senderId} (no active connection).");
                return;
            }

            // Never trust a client-supplied identity. The handshake owns the
            // transport-to-player mapping.
            ulong playerId = registeredPlayerId;
            packet.PlayerId = playerId;

            bool isFirstPosition = !_lastPlayerPositions.ContainsKey(senderId);
            _lastPlayerPositions[senderId] = packet;

            PlayerManager.Instance.UpdateRemotePlayer(playerId, packet.Position, packet.Velocity, packet.Rotation, packet.FlipX, packet.Sequence, packet.SpriteId, packet.SpriteCollectionName, packet.IsDodgeRolling, packet.CharacterPrefabKey);

            if (isFirstPosition)
            {
                Broadcast(new PlayerJoinPacket
                {
                    PlayerId = playerId,
                    Position = packet.Position,
                    Rotation = packet.Rotation
                }, senderId, reliable: true);
            }

            // Relay this player's position to every other connected client.
            Broadcast(packet, senderId, reliable: false);
        }

        private bool TryGetAuthenticatedPlayer(ulong senderId, out ulong playerId)
        {
            playerId = 0;
            return _connectedClients.Contains(senderId) &&
                   _clientPlayerIds.TryGetValue(senderId, out playerId) &&
                   playerId != 0;
        }

        public void HandleProjectile(ulong senderId, ProjectileFiredPacket packet)
        {
            ulong playerId;
            if (!TryGetAuthenticatedPlayer(senderId, out playerId)) return;
            packet.PlayerId = playerId;
            GameplaySyncManager.Instance.ApplyRemoteProjectile(packet, authoritative: true);
            Broadcast(packet, senderId, reliable: false);
        }

        public void HandleLocalProjectile(ProjectileFiredPacket packet)
        {
            packet.PlayerId = SteamReflectionHelper.GetLocalSteamId();
            Broadcast(packet, reliable: false);
        }

        public void HandleBeam(ulong senderId, PlayerBeamStatePacket packet)
        {
            ulong playerId;
            if (!TryGetAuthenticatedPlayer(senderId, out playerId)) return;
            packet.PlayerId = playerId;
            GameplaySyncManager.Instance.ApplyRemoteBeam(packet, authoritative: true);
            Broadcast(packet, senderId, reliable: false);
        }

        public void HandleLocalBeam(PlayerBeamStatePacket packet)
        {
            packet.PlayerId = SteamReflectionHelper.GetLocalSteamId();
            Broadcast(packet, reliable: false);
        }

        public void HandlePickup(ulong senderId, ItemPickupPacket packet)
        {
            ulong playerId;
            if (!TryGetAuthenticatedPlayer(senderId, out playerId)) return;
            packet.PlayerId = playerId;
            GameplaySyncManager.Instance.ApplyRemotePickup(packet);
            Broadcast(packet, senderId, reliable: true);
        }

        public void HandleLocalPickup(ItemPickupPacket packet)
        {
            packet.PlayerId = SteamReflectionHelper.GetLocalSteamId();
            Broadcast(packet, reliable: true);
        }

        public void HandleVitals(ulong senderId, PlayerVitalsPacket packet)
        {
            ulong playerId;
            if (!TryGetAuthenticatedPlayer(senderId, out playerId)) return;
            packet.PlayerId = playerId;
            GameplaySyncManager.Instance.RecordVitals(packet);
            Broadcast(packet, senderId, reliable: false);
        }

        public void HandleLocalVitals(PlayerVitalsPacket packet)
        {
            packet.PlayerId = SteamReflectionHelper.GetLocalSteamId();
            GameplaySyncManager.Instance.RecordVitals(packet);
            Broadcast(packet, reliable: false);
        }

        public Dictionary<ulong, Vector2> GetKnownPlayerPositions()
        {
            var result = new Dictionary<ulong, Vector2>();
            ulong hostId = SteamReflectionHelper.GetLocalSteamId();
            Vector3 hostPosition = ETGReflectionHelper.GetPlayerPosition();
            result[hostId] = new Vector2(hostPosition.x, hostPosition.y);

            foreach (var pair in _lastPlayerPositions)
            {
                ulong playerId;
                if (_clientPlayerIds.TryGetValue(pair.Key, out playerId) && playerId != 0)
                    result[playerId] = pair.Value.Position;
            }
            return result;
        }

        public void SendPacket(ulong targetId, INetworkPacket packet, bool reliable = true)
        {
            byte[] data = Serialization.PacketSerializer.Serialize(packet);
            _p2p.SendPacket(targetId, data, reliable);
        }

        public void Broadcast(INetworkPacket packet, ulong excludeId = 0, bool reliable = true)
        {
            byte[] data = Serialization.PacketSerializer.Serialize(packet);
            int count = 0;
            foreach (var client in _connectedClients)
            {
                if (client != excludeId)
                {
                    _p2p.SendPacket(client, data, reliable);
                    count++;
                }
            }
        }
        public void HandleClientDisconnect(ulong clientId)
        {
            if (_connectedClients.Remove(clientId))
            {
                ulong playerId;
                if (!_clientPlayerIds.TryGetValue(clientId, out playerId) || playerId == 0)
                    playerId = clientId;

                _clientPlayerIds.Remove(clientId);
                _lastPlayerPositions.Remove(clientId);
                PlayerManager.Instance.RemoveRemotePlayer(playerId);
                GameplaySyncManager.Instance.RemovePlayer(playerId);
                Broadcast(new PlayerLeavePacket { PlayerId = playerId }, reliable: true);
                Debug.Log($"Client {clientId} disconnected.");
            }
        }
    }

}

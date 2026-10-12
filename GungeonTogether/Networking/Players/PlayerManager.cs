using System.Collections.Generic;
using UnityEngine;
using GungeonTogether.Networking.Steam;
using GungeonTogether.Networking.Packets;

namespace GungeonTogether.Networking
{
    public class PlayerManager : MonoBehaviour
    {
        private static PlayerManager _instance;
        public static PlayerManager Instance
        {
            get
            {
                if (_instance == null)
                {
                    GameObject go = new GameObject("PlayerManager");
                    _instance = go.AddComponent<PlayerManager>();
                    DontDestroyOnLoad(go);
                }
                return _instance;
            }
        }

        private Dictionary<ulong, RemotePlayerAvatar> _remotePlayers = new Dictionary<ulong, RemotePlayerAvatar>();
        private Vector2 _lastLocalPosition;
        private float _lastLocalSampleTime;
        private bool _hasLocalSample;
        private uint _localSequence;

        public PlayerPositionPacket CreateLocalPositionPacket(ulong playerId)
        {
            GameManager gameManager = Object.FindObjectOfType<GameManager>();
            PlayerController player = gameManager != null ? gameManager.PrimaryPlayer : null;
            if (player == null) return null;

            Vector3 pos3 = player.transform.position;
            Vector2 position = new Vector2(pos3.x, pos3.y);
            float now = Time.realtimeSinceStartup;
            Vector2 velocity = Vector2.zero;
            if (_hasLocalSample)
            {
                float elapsed = now - _lastLocalSampleTime;
                if (elapsed > 0.001f && elapsed < 1f)
                    velocity = (position - _lastLocalPosition) / elapsed;
            }
            _lastLocalPosition = position;
            _lastLocalSampleTime = now;
            _hasLocalSample = true;

            var packet = new PlayerPositionPacket
            {
                PlayerId = playerId,
                Sequence = ++_localSequence,
                SenderTime = now,
                Position = position,
                Velocity = velocity,
                Rotation = player.transform.eulerAngles.z,
                IsGrounded = true,
                IsDodgeRolling = player.IsDodgeRolling,
                AnimationState = player.spriteAnimator != null ? player.spriteAnimator.CurrentFrame : 0,
                SpriteId = player.sprite != null ? player.sprite.spriteId : -1,
                SpriteCollectionName = player.sprite != null && player.sprite.Collection != null ? player.sprite.Collection.name : "",
                FlipX = player.sprite != null && player.sprite.FlipX
            };

            return packet;
        }

        public void SpawnRemotePlayer(ulong steamId, Vector2 position, float rotation)
        {
            RemotePlayerAvatar existing;
            if (_remotePlayers.TryGetValue(steamId, out existing))
            {
                if (existing != null) return;
                _remotePlayers.Remove(steamId);
            }

            RemotePlayerAvatar player = RemotePlayerAvatar.Create(steamId, position, rotation);
            _remotePlayers[steamId] = player;
            Debug.Log($"[PlayerManager] Spawned remote player {steamId} at {position}");
        }

        public void UpdateRemotePlayer(ulong steamId, Vector2 position, Vector2 velocity, float rotation, bool flipX, uint sequence, int spriteId, string spriteCollectionName, bool isDodgeRolling)
        {
            RemotePlayerAvatar player;
            if (!_remotePlayers.TryGetValue(steamId, out player) || player == null)
            {
                _remotePlayers.Remove(steamId);
                SpawnRemotePlayer(steamId, position, rotation);
                _remotePlayers.TryGetValue(steamId, out player);
            }

            if (player != null)
                player.Apply(position, velocity, rotation, flipX, sequence, spriteId, spriteCollectionName, isDodgeRolling);
        }

        public void RemoveRemotePlayer(ulong steamId)
        {
            RemotePlayerAvatar player;
            if (_remotePlayers.TryGetValue(steamId, out player))
            {
                Destroy(player.gameObject);
                _remotePlayers.Remove(steamId);
                Debug.Log($"[PlayerManager] Removed remote player {steamId}");
            }
        }

        public void SetRemotePlayerDowned(ulong steamId, bool downed)
        {
            RemotePlayerAvatar player;
            if (_remotePlayers.TryGetValue(steamId, out player))
                player.SetDowned(downed);
        }

        public void ClearAll()
        {
            foreach (var kvp in _remotePlayers)
                if (kvp.Value != null) Destroy(kvp.Value.gameObject);
            _remotePlayers.Clear();
            _hasLocalSample = false;
            _localSequence = 0;
        }
    }
}

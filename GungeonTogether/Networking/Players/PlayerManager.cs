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
        private Dictionary<ulong, int> _remoteSlots = new Dictionary<ulong, int>();
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
                FlipX = player.sprite != null && player.sprite.FlipX,
                CharacterPrefabKey = player.gameObject != null ? player.gameObject.name.Replace("(Clone)", "").Trim() : ""
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

            int slot = GetOrAllocateSlot(steamId);
            RemotePlayerAvatar player = RemotePlayerAvatar.Create(steamId, slot, position, rotation);
            _remotePlayers[steamId] = player;
            Debug.Log($"[PlayerManager] Spawned real remote player {steamId} in slot {slot} at {position}");
        }

        public void UpdateRemotePlayer(ulong steamId, Vector2 position, Vector2 velocity, float rotation, bool flipX, uint sequence, int spriteId, string spriteCollectionName, bool isDodgeRolling, string characterPrefabKey)
        {
            RemotePlayerAvatar player;
            if (!_remotePlayers.TryGetValue(steamId, out player) || player == null)
            {
                _remotePlayers.Remove(steamId);
                SpawnRemotePlayer(steamId, position, rotation);
                _remotePlayers.TryGetValue(steamId, out player);
            }

            if (player != null)
                player.Apply(position, velocity, rotation, flipX, sequence, spriteId, spriteCollectionName, isDodgeRolling, characterPrefabKey);
        }

        public PlayerController GetRemotePlayerController(ulong steamId)
        {
            RemotePlayerAvatar avatar;
            return _remotePlayers.TryGetValue(steamId, out avatar) && avatar != null ? avatar.PlayerController : null;
        }

        public bool IsRemotePlayer(PlayerController player)
        {
            if (player == null) return false;
            foreach (var pair in _remotePlayers)
                if (pair.Value != null && pair.Value.PlayerController == player) return true;
            return false;
        }

        public void RemoveRemotePlayer(ulong steamId)
        {
            RemotePlayerAvatar player;
            if (_remotePlayers.TryGetValue(steamId, out player))
            {
                if (player != null) player.Detach();
                Destroy(player.gameObject);
                _remotePlayers.Remove(steamId);
                _remoteSlots.Remove(steamId);
                Debug.Log($"[PlayerManager] Removed remote player {steamId}");
                RebalanceSlots();
            }
        }

        public void SetRemotePlayerDowned(ulong steamId, bool downed, float health = 0.5f, float armor = 0f)
        {
            RemotePlayerAvatar player;
            if (_remotePlayers.TryGetValue(steamId, out player))
                player.SetDowned(downed, health, armor);
        }

        public void ClearAll()
        {
            foreach (var kvp in _remotePlayers)
                if (kvp.Value != null)
                {
                    kvp.Value.Detach();
                    Destroy(kvp.Value.gameObject);
                }
            _remotePlayers.Clear();
            _remoteSlots.Clear();
            _hasLocalSample = false;
            _localSequence = 0;
            RestoreSinglePlayerMode();
        }

        private int GetOrAllocateSlot(ulong steamId)
        {
            int existing;
            if (_remoteSlots.TryGetValue(steamId, out existing)) return existing;
            int slot = _remoteSlots.ContainsValue(1) ? 2 : 1;
            _remoteSlots[steamId] = slot;
            return slot;
        }

        private void RebalanceSlots()
        {
            if (_remotePlayers.Count == 0)
            {
                RestoreSinglePlayerMode();
                return;
            }

            if (!_remoteSlots.ContainsValue(1))
            {
                foreach (var pair in _remotePlayers)
                {
                    _remoteSlots[pair.Key] = 1;
                    if (pair.Value != null) pair.Value.SetPlayerIndex(1);
                    break;
                }
            }

            GameManager gm = GameManager.Instance;
            if (gm != null) gm.RefreshAllPlayers();
        }

        private static void RestoreSinglePlayerMode()
        {
            GameManager gm = GameManager.Instance;
            if (gm == null) return;
            gm.SecondaryPlayer = null;
            gm.CurrentGameType = GameManager.GameType.SINGLE_PLAYER;
            gm.RefreshAllPlayers();
            if (gm.PrimaryPlayer != null) gm.PrimaryPlayer.ReinitializeMovementRestrictors();
            if (gm.MainCameraController != null) gm.MainCameraController.ClearPlayerCache();
        }
    }
}

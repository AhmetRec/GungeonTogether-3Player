using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using GungeonTogether.Networking.Sync;
using Debug = GungeonTogether.Systems.Logging.Debug;

namespace GungeonTogether.Networking
{
    /// <summary>
    /// A network-controlled real Gungeon player. Older builds drew a loose
    /// sprite, which meant the camera, doors, enemies, collisions and items did
    /// not know the remote participant existed.
    /// </summary>
    public class RemotePlayerAvatar : MonoBehaviour
    {
        private const string InputOverride = "gungeon-together-remote";
        private static readonly MethodInfo IgnoredByCameraSetter = AccessTools.PropertySetter(typeof(PlayerController), "IgnoredByCamera");

        private ulong _steamId;
        private int _playerIndex;
        private PlayerController _player;
        private Vector3 _targetPosition;
        private Vector2 _networkVelocity;
        private float _lastSnapshotTime;
        private uint _lastSequence;
        private bool _hasTarget;
        private bool _isDowned;
        private bool _isDodgeRolling;
        private bool _flipX;
        private int _spriteId = -1;
        private string _spriteCollectionName = "";
        private string _characterPrefabKey = "";
        private float _nextSpawnAttempt;
        private float _nextCollectionRetry;
        private tk2dSpriteCollectionData _resolvedCollection;
        private int _gunId = -1;
        private string _gunKey = "";
        private float _nextEquipmentRefresh;
        private bool _suspendedForWorldLoad;
        private Vector2 _aimDirection = Vector2.right;

        public ulong SteamId { get { return _steamId; } }
        public int PlayerIndex { get { return _playerIndex; } }
        public PlayerController PlayerController { get { return _player; } }

        public void SetAimDirection(Vector2 direction)
        {
            if (direction.sqrMagnitude > 0.001f) _aimDirection = direction.normalized;
        }

        public static RemotePlayerAvatar Create(ulong steamId, int playerIndex, Vector2 position, float rotation)
        {
            GameObject go = new GameObject("RemotePlayerDriver_" + steamId);
            DontDestroyOnLoad(go);
            RemotePlayerAvatar avatar = go.AddComponent<RemotePlayerAvatar>();
            avatar._steamId = steamId;
            avatar._playerIndex = playerIndex;
            avatar._targetPosition = new Vector3(position.x, position.y, 0f);
            avatar._hasTarget = true;
            return avatar;
        }

        public void SetPlayerIndex(int playerIndex)
        {
            _playerIndex = playerIndex;
            if (_player != null)
            {
                _player.PlayerIDX = ToNativePlayerIndex(playerIndex);
                RegisterWithGame();
            }
        }

        public void Apply(Vector2 position, Vector2 velocity, float rotation, bool flipX, uint sequence,
            int spriteId, string spriteCollectionName, bool isDodgeRolling, string characterPrefabKey,
            int gunId, string gunKey)
        {
            if (_lastSequence != 0 && sequence != 0 && sequence <= _lastSequence) return;
            _lastSequence = sequence;
            _targetPosition = new Vector3(position.x, position.y, 0f);
            _networkVelocity = velocity;
            _lastSnapshotTime = Time.realtimeSinceStartup;
            _hasTarget = true;
            _flipX = flipX;
            _spriteId = spriteId;
            _spriteCollectionName = spriteCollectionName ?? "";
            _isDodgeRolling = isDodgeRolling;
            _gunId = gunId;
            _gunKey = gunKey ?? "";

            if (!string.IsNullOrEmpty(characterPrefabKey) && _characterPrefabKey != characterPrefabKey)
            {
                _characterPrefabKey = characterPrefabKey;
                if (_player == null) _nextSpawnAttempt = 0f;
            }
        }

        public void SetDowned(bool downed, float health, float armor)
        {
            _isDowned = downed;
            if (_player == null || _player.healthHaver == null) return;

            _player.healthHaver.ForceSetCurrentHealth(Mathf.Max(0.5f, health));
            _player.healthHaver.Armor = Mathf.Max(0f, armor);
            _player.healthHaver.IsVulnerable = false;
            if (_player.sprite != null)
                _player.sprite.color = downed ? new Color(0.55f, 0.55f, 0.55f, 0.8f) : Color.white;
        }

        public void Detach()
        {
            PlayerController doomed = _player;
            _player = null;
            if (doomed != null)
            {
                doomed.gameObject.SetActive(false);
                Destroy(doomed.gameObject);
            }
        }

        private void OnDestroy()
        {
            Detach();
        }

        private void Update()
        {
            if (_player == null)
            {
                if (Time.realtimeSinceStartup >= _nextSpawnAttempt)
                {
                    _nextSpawnAttempt = Time.realtimeSinceStartup + 0.5f;
                    TryCreatePlayer();
                }
                return;
            }

            if (NetworkManager.Instance.IsClient && !WorldSyncManager.Instance.IsClientWorldReady)
            {
                SuspendForWorldLoad();
                return;
            }

            if (_suspendedForWorldLoad)
            {
                _suspendedForWorldLoad = false;
                RegisterWithGame();
            }

            _player.PlayerIDX = ToNativePlayerIndex(_playerIndex);
            UpdateCameraParticipation();
            _player.CurrentInputState = PlayerInputState.NoInput;
            if (!_player.IsInputOverridden) _player.SetInputOverride(InputOverride);
            if (_player.healthHaver != null) _player.healthHaver.IsVulnerable = false;
        }

        private void LateUpdate()
        {
            if (_player == null || !_hasTarget) return;
            if (NetworkManager.Instance.IsClient && !WorldSyncManager.Instance.IsClientWorldReady) return;

            float age = Mathf.Clamp(Time.realtimeSinceStartup - _lastSnapshotTime, 0f, 0.10f);
            Vector3 predicted = _targetPosition + new Vector3(_networkVelocity.x, _networkVelocity.y, 0f) * age;
            float distance = Vector3.Distance(_player.transform.position, predicted);
            if (_player.specRigidbody != null)
            {
                if (distance > 4f)
                {
                    // Large gaps mean a room transition or a missed snapshot.
                    // Use the game's warp path so the rigidbody and triggers are
                    // rebuilt together instead of writing the transform alone.
                    _player.WarpToPoint(predicted, false, false);
                }
                else
                {
                    // Drive the proxy through the real physics body. Direct
                    // transform interpolation made it visibly pass through
                    // tables, walls and other local-coop obstacles.
                    Vector2 correction = (predicted - _player.transform.position).XY() * 8f;
                    Vector2 desiredVelocity = _networkVelocity + correction;
                    _player.specRigidbody.Velocity = Vector2.ClampMagnitude(desiredVelocity, 18f);
                }
                _player.specRigidbody.RecheckTriggers = true;
            }
            else
            {
                _player.transform.position = Vector3.Lerp(_player.transform.position, predicted, 1f - Mathf.Exp(-18f * Time.deltaTime));
            }

            if (_aimDirection.sqrMagnitude > 0.01f)
                _player.ForceStaticFaceDirection(_aimDirection);

            ApplyNetworkVisual();
            ApplyNetworkEquipment();
        }

        private void SetIgnoredByCamera(bool ignored)
        {
            if (_player == null || IgnoredByCameraSetter == null || _player.IgnoredByCamera == ignored) return;
            try { IgnoredByCameraSetter.Invoke(_player, new object[] { ignored }); }
            catch (Exception e) { Debug.LogWarning("[RemotePlayer] Could not update co-op camera participation: " + e.Message); }
        }

        private void UpdateCameraParticipation()
        {
            PlayerController local = ETGReflectionHelper.GetPrimaryPlayer() as PlayerController;
            bool loading = NetworkManager.Instance.IsClient && !WorldSyncManager.Instance.IsClientWorldReady;
            bool sameRoom = local != null && _player != null && local.CurrentRoom != null && local.CurrentRoom == _player.CurrentRoom;
            SetIgnoredByCamera(loading || !sameRoom);
        }

        private void SuspendForWorldLoad()
        {
            _suspendedForWorldLoad = true;
            if (_player == null) return;
            SetIgnoredByCamera(true);

            PlayerController localPlayer = ETGReflectionHelper.GetPrimaryPlayer() as PlayerController;
            Vector3 safePosition = localPlayer != null ? localPlayer.transform.position : _player.transform.position;
            if (localPlayer != null)
            {
                _targetPosition = safePosition;
                _networkVelocity = Vector2.zero;
            }
            if (_player.specRigidbody != null)
            {
                _player.specRigidbody.Velocity = Vector2.zero;
                // Only re-warp when the proxy actually drifted; warping every
                // frame of a load rebuilt its rigidbody hundreds of times.
                if (localPlayer != null && Vector2.Distance(_player.transform.position, safePosition) > 1f)
                    _player.WarpToPoint(safePosition, false, false);
            }
            else if (localPlayer != null)
            {
                _player.transform.position = safePosition;
            }

            // Keep the persistent proxy on top of the local player while the
            // new dungeon is loading. Otherwise the co-op camera tries to frame
            // coordinates from two different scenes and renders mostly black.
            if (_player.sprite != null) _player.sprite.renderer.enabled = false;
        }

        private void ApplyNetworkEquipment()
        {
            if (_player == null || _player.inventory == null || Time.realtimeSinceStartup < _nextEquipmentRefresh) return;
            _nextEquipmentRefresh = Time.realtimeSinceStartup + 0.25f;
            Gun current = _player.CurrentGun;
            if (current != null && GunMatches(current)) return;
            for (int i = 0; i < _player.inventory.AllGuns.Count; i++)
            {
                Gun gun = _player.inventory.AllGuns[i];
                if (gun != null && GunMatches(gun))
                {
                    _player.ChangeToGunSlot(i);
                    return;
                }
            }
        }

        private bool GunMatches(Gun gun)
        {
            if (gun == null) return false;
            return !string.IsNullOrEmpty(_gunKey)
                ? string.Equals(ModCompatibility.GetPickupKey(gun), _gunKey, StringComparison.Ordinal)
                : gun.PickupObjectId == _gunId;
        }

        private bool TryCreatePlayer()
        {
            // PlayerJoin is deliberately sent before WorldState. On a client,
            // spawning the host's real PlayerController before the matching
            // dungeon is loaded makes the co-op camera frame two unrelated map
            // coordinates and produces the black, extremely zoomed-out view.
            if (NetworkManager.Instance.IsClient && !WorldSyncManager.Instance.IsClientWorldReady)
                return false;

            GameManager gm = GameManager.Instance;
            if (gm == null || gm.PrimaryPlayer == null) return false;

            GameObject prefab = ResolvePlayerPrefab(_characterPrefabKey);
            if (prefab == null)
            {
                Debug.LogWarning("[RemotePlayer] Could not load a player prefab for " + _steamId);
                return false;
            }

            try
            {
                GameObject playerObject = Instantiate(prefab, _targetPosition, Quaternion.identity);
                playerObject.name = "GT_RemotePlayer_" + _steamId;
                DontDestroyOnLoad(playerObject);
                PlayerController player = playerObject.GetComponent<PlayerController>();
                if (player == null)
                {
                    Destroy(playerObject);
                    return false;
                }

                // The base game owns UI/input arrays for indices 0 and 1 only.
                // Extra network players remain distinct in our Steam-ID/slot
                // mapping but deliberately use native co-op index 1. This lets
                // AllPlayers contain three controllers without indexing beyond
                // the game's two-player HUD and input arrays.
                player.PlayerIDX = ToNativePlayerIndex(_playerIndex);
                player.ActorName = "Network Player " + _playerIndex;
                playerObject.SetActive(true);
                player.CurrentInputState = PlayerInputState.NoInput;
                player.SetInputOverride(InputOverride);
                if (player.healthHaver != null) player.healthHaver.IsVulnerable = false;
                if (player.specRigidbody != null)
                {
                    player.specRigidbody.CollideWithOthers = true;
                    player.specRigidbody.CollideWithTileMap = true;
                    player.specRigidbody.Reinitialize();
                }
                _player = player;

                RegisterWithGame();
                UpdateCameraParticipation();
                if (player.sprite != null) player.sprite.color = Color.white;
                Debug.Log("[RemotePlayer] Registered real PlayerController for " + _steamId + " in slot " + _playerIndex);
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[RemotePlayer] PlayerController creation failed for " + _steamId + ": " + e);
                return false;
            }
        }

        private void RegisterWithGame()
        {
            GameManager gm = GameManager.Instance;
            if (gm == null || _player == null) return;

            if (_player.sprite != null) _player.sprite.renderer.enabled = true;

            gm.CurrentGameType = GameManager.GameType.COOP_2_PLAYER;
            if (_playerIndex == 1 || gm.SecondaryPlayer == null)
                gm.SecondaryPlayer = _player;
            gm.RefreshAllPlayers();
            PlayerManager.Instance.RefreshGamePlayerArray();

            if (gm.PrimaryPlayer != null)
                gm.PrimaryPlayer.ReinitializeMovementRestrictors();
            if (gm.MainCameraController != null)
                gm.MainCameraController.ClearPlayerCache();
            if (PhysicsEngine.Instance != null && _player.specRigidbody != null)
                PhysicsEngine.Instance.RegisterOverlappingGhostCollisionExceptions(_player.specRigidbody);

            try
            {
                if (gm.IsFoyer && Foyer.Instance != null)
                    Foyer.Instance.ProcessPlayerEnteredFoyer(_player);
                if (GameUIRoot.Instance != null)
                    GameUIRoot.Instance.ConvertCoreUIToCoopMode();
            }
            catch (Exception e)
            {
                Debug.LogWarning("[RemotePlayer] Co-op UI/foyer registration was incomplete: " + e.Message);
            }
        }

        private void ApplyNetworkVisual()
        {
            if (_player == null || _player.sprite == null) return;
            tk2dBaseSprite sprite = _player.sprite;
            sprite.FlipX = _flipX;

            if (!string.IsNullOrEmpty(_spriteCollectionName) &&
                (_resolvedCollection == null || _resolvedCollection.name != _spriteCollectionName) &&
                Time.realtimeSinceStartup >= _nextCollectionRetry)
            {
                _nextCollectionRetry = Time.realtimeSinceStartup + 1f;
                _resolvedCollection = FindCollection(_spriteCollectionName);
            }

            if (_resolvedCollection != null && _resolvedCollection.spriteDefinitions != null &&
                _spriteId >= 0 && _spriteId < _resolvedCollection.spriteDefinitions.Length)
                sprite.SetSprite(_resolvedCollection, _spriteId);
            else if (_spriteId >= 0 && sprite.Collection != null && sprite.Collection.spriteDefinitions != null &&
                     _spriteId < sprite.Collection.spriteDefinitions.Length)
                sprite.SetSprite(_spriteId);

            sprite.color = _isDowned
                ? new Color(0.55f, 0.55f, 0.55f, 0.8f)
                : (_isDodgeRolling ? new Color(1f, 1f, 1f, 0.88f) : Color.white);
        }

        private static GameObject ResolvePlayerPrefab(string key)
        {
            string normalized = NormalizePrefabKey(key);
            GameObject prefab = null;
            if (!string.IsNullOrEmpty(normalized))
            {
                prefab = ResourceCache.Acquire(normalized) as GameObject;
                if (prefab == null) prefab = BraveResources.Load(normalized) as GameObject;
            }
            if (prefab == null) prefab = ResourceCache.Acquire("PlayerCoopCultist") as GameObject;
            if (prefab == null) prefab = BraveResources.Load("PlayerCoopCultist") as GameObject;
            return prefab;
        }

        private static string NormalizePrefabKey(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            return value.Replace("(Clone)", "").Trim();
        }

        private static int ToNativePlayerIndex(int networkSlot)
        {
            return networkSlot <= 0 ? 0 : 1;
        }

        private static tk2dSpriteCollectionData FindCollection(string collectionName)
        {
            UnityEngine.Object[] collections = Resources.FindObjectsOfTypeAll(typeof(tk2dSpriteCollectionData));
            for (int i = 0; i < collections.Length; i++)
            {
                tk2dSpriteCollectionData collection = collections[i] as tk2dSpriteCollectionData;
                if (collection != null && collection.name == collectionName) return collection;
            }
            return null;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using GungeonTogether.Networking.Packets;
using GungeonTogether.Networking.Steam;
using Debug = GungeonTogether.Systems.Logging.Debug;

namespace GungeonTogether.Networking.Sync
{
    public class GameplaySyncManager : MonoBehaviour
    {
        private static GameplaySyncManager _instance;
        public static GameplaySyncManager Instance
        {
            get
            {
                if (_instance == null)
                {
                    var go = new GameObject("GameplaySyncManager");
                    _instance = go.AddComponent<GameplaySyncManager>();
                    DontDestroyOnLoad(go);
                }
                return _instance;
            }
        }

        private const float VitalsInterval = 0.2f;
        private const float BeamInterval = 0.05f;
        private const float ReviveDistance = 2.25f;
        private const float ReviveSeconds = 3f;
        private const float GroundPickupScanInterval = 0.5f;

        private PlayerController _boundPlayer;
        private float _nextVitalsTime;
        private float _nextBeamTime;
        private int _nextBeamId = 1;
        private bool _applyingRemoteBeam;
        private bool _applyingRemoteItem;
        private bool _applyingRemoteBlank;
        private bool _applyingRemotePickup;
        private bool _isDowned;
        private readonly Dictionary<ulong, PlayerVitalsPacket> _vitals = new Dictionary<ulong, PlayerVitalsPacket>();
        private readonly Dictionary<ulong, float> _vitalsTime = new Dictionary<ulong, float>();
        private readonly Dictionary<ulong, float> _reviveProgress = new Dictionary<ulong, float>();
        private readonly Dictionary<int, LocalBeamInfo> _localBeams = new Dictionary<int, LocalBeamInfo>();
        private readonly Dictionary<string, BeamController> _remoteBeams = new Dictionary<string, BeamController>();
        private readonly List<ItemPickupPacket> _pickupHistory = new List<ItemPickupPacket>();
        private readonly HashSet<string> _pickupHistoryKeys = new HashSet<string>();
        private string _pickupHistoryWorld = "";
        private float _nextGroundPickupScan;
        private int _nextGroundPickupId = 1;
        private readonly Dictionary<PickupObject, int> _hostGroundPickupIds = new Dictionary<PickupObject, int>();
        private readonly Dictionary<int, PickupObject> _clientGroundPickups = new Dictionary<int, PickupObject>();
        private readonly Dictionary<string, float> _pendingPickupClaims = new Dictionary<string, float>();

        private class LocalBeamInfo
        {
            public int Id;
            public BeamController Beam;
            public int GunId;
            public string GunKey;
            public string ProjectileName;
        }

        private void Awake()
        {
            InstallPatches();
        }

        private void Update()
        {
            BindLocalPlayer();
            if (!IsNetworkReady()) return;

            if (Time.realtimeSinceStartup >= _nextVitalsTime)
            {
                _nextVitalsTime = Time.realtimeSinceStartup + VitalsInterval;
                SendLocalVitals();
            }

            if (Time.realtimeSinceStartup >= _nextBeamTime)
            {
                _nextBeamTime = Time.realtimeSinceStartup + BeamInterval;
                SendLocalBeamStates();
            }

            if (NetworkManager.Instance.IsHost)
            {
                UpdateRevives();
                if (Time.realtimeSinceStartup >= _nextGroundPickupScan)
                {
                    _nextGroundPickupScan = Time.realtimeSinceStartup + GroundPickupScanInterval;
                    ScanGroundPickups();
                }
            }
        }

        private bool IsNetworkReady()
        {
            if (NetworkManager.Instance.IsHost) return NetworkManager.Instance.Host != null;
            return NetworkManager.Instance.IsClient && NetworkManager.Instance.Client != null && NetworkManager.Instance.Client.IsConnected;
        }

        private void BindLocalPlayer()
        {
            var player = ETGReflectionHelper.GetPrimaryPlayer() as PlayerController;
            if (player == _boundPlayer) return;

            if (_boundPlayer != null)
            {
                _boundPlayer.PostProcessProjectile -= OnLocalProjectile;
                _boundPlayer.PostProcessBeam -= OnLocalBeam;
                _boundPlayer.OnUsedPlayerItem -= OnLocalItemUsed;
                _boundPlayer.OnUsedBlank -= OnLocalBlankUsed;
            }

            _boundPlayer = player;
            if (_boundPlayer != null)
            {
                _boundPlayer.PostProcessProjectile += OnLocalProjectile;
                _boundPlayer.PostProcessBeam += OnLocalBeam;
                _boundPlayer.OnUsedPlayerItem += OnLocalItemUsed;
                _boundPlayer.OnUsedBlank += OnLocalBlankUsed;
            }
        }

        private void OnLocalBlankUsed(PlayerController player, int remainingBlanks)
        {
            if (_applyingRemoteBlank || !IsNetworkReady() || player != _boundPlayer) return;
            var packet = new PlayerBlankUsedPacket
            {
                PlayerId = SteamReflectionHelper.GetLocalSteamId(),
                RemainingBlanks = remainingBlanks
            };
            if (NetworkManager.Instance.IsHost)
                NetworkManager.Instance.Host.HandleLocalBlankUsed(packet);
            else
                NetworkManager.Instance.Client.SendPacket(0, packet, reliable: true);
        }

        public void ApplyRemoteBlankUsed(PlayerBlankUsedPacket packet)
        {
            PlayerController player = packet.PlayerId == SteamReflectionHelper.GetLocalSteamId()
                ? _boundPlayer
                : PlayerManager.Instance.GetRemotePlayerController(packet.PlayerId);
            if (player == null) return;
            try
            {
                _applyingRemoteBlank = true;
                player.Blanks = Mathf.Max(1, packet.RemainingBlanks + 1);
                player.ForceConsumableBlank();
                player.Blanks = Mathf.Max(0, packet.RemainingBlanks);
            }
            finally { _applyingRemoteBlank = false; }
        }

        private void OnLocalItemUsed(PlayerController player, PlayerItem item)
        {
            if (_applyingRemoteItem || !IsNetworkReady() || player != _boundPlayer || item == null) return;
            var packet = new PlayerItemUsedPacket
            {
                PlayerId = SteamReflectionHelper.GetLocalSteamId(),
                PickupId = item.PickupObjectId,
                PickupKey = ModCompatibility.GetPickupKey(item)
            };
            if (NetworkManager.Instance.IsHost)
                NetworkManager.Instance.Host.HandleLocalItemUsed(packet);
            else
                NetworkManager.Instance.Client.SendPacket(0, packet, reliable: true);
        }

        public void ApplyRemoteItemUsed(PlayerItemUsedPacket packet)
        {
            PlayerController player = packet.PlayerId == SteamReflectionHelper.GetLocalSteamId()
                ? _boundPlayer
                : PlayerManager.Instance.GetRemotePlayerController(packet.PlayerId);
            if (player == null) return;
            foreach (PlayerItem item in player.activeItems)
            {
                if (item == null || !PickupMatches(item, packet.PickupKey, packet.PickupId)) continue;
                try
                {
                    _applyingRemoteItem = true;
                    float destroyTime;
                    item.Use(player, out destroyTime);
                }
                catch (Exception e) { Debug.LogWarning("[GameplaySync] Remote active item failed: " + e.Message); }
                finally { _applyingRemoteItem = false; }
                return;
            }
        }

        private void OnLocalProjectile(Projectile projectile, float effectChanceScalar)
        {
            if (!IsNetworkReady() || projectile == null || projectile.SpawnedFromOtherPlayerProjectile) return;

            int gunId = -1;
            if (projectile.PossibleSourceGun != null)
                gunId = projectile.PossibleSourceGun.PickupObjectId;
            else if (_boundPlayer != null && _boundPlayer.CurrentGun != null)
                gunId = _boundPlayer.CurrentGun.PickupObjectId;
            if (gunId < 0) return;

            ulong localId = SteamReflectionHelper.GetLocalSteamId();
            var packet = new ProjectileFiredPacket
            {
                PlayerId = localId,
                GunId = gunId,
                GunKey = ModCompatibility.GetPickupKey(projectile.PossibleSourceGun != null ? projectile.PossibleSourceGun : (_boundPlayer != null ? _boundPlayer.CurrentGun : null)),
                ProjectileName = NormalizePrefabName(projectile.name),
                Position = projectile.transform.position,
                Direction = projectile.Direction,
                Speed = projectile.Speed,
                Damage = projectile.baseData != null ? projectile.baseData.damage : projectile.ModifiedDamage
            };

            if (NetworkManager.Instance.IsHost)
                NetworkManager.Instance.Host.HandleLocalProjectile(packet);
            else
                NetworkManager.Instance.Client.SendPacket(0, packet, reliable: false);
        }

        public void ApplyRemoteProjectile(ProjectileFiredPacket packet, bool authoritative)
        {
            try
            {
                Gun gun = ModCompatibility.ResolvePickup(packet.GunKey, packet.GunId) as Gun;
                Projectile prefab = ResolveProjectile(gun, packet.ProjectileName);
                if (prefab == null) return;

                float angle = Mathf.Atan2(packet.Direction.y, packet.Direction.x) * Mathf.Rad2Deg;
                GameObject go = SpawnManager.SpawnProjectile(prefab.gameObject, packet.Position, Quaternion.Euler(0f, 0f, angle), true);
                Projectile projectile = go != null ? go.GetComponent<Projectile>() : null;
                if (projectile == null) return;

                projectile.SpawnedFromOtherPlayerProjectile = true;
                projectile.Direction = packet.Direction.normalized;
                projectile.Speed = packet.Speed;

                PlayerController owner = PlayerManager.Instance.GetRemotePlayerController(packet.PlayerId);
                if (owner == null) owner = ETGReflectionHelper.GetPrimaryPlayer() as PlayerController;
                if (owner != null)
                {
                    projectile.Owner = owner;
                    projectile.Shooter = owner.specRigidbody;
                }

                if (projectile.baseData != null)
                    projectile.baseData.damage = authoritative ? packet.Damage : 0f;
                projectile.SendInDirection(packet.Direction.normalized, true, true);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[GameplaySync] Could not spawn remote projectile: " + e.Message);
            }
        }

        private void OnLocalBeam(BeamController beam)
        {
            if (_applyingRemoteBeam || !IsNetworkReady() || beam == null) return;
            foreach (var pair in _localBeams)
                if (pair.Value.Beam == beam) return;

            Gun gun = beam.Gun != null ? beam.Gun : (_boundPlayer != null ? _boundPlayer.CurrentGun : null);
            Projectile projectile = beam.projectile;
            var info = new LocalBeamInfo
            {
                Id = _nextBeamId++,
                Beam = beam,
                GunId = gun != null ? gun.PickupObjectId : -1,
                GunKey = ModCompatibility.GetPickupKey(gun),
                ProjectileName = projectile != null ? NormalizePrefabName(projectile.name) : ""
            };
            _localBeams[info.Id] = info;
            SendBeamState(info, true);
        }

        private void SendLocalBeamStates()
        {
            if (_localBeams.Count == 0) return;
            var finished = new List<int>();
            foreach (var pair in _localBeams)
            {
                LocalBeamInfo info = pair.Value;
                if (info.Beam == null)
                {
                    SendBeamState(info, false);
                    finished.Add(pair.Key);
                }
                else
                {
                    SendBeamState(info, true);
                }
            }
            foreach (int id in finished) _localBeams.Remove(id);
        }

        private void SendBeamState(LocalBeamInfo info, bool active)
        {
            var packet = new PlayerBeamStatePacket
            {
                PlayerId = SteamReflectionHelper.GetLocalSteamId(),
                BeamId = info.Id,
                GunId = info.GunId,
                GunKey = info.GunKey,
                ProjectileName = info.ProjectileName,
                Origin = active && info.Beam != null ? info.Beam.Origin : Vector2.zero,
                Direction = active && info.Beam != null ? info.Beam.Direction : Vector2.right,
                DamageModifier = active && info.Beam != null ? info.Beam.DamageModifier : 0f,
                Active = active
            };

            if (NetworkManager.Instance.IsHost)
                NetworkManager.Instance.Host.HandleLocalBeam(packet);
            else if (NetworkManager.Instance.Client != null)
                NetworkManager.Instance.Client.SendPacket(0, packet, reliable: !active);
        }

        public void ApplyRemoteBeam(PlayerBeamStatePacket packet, bool authoritative)
        {
            string key = packet.PlayerId + ":" + packet.BeamId;
            BeamController beam;
            if (!packet.Active)
            {
                if (_remoteBeams.TryGetValue(key, out beam) && beam != null)
                    beam.DestroyBeam();
                _remoteBeams.Remove(key);
                return;
            }

            if (!_remoteBeams.TryGetValue(key, out beam) || beam == null)
            {
                Gun gun = ModCompatibility.ResolvePickup(packet.GunKey, packet.GunId) as Gun;
                Projectile prefab = ResolveProjectile(gun, packet.ProjectileName);
                PlayerController owner = PlayerManager.Instance.GetRemotePlayerController(packet.PlayerId);
                if (owner == null) owner = ETGReflectionHelper.GetPrimaryPlayer() as PlayerController;
                if (prefab == null || owner == null) return;

                float angle = Mathf.Atan2(packet.Direction.y, packet.Direction.x) * Mathf.Rad2Deg;
                try
                {
                    _applyingRemoteBeam = true;
                    beam = BeamController.FreeFireBeam(prefab, owner, angle, 3600f, false);
                }
                finally
                {
                    _applyingRemoteBeam = false;
                }
                if (beam == null) return;
                beam.Gun = gun;
                _remoteBeams[key] = beam;
            }

            beam.Origin = packet.Origin;
            beam.Direction = packet.Direction.normalized;
            beam.DamageModifier = authoritative ? packet.DamageModifier : 0f;
            beam.HitsEnemies = authoritative;
            beam.HitsPlayers = false;
        }

        private static Projectile ResolveProjectile(Gun gun, string projectileName)
        {
            if (gun == null) return null;
            Projectile fallback = null;
            ProjectileVolleyData volley = gun.Volley;
            if (volley != null && volley.projectiles != null)
            {
                foreach (ProjectileModule module in volley.projectiles)
                {
                    if (module == null) continue;
                    if (module.projectiles != null)
                    {
                        foreach (Projectile projectile in module.projectiles)
                        {
                            if (fallback == null) fallback = projectile;
                            if (MatchesProjectile(projectile, projectileName)) return projectile;
                        }
                    }
                    if (module.chargeProjectiles != null)
                    {
                        foreach (ProjectileModule.ChargeProjectile charged in module.chargeProjectiles)
                        {
                            Projectile projectile = charged != null ? charged.Projectile : null;
                            if (fallback == null) fallback = projectile;
                            if (MatchesProjectile(projectile, projectileName)) return projectile;
                        }
                    }
                    if (module.usesOptionalFinalProjectile && MatchesProjectile(module.finalProjectile, projectileName))
                        return module.finalProjectile;
                }
            }

            if (fallback != null) return fallback;
            if (gun.DefaultModule != null && gun.DefaultModule.projectiles != null && gun.DefaultModule.projectiles.Count > 0)
                return gun.DefaultModule.projectiles[0];
            return null;
        }

        private static bool MatchesProjectile(Projectile projectile, string projectileName)
        {
            return projectile != null && !string.IsNullOrEmpty(projectileName) &&
                   string.Equals(NormalizePrefabName(projectile.name), projectileName, StringComparison.Ordinal);
        }

        private static string NormalizePrefabName(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            return value.Replace("(Clone)", "").Trim();
        }

        public void ClearNetworkState()
        {
            foreach (var pair in _remoteBeams)
                if (pair.Value != null) pair.Value.DestroyBeam();
            _remoteBeams.Clear();
            _localBeams.Clear();
            _nextBeamId = 1;
            _vitals.Clear();
            _vitalsTime.Clear();
            _reviveProgress.Clear();
            _isDowned = false;
            _pickupHistory.Clear();
            _pickupHistoryKeys.Clear();
            _pickupHistoryWorld = "";
            _hostGroundPickupIds.Clear();
            _clientGroundPickups.Clear();
            _pendingPickupClaims.Clear();
            _nextGroundPickupId = 1;
        }

        public bool ApplyRemotePickup(ItemPickupPacket packet)
        {
            PickupObject best = null;
            if (packet.WorldObjectId > 0)
            {
                if (NetworkManager.Instance.IsHost)
                {
                    foreach (var pair in _hostGroundPickupIds)
                        if (pair.Value == packet.WorldObjectId && pair.Key != null) { best = pair.Key; break; }
                }
                else
                {
                    _clientGroundPickups.TryGetValue(packet.WorldObjectId, out best);
                }
            }

            float bestDistance = 2.25f;
            foreach (PickupObject pickup in UnityEngine.Object.FindObjectsOfType<PickupObject>())
            {
                if (best != null) break;
                if (pickup == null || !pickup.gameObject.activeInHierarchy) continue;
                if (pickup.GetComponent<DebrisObject>() == null) continue;
                string pickupKey = ModCompatibility.GetPickupKey(pickup);
                bool identityMatches = !string.IsNullOrEmpty(packet.PickupKey)
                    ? string.Equals(pickupKey, packet.PickupKey, StringComparison.Ordinal)
                    : pickup.PickupObjectId == packet.PickupId;
                if (!identityMatches) continue;
                float distance = Vector2.Distance(pickup.transform.position, packet.Position);
                if (distance < bestDistance)
                {
                    best = pickup;
                    bestDistance = distance;
                }
            }

            if (best == null) return false;

            PlayerController recipient = packet.PlayerId == SteamReflectionHelper.GetLocalSteamId()
                ? ETGReflectionHelper.GetPrimaryPlayer() as PlayerController
                : PlayerManager.Instance.GetRemotePlayerController(packet.PlayerId);
            if (recipient == null)
            {
                UnityEngine.Object.Destroy(best.gameObject);
                return false;
            }

            // Give the proxy the item as well as removing the world pickup. This
            // keeps its inventory, gun renderer, passive effects and co-op state
            // in line with the owning machine. The pickup Harmony hook ignores
            // non-primary players, so this cannot echo back over the network.
            try
            {
                _applyingRemotePickup = true;
                MethodInfo pickupMethod = best.GetType().GetMethod("Pickup", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null, new[] { typeof(PlayerController) }, null);
                if (pickupMethod == null) return false;
                pickupMethod.Invoke(best, new object[] { recipient });
                if (packet.WorldObjectId > 0) _clientGroundPickups.Remove(packet.WorldObjectId);
                _pendingPickupClaims.Remove(GetPickupClaimKey(packet.PickupKey, packet.PickupId, packet.Position));
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[GameplaySync] Could not apply remote pickup to PlayerController: " + e.Message);
                if (best != null) UnityEngine.Object.Destroy(best.gameObject);
                return false;
            }
            finally
            {
                _applyingRemotePickup = false;
            }
        }

        private void ScanGroundPickups()
        {
            var dead = new List<PickupObject>();
            foreach (var pair in _hostGroundPickupIds)
                if (pair.Key == null) dead.Add(pair.Key);
            foreach (PickupObject pickup in dead) _hostGroundPickupIds.Remove(pickup);

            foreach (PickupObject pickup in UnityEngine.Object.FindObjectsOfType<PickupObject>())
            {
                if (pickup == null || !pickup.gameObject.activeInHierarchy || pickup.GetComponent<DebrisObject>() == null) continue;
                RegisterHostGroundPickup(pickup);
            }
        }

        private int RegisterHostGroundPickup(PickupObject pickup)
        {
            if (pickup == null || pickup.GetComponent<DebrisObject>() == null) return 0;
            int existing;
            if (_hostGroundPickupIds.TryGetValue(pickup, out existing)) return existing;
            int id = _nextGroundPickupId++;
            _hostGroundPickupIds[pickup] = id;
            if (NetworkManager.Instance.Host != null)
                NetworkManager.Instance.Host.Broadcast(CreateGroundPickupPacket(pickup, id), reliable: true);
            return id;
        }

        private static GroundPickupSpawnPacket CreateGroundPickupPacket(PickupObject pickup, int id)
        {
            return new GroundPickupSpawnPacket
            {
                WorldObjectId = id,
                PickupId = pickup.PickupObjectId,
                PickupKey = ModCompatibility.GetPickupKey(pickup),
                Position = pickup.transform.position
            };
        }

        public void SendGroundPickupSnapshotTo(ulong targetId)
        {
            if (!NetworkManager.Instance.IsHost) return;
            ScanGroundPickups();
            foreach (var pair in _hostGroundPickupIds)
                if (pair.Key != null) NetworkManager.Instance.Host.SendPacket(targetId, CreateGroundPickupPacket(pair.Key, pair.Value), reliable: true);
        }

        public void ApplyGroundPickupSpawn(GroundPickupSpawnPacket packet)
        {
            PickupObject existing;
            if (_clientGroundPickups.TryGetValue(packet.WorldObjectId, out existing) && existing != null) return;

            foreach (PickupObject pickup in UnityEngine.Object.FindObjectsOfType<PickupObject>())
            {
                if (pickup == null || !pickup.gameObject.activeInHierarchy || pickup.GetComponent<DebrisObject>() == null) continue;
                bool matches = !string.IsNullOrEmpty(packet.PickupKey)
                    ? string.Equals(ModCompatibility.GetPickupKey(pickup), packet.PickupKey, StringComparison.Ordinal)
                    : pickup.PickupObjectId == packet.PickupId;
                if (matches && Vector2.Distance(pickup.transform.position, packet.Position) <= 2.25f)
                {
                    _clientGroundPickups[packet.WorldObjectId] = pickup;
                    return;
                }
            }

            PickupObject prefab = ModCompatibility.ResolvePickup(packet.PickupKey, packet.PickupId);
            if (prefab == null) return;
            DebrisObject debris = LootEngine.SpawnItem(prefab.gameObject, packet.Position, Vector2.zero, 0f, false, true, false);
            PickupObject spawned = debris != null ? debris.GetComponent<PickupObject>() : null;
            if (spawned != null) _clientGroundPickups[packet.WorldObjectId] = spawned;
        }

        public void RecordAuthoritativePickup(ItemPickupPacket packet)
        {
            EnsurePickupHistoryWorld();
            string key = packet.PlayerId + "|" + packet.PickupKey + "|" + packet.PickupId + "|" +
                         Mathf.RoundToInt(packet.Position.x * 4f) + "," + Mathf.RoundToInt(packet.Position.y * 4f);
            if (_pickupHistoryKeys.Add(key)) _pickupHistory.Add(packet);
        }

        public void SendPickupSnapshotTo(ulong targetId)
        {
            EnsurePickupHistoryWorld();
            foreach (ItemPickupPacket packet in _pickupHistory)
                NetworkManager.Instance.Host.SendPacket(targetId, packet, reliable: true);
        }

        private void EnsurePickupHistoryWorld()
        {
            string world = ETGReflectionHelper.IsInFoyer() ? "foyer" :
                ETGReflectionHelper.GetCurrentLevelName() + "|" + ETGReflectionHelper.GetCurrentRunSeed();
            if (world == _pickupHistoryWorld) return;
            _pickupHistoryWorld = world;
            _pickupHistory.Clear();
            _pickupHistoryKeys.Clear();
        }

        private static bool HasPickup(PlayerController player, string pickupKey, int pickupId)
        {
            if (player.inventory != null)
                foreach (Gun gun in player.inventory.AllGuns)
                    if (gun != null && PickupMatches(gun, pickupKey, pickupId)) return true;
            foreach (PassiveItem passive in player.passiveItems)
                if (passive != null && PickupMatches(passive, pickupKey, pickupId)) return true;
            foreach (PlayerItem active in player.activeItems)
                if (active != null && PickupMatches(active, pickupKey, pickupId)) return true;
            return false;
        }

        private static bool PickupMatches(PickupObject pickup, string key, int id)
        {
            return !string.IsNullOrEmpty(key)
                ? string.Equals(ModCompatibility.GetPickupKey(pickup), key, StringComparison.Ordinal)
                : pickup.PickupObjectId == id;
        }

        /// <summary>Latest vitals of any known player plus the seconds since they arrived.</summary>
        public bool TryGetVitals(ulong playerId, out PlayerVitalsPacket vitals, out float ageSeconds)
        {
            ageSeconds = float.MaxValue;
            if (!_vitals.TryGetValue(playerId, out vitals)) return false;
            float received;
            if (_vitalsTime.TryGetValue(playerId, out received))
                ageSeconds = Time.realtimeSinceStartup - received;
            return true;
        }

        public List<ulong> GetKnownPlayerIds()
        {
            return new List<ulong>(_vitals.Keys);
        }

        public void RecordVitals(PlayerVitalsPacket packet)
        {
            _vitals[packet.PlayerId] = packet;
            _vitalsTime[packet.PlayerId] = Time.realtimeSinceStartup;
            PlayerManager.Instance.SetRemotePlayerDowned(packet.PlayerId, packet.IsDowned, packet.Health, packet.Armor);
            PlayerController remote = PlayerManager.Instance.GetRemotePlayerController(packet.PlayerId);
            if (remote != null && remote.CurrentGun != null && packet.CurrentGunAmmo >= 0)
                remote.CurrentGun.CurrentAmmo = packet.CurrentGunAmmo;
        }

        public void ApplyHostEconomy(PlayerVitalsPacket packet)
        {
            PlayerController local = ETGReflectionHelper.GetPrimaryPlayer() as PlayerController;
            if (local == null || local.carriedConsumables == null) return;
            local.carriedConsumables.Currency = Mathf.Max(0, packet.Currency);
            local.carriedConsumables.KeyBullets = Mathf.Max(0, packet.Keys);
            local.carriedConsumables.ForceUpdateUI();
        }

        public void SynchronizeSharedEconomyFrom(ulong playerId)
        {
            if (!NetworkManager.Instance.IsHost || GameManager.Instance == null) return;
            PlayerController source = playerId == SteamReflectionHelper.GetLocalSteamId()
                ? GameManager.Instance.PrimaryPlayer
                : PlayerManager.Instance.GetRemotePlayerController(playerId);
            if (source == null || source.carriedConsumables == null) return;

            int currency = Mathf.Max(0, source.carriedConsumables.Currency);
            int keys = Mathf.Max(0, source.carriedConsumables.KeyBullets);
            foreach (PlayerController player in GameManager.Instance.AllPlayers)
            {
                if (player == null || player.carriedConsumables == null) continue;
                player.carriedConsumables.Currency = currency;
                player.carriedConsumables.KeyBullets = keys;
                player.carriedConsumables.ForceUpdateUI();
            }
            _nextVitalsTime = 0f;
        }

        public void RemovePlayer(ulong playerId)
        {
            _vitals.Remove(playerId);
            _vitalsTime.Remove(playerId);
            _reviveProgress.Remove(playerId);
        }

        public void ApplyRevive(PlayerRevivePacket packet)
        {
            ulong localId = SteamReflectionHelper.GetLocalSteamId();
            if (packet.PlayerId == localId && _boundPlayer != null)
            {
                _isDowned = false;
                _boundPlayer.healthHaver.ForceSetCurrentHealth(Mathf.Max(0.5f, packet.Health));
                _boundPlayer.healthHaver.IsVulnerable = true;
                _boundPlayer.CurrentInputState = PlayerInputState.AllInput;
            }

            PlayerVitalsPacket state;
            if (_vitals.TryGetValue(packet.PlayerId, out state))
            {
                state.IsDowned = false;
                state.Health = packet.Health;
            }
            PlayerManager.Instance.SetRemotePlayerDowned(packet.PlayerId, false);
        }

        private void SendLocalVitals()
        {
            if (_boundPlayer == null || _boundPlayer.healthHaver == null) return;
            var packet = new PlayerVitalsPacket
            {
                PlayerId = SteamReflectionHelper.GetLocalSteamId(),
                Health = _boundPlayer.healthHaver.GetCurrentHealth(),
                Armor = _boundPlayer.healthHaver.Armor,
                IsDowned = _isDowned,
                Currency = _boundPlayer.carriedConsumables != null ? _boundPlayer.carriedConsumables.Currency : 0,
                Keys = _boundPlayer.carriedConsumables != null ? _boundPlayer.carriedConsumables.KeyBullets : 0,
                Blanks = _boundPlayer.Blanks,
                CurrentGunAmmo = _boundPlayer.CurrentGun != null ? _boundPlayer.CurrentGun.CurrentAmmo : -1,
                MaxHealth = _boundPlayer.healthHaver.GetMaxHealth(),
                MaxArmor = Mathf.Max(_boundPlayer.healthHaver.Armor, 0f),
                MaxAmmo = _boundPlayer.CurrentGun != null ? _boundPlayer.CurrentGun.AdjustedMaxAmmo : -1,
                InfiniteAmmo = _boundPlayer.CurrentGun != null && _boundPlayer.CurrentGun.InfiniteAmmo,
                ActiveItemKey = _boundPlayer.CurrentItem != null ? ModCompatibility.GetPickupKey(_boundPlayer.CurrentItem) : "",
                ActiveItemId = _boundPlayer.CurrentItem != null ? _boundPlayer.CurrentItem.PickupObjectId : -1,
                PingMs = NetworkManager.Instance.GetLocalPingMs()
            };

            RecordVitals(packet);
            if (NetworkManager.Instance.IsHost)
                NetworkManager.Instance.Host.HandleLocalVitals(packet);
            else
                NetworkManager.Instance.Client.SendPacket(0, packet, reliable: false);
        }

        private void UpdateRevives()
        {
            Dictionary<ulong, Vector2> positions = NetworkManager.Instance.Host.GetKnownPlayerPositions();
            foreach (var pair in _vitals)
            {
                ulong downedId = pair.Key;
                if (!pair.Value.IsDowned || !positions.ContainsKey(downedId))
                {
                    _reviveProgress.Remove(downedId);
                    continue;
                }

                bool helperNearby = false;
                foreach (var helper in _vitals)
                {
                    if (helper.Key == downedId || helper.Value.IsDowned || !positions.ContainsKey(helper.Key)) continue;
                    if (Vector2.Distance(positions[downedId], positions[helper.Key]) <= ReviveDistance)
                    {
                        helperNearby = true;
                        break;
                    }
                }

                float progress = _reviveProgress.ContainsKey(downedId) ? _reviveProgress[downedId] : 0f;
                progress = helperNearby ? progress + Time.deltaTime : 0f;
                _reviveProgress[downedId] = progress;
                if (progress >= ReviveSeconds)
                {
                    var revive = new PlayerRevivePacket { PlayerId = downedId, Health = 1f };
                    ApplyRevive(revive);
                    NetworkManager.Instance.Host.Broadcast(revive, reliable: true);
                    _reviveProgress.Remove(downedId);
                }
            }
        }

        private bool TryEnterDowned(PlayerController player)
        {
            if (!IsNetworkReady() || player == null || player != (ETGReflectionHelper.GetPrimaryPlayer() as PlayerController)) return false;
            if (_isDowned) return true;

            _isDowned = true;
            if (player.healthHaver != null)
            {
                player.healthHaver.ForceSetCurrentHealth(0.5f);
                player.healthHaver.IsVulnerable = false;
            }
            player.CurrentInputState = PlayerInputState.NoInput;
            _nextVitalsTime = 0f;
            Debug.Log("[GameplaySync] Local player is downed; waiting for a teammate revive.");
            return true;
        }

        private void OnLocalPickup(PickupObject pickup, PlayerController player)
        {
            if (_applyingRemotePickup || !IsNetworkReady() || pickup == null || player == null || player != (ETGReflectionHelper.GetPrimaryPlayer() as PlayerController)) return;
            var packet = new ItemPickupPacket
            {
                PlayerId = SteamReflectionHelper.GetLocalSteamId(),
                PickupId = pickup.PickupObjectId,
                PickupKey = ModCompatibility.GetPickupKey(pickup),
                Position = pickup.transform.position,
                WorldObjectId = GetGroundPickupId(pickup)
            };

            if (NetworkManager.Instance.IsHost)
                NetworkManager.Instance.Host.HandleLocalPickup(packet);
        }

        private bool RequestClientPickup(PickupObject pickup, PlayerController player)
        {
            if (_applyingRemotePickup || !NetworkManager.Instance.IsClient || !IsNetworkReady() || pickup == null ||
                player == null || player != (ETGReflectionHelper.GetPrimaryPlayer() as PlayerController)) return true;

            string key = GetPickupClaimKey(ModCompatibility.GetPickupKey(pickup), pickup.PickupObjectId, pickup.transform.position);
            float last;
            if (!_pendingPickupClaims.TryGetValue(key, out last) || Time.realtimeSinceStartup - last >= 1f)
            {
                _pendingPickupClaims[key] = Time.realtimeSinceStartup;
                NetworkManager.Instance.Client.SendPacket(0, new ItemPickupPacket
                {
                    PlayerId = SteamReflectionHelper.GetLocalSteamId(),
                    PickupId = pickup.PickupObjectId,
                    PickupKey = ModCompatibility.GetPickupKey(pickup),
                    Position = pickup.transform.position,
                    WorldObjectId = GetGroundPickupId(pickup)
                }, reliable: true);
            }
            return false;
        }

        private int GetGroundPickupId(PickupObject pickup)
        {
            int id;
            if (NetworkManager.Instance.IsHost && _hostGroundPickupIds.TryGetValue(pickup, out id)) return id;
            foreach (var pair in _clientGroundPickups)
                if (pair.Value == pickup) return pair.Key;
            return 0;
        }

        private static string GetPickupClaimKey(string key, int id, Vector2 position)
        {
            return key + "|" + id + "|" + Mathf.RoundToInt(position.x * 4f) + "," + Mathf.RoundToInt(position.y * 4f);
        }

        private void InstallPatches()
        {
            Harmony harmony = new Harmony("com.llamerrr.gungeontogether.gameplay-sync");
            var prefix = new HarmonyMethod(typeof(GameplaySyncManager).GetMethod("PickupPrefix", BindingFlags.NonPublic | BindingFlags.Static));
            var postfix = new HarmonyMethod(typeof(GameplaySyncManager).GetMethod("PickupPostfix", BindingFlags.NonPublic | BindingFlags.Static));
            var seen = new HashSet<MethodBase>();
            foreach (Type type in typeof(PickupObject).Assembly.GetTypes())
            {
                try
                {
                    if (type.IsAbstract || !typeof(PickupObject).IsAssignableFrom(type)) continue;
                    MethodInfo method = type.GetMethod("Pickup", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                        null, new[] { typeof(PlayerController) }, null);
                    if (method == null || method.IsAbstract || !seen.Add(method)) continue;
                    harmony.Patch(method, prefix, postfix);
                    Debug.Log("[GameplaySync] Patched pickup handler: " + method.DeclaringType.FullName + "." + method.Name);
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[GameplaySync] Could not patch " + type.FullName + ".Pickup: " + e);
                }
            }

            try
            {
                MethodInfo die = AccessTools.Method(typeof(HealthHaver), "Die", new[] { typeof(Vector2) });
                MethodInfo downedPrefix = typeof(GameplaySyncManager).GetMethod("PlayerHealthDiePrefix", BindingFlags.NonPublic | BindingFlags.Static);
                if (die != null && downedPrefix != null)
                    harmony.Patch(die, new HarmonyMethod(downedPrefix));
            }
            catch (Exception e)
            {
                Debug.LogWarning("[GameplaySync] Could not install network revive patch: " + e);
            }

        }

        private static bool PickupPrefix(PickupObject __instance, PlayerController __0)
        {
            if (_instance == null) return true;
            if (NetworkManager.Instance.IsHost && __0 == GameManager.Instance.PrimaryPlayer)
            {
                _instance.RegisterHostGroundPickup(__instance);
                return true;
            }
            return _instance.RequestClientPickup(__instance, __0);
        }

        private static bool PlayerHealthDiePrefix(HealthHaver __instance)
        {
            if (_instance == null || __instance == null) return true;
            PlayerController player = __instance.GetComponent<PlayerController>();
            return !_instance.TryEnterDowned(player);
        }

        private static void PickupPostfix(PickupObject __instance, PlayerController __0)
        {
            if (_instance != null) _instance.OnLocalPickup(__instance, __0);
        }

    }
}

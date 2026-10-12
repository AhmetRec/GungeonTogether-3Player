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

        private PlayerController _boundPlayer;
        private float _nextVitalsTime;
        private float _nextBeamTime;
        private int _nextBeamId = 1;
        private bool _applyingRemoteBeam;
        private bool _isDowned;
        private readonly Dictionary<ulong, PlayerVitalsPacket> _vitals = new Dictionary<ulong, PlayerVitalsPacket>();
        private readonly Dictionary<ulong, float> _reviveProgress = new Dictionary<ulong, float>();
        private readonly Dictionary<int, LocalBeamInfo> _localBeams = new Dictionary<int, LocalBeamInfo>();
        private readonly Dictionary<string, BeamController> _remoteBeams = new Dictionary<string, BeamController>();

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
                UpdateRevives();
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
            }

            _boundPlayer = player;
            if (_boundPlayer != null)
            {
                _boundPlayer.PostProcessProjectile += OnLocalProjectile;
                _boundPlayer.PostProcessBeam += OnLocalBeam;
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

                var localPlayer = ETGReflectionHelper.GetPrimaryPlayer() as PlayerController;
                if (localPlayer != null)
                {
                    projectile.Owner = localPlayer;
                    projectile.Shooter = localPlayer.specRigidbody;
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
                PlayerController owner = ETGReflectionHelper.GetPrimaryPlayer() as PlayerController;
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
            _reviveProgress.Clear();
            _isDowned = false;
        }

        public void ApplyRemotePickup(ItemPickupPacket packet)
        {
            PickupObject[] pickups = UnityEngine.Object.FindObjectsOfType<PickupObject>();
            PickupObject best = null;
            float bestDistance = 2.25f;
            foreach (PickupObject pickup in pickups)
            {
                if (pickup == null || !pickup.gameObject.activeInHierarchy) continue;
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

            if (best != null)
                UnityEngine.Object.Destroy(best.gameObject);
        }

        public void RecordVitals(PlayerVitalsPacket packet)
        {
            _vitals[packet.PlayerId] = packet;
            PlayerManager.Instance.SetRemotePlayerDowned(packet.PlayerId, packet.IsDowned);
        }

        public void RemovePlayer(ulong playerId)
        {
            _vitals.Remove(playerId);
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
                IsDowned = _isDowned
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
            if (!IsNetworkReady() || pickup == null || player == null || player != (ETGReflectionHelper.GetPrimaryPlayer() as PlayerController)) return;
            var packet = new ItemPickupPacket
            {
                PlayerId = SteamReflectionHelper.GetLocalSteamId(),
                PickupId = pickup.PickupObjectId,
                PickupKey = ModCompatibility.GetPickupKey(pickup),
                Position = pickup.transform.position
            };

            if (NetworkManager.Instance.IsHost)
                NetworkManager.Instance.Host.HandleLocalPickup(packet);
            else
                NetworkManager.Instance.Client.SendPacket(0, packet, reliable: true);
        }

        private void InstallPatches()
        {
            try
            {
                Harmony harmony = new Harmony("com.llamerrr.gungeontogether.gameplay-sync");
                var postfix = new HarmonyMethod(typeof(GameplaySyncManager).GetMethod("PickupPostfix", BindingFlags.NonPublic | BindingFlags.Static));
                var seen = new HashSet<MethodBase>();
                foreach (Type type in new[] { typeof(PickupObject), typeof(Gun), typeof(PassiveItem), typeof(PlayerItem) })
                {
                    MethodInfo method = AccessTools.Method(type, "Pickup", new[] { typeof(PlayerController) });
                    if (method != null && seen.Add(method)) harmony.Patch(method, null, postfix);
                }

                MethodInfo die = AccessTools.Method(typeof(PlayerController), "Die");
                if (die != null)
                {
                    var prefix = new HarmonyMethod(typeof(GameplaySyncManager).GetMethod("DeathPrefix", BindingFlags.NonPublic | BindingFlags.Static));
                    harmony.Patch(die, prefix, null);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[GameplaySync] Harmony patch setup failed: " + e.Message);
            }
        }

        private static void PickupPostfix(PickupObject __instance, PlayerController __0)
        {
            if (_instance != null) _instance.OnLocalPickup(__instance, __0);
        }

        private static bool DeathPrefix(PlayerController __instance)
        {
            return _instance == null || !_instance.TryEnterDowned(__instance);
        }
    }
}

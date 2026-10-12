using System;
using System.Collections.Generic;
using UnityEngine;
using GungeonTogether.Networking.Packets;
using Debug = GungeonTogether.Systems.Logging.Debug;

namespace GungeonTogether.Networking.Sync
{
    public class EnemyProjectileSyncManager : MonoBehaviour
    {
        private static EnemyProjectileSyncManager _instance;
        public static EnemyProjectileSyncManager Instance
        {
            get
            {
                if (_instance == null)
                {
                    var go = new GameObject("EnemyProjectileSyncManager");
                    _instance = go.AddComponent<EnemyProjectileSyncManager>();
                    DontDestroyOnLoad(go);
                }
                return _instance;
            }
        }

        private const float StateInterval = 0.08f;
        private int _nextProjectileId = 1;
        private float _nextStateTime;
        private readonly Dictionary<AIActor, Action<string, Projectile>> _hostHandlers = new Dictionary<AIActor, Action<string, Projectile>>();
        private readonly Dictionary<int, HostProjectileInfo> _hostProjectiles = new Dictionary<int, HostProjectileInfo>();
        private readonly Dictionary<int, Projectile> _remoteProjectiles = new Dictionary<int, Projectile>();
        private readonly Dictionary<int, EnemyProjectileSpawnPacket> _pendingSpawns = new Dictionary<int, EnemyProjectileSpawnPacket>();
        private readonly HashSet<AIActor> _clientEnemies = new HashSet<AIActor>();

        private class HostProjectileInfo
        {
            public int EnemyId;
            public string BulletName;
            public Projectile Projectile;
        }

        private void Update()
        {
            if (NetworkManager.Instance.IsClient)
                RetryPendingSpawns();
            if (!NetworkManager.Instance.IsHost || NetworkManager.Instance.Host == null) return;
            if (Time.realtimeSinceStartup < _nextStateTime) return;
            _nextStateTime = Time.realtimeSinceStartup + StateInterval;

            var dead = new List<int>();
            foreach (var pair in _hostProjectiles)
            {
                Projectile projectile = pair.Value.Projectile;
                if (projectile == null)
                {
                    NetworkManager.Instance.Host.Broadcast(new EnemyProjectileDestroyPacket { ProjectileId = pair.Key }, reliable: true);
                    dead.Add(pair.Key);
                    continue;
                }

                NetworkManager.Instance.Host.Broadcast(new EnemyProjectileStatePacket
                {
                    ProjectileId = pair.Key,
                    Position = projectile.transform.position,
                    Direction = projectile.Direction,
                    Speed = projectile.Speed
                }, reliable: false);
            }
            foreach (int id in dead) _hostProjectiles.Remove(id);
        }

        public void TrackHostEnemy(int enemyId, AIActor actor)
        {
            if (actor == null || actor.bulletBank == null || _hostHandlers.ContainsKey(actor)) return;

            Action<string, Projectile> handler = (bulletName, projectile) => OnHostProjectile(enemyId, bulletName, projectile);
            _hostHandlers[actor] = handler;
            actor.bulletBank.OnProjectileCreatedWithSource += handler;
        }

        private void OnHostProjectile(int enemyId, string bulletName, Projectile projectile)
        {
            if (projectile == null || NetworkManager.Instance.Host == null) return;
            int projectileId = _nextProjectileId++;
            _hostProjectiles[projectileId] = new HostProjectileInfo
            {
                EnemyId = enemyId,
                BulletName = bulletName,
                Projectile = projectile
            };
            projectile.OnDestruction += ignored => OnHostProjectileDestroyed(projectileId);

            NetworkManager.Instance.Host.Broadcast(new EnemyProjectileSpawnPacket
            {
                ProjectileId = projectileId,
                EnemyId = enemyId,
                BulletName = bulletName,
                Position = projectile.transform.position,
                Direction = projectile.Direction,
                Speed = projectile.Speed,
                Damage = projectile.baseData != null ? projectile.baseData.damage : 0f
            }, reliable: true);
        }

        private void OnHostProjectileDestroyed(int projectileId)
        {
            if (!_hostProjectiles.Remove(projectileId)) return;
            if (NetworkManager.Instance.IsHost && NetworkManager.Instance.Host != null)
                NetworkManager.Instance.Host.Broadcast(new EnemyProjectileDestroyPacket { ProjectileId = projectileId }, reliable: true);
        }

        public void TrackClientEnemy(int enemyId, AIActor actor)
        {
            if (actor == null) return;
            actor.CanTargetPlayers = false;
            if (actor.healthHaver != null)
                actor.healthHaver.IsVulnerable = false;
            if (actor.behaviorSpeculator != null)
                actor.behaviorSpeculator.enabled = false;
            if (!_clientEnemies.Add(actor))
            {
                RetryPendingSpawns();
                return;
            }

            // A locally generated enemy can fire before its network spawn arrives.
            // Remove those bullets so only host-created projectile packets survive.
            foreach (Projectile projectile in UnityEngine.Object.FindObjectsOfType<Projectile>())
            {
                if (projectile != null && projectile.Owner == actor)
                    UnityEngine.Object.Destroy(projectile.gameObject);
            }
            RetryPendingSpawns();
        }

        public void ApplySpawn(EnemyProjectileSpawnPacket packet)
        {
            try
            {
                Projectile existing;
                if (_remoteProjectiles.TryGetValue(packet.ProjectileId, out existing) && existing != null)
                    return;
                GameObject enemyObject = NetworkEntityManager.Instance.GetRemote(packet.EnemyId);
                AIActor actor = enemyObject != null ? enemyObject.GetComponent<AIActor>() : null;
                if (actor == null || actor.bulletBank == null)
                {
                    _pendingSpawns[packet.ProjectileId] = packet;
                    return;
                }
                AIBulletBank.Entry entry = actor.bulletBank.GetBullet(packet.BulletName);
                if (entry == null || entry.BulletObject == null) return;

                float angle = Mathf.Atan2(packet.Direction.y, packet.Direction.x) * Mathf.Rad2Deg;
                GameObject go = SpawnManager.SpawnProjectile(entry.BulletObject, packet.Position, Quaternion.Euler(0f, 0f, angle), true);
                Projectile projectile = go != null ? go.GetComponent<Projectile>() : null;
                if (projectile == null) return;

                projectile.Owner = actor;
                projectile.Shooter = actor.specRigidbody;
                projectile.Direction = packet.Direction.normalized;
                projectile.Speed = packet.Speed;
                if (projectile.baseData != null) projectile.baseData.damage = packet.Damage;
                projectile.SendInDirection(packet.Direction.normalized, true, true);
                _remoteProjectiles[packet.ProjectileId] = projectile;
                _pendingSpawns.Remove(packet.ProjectileId);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[EnemyProjectileSync] Spawn failed: " + e.Message);
            }
        }

        public void ApplyState(EnemyProjectileStatePacket packet)
        {
            Projectile projectile;
            if (!_remoteProjectiles.TryGetValue(packet.ProjectileId, out projectile) || projectile == null) return;
            projectile.transform.position = packet.Position;
            projectile.Direction = packet.Direction.normalized;
            projectile.Speed = packet.Speed;
        }

        public void ApplyDestroy(EnemyProjectileDestroyPacket packet)
        {
            Projectile projectile;
            if (_remoteProjectiles.TryGetValue(packet.ProjectileId, out projectile) && projectile != null)
                UnityEngine.Object.Destroy(projectile.gameObject);
            _remoteProjectiles.Remove(packet.ProjectileId);
            _pendingSpawns.Remove(packet.ProjectileId);
        }

        private void RetryPendingSpawns()
        {
            if (_pendingSpawns.Count == 0) return;
            var packets = new List<EnemyProjectileSpawnPacket>(_pendingSpawns.Values);
            foreach (EnemyProjectileSpawnPacket packet in packets)
            {
                if (NetworkEntityManager.Instance.GetRemote(packet.EnemyId) != null)
                    ApplySpawn(packet);
            }
        }

        public void SendSnapshotTo(ulong targetId)
        {
            if (!NetworkManager.Instance.IsHost || NetworkManager.Instance.Host == null) return;
            foreach (var pair in _hostProjectiles)
            {
                HostProjectileInfo info = pair.Value;
                Projectile projectile = info.Projectile;
                if (projectile == null) continue;
                NetworkManager.Instance.Host.SendPacket(targetId, new EnemyProjectileSpawnPacket
                {
                    ProjectileId = pair.Key,
                    EnemyId = info.EnemyId,
                    BulletName = info.BulletName,
                    Position = projectile.transform.position,
                    Direction = projectile.Direction,
                    Speed = projectile.Speed,
                    Damage = projectile.baseData != null ? projectile.baseData.damage : 0f
                }, reliable: true);
            }
        }

        public void Clear()
        {
            foreach (var pair in _hostHandlers)
            {
                if (pair.Key != null && pair.Key.bulletBank != null)
                    pair.Key.bulletBank.OnProjectileCreatedWithSource -= pair.Value;
            }
            foreach (var pair in _remoteProjectiles)
            {
                if (pair.Value != null) UnityEngine.Object.Destroy(pair.Value.gameObject);
            }
            _hostHandlers.Clear();
            _hostProjectiles.Clear();
            _remoteProjectiles.Clear();
            _pendingSpawns.Clear();
            _clientEnemies.Clear();
            _nextProjectileId = 1;
        }
    }
}

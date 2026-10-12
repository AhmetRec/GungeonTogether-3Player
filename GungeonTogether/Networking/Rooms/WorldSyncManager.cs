using UnityEngine;
using GungeonTogether.Networking;
using GungeonTogether.Networking.Packets;
using System.Collections;

namespace GungeonTogether.Networking.Sync
{
    public class WorldSyncManager : MonoBehaviour
    {
        private static WorldSyncManager _instance;
        public static WorldSyncManager Instance
        {
            get
            {
                if (_instance == null)
                {
                    GameObject go = new GameObject("WorldSyncManager");
                    _instance = go.AddComponent<WorldSyncManager>();
                    DontDestroyOnLoad(go);
                }
                return _instance;
            }
        }

        private string _lastWorldState = ""; // string cache to detect changes
        private string _candidateWorldState = "";
        private float _candidateSince;
        private bool _hasAppliedInitialWorldState;
        private Coroutine _pendingPositionApply;

        private void Awake()
        {
            ETGReflectionHelper.Initialise();
        }

        private void Update()
        {
            if (!NetworkManager.Instance.IsHost && !NetworkManager.Instance.IsClient) return;

            if (NetworkManager.Instance.IsHost)
            {
                SyncHostWorldState();
            }
        }

        private void SyncHostWorldState()
        {
            var gm = ETGReflectionHelper.GetGameManager();
            if (gm == null) return;

            bool isFoyer = ETGReflectionHelper.IsInFoyer();
            int floorIndex = ETGReflectionHelper.GetCurrentFloorIndex();
            Vector3 pos = ETGReflectionHelper.GetPlayerPosition();
            float rot = ETGReflectionHelper.GetPlayerRotation();

            // Build a unique string representing the state
            int runSeed = ETGReflectionHelper.GetCurrentRunSeed();
            string levelName = ETGReflectionHelper.GetCurrentLevelName();
            if (!isFoyer && (floorIndex < 0 || string.IsNullOrEmpty(levelName))) return;

            // A room change is not a world change. Including roomId here used to
            // teleport every client to the host whenever the host crossed a door.
            string currentState = $"{isFoyer}|{floorIndex}|{levelName}|{runSeed}";

            // Dungeon loading exposes several short-lived floor values. Wait for
            // a stable topology before asking clients to load it.
            if (currentState != _candidateWorldState)
            {
                _candidateWorldState = currentState;
                _candidateSince = Time.realtimeSinceStartup;
                return;
            }
            if (Time.realtimeSinceStartup - _candidateSince < 0.35f) return;

            if (currentState != _lastWorldState)
            {
                _lastWorldState = currentState;
                Debug.Log($"[WorldSync] Host world state changed: isFoyer={isFoyer}, floor={floorIndex}, pos={pos}");

                var packet = new WorldStatePacket
                {
                    IsFoyer = isFoyer,
                    FloorIndex = floorIndex,
                    RunSeed = runSeed,
                    LevelName = levelName,
                    RoomIdentifier = ETGReflectionHelper.GetCurrentRoomIdentifier(),
                    Position = new Vector2(pos.x, pos.y),
                    Rotation = rot
                };
                NetworkManager.Instance.Host.Broadcast(packet, reliable: true);
            }
        }

        // Client applies the world state when packet arrives (handled in NetworkManager)
        public void ApplyWorldState(WorldStatePacket packet)
        {
            if (packet == null || (!packet.IsFoyer && (packet.FloorIndex < 0 || string.IsNullOrEmpty(packet.LevelName))))
                return;

            if (LoadingSyncManager.Instance != null && LoadingSyncManager.Instance.IsClientLoading)
            {
                BeginDelayedApply(packet);
            }
            else
            {
                ApplyNow(packet);
            }
        }

        private void BeginDelayedApply(WorldStatePacket packet)
        {
            if (_pendingPositionApply != null) StopCoroutine(_pendingPositionApply);
            _pendingPositionApply = StartCoroutine(DelayedApply(packet));
        }

        private IEnumerator DelayedApply(WorldStatePacket packet)
        {
            float deadline = Time.realtimeSinceStartup + 12f;
            while (LoadingSyncManager.Instance.IsClientLoading && Time.realtimeSinceStartup < deadline)
                yield return new WaitForSeconds(0.1f);
            _pendingPositionApply = null;
            ApplyNow(packet);
        }

        private void ApplyNow(WorldStatePacket packet)
        {
            bool sameWorld = IsSameWorld(packet);
            if (_hasAppliedInitialWorldState && sameWorld)
            {
                // Normal movement and room traversal are driven by player input
                // and entity snapshots, never by hard teleports.
                return;
            }

            _hasAppliedInitialWorldState = true;

            // If we need to change floor or foyer
            if (packet.IsFoyer)
            {
                if (!ETGReflectionHelper.IsInFoyer())
                {
                    ETGReflectionHelper.TeleportToFoyer();
                    if (_pendingPositionApply != null) StopCoroutine(_pendingPositionApply);
                    _pendingPositionApply = StartCoroutine(SetPositionAfterDelay(packet.Position, packet.Rotation, 0.5f));
                }
                else
                {
                    ETGReflectionHelper.TeleportToPosition(packet.Position, packet.Rotation);
                }
            }
            else
            {
                int currentFloor = ETGReflectionHelper.GetCurrentFloorIndex();
                string currentLevel = ETGReflectionHelper.GetCurrentLevelName();
                if (currentFloor != packet.FloorIndex || !string.Equals(currentLevel, packet.LevelName, System.StringComparison.OrdinalIgnoreCase))
                {
                    ETGReflectionHelper.SetCurrentRunSeed(packet.RunSeed);
                    ETGReflectionHelper.TeleportToLevel(packet.LevelName, packet.FloorIndex);
                    BeginPositionAfterLoad(packet);
                }
                else
                {
                    ETGReflectionHelper.TeleportToPosition(packet.Position, packet.Rotation);
                }
            }
        }

        private bool IsSameWorld(WorldStatePacket packet)
        {
            if (packet.IsFoyer) return ETGReflectionHelper.IsInFoyer();
            return !ETGReflectionHelper.IsInFoyer() &&
                   ETGReflectionHelper.GetCurrentFloorIndex() == packet.FloorIndex &&
                   string.Equals(ETGReflectionHelper.GetCurrentLevelName(), packet.LevelName, System.StringComparison.OrdinalIgnoreCase);
        }

        private void BeginPositionAfterLoad(WorldStatePacket packet)
        {
            if (_pendingPositionApply != null) StopCoroutine(_pendingPositionApply);
            _pendingPositionApply = StartCoroutine(SetPositionWhenLoaded(packet));
        }

        private IEnumerator SetPositionWhenLoaded(WorldStatePacket packet)
        {
            float deadline = Time.realtimeSinceStartup + 12f;
            while (!IsSameWorld(packet) && Time.realtimeSinceStartup < deadline)
                yield return new WaitForSeconds(0.1f);
            yield return null;
            if (IsSameWorld(packet))
                ETGReflectionHelper.TeleportToPosition(packet.Position, packet.Rotation);
            _pendingPositionApply = null;
        }

        private IEnumerator SetPositionAfterDelay(Vector2 pos, float rot, float delay)
        {
            yield return new WaitForSeconds(delay);
            ETGReflectionHelper.TeleportToPosition(pos, rot);
            _pendingPositionApply = null;
        }

        public void ResetSession()
        {
            _lastWorldState = "";
            _candidateWorldState = "";
            _candidateSince = 0f;
            _hasAppliedInitialWorldState = false;
            if (_pendingPositionApply != null)
            {
                StopCoroutine(_pendingPositionApply);
                _pendingPositionApply = null;
            }
        }
    }
}

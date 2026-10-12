using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Dungeonator;
using HarmonyLib;
using UnityEngine;
using GungeonTogether.Networking;
using GungeonTogether.Networking.Packets;

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

        // How long the host's world must stay unchanged and fully loaded
        // before it is published to clients.
        private const float StableSeconds = 0.35f;

        private string _lastWorldState = ""; // string cache to detect changes
        private string _candidateWorldState = "";
        private float _candidateSince;
        private bool _hasAppliedInitialWorldState;
        private bool _clientWorldReady;
        private Coroutine _pendingPositionApply;
        private readonly HashSet<ulong> _pendingInitialTargets = new HashSet<ulong>();

        // Shared-seed bookkeeping (host only). Enter the Gungeon generates an
        // unseeded run from a random number, so without this the host and the
        // clients build completely different maps from "seed 0".
        private static int _hostAssignedSeed;
        private static bool _needsFreshSeed = true;
        private static bool _patchesInstalled;

        // Layout fingerprint cache.
        private Dungeon _hashedDungeon;
        private int _cachedLayoutHash;

        // On-screen diagnostics so a desync is visible instead of a black view.
        private string _warning = "";
        private float _warningUntil;

        public bool IsClientWorldReady => !NetworkManager.Instance.IsClient || _clientWorldReady;

        public void SetClientLoading(bool isLoading)
        {
            if (NetworkManager.Instance.IsClient && isLoading)
                _clientWorldReady = false;
        }

        private void Awake()
        {
            ETGReflectionHelper.Initialise();
            InstallPatches();
        }

        private void Update()
        {
            if (!NetworkManager.Instance.IsHost && !NetworkManager.Instance.IsClient) return;

            if (NetworkManager.Instance.IsHost)
            {
                TrackRunBoundary();
                SyncHostWorldState();
            }
        }

        // ------------------------------------------------------------------
        // Shared run seed
        // ------------------------------------------------------------------

        private static void InstallPatches()
        {
            if (_patchesInstalled) return;
            _patchesInstalled = true;
            try
            {
                Harmony harmony = new Harmony("com.llamerrr.gungeontogether.world-seed");
                MethodInfo target = AccessTools.Method(typeof(Dungeon), "GetDungeonSeed");
                MethodInfo prefix = typeof(WorldSyncManager).GetMethod("GetDungeonSeedPrefix", BindingFlags.NonPublic | BindingFlags.Static);
                if (target != null && prefix != null)
                {
                    harmony.Patch(target, new HarmonyMethod(prefix));
                    UnityEngine.Debug.Log("[WorldSync] Patched Dungeon.GetDungeonSeed for shared network seeds.");
                }
                else
                {
                    UnityEngine.Debug.LogError("[WorldSync] Dungeon.GetDungeonSeed was not found; host and clients will generate different maps.");
                }
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogError("[WorldSync] Could not install seed patch: " + e);
            }
        }

        // Runs right before every dungeon generation. On the host it makes sure
        // the run has a non-zero seed, which the clients then reuse.
        private static void GetDungeonSeedPrefix()
        {
            try
            {
                NetworkManager network = NetworkManager.Instance;
                GameManager gm = GameManager.Instance;
                if (gm == null) return;

                if (network.IsClient)
                {
                    if (gm.CurrentRunSeed == 0)
                        UnityEngine.Debug.LogWarning("[WorldSync] Client is generating a floor without the host seed; the map will not match the host.");
                    return;
                }

                if (!network.IsHost || network.Host == null || gm.IsFoyer) return;

                int seed = gm.CurrentRunSeed;
                bool staleFromPreviousRun = seed != 0 && seed == _hostAssignedSeed && _needsFreshSeed;
                if (seed != 0 && !staleFromPreviousRun)
                {
                    // Either our seed for this run or one the host typed in
                    // through the game's own seeded-run option.
                    _needsFreshSeed = false;
                    return;
                }

                int newSeed = new System.Random().Next(1, 1000000000);
                _hostAssignedSeed = newSeed;
                _needsFreshSeed = false;
                // The setter re-runs the meta injection / reward preprocessing
                // with this seed, exactly like the base game's seeded runs, so
                // the clients get the same special rooms as well.
                gm.CurrentRunSeed = newSeed;
                UnityEngine.Debug.Log("[WorldSync] Host assigned shared run seed " + newSeed + ".");
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogError("[WorldSync] Seed prefix failed: " + e);
            }
        }

        private void TrackRunBoundary()
        {
            // Being back in the Breach means the next floor starts a new run,
            // so the seed we assigned to the previous run must not be reused.
            if (ETGReflectionHelper.IsInFoyer() && !ETGReflectionHelper.IsLevelLoading())
                _needsFreshSeed = true;
        }

        // ------------------------------------------------------------------
        // Host
        // ------------------------------------------------------------------

        private void SyncHostWorldState()
        {
            WorldStatePacket packet;
            if (!TryBuildHostWorldState(out packet))
            {
                // Still loading or the player has not been placed yet. Never
                // publish half-loaded state: the old position would belong to
                // the previous floor and drop clients into a wall.
                _candidateWorldState = "";
                return;
            }

            string currentState = $"{packet.IsFoyer}|{packet.FloorIndex}|{packet.LevelName}|{packet.RunSeed}";

            if (currentState != _candidateWorldState)
            {
                _candidateWorldState = currentState;
                _candidateSince = Time.realtimeSinceStartup;
                return;
            }
            if (Time.realtimeSinceStartup - _candidateSince < StableSeconds) return;

            if (currentState != _lastWorldState)
            {
                _lastWorldState = currentState;
                UnityEngine.Debug.Log($"[WorldSync] Host world state changed: isFoyer={packet.IsFoyer}, floor={packet.FloorIndex}, level={packet.LevelName}, seed={packet.RunSeed}, layout={packet.LayoutHash}, pos={packet.Position}");
                NetworkManager.Instance.Host.Broadcast(packet, reliable: true);
                _pendingInitialTargets.Clear();
                return;
            }

            if (_pendingInitialTargets.Count > 0)
            {
                foreach (ulong target in _pendingInitialTargets)
                    NetworkManager.Instance.Host.SendPacket(target, packet, reliable: true);
                _pendingInitialTargets.Clear();
            }
        }

        /// <summary>
        /// Sends the host world to a newly joined or reconnecting client. If the
        /// host is in the middle of a floor transition the send is queued until
        /// the new floor is stable.
        /// </summary>
        public void SendWorldStateTo(ulong targetId)
        {
            if (!NetworkManager.Instance.IsHost || NetworkManager.Instance.Host == null) return;
            WorldStatePacket packet;
            bool stable = TryBuildHostWorldState(out packet) &&
                          _lastWorldState == $"{packet.IsFoyer}|{packet.FloorIndex}|{packet.LevelName}|{packet.RunSeed}";
            if (stable)
            {
                NetworkManager.Instance.Host.SendPacket(targetId, packet, reliable: true);
                UnityEngine.Debug.Log($"[WorldSync] Sent initial world state to {targetId}: isFoyer={packet.IsFoyer}, level={packet.LevelName}");
            }
            else
            {
                _pendingInitialTargets.Add(targetId);
                UnityEngine.Debug.Log($"[WorldSync] Host world is still loading; queued initial world state for {targetId}.");
            }
        }

        private bool TryBuildHostWorldState(out WorldStatePacket packet)
        {
            packet = null;
            GameManager gm = GameManager.Instance;
            if (gm == null || ETGReflectionHelper.IsLevelLoading()) return false;

            PlayerController player = gm.PrimaryPlayer;
            if (player == null) return false;

            bool isFoyer = ETGReflectionHelper.IsInFoyer();
            int floorIndex = ETGReflectionHelper.GetCurrentFloorIndex();
            string levelName = ETGReflectionHelper.GetCurrentLevelName();
            if (!isFoyer)
            {
                // Secret and modded floors live in customFloors and report
                // index -1; the scene name is what identifies them.
                if (string.IsNullOrEmpty(levelName)) return false;
                // Until the dungeon has placed the player in a room, the
                // transform still holds the previous floor's coordinates.
                if (player.CurrentRoom == null || gm.Dungeon == null || gm.Dungeon.data == null) return false;
            }

            Vector3 pos = player.transform.position;
            packet = new WorldStatePacket
            {
                IsFoyer = isFoyer,
                FloorIndex = floorIndex,
                RunSeed = ETGReflectionHelper.GetCurrentRunSeed(),
                LevelName = levelName,
                RoomIdentifier = ETGReflectionHelper.GetCurrentRoomIdentifier(),
                Position = new Vector2(pos.x, pos.y),
                Rotation = player.transform.eulerAngles.z,
                LayoutHash = isFoyer ? 0 : GetLayoutHash()
            };
            return true;
        }

        // ------------------------------------------------------------------
        // Client
        // ------------------------------------------------------------------

        // Client applies the world state when packet arrives (handled in NetworkManager)
        public void ApplyWorldState(WorldStatePacket packet)
        {
            if (packet == null || (!packet.IsFoyer && string.IsNullOrEmpty(packet.LevelName)))
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
            bool isInitialState = !_hasAppliedInitialWorldState;
            bool sameWorld = IsSameWorld(packet);
            if (!isInitialState && sameWorld)
            {
                // Normal movement and room traversal are driven by player input
                // and entity snapshots, never by hard teleports.
                if (!_clientWorldReady)
                    CompletePlacement(packet.Position, packet.Rotation);
                return;
            }

            _hasAppliedInitialWorldState = true;
            _clientWorldReady = false;

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
                    CompletePlacement(packet.Position, packet.Rotation);
                }
                return;
            }

            bool mustRegenerateDungeon = isInitialState || !sameWorld;
            if (mustRegenerateDungeon)
            {
                // A level name is not enough to identify a Gungeon floor: two
                // machines can be in tt5 at the same time with completely
                // different generated room layouts. Always regenerate from the
                // host seed before using any host coordinates.
                if (packet.RunSeed == 0)
                    UnityEngine.Debug.LogWarning("[WorldSync] Host sent run seed 0; the host is probably running an older build.");

                // Loading a floor straight from the Breach skips the elevator
                // ride that normally clears the foyer state. Without this the
                // client stays flagged as "in the foyer" inside the dungeon:
                // guns stay hidden and the world never counts as loaded.
                ETGReflectionHelper.LeaveFoyerForNetworkLoad();
                ETGReflectionHelper.SetCurrentRunSeed(packet.RunSeed);
                ETGReflectionHelper.TeleportToLevel(packet.LevelName, packet.FloorIndex);
                BeginPositionAfterLoad(packet);
            }
            else
            {
                CompletePlacement(packet.Position, packet.Rotation);
            }
        }

        private bool IsSameWorld(WorldStatePacket packet)
        {
            if (packet.IsFoyer) return ETGReflectionHelper.IsInFoyer();
            if (ETGReflectionHelper.IsInFoyer()) return false;
            if (!string.Equals(ETGReflectionHelper.GetCurrentLevelName(), packet.LevelName, StringComparison.OrdinalIgnoreCase))
                return false;
            if (ETGReflectionHelper.GetCurrentRunSeed() != packet.RunSeed) return false;
            // Custom floors report -1 on both machines; only compare real indices.
            return packet.FloorIndex < 0 || ETGReflectionHelper.GetCurrentFloorIndex() == packet.FloorIndex;
        }

        private void BeginPositionAfterLoad(WorldStatePacket packet)
        {
            if (_pendingPositionApply != null) StopCoroutine(_pendingPositionApply);
            _pendingPositionApply = StartCoroutine(SetPositionWhenLoaded(packet));
        }

        private IEnumerator SetPositionWhenLoaded(WorldStatePacket packet)
        {
            // LoadCustomLevel marks the manager as loading immediately. Give it a
            // frame to start so the old dungeon is never mistaken for the new one.
            yield return null;

            float deadline = Time.realtimeSinceStartup + 60f;
            while ((ETGReflectionHelper.IsLevelLoading() || !IsSameWorld(packet) ||
                    ETGReflectionHelper.GetPrimaryPlayer() == null) &&
                   Time.realtimeSinceStartup < deadline)
            {
                ETGReflectionHelper.ClearStaleFoyerFlag();
                yield return new WaitForSeconds(0.1f);
            }

            if (ETGReflectionHelper.IsLevelLoading() || !IsSameWorld(packet) ||
                ETGReflectionHelper.GetPrimaryPlayer() == null)
            {
                UnityEngine.Debug.LogWarning($"[WorldSync] Timed out while loading the host dungeon (level={ETGReflectionHelper.GetCurrentLevelName()}, seed={ETGReflectionHelper.GetCurrentRunSeed()}, foyer={ETGReflectionHelper.IsInFoyer()}).");
                RejectMismatchedWorld("Host katı yüklenemedi. Herkesin modlarını ve açılmış içeriklerini eşitleyin.");
                _pendingPositionApply = null;
                yield break;
            }

            yield return null;
            yield return null;

            int localHash = GetLayoutHash();
            bool layoutMatches = packet.LayoutHash == 0 || localHash == packet.LayoutHash;
            if (!layoutMatches)
            {
                UnityEngine.Debug.LogError($"[WorldSync] Map layout differs from the host (host={packet.LayoutHash}, client={localHash}). " +
                    "Same seed but different map usually means different mods, mod versions or unlocked content.");
                RejectMismatchedWorld("Harita host ile aynı değil. Mod sürümlerini ve açılmış içerikleri eşitleyin.");
                _pendingPositionApply = null;
                yield break;
            }

            // The scene can report that loading has finished before the target
            // room and its collision data have been registered. Keep trying
            // instead of leaving the client permanently stuck in a black view.
            float placementDeadline = Time.realtimeSinceStartup + 15f;
            while (!CompletePlacement(packet.Position, packet.Rotation) &&
                   Time.realtimeSinceStartup < placementDeadline)
                yield return new WaitForSeconds(0.2f);

            if (!_clientWorldReady)
            {
                UnityEngine.Debug.LogWarning("[WorldSync] Host dungeon loaded, but its room never became ready for client placement.");
                FallBackToLocalSpawn("Could not reach the host's position; staying at the floor entrance.");
            }
            _pendingPositionApply = null;
        }

        private IEnumerator SetPositionAfterDelay(Vector2 pos, float rot, float delay)
        {
            yield return new WaitForSeconds(delay);
            float deadline = Time.realtimeSinceStartup + 15f;
            while (!CompletePlacement(pos, rot) && Time.realtimeSinceStartup < deadline)
                yield return new WaitForSeconds(0.2f);

            if (!_clientWorldReady)
            {
                UnityEngine.Debug.LogWarning("[WorldSync] Foyer loaded, but client placement never became ready.");
                FallBackToLocalSpawn("Could not reach the host in the Breach.");
            }
            _pendingPositionApply = null;
        }

        private bool CompletePlacement(Vector2 pos, float rot)
        {
            if (!ETGReflectionHelper.TeleportToPosition(pos, rot))
            {
                return false;
            }

            _clientWorldReady = true;
            UnityEngine.Debug.Log($"[WorldSync] Client world ready at {pos}.");
            return true;
        }

        // Keeping the client "not ready" forever hides every remote player and
        // defers every world packet, which is what produced the endless black
        // screen. Stay where the game placed us and keep playing instead.
        private void FallBackToLocalSpawn(string reason)
        {
            ETGReflectionHelper.ClearStaleFoyerFlag();
            ETGReflectionHelper.RefreshLocalCamera();
            _clientWorldReady = true;
            ShowWarning(reason);
        }

        private void RejectMismatchedWorld(string reason)
        {
            ShowWarning(reason + " Lobi bağlantısı kapatılıyor.");
            StartCoroutine(DisconnectMismatchedClient());
        }

        private IEnumerator DisconnectMismatchedClient()
        {
            yield return new WaitForSeconds(1.5f);
            if (NetworkManager.Instance.Client != null) NetworkManager.Instance.Client.Disconnect();
            GungeonTogether.Networking.Steam.SteamLobbyManager.Instance.LeaveLobby();
            NetworkManager.Instance.Shutdown();
            ETGReflectionHelper.TeleportToFoyer();
        }

        // ------------------------------------------------------------------
        // Layout fingerprint
        // ------------------------------------------------------------------

        private int GetLayoutHash()
        {
            GameManager gm = GameManager.Instance;
            Dungeon dungeon = gm != null ? gm.Dungeon : null;
            if (dungeon == null || dungeon.data == null || dungeon.data.rooms == null) return 0;
            if (dungeon == _hashedDungeon && _cachedLayoutHash != 0) return _cachedLayoutHash;

            List<string> rooms = new List<string>();
            foreach (RoomHandler room in dungeon.data.rooms)
            {
                if (room == null || room.area == null) continue;
                rooms.Add(room.area.basePosition.x + "," + room.area.basePosition.y + ":" +
                          room.area.dimensions.x + "x" + room.area.dimensions.y);
            }
            rooms.Sort(StringComparer.Ordinal);

            // FNV-1a: stable across machines, unlike string.GetHashCode.
            uint hash = 2166136261;
            foreach (string room in rooms)
            {
                for (int i = 0; i < room.Length; i++)
                {
                    hash ^= room[i];
                    hash *= 16777619;
                }
                hash ^= '|';
                hash *= 16777619;
            }
            int result = (int)hash;
            if (result == 0) result = 1;
            _hashedDungeon = dungeon;
            _cachedLayoutHash = result;
            return result;
        }

        // ------------------------------------------------------------------
        // Diagnostics
        // ------------------------------------------------------------------

        public void ShowWarning(string message)
        {
            _warning = "Gungeon Together: " + message;
            _warningUntil = Time.realtimeSinceStartup + 12f;
        }

        private void OnGUI()
        {
            if (string.IsNullOrEmpty(_warning) || Time.realtimeSinceStartup > _warningUntil) return;
            GUIStyle style = new GUIStyle(GUI.skin.box);
            style.fontSize = 18;
            style.wordWrap = true;
            style.normal.textColor = new Color(1f, 0.85f, 0.3f);
            float width = Mathf.Min(900f, Screen.width - 40f);
            GUI.Box(new Rect((Screen.width - width) / 2f, 20f, width, 60f), _warning, style);
        }

        public void ResetSession()
        {
            _lastWorldState = "";
            _candidateWorldState = "";
            _candidateSince = 0f;
            _hasAppliedInitialWorldState = false;
            _clientWorldReady = false;
            _pendingInitialTargets.Clear();
            _needsFreshSeed = true;
            _hashedDungeon = null;
            _cachedLayoutHash = 0;
            if (_pendingPositionApply != null)
            {
                StopCoroutine(_pendingPositionApply);
                _pendingPositionApply = null;
            }
        }
    }
}

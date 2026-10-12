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
    /// <summary>
    /// Replicates the ordinary co-op actions that change a generated floor:
    /// interacting with chests/doors/shops/shrines and breaking props. Clients
    /// request the action; the host executes it and relays the result.
    /// </summary>
    public class WorldEventSyncManager : MonoBehaviour
    {
        private static WorldEventSyncManager _instance;
        public static WorldEventSyncManager Instance
        {
            get
            {
                if (_instance == null)
                {
                    GameObject go = new GameObject("WorldEventSyncManager");
                    _instance = go.AddComponent<WorldEventSyncManager>();
                    DontDestroyOnLoad(go);
                }
                return _instance;
            }
        }

        private bool _applyingNetworkEvent;
        private string _historyWorld = "";
        private readonly List<InteractableUsedPacket> _interactionHistory = new List<InteractableUsedPacket>();
        private readonly List<BreakableStatePacket> _breakHistory = new List<BreakableStatePacket>();
        private readonly HashSet<string> _breakKeys = new HashSet<string>();

        private sealed class BreakState
        {
            public string ObjectType;
            public Vector2 Position;
            public bool BroadcastAfter;
        }

        private void Awake()
        {
            InstallPatches();
        }

        private void Update()
        {
            if (NetworkManager.Instance.IsHost) EnsureHistoryWorld();
        }

        private static bool IsReady()
        {
            if (NetworkManager.Instance.IsHost) return NetworkManager.Instance.Host != null;
            return NetworkManager.Instance.IsClient && NetworkManager.Instance.Client != null &&
                   NetworkManager.Instance.Client.IsConnected && WorldSyncManager.Instance.IsClientWorldReady;
        }

        private void InstallPatches()
        {
            Harmony harmony = new Harmony("com.llamerrr.gungeontogether.world-events");
            HarmonyMethod interactPrefix = new HarmonyMethod(typeof(WorldEventSyncManager).GetMethod("InteractPrefix", BindingFlags.NonPublic | BindingFlags.Static));
            HarmonyMethod interactPostfix = new HarmonyMethod(typeof(WorldEventSyncManager).GetMethod("InteractPostfix", BindingFlags.NonPublic | BindingFlags.Static));
            var patched = new HashSet<MethodBase>();

            foreach (Type type in typeof(IPlayerInteractable).Assembly.GetTypes())
            {
                if (type.IsAbstract || !typeof(IPlayerInteractable).IsAssignableFrom(type)) continue;
                MethodInfo method = AccessTools.Method(type, "Interact", new[] { typeof(PlayerController) });
                if (method == null || method.IsAbstract || !patched.Add(method)) continue;
                try { harmony.Patch(method, interactPrefix, interactPostfix); }
                catch (Exception e) { Debug.LogWarning("[WorldEvents] Could not patch " + type.FullName + ".Interact: " + e.Message); }
            }

            PatchBreakMethods(harmony, typeof(MajorBreakable));
            PatchBreakMethods(harmony, typeof(MinorBreakable));
            Debug.Log("[WorldEvents] Interaction and breakable synchronization installed.");
        }

        private static void PatchBreakMethods(Harmony harmony, Type type)
        {
            HarmonyMethod prefix = new HarmonyMethod(typeof(WorldEventSyncManager).GetMethod("BreakPrefix", BindingFlags.NonPublic | BindingFlags.Static));
            HarmonyMethod postfix = new HarmonyMethod(typeof(WorldEventSyncManager).GetMethod("BreakPostfix", BindingFlags.NonPublic | BindingFlags.Static));
            foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (method.Name != "Break" || method.IsAbstract) continue;
                try { harmony.Patch(method, prefix, postfix); }
                catch (Exception e) { Debug.LogWarning("[WorldEvents] Could not patch " + type.Name + ".Break: " + e.Message); }
            }
        }

        private static bool InteractPrefix(object __instance, PlayerController __0)
        {
            if (_instance == null || _instance._applyingNetworkEvent || !IsReady()) return true;
            if (__0 == null || PlayerManager.Instance.IsRemotePlayer(__0) || __0 != GameManager.Instance.PrimaryPlayer) return true;
            Component component = __instance as Component;
            if (component == null || __instance is PickupObject) return true;

            if (NetworkManager.Instance.IsClient)
            {
                NetworkManager.Instance.Client.SendPacket(0, CreateInteractionPacket(__instance, component.transform.position), reliable: true);
                return false;
            }
            return true;
        }

        private static void InteractPostfix(object __instance, PlayerController __0)
        {
            if (_instance == null || _instance._applyingNetworkEvent || !IsReady() || !NetworkManager.Instance.IsHost) return;
            if (__0 == null || __0 != GameManager.Instance.PrimaryPlayer) return;
            Component component = __instance as Component;
            if (component == null || __instance is PickupObject) return;

            InteractableUsedPacket packet = CreateInteractionPacket(__instance, component.transform.position);
            _instance.RecordInteraction(packet);
            GameplaySyncManager.Instance.SynchronizeSharedEconomyFrom(packet.PlayerId);
            NetworkManager.Instance.Host.Broadcast(packet, reliable: true);
        }

        private static InteractableUsedPacket CreateInteractionPacket(object target, Vector2 position)
        {
            return new InteractableUsedPacket
            {
                PlayerId = SteamReflectionHelper.GetLocalSteamId(),
                ObjectType = target.GetType().FullName ?? target.GetType().Name,
                Position = position
            };
        }

        private static bool BreakPrefix(object __instance, ref BreakState __state)
        {
            __state = null;
            if (_instance == null || _instance._applyingNetworkEvent || !IsReady()) return true;
            Component component = __instance as Component;
            if (component == null) return true;

            var packet = new BreakableStatePacket
            {
                ObjectType = __instance.GetType().FullName ?? __instance.GetType().Name,
                Position = component.transform.position
            };
            if (NetworkManager.Instance.IsClient)
            {
                NetworkManager.Instance.Client.SendPacket(0, packet, reliable: true);
                return false;
            }

            __state = new BreakState { ObjectType = packet.ObjectType, Position = packet.Position, BroadcastAfter = true };
            return true;
        }

        private static void BreakPostfix(BreakState __state)
        {
            if (_instance == null || __state == null || !__state.BroadcastAfter || _instance._applyingNetworkEvent ||
                !NetworkManager.Instance.IsHost || NetworkManager.Instance.Host == null) return;
            var packet = new BreakableStatePacket { ObjectType = __state.ObjectType, Position = __state.Position };
            if (_instance.RecordBreak(packet))
                NetworkManager.Instance.Host.Broadcast(packet, reliable: true);
        }

        public void ApplyInteraction(InteractableUsedPacket packet)
        {
            if (packet == null) return;
            IPlayerInteractable target = FindInteractable(packet.ObjectType, packet.Position);
            PlayerController player = packet.PlayerId == SteamReflectionHelper.GetLocalSteamId()
                ? GameManager.Instance.PrimaryPlayer
                : PlayerManager.Instance.GetRemotePlayerController(packet.PlayerId);
            if (target == null || player == null)
            {
                Debug.LogWarning("[WorldEvents] Could not resolve interaction " + packet.ObjectType + " at " + packet.Position);
                return;
            }

            try
            {
                _applyingNetworkEvent = true;
                target.Interact(player);
            }
            catch (Exception e) { Debug.LogWarning("[WorldEvents] Interaction failed: " + e.Message); }
            finally { _applyingNetworkEvent = false; }
        }

        public void ApplyBreak(BreakableStatePacket packet)
        {
            if (packet == null) return;
            Component target = FindComponent(packet.ObjectType, packet.Position, 1.5f);
            if (target == null) return;
            MethodInfo method = FindBreakMethod(target.GetType());
            if (method == null) return;

            try
            {
                _applyingNetworkEvent = true;
                ParameterInfo[] parameters = method.GetParameters();
                object[] args = new object[parameters.Length];
                for (int i = 0; i < parameters.Length; i++)
                {
                    Type t = parameters[i].ParameterType;
                    args[i] = t == typeof(Vector2) ? (object)Vector2.zero :
                              t == typeof(Vector3) ? (object)Vector3.zero :
                              t.IsValueType ? Activator.CreateInstance(t) : null;
                }
                method.Invoke(target, args);
            }
            catch (Exception e) { Debug.LogWarning("[WorldEvents] Breakable apply failed: " + e.Message); }
            finally { _applyingNetworkEvent = false; }
        }

        public void HandleHostInteraction(InteractableUsedPacket packet)
        {
            ApplyInteraction(packet);
            RecordInteraction(packet);
        }

        public void HandleHostBreak(BreakableStatePacket packet)
        {
            ApplyBreak(packet);
            RecordBreak(packet);
        }

        public void SendSnapshotTo(ulong targetId)
        {
            EnsureHistoryWorld();
            foreach (InteractableUsedPacket packet in _interactionHistory)
                NetworkManager.Instance.Host.SendPacket(targetId, packet, reliable: true);
            foreach (BreakableStatePacket packet in _breakHistory)
                NetworkManager.Instance.Host.SendPacket(targetId, packet, reliable: true);
        }

        public void ResetSession()
        {
            _historyWorld = "";
            _interactionHistory.Clear();
            _breakHistory.Clear();
            _breakKeys.Clear();
        }

        private void RecordInteraction(InteractableUsedPacket packet)
        {
            EnsureHistoryWorld();
            if (IsPersistentInteraction(packet.ObjectType)) _interactionHistory.Add(packet);
        }

        private bool RecordBreak(BreakableStatePacket packet)
        {
            EnsureHistoryWorld();
            string key = EventKey(packet.ObjectType, packet.Position);
            if (!_breakKeys.Add(key)) return false;
            _breakHistory.Add(packet);
            return true;
        }

        private void EnsureHistoryWorld()
        {
            string world = ETGReflectionHelper.IsInFoyer() ? "foyer" :
                ETGReflectionHelper.GetCurrentLevelName() + "|" + ETGReflectionHelper.GetCurrentRunSeed();
            if (world == _historyWorld) return;
            _historyWorld = world;
            _interactionHistory.Clear();
            _breakHistory.Clear();
            _breakKeys.Clear();
        }

        private static bool IsPersistentInteraction(string type)
        {
            if (string.IsNullOrEmpty(type)) return false;
            return type.IndexOf("Chest", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   type.IndexOf("Door", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   type.IndexOf("Shop", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   type.IndexOf("Shrine", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   type.IndexOf("Elevator", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   type.IndexOf("Sell", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   type.IndexOf("Lock", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static IPlayerInteractable FindInteractable(string typeName, Vector2 position)
        {
            Component component = FindComponent(typeName, position, 3f);
            return component as IPlayerInteractable;
        }

        private static Component FindComponent(string typeName, Vector2 position, float radius)
        {
            Component best = null;
            float bestDistance = radius;
            foreach (MonoBehaviour component in UnityEngine.Object.FindObjectsOfType<MonoBehaviour>())
            {
                if (component == null) continue;
                Type type = component.GetType();
                if (!string.Equals(type.FullName, typeName, StringComparison.Ordinal) &&
                    !string.Equals(type.Name, typeName, StringComparison.Ordinal)) continue;
                float distance = Vector2.Distance(component.transform.position, position);
                if (distance < bestDistance) { best = component; bestDistance = distance; }
            }
            return best;
        }

        private static MethodInfo FindBreakMethod(Type type)
        {
            foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                if (method.Name == "Break" && !method.IsAbstract) return method;
            return null;
        }

        private static string EventKey(string type, Vector2 position)
        {
            return type + "@" + Mathf.RoundToInt(position.x * 4f) + "," + Mathf.RoundToInt(position.y * 4f);
        }
    }
}

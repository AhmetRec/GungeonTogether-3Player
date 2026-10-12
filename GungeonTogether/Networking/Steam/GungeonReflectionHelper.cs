using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using GungeonTogether.Systems.Logging;

namespace GungeonTogether.Networking
{
    public static class ETGReflectionHelper
    {
        private static Assembly _gameAssembly;
        private static Type _gameManagerType;
        private static Type _roomHandlerType;
        private static Type _enemyControllerType;

        // GameManager
        private static PropertyInfo _gameManagerInstanceProperty;
        private static PropertyInfo _currentFloorProperty;
        private static PropertyInfo _currentRoomHandlerProperty;
        private static PropertyInfo _isFoyerProperty;
        private static MethodInfo _goToFoyerMethod;
        private static MethodInfo _loadFloorMethod;
        private static PropertyInfo _primaryPlayerProperty;
        private static PropertyInfo _currentRunSeedProperty;
        private static MethodInfo _getLastLoadedLevelDefinitionMethod;
        private static MethodInfo _loadCustomLevelMethod;
        private static FieldInfo _customFloorsField;

        // RoomHandler
        private static FieldInfo _activeEnemiesField;

        // EnemyController
        private static PropertyInfo _healthProperty;
        private static FieldInfo _aiStateField;
        private static FieldInfo _isDeadField;

        //player
        private static PropertyInfo _playerHealthProperty;
        private static PropertyInfo _maxHealthProperty;
        private static PropertyInfo _armorProperty;
        private static PropertyInfo _maxArmorProperty;
        private static FieldInfo _currentGunField;
        private static FieldInfo _gunInventoryField;
        private static PropertyInfo _currentAmmoProperty;
        private static PropertyInfo _maxAmmoProperty;
        private static PropertyInfo _activeItemProperty;

        private static bool _initialised;

        public static void Initialise()
        {
            if (_initialised) return;

            try
            {
                // ETG's game assembly is "Assembly-CSharp"
                _gameAssembly = Assembly.Load("Assembly-CSharp");
                if (_gameAssembly == null)
                {
                    UnityEngine.Debug.LogError("[ETGReflection] Could not load Assembly-CSharp");
                    return;
                }

                _gameManagerType = _gameAssembly.GetType("GameManager");
                _roomHandlerType = _gameAssembly.GetType("RoomHandler");
                _enemyControllerType = _gameAssembly.GetType("AIActor");

                if (_gameManagerType != null)
                {
                    // Singleton instance
                    _gameManagerInstanceProperty = _gameManagerType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
                    if (_gameManagerInstanceProperty == null)
                    {
                        var method = _gameManagerType.GetMethod("GetInstance", BindingFlags.Public | BindingFlags.Static);
                        if (method != null)
                            _gameManagerInstanceProperty = _gameManagerType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static); // not needed
                        // We'll handle fallback in GetGameManager()
                    }

                    _currentFloorProperty = _gameManagerType.GetProperty("CurrentFloor", BindingFlags.Public | BindingFlags.Instance);
                    _currentRoomHandlerProperty = _gameManagerType.GetProperty("CurrentRoomHandler", BindingFlags.Public | BindingFlags.Instance);
                    _isFoyerProperty = _gameManagerType.GetProperty("IsFoyer", BindingFlags.Public | BindingFlags.Instance);
                    _goToFoyerMethod = _gameManagerType.GetMethod("GoToFoyer", BindingFlags.Public | BindingFlags.Instance);
                    _loadFloorMethod = _gameManagerType.GetMethod("LoadLevelByIndex", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(int) }, null);
                    _primaryPlayerProperty = _gameManagerType.GetProperty("PrimaryPlayer", BindingFlags.Public | BindingFlags.Instance);
                    _currentRunSeedProperty = _gameManagerType.GetProperty("CurrentRunSeed", BindingFlags.Public | BindingFlags.Instance);
                    _getLastLoadedLevelDefinitionMethod = _gameManagerType.GetMethod("GetLastLoadedLevelDefinition", BindingFlags.Public | BindingFlags.Instance);
                    _loadCustomLevelMethod = _gameManagerType.GetMethod("LoadCustomLevel", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(string) }, null);
                    _customFloorsField = _gameManagerType.GetField("customFloors", BindingFlags.Public | BindingFlags.Instance);
                }

                if (_roomHandlerType != null)
                {
                    _activeEnemiesField = _roomHandlerType.GetField("activeEnemies", BindingFlags.NonPublic | BindingFlags.Instance);
                }

                if (_enemyControllerType != null)
                {
                    _healthProperty = _enemyControllerType.GetProperty("Health", BindingFlags.Public | BindingFlags.Instance);
                    _aiStateField = _enemyControllerType.GetField("_aiState", BindingFlags.NonPublic | BindingFlags.Instance);
                    if (_aiStateField == null)
                        _aiStateField = _enemyControllerType.GetField("m_aiState", BindingFlags.NonPublic | BindingFlags.Instance);
                    _isDeadField = _enemyControllerType.GetField("IsDead", BindingFlags.Public | BindingFlags.Instance);
                    if (_isDeadField == null)
                        _isDeadField = _enemyControllerType.GetField("_dead", BindingFlags.NonPublic | BindingFlags.Instance);
                }

                var playerControllerType = _gameAssembly.GetType("PlayerController");
                if (playerControllerType != null)
                {
                    _playerHealthProperty = playerControllerType.GetProperty("Health", BindingFlags.Public | BindingFlags.Instance);
                    _maxHealthProperty = playerControllerType.GetProperty("MaxHealth", BindingFlags.Public | BindingFlags.Instance);
                    _armorProperty = playerControllerType.GetProperty("Armor", BindingFlags.Public | BindingFlags.Instance);
                    _maxArmorProperty = playerControllerType.GetProperty("MaxArmor", BindingFlags.Public | BindingFlags.Instance);
                    _currentGunField = playerControllerType.GetField("CurrentGun", BindingFlags.Public | BindingFlags.Instance);
                    _gunInventoryField = playerControllerType.GetField("GunInventory", BindingFlags.Public | BindingFlags.Instance);
                    _currentAmmoProperty = playerControllerType.GetProperty("CurrentAmmo", BindingFlags.Public | BindingFlags.Instance);
                    _maxAmmoProperty = playerControllerType.GetProperty("MaxAmmo", BindingFlags.Public | BindingFlags.Instance);
                    _activeItemProperty = playerControllerType.GetProperty("ActiveItem", BindingFlags.Public | BindingFlags.Instance);
                }

                _initialised = true;
                UnityEngine.Debug.Log("[ETGReflection] Initialized successfully.");
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogError($"[ETGReflection] Initialization failed: {e.Message}");
                UnityEngine.Debug.LogError(e.StackTrace);
            }
        }

        // ---- GameManager helpers ----

        public static object GetGameManager()
        {
            if (_gameManagerType == null) return null;
            // Usually GameManager has a static Instance property
            var instanceProp = _gameManagerType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
            if (instanceProp != null) return instanceProp.GetValue(null, null);
            // Fallback: FindObjectOfType
            var method = _gameManagerType.GetMethod("FindObjectOfType", new[] { typeof(Type) });
            if (method != null) return method.Invoke(null, new object[] { _gameManagerType });
            return null;
        }

        public static bool IsInFoyer()
        {
            var gm = GetGameManager();
            if (gm == null) return false;
            if (_isFoyerProperty != null)
                return (bool)_isFoyerProperty.GetValue(gm, null);
            // Fallback: check scene name
            return UnityEngine.SceneManagement.SceneManager.GetActiveScene().name == "Foyer";
        }

        public static int GetCurrentFloorIndex()
        {
            var gm = GetGameManager();
            if (gm == null) return -1;
            if (_currentFloorProperty != null)
            {
                var floor = _currentFloorProperty.GetValue(gm, null);
                if (floor != null)
                {
                    if (floor is int) return (int)floor;
                    var idxProp = floor.GetType().GetProperty("Index", BindingFlags.Public | BindingFlags.Instance);
                    if (idxProp != null) return (int)idxProp.GetValue(floor, null);
                    // Try name
                    var nameProp = floor.GetType().GetProperty("Name", BindingFlags.Public | BindingFlags.Instance);
                    if (nameProp != null) return nameProp.GetValue(floor, null).GetHashCode();
                }
            }
            return -1;
        }

        public static string GetCurrentRoomIdentifier()
        {
            var room = GetCurrentRoomHandler();
            if (room != null)
            {
                var handler = room as Dungeonator.RoomHandler;
                if (handler != null)
                    return handler.GetRoomName() + "@" + handler.area.basePosition.x + "," + handler.area.basePosition.y;

                var getName = room.GetType().GetMethod("GetRoomName", BindingFlags.Public | BindingFlags.Instance);
                if (getName != null)
                {
                    var value = getName.Invoke(room, null);
                    if (value != null) return value.ToString();
                }
                return room.GetHashCode().ToString();
            }
            return "";
        }

        public static int GetCurrentRunSeed()
        {
            var gm = GetGameManager();
            if (gm == null || _currentRunSeedProperty == null) return 0;
            object value = _currentRunSeedProperty.GetValue(gm, null);
            return value != null ? Convert.ToInt32(value) : 0;
        }

        public static void SetCurrentRunSeed(int seed)
        {
            var gm = GetGameManager();
            if (gm == null || _currentRunSeedProperty == null || !_currentRunSeedProperty.CanWrite) return;
            _currentRunSeedProperty.SetValue(gm, seed, null);
        }

        public static string GetCurrentLevelName()
        {
            var gm = GetGameManager();
            if (gm != null && _getLastLoadedLevelDefinitionMethod != null)
            {
                object definition = _getLastLoadedLevelDefinitionMethod.Invoke(gm, null);
                if (definition != null)
                {
                    FieldInfo nameField = definition.GetType().GetField("dungeonSceneName", BindingFlags.Public | BindingFlags.Instance);
                    object value = nameField != null ? nameField.GetValue(definition) : null;
                    if (value != null) return value.ToString();
                }
            }
            return UnityEngine.SceneManagement.SceneManager.GetActiveScene().name ?? "";
        }

        public static void TeleportToLevel(string levelName, int floorIndex)
        {
            var gm = GetGameManager();
            bool isCustom = false;
            if (gm != null && _customFloorsField != null && !string.IsNullOrEmpty(levelName))
            {
                var floors = _customFloorsField.GetValue(gm) as System.Collections.IEnumerable;
                if (floors != null)
                {
                    foreach (object definition in floors)
                    {
                        if (definition == null) continue;
                        FieldInfo nameField = definition.GetType().GetField("dungeonSceneName", BindingFlags.Public | BindingFlags.Instance);
                        string name = nameField != null ? nameField.GetValue(definition) as string : null;
                        if (string.Equals(name, levelName, StringComparison.OrdinalIgnoreCase))
                        {
                            isCustom = true;
                            break;
                        }
                    }
                }
            }

            if (isCustom && _loadCustomLevelMethod != null)
                _loadCustomLevelMethod.Invoke(gm, new object[] { levelName });
            else
                TeleportToFloor(floorIndex);
        }

        public static Vector3 GetPlayerPosition()
        {
            var gm = GetGameManager();
            if (gm == null) return Vector3.zero;
            if (_primaryPlayerProperty != null)
            {
                var player = _primaryPlayerProperty.GetValue(gm, null);
                if (player != null)
                {
                    var transformProp = player.GetType().GetProperty("transform");
                    if (transformProp != null)
                    {
                        var t = transformProp.GetValue(player, null) as Transform;
                        if (t != null) return t.position;
                    }
                }
            }
            return Vector3.zero;
        }

        public static float GetPlayerRotation()
        {
            var gm = GetGameManager();
            if (gm == null) return 0f;
            if (_primaryPlayerProperty != null)
            {
                var player = _primaryPlayerProperty.GetValue(gm, null);
                if (player != null)
                {
                    var transformProp = player.GetType().GetProperty("transform");
                    if (transformProp != null)
                    {
                        var t = transformProp.GetValue(player, null) as Transform;
                        if (t != null) return t.eulerAngles.z;
                    }
                }
            }
            return 0f;
        }

        public static void TeleportToFoyer()
        {
            var gm = GetGameManager();
            if (gm == null || _goToFoyerMethod == null) return;
            _goToFoyerMethod.Invoke(gm, null);
        }

        public static void TeleportToFloor(int floorIndex)
        {
            var gm = GetGameManager();
            if (gm == null || _loadFloorMethod == null) return;
            _loadFloorMethod.Invoke(gm, new object[] { floorIndex });
        }

        public static void TeleportToPosition(Vector2 position, float rotation)
        {
            var gm = GetGameManager();
            if (gm == null) return;
            if (_primaryPlayerProperty != null)
            {
                var player = _primaryPlayerProperty.GetValue(gm, null);
                if (player != null)
                {
                    var transformProp = player.GetType().GetProperty("transform");
                    if (transformProp != null)
                    {
                        var t = transformProp.GetValue(player, null) as Transform;
                        if (t != null)
                        {
                            t.position = new Vector3(position.x, position.y, 0f);
                            t.rotation = Quaternion.Euler(0f, 0f, rotation);
                        }
                    }
                }
            }
        }

        // ---- Enemies ----

        public static object GetCurrentRoomHandler()
        {
            object player = GetPrimaryPlayer();
            if (player != null)
            {
                PropertyInfo roomProperty = player.GetType().GetProperty("CurrentRoom", BindingFlags.Public | BindingFlags.Instance);
                if (roomProperty != null) return roomProperty.GetValue(player, null);
            }

            var gm = GetGameManager();
            if (gm == null || _currentRoomHandlerProperty == null) return null;
            return _currentRoomHandlerProperty.GetValue(gm, null);
        }

        public static List<object> GetActiveEnemies()
        {
            if (!_initialised || _activeEnemiesField == null) return new List<object>();
            var room = GetCurrentRoomHandler();
            if (room == null) return new List<object>();
            var result = new List<object>();
            var enemies = _activeEnemiesField.GetValue(room) as System.Collections.IEnumerable;
            if (enemies == null) return result;
            foreach (object enemy in enemies)
                if (enemy != null) result.Add(enemy);
            return result;
        }

        public static float GetEnemyHealth(object enemy)
        {
            var actor = enemy as AIActor;
            return actor != null && actor.healthHaver != null ? actor.healthHaver.GetCurrentHealth() : 0f;
        }

        public static int GetEnemyAIState(object enemy)
        {
            var actor = enemy as AIActor;
            return actor != null ? (int)actor.State : 0;
        }

        public static bool IsEnemyDead(object enemy)
        {
            var actor = enemy as AIActor;
            return actor != null && actor.healthHaver != null && actor.healthHaver.IsDead;
        }

        public static Vector3 GetEnemyPosition(object enemy)
        {
            if (enemy == null) return Vector3.zero;
            var transformProp = enemy.GetType().GetProperty("transform");
            if (transformProp == null) return Vector3.zero;
            Transform t = transformProp.GetValue(enemy, null) as Transform;
            return t != null ? t.position : Vector3.zero;
        }

        public static float GetEnemyRotation(object enemy)
        {
            if (enemy == null) return 0f;
            var transformProp = enemy.GetType().GetProperty("transform");
            if (transformProp == null) return 0f;
            Transform t = transformProp.GetValue(enemy, null) as Transform;
            return t != null ? t.eulerAngles.z : 0f;
        }
        public static object GetPrimaryPlayer()
        {
            var gm = GetGameManager();
            if (gm == null) return null;
            if (_primaryPlayerProperty != null) return _primaryPlayerProperty.GetValue(gm, null);
            return null;
        }

        public static float GetPlayerHealth()
        {
            var player = GetPrimaryPlayer() as PlayerController;
            return player != null && player.healthHaver != null ? player.healthHaver.GetCurrentHealth() : 0f;
        }
        public static float GetPlayerMaxHealth()
        {
            var player = GetPrimaryPlayer() as PlayerController;
            return player != null && player.healthHaver != null ? player.healthHaver.GetMaxHealth() : 0f;
        }
        public static float GetPlayerArmor()
        {
            var player = GetPrimaryPlayer() as PlayerController;
            return player != null && player.healthHaver != null ? player.healthHaver.Armor : 0f;
        }
        public static float GetPlayerMaxArmor() => GetPlayerArmor();
        public static int GetPlayerCurrentGunIndex()
        {
            var player = GetPrimaryPlayer();
            if (player == null || _currentGunField == null) return -1;
            var gun = _currentGunField.GetValue(player);
            if (gun == null) return -1;
            // Gun might have an index; for now, we can get its position in inventory
            var inv = _gunInventoryField?.GetValue(player) as System.Collections.IList;
            if (inv != null) return inv.IndexOf(gun);
            return -1;
        }
        public static int GetPlayerAmmo() => (int)GetFloatProperty(GetPrimaryPlayer(), _currentAmmoProperty);
        public static int GetPlayerMaxAmmo() => (int)GetFloatProperty(GetPrimaryPlayer(), _maxAmmoProperty);
        // Active item ID or type name
        public static string GetActiveItemName()
        {
            var player = GetPrimaryPlayer();
            if (player == null || _activeItemProperty == null) return "";
            var item = _activeItemProperty.GetValue(player, null);
            return item?.GetType().Name ?? "";
        }

        private static float GetFloatProperty(object obj, PropertyInfo prop)
        {
            if (obj == null || prop == null) return 0f;
            return (float)prop.GetValue(obj, null);
        }
    }
}

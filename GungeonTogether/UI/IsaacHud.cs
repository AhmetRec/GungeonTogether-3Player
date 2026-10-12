using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using GungeonTogether.Networking;
using GungeonTogether.Networking.Packets;
using GungeonTogether.Networking.Steam;
using GungeonTogether.Networking.Sync;
using Debug = GungeonTogether.Systems.Logging.Debug;

namespace GungeonTogether.UI
{
    /// <summary>
    /// Isaac-Online style HUD: every player gets a panel in one corner of the
    /// screen (name, connection bars, hearts, blanks/keys/money, gun and ammo,
    /// active item and collected items). The game's own HUD only has two player
    /// slots, so while an online session is running it is hidden through the
    /// game's own HideCoreUI mechanism and this overlay takes over.
    /// F8 switches back to the game's HUD at any time.
    /// </summary>
    public class IsaacHud : MonoBehaviour
    {
        private static IsaacHud _instance;
        public static IsaacHud Instance
        {
            get
            {
                if (_instance == null)
                {
                    GameObject go = new GameObject("GungeonTogetherHud");
                    _instance = go.AddComponent<IsaacHud>();
                    DontDestroyOnLoad(go);
                }
                return _instance;
            }
        }

        private const string HideReason = "gungeon-together-hud";
        private const float RemoteForgetSeconds = 20f;
        private const int HeartsPerRow = 6;
        private const int PassivesPerRow = 10;
        private const int MaxPassiveRows = 2;

        private static readonly Color[] PlayerColors =
        {
            new Color(0.96f, 0.66f, 0.24f), // host / first
            new Color(0.35f, 0.69f, 1.00f),
            new Color(0.48f, 0.85f, 0.48f),
            new Color(0.85f, 0.42f, 0.85f)
        };

        private class Entry
        {
            public ulong Id;
            public string Name;
            public bool Local;
            public PlayerController Controller;
            public Color Color;
        }

        private class View
        {
            public float Health, MaxHealth, Armor;
            public int Blanks, Keys, Currency, Ammo, MaxAmmo;
            public bool InfiniteAmmo, Downed, HasData;
            public float Age;
            public int PingMs = -1;
            public Gun Gun;
            public PlayerItem Active;
            public List<PassiveItem> Passives;
        }

        private bool _userEnabled = true;
        private bool _nativeHidden;
        private bool _active;
        private bool _hiddenByGame;
        private float _nextRefresh;
        private float _nextSessionCheck;
        private bool _sessionWantsHud;
        private float _nextNameRefresh;
        private float _hintUntil;
        private float _nextErrorLog;
        private readonly List<Entry> _entries = new List<Entry>();
        private readonly Dictionary<ulong, string> _names = new Dictionary<ulong, string>();

        private GUIStyle _label;
        private Texture2D _white;
        private Texture2D _heartFull, _heartHalf, _heartEmpty, _shield, _blank, _key, _coin;

        private static FieldInfo _coreHiddenField;
        private static FieldInfo _overridesField;
        private static FieldInfo _baseValueField;
        private static FieldInfo _overrideKeyField;
        private static bool _reflectionTried;

        // ------------------------------------------------------------------
        // State handling
        // ------------------------------------------------------------------

        private void Update()
        {
            try
            {
                if (Input.GetKeyDown(KeyCode.F8))
                {
                    _userEnabled = !_userEnabled;
                    Debug.Log("[Hud] Isaac-style HUD " + (_userEnabled ? "enabled" : "disabled"));
                }

                if (Time.realtimeSinceStartup >= _nextSessionCheck)
                {
                    _nextSessionCheck = Time.realtimeSinceStartup + 0.25f;
                    _sessionWantsHud = ShouldBeActive();
                }
                bool want = _userEnabled && _sessionWantsHud;
                if (want != _active)
                {
                    _active = want;
                    if (want) _hintUntil = Time.realtimeSinceStartup + 12f;
                }
                SetNativeHidden(_active);

                if (_active && Time.realtimeSinceStartup >= _nextRefresh)
                {
                    _nextRefresh = Time.realtimeSinceStartup + 0.25f;
                    RefreshEntries();
                    _hiddenByGame = IsHiddenByGame();
                    if (_nativeHidden && !_hiddenByGame) RestoreReloadBars();
                }
            }
            catch (Exception e)
            {
                LogErrorThrottled("update", e);
            }
        }

        private static bool ShouldBeActive()
        {
            NetworkManager network = NetworkManager.Instance;
            if (!network.IsHost && !network.IsClient) return false;
            if (network.IsClient && (network.Client == null || !network.Client.IsConnected)) return false;
            if (network.IsHost && network.Host == null) return false;

            GameManager gm = GameManager.Instance;
            if (gm == null || gm.IsFoyer || gm.PrimaryPlayer == null) return false;

            // Only take over once somebody else is actually in the session.
            ulong localId = SteamReflectionHelper.GetLocalSteamId();
            foreach (ulong id in GameplaySyncManager.Instance.GetKnownPlayerIds())
                if (id != localId) return true;
            return false;
        }

        private void SetNativeHidden(bool hide)
        {
            if (hide == _nativeHidden) return;
            GameUIRoot root = GameUIRoot.Instance;
            if (root == null) return;
            try
            {
                if (hide) root.HideCoreUI(HideReason);
                else root.ShowCoreUI(HideReason);
                _nativeHidden = hide;
            }
            catch (Exception e)
            {
                LogErrorThrottled("native-hud", e);
                _nativeHidden = hide; // do not retry every frame
            }
        }

        // HideCoreUI also hides the reload bars next to the characters. They
        // are gameplay feedback rather than HUD, so keep them visible.
        private static FieldInfo _reloadBarsField;
        private static bool _reloadBarsTried;

        private static void RestoreReloadBars()
        {
            GameUIRoot root = GameUIRoot.Instance;
            if (root == null) return;
            try
            {
                if (!_reloadBarsTried)
                {
                    _reloadBarsTried = true;
                    _reloadBarsField = typeof(GameUIRoot).GetField("m_extantReloadBars", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                }
                List<GameUIReloadBarController> bars = _reloadBarsField != null ? _reloadBarsField.GetValue(root) as List<GameUIReloadBarController> : null;
                if (bars == null) return;
                for (int i = 0; i < bars.Count; i++)
                    if (bars[i] != null) bars[i].SetInvisibility(false, "CoreUI");
            }
            catch (Exception) { }
        }

        private void OnDestroy()
        {
            try
            {
                if (_nativeHidden && GameUIRoot.Instance != null) GameUIRoot.Instance.ShowCoreUI(HideReason);
            }
            catch (Exception) { }
        }

        // The game hides its own HUD for pauses, boss intros, cutscenes and so
        // on. Follow suit instead of drawing over them.
        private bool IsHiddenByGame()
        {
            GameManager gm = GameManager.Instance;
            if (gm == null || gm.IsPaused || gm.IsLoadingLevel) return true;
            if (Foyer.DoMainMenu) return true;

            GameUIRoot root = GameUIRoot.Instance;
            if (root == null) return true;
            try
            {
                if (!_reflectionTried)
                {
                    _reflectionTried = true;
                    _coreHiddenField = typeof(GameUIRoot).GetField("CoreUIHidden", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (_coreHiddenField != null)
                    {
                        Type type = _coreHiddenField.FieldType;
                        _overridesField = type.GetField("m_overrides", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        _baseValueField = type.GetField("BaseValue", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    }
                }
                if (_coreHiddenField == null) return false;
                object flag = _coreHiddenField.GetValue(root);
                if (flag == null) return false;
                if (_baseValueField != null && (bool)_baseValueField.GetValue(flag)) return true;
                IEnumerable overrides = _overridesField != null ? _overridesField.GetValue(flag) as IEnumerable : null;
                if (overrides == null) return false;
                foreach (object item in overrides)
                {
                    if (item == null) continue;
                    if (_overrideKeyField == null) _overrideKeyField = item.GetType().GetField("key", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    string key = _overrideKeyField != null ? _overrideKeyField.GetValue(item) as string : null;
                    if (key != HideReason) return true;
                }
            }
            catch (Exception e)
            {
                LogErrorThrottled("hidden-check", e);
            }
            return false;
        }

        // ------------------------------------------------------------------
        // Participants
        // ------------------------------------------------------------------

        private void RefreshEntries()
        {
            NetworkManager network = NetworkManager.Instance;
            ulong localId = SteamReflectionHelper.GetLocalSteamId();
            ulong hostId = network.IsHost ? localId : (network.Client != null ? network.Client.HostId : 0UL);

            List<ulong> ids = new List<ulong>();
            ids.Add(localId);
            List<ulong> others = new List<ulong>();
            foreach (ulong id in GameplaySyncManager.Instance.GetKnownPlayerIds())
            {
                if (id == localId || id == 0) continue;
                PlayerVitalsPacket vitals;
                float age;
                GameplaySyncManager.Instance.TryGetVitals(id, out vitals, out age);
                if (age > RemoteForgetSeconds && PlayerManager.Instance.GetRemotePlayerController(id) == null) continue;
                others.Add(id);
            }
            others.Sort();

            // The same player always sits in the same corner on every screen:
            // host first, then everyone else by Steam ID.
            if (hostId != 0 && hostId != localId && others.Remove(hostId)) ids.Insert(0, hostId);
            else if (hostId == localId) { /* local is the host, already first */ }
            ids.AddRange(others);

            if (Time.realtimeSinceStartup >= _nextNameRefresh)
            {
                _nextNameRefresh = Time.realtimeSinceStartup + 10f;
                _names.Clear();
            }

            _entries.Clear();
            for (int i = 0; i < ids.Count && i < 4; i++)
            {
                ulong id = ids[i];
                string name;
                if (!_names.TryGetValue(id, out name))
                {
                    try { name = SteamReflectionHelper.GetPlayerName(id); } catch (Exception) { name = null; }
                    if (string.IsNullOrEmpty(name)) name = "Oyuncu " + (i + 1);
                    _names[id] = name;
                }
                _entries.Add(new Entry
                {
                    Id = id,
                    Name = name,
                    Local = id == localId,
                    Controller = id == localId ? GameManager.Instance.PrimaryPlayer : PlayerManager.Instance.GetRemotePlayerController(id),
                    Color = PlayerColors[i % PlayerColors.Length]
                });
            }
        }

        private static View BuildView(Entry entry)
        {
            View view = new View();
            PlayerController pc = entry.Controller;

            if (entry.Local && pc != null && pc.healthHaver != null)
            {
                view.HasData = true;
                view.Health = pc.healthHaver.GetCurrentHealth();
                view.MaxHealth = pc.healthHaver.GetMaxHealth();
                view.Armor = pc.healthHaver.Armor;
                view.Blanks = pc.Blanks;
                if (pc.carriedConsumables != null)
                {
                    view.Keys = pc.carriedConsumables.KeyBullets;
                    view.Currency = pc.carriedConsumables.Currency;
                }
                view.Gun = pc.CurrentGun;
                if (view.Gun != null)
                {
                    view.Ammo = view.Gun.CurrentAmmo;
                    view.MaxAmmo = view.Gun.AdjustedMaxAmmo;
                    view.InfiniteAmmo = view.Gun.InfiniteAmmo;
                }
                view.Active = pc.CurrentItem;
                view.Passives = pc.passiveItems;
                view.Age = 0f;
                view.PingMs = NetworkManager.Instance.GetLocalPingMs();
                return view;
            }

            PlayerVitalsPacket vitals;
            float age;
            if (GameplaySyncManager.Instance.TryGetVitals(entry.Id, out vitals, out age))
            {
                view.HasData = true;
                view.Age = age;
                view.PingMs = NetworkManager.Instance.IsClient && NetworkManager.Instance.Client != null &&
                    entry.Id == NetworkManager.Instance.Client.HostId
                    ? NetworkManager.Instance.GetLocalPingMs()
                    : vitals.PingMs;
                view.Health = vitals.Health;
                view.MaxHealth = vitals.MaxHealth;
                view.Armor = vitals.Armor;
                view.Blanks = vitals.Blanks;
                view.Keys = vitals.Keys;
                view.Currency = vitals.Currency;
                view.Ammo = vitals.CurrentGunAmmo;
                view.MaxAmmo = vitals.MaxAmmo;
                view.InfiniteAmmo = vitals.InfiniteAmmo;
                view.Downed = vitals.IsDowned;
                if (!string.IsNullOrEmpty(vitals.ActiveItemKey) || vitals.ActiveItemId >= 0)
                    view.Active = ModCompatibility.ResolvePickup(vitals.ActiveItemKey, vitals.ActiveItemId) as PlayerItem;
            }
            else
            {
                view.Age = float.MaxValue;
            }
            if (pc != null)
            {
                view.Gun = pc.CurrentGun;
                view.Passives = pc.passiveItems;
            }
            return view;
        }

        // ------------------------------------------------------------------
        // Drawing
        // ------------------------------------------------------------------

        private void OnGUI()
        {
            if (Event.current == null || Event.current.type != EventType.Repaint) return;
            if (!_active || _hiddenByGame || _entries.Count == 0) return;

            try
            {
                EnsureResources();
                float s = Mathf.Clamp(Screen.height / 1080f, 0.75f, 1.6f);
                float margin = 12f * s;

                for (int i = 0; i < _entries.Count; i++)
                {
                    Entry entry = _entries[i];
                    View view = BuildView(entry);
                    float width = PanelWidth * s;
                    float height = MeasurePanel(view, s);
                    float x = (i % 2 == 0) ? margin : Screen.width - margin - width;
                    float y = (i < 2) ? margin : Screen.height - margin - height;
                    DrawPanel(new Rect(x, y, width, height), entry, view, s);
                }

                if (Time.realtimeSinceStartup < _hintUntil)
                {
                    float hintW = 360f * s;
                    Rect hint = new Rect((Screen.width - hintW) / 2f, margin, hintW, 24f * s);
                    Fill(hint, new Color(0f, 0f, 0f, 0.55f));
                    Text(hint, "F8: Isaac HUD / oyunun HUD'u", new Color(1f, 0.9f, 0.5f), Mathf.RoundToInt(15f * s), AlignCenter);
                }
            }
            catch (Exception e)
            {
                LogErrorThrottled("draw", e);
            }
        }

        private const float PanelWidth = 248f;
        private const float Pad = 8f;
        private const float NameH = 22f;
        private const float HeartSize = 20f;
        private const float HeartRowH = 23f;
        private const float ConsH = 20f;
        private const float GearH = 36f;
        private const float PassiveSize = 21f;
        private const float PassiveRowH = 23f;

        private static int HeartRows(View view)
        {
            int icons = Mathf.CeilToInt(Mathf.Max(view.MaxHealth, 0f)) + Mathf.CeilToInt(Mathf.Max(view.Armor, 0f));
            icons = Mathf.Clamp(icons, 1, HeartsPerRow * 3);
            return (icons + HeartsPerRow - 1) / HeartsPerRow;
        }

        private static int PassiveRows(View view)
        {
            int count = CountPassives(view);
            return count == 0 ? 0 : Mathf.Min(MaxPassiveRows, (count + PassivesPerRow - 1) / PassivesPerRow);
        }

        private static int CountPassives(View view)
        {
            if (view.Passives == null) return 0;
            int count = 0;
            for (int i = 0; i < view.Passives.Count; i++)
                if (view.Passives[i] != null && view.Passives[i].sprite != null) count++;
            return count;
        }

        private static float MeasurePanel(View view, float s)
        {
            float h = Pad + NameH + 4f + HeartRows(view) * HeartRowH + 2f + ConsH + 4f + GearH;
            int rows = PassiveRows(view);
            if (rows > 0) h += 6f + rows * PassiveRowH;
            return (h + Pad) * s;
        }

        private void DrawPanel(Rect panel, Entry entry, View view, float s)
        {
            bool offline = !entry.Local && (!view.HasData || view.Age > 6f);
            float dim = offline ? 0.45f : 1f;

            Fill(panel, new Color(0.04f, 0.03f, 0.05f, 0.72f * dim + 0.1f));
            Color border = entry.Color;
            border.a = entry.Local ? 1f : 0.75f * dim;
            Border(panel, border, entry.Local ? Mathf.Max(2f, 3f * s) : Mathf.Max(1f, 2f * s));

            float x0 = panel.x + Pad * s;
            float y = panel.y + Pad * s;
            float inner = panel.width - 2f * Pad * s;

            // Name + connection bars
            string title = entry.Local ? entry.Name + " (SEN)" : entry.Name;
            Text(new Rect(x0, y, inner - 44f * s, NameH * s), title, entry.Color, Mathf.RoundToInt(17f * s), AlignLeft);
            if (!entry.Local) DrawSignal(new Rect(panel.xMax - (Pad + 34f) * s, y + 3f * s, 30f * s, 16f * s), view.PingMs);
            y += (NameH + 4f) * s;

            // Hearts and armor
            if (view.Downed)
            {
                Text(new Rect(x0, y, inner, HeartRowH * s), "YERDE - canlandir!", new Color(1f, 0.35f, 0.35f), Mathf.RoundToInt(16f * s), AlignLeft);
                y += HeartRowH * s;
            }
            else
            {
                DrawHearts(x0, y, view, s);
                y += HeartRows(view) * HeartRowH * s;
            }
            y += 2f * s;

            // Blanks / keys / money
            float cx = x0;
            cx = DrawCounter(cx, y, _blank, view.Blanks, s);
            cx = DrawCounter(cx, y, _key, view.Keys, s);
            DrawCounter(cx, y, _coin, view.Currency, s);
            y += (ConsH + 4f) * s;

            // Gun + ammo, active item
            DrawGear(new Rect(x0, y, inner, GearH * s), view, s);
            y += GearH * s;

            // Collected items
            int rows = PassiveRows(view);
            if (rows > 0)
            {
                y += 6f * s;
                DrawPassives(x0, y, view, rows, s);
            }

            if (offline)
                Text(panel, "bağlantı yok", new Color(1f, 0.5f, 0.4f), Mathf.RoundToInt(16f * s), AlignCenter);
        }

        private void DrawHearts(float x0, float y0, View view, float s)
        {
            int maxHearts = Mathf.Max(1, Mathf.CeilToInt(view.MaxHealth));
            int armor = Mathf.CeilToInt(Mathf.Max(view.Armor, 0f));
            int total = Mathf.Min(maxHearts + armor, HeartsPerRow * 3);
            for (int i = 0; i < total; i++)
            {
                Texture2D icon;
                if (i < maxHearts)
                {
                    float remaining = view.Health - i;
                    icon = remaining >= 1f ? _heartFull : (remaining >= 0.5f ? _heartHalf : _heartEmpty);
                }
                else
                {
                    icon = _shield;
                }
                float x = x0 + (i % HeartsPerRow) * (HeartSize + 3f) * s;
                float y = y0 + (i / HeartsPerRow) * HeartRowH * s;
                DrawPixelIcon(icon, new Rect(x, y, HeartSize * s, HeartSize * s));
            }
        }

        private float DrawCounter(float x, float y, Texture2D icon, int value, float s)
        {
            DrawPixelIcon(icon, new Rect(x, y + 1f * s, 16f * s, 16f * s));
            Text(new Rect(x + 19f * s, y, 44f * s, ConsH * s), value.ToString(), Color.white, Mathf.RoundToInt(16f * s), AlignLeft);
            return x + 76f * s;
        }

        private void DrawGear(Rect area, View view, float s)
        {
            Rect gunBox = new Rect(area.x, area.y, 64f * s, area.height);
            Fill(gunBox, new Color(1f, 1f, 1f, 0.07f));
            DrawTk2dSprite(view.Gun != null ? view.Gun.sprite : null, gunBox);

            string ammo;
            if (view.InfiniteAmmo) ammo = "INF";
            else if (view.Ammo < 0) ammo = "-";
            else ammo = view.MaxAmmo > 0 ? view.Ammo + "/" + view.MaxAmmo : view.Ammo.ToString();
            Text(new Rect(gunBox.xMax + 6f * s, area.y, area.width - gunBox.width - 48f * s, area.height), ammo, Color.white, Mathf.RoundToInt(16f * s), AlignLeft);

            Rect itemBox = new Rect(area.xMax - area.height, area.y, area.height, area.height);
            Fill(itemBox, new Color(1f, 1f, 1f, 0.07f));
            if (view.Active != null) DrawTk2dSprite(view.Active.sprite, itemBox);
        }

        private void DrawPassives(float x0, float y0, View view, int rows, float s)
        {
            int capacity = rows * PassivesPerRow;
            int total = CountPassives(view);
            int shown = 0;
            int limit = total > capacity ? capacity - 1 : capacity; // leave room for "+N"
            for (int i = 0; i < view.Passives.Count && shown < limit; i++)
            {
                PassiveItem item = view.Passives[i];
                if (item == null || item.sprite == null) continue;
                float x = x0 + (shown % PassivesPerRow) * (PassiveSize + 1f) * s;
                float y = y0 + (shown / PassivesPerRow) * PassiveRowH * s;
                DrawTk2dSprite(item.sprite, new Rect(x, y, PassiveSize * s, PassiveSize * s));
                shown++;
            }
            if (total > shown)
            {
                float x = x0 + (shown % PassivesPerRow) * (PassiveSize + 1f) * s;
                float y = y0 + (shown / PassivesPerRow) * PassiveRowH * s;
                Text(new Rect(x, y, PassiveSize * 1.6f * s, PassiveSize * s), "+" + (total - shown), Color.white, Mathf.RoundToInt(15f * s), AlignLeft);
            }
        }

        private void DrawSignal(Rect r, int pingMs)
        {
            int bars = pingMs < 0 ? 0 : pingMs < 70 ? 4 : pingMs < 130 ? 3 : pingMs < 220 ? 2 : pingMs < 400 ? 1 : 0;
            Color color = bars >= 3 ? new Color(0.35f, 0.9f, 0.35f) : bars == 2 ? new Color(0.95f, 0.85f, 0.25f) : new Color(0.95f, 0.3f, 0.25f);
            float barW = r.width / 4f - 2f;
            for (int i = 0; i < 4; i++)
            {
                float h = r.height * (0.35f + 0.65f * (i + 1) / 4f);
                Rect bar = new Rect(r.x + i * (barW + 2f), r.yMax - h, barW, h);
                Fill(bar, i < bars ? color : new Color(1f, 1f, 1f, 0.15f));
            }
        }

        // ------------------------------------------------------------------
        // Drawing helpers
        // ------------------------------------------------------------------

        private void Fill(Rect r, Color c)
        {
            Color old = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(r, _white);
            GUI.color = old;
        }

        private void Border(Rect r, Color c, float t)
        {
            Fill(new Rect(r.x, r.y, r.width, t), c);
            Fill(new Rect(r.x, r.yMax - t, r.width, t), c);
            Fill(new Rect(r.x, r.y, t, r.height), c);
            Fill(new Rect(r.xMax - t, r.y, t, r.height), c);
        }

        private const int AlignLeft = 0;
        private const int AlignCenter = 1;

        // TextAnchor/FontStyle live in a Unity module the build does not
        // reference, so alignment is done by hand with CalcSize.
        private void Text(Rect r, string text, Color color, int size, int align)
        {
            _label.fontSize = Mathf.Max(8, size);
            Vector2 measured = _label.CalcSize(new GUIContent(text));
            float x = r.x;
            if (align == AlignCenter) x = r.x + (r.width - measured.x) / 2f;
            Rect target = new Rect(x, r.y + (r.height - measured.y) / 2f, Mathf.Max(r.width, measured.x), measured.y);
            _label.normal.textColor = new Color(0f, 0f, 0f, 0.9f);
            GUI.Label(new Rect(target.x + 1f, target.y + 1f, target.width, target.height), text, _label);
            _label.normal.textColor = color;
            GUI.Label(target, text, _label);
        }

        private static void DrawPixelIcon(Texture2D icon, Rect box)
        {
            if (icon == null) return;
            float scale = Mathf.Max(1f, Mathf.Floor(Mathf.Min(box.width / icon.width, box.height / icon.height)));
            float w = icon.width * scale;
            float h = icon.height * scale;
            GUI.DrawTexture(new Rect(box.x + (box.width - w) / 2f, box.y + (box.height - h) / 2f, w, h), icon);
        }

        // Draws a tk2d atlas sprite (guns and items) without needing the
        // texture to be readable.
        private static bool DrawTk2dSprite(tk2dBaseSprite sprite, Rect box)
        {
            if (sprite == null || sprite.Collection == null) return false;
            tk2dSpriteDefinition[] defs = sprite.Collection.spriteDefinitions;
            int id = sprite.spriteId;
            if (defs == null || id < 0 || id >= defs.Length) return false;
            tk2dSpriteDefinition def = defs[id];
            if (def == null || def.uvs == null || def.uvs.Length < 4) return false;
            Material material = def.materialInst != null ? def.materialInst : def.material;
            Texture texture = material != null ? material.mainTexture : null;
            if (texture == null) return false;

            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            for (int i = 0; i < def.uvs.Length; i++)
            {
                minX = Mathf.Min(minX, def.uvs[i].x); maxX = Mathf.Max(maxX, def.uvs[i].x);
                minY = Mathf.Min(minY, def.uvs[i].y); maxY = Mathf.Max(maxY, def.uvs[i].y);
            }
            Rect uv = Rect.MinMaxRect(minX, minY, maxX, maxY);
            float regionW = uv.width * texture.width;
            float regionH = uv.height * texture.height;
            if (regionW < 1f || regionH < 1f) return false;

            bool rotated = def.flipped != tk2dSpriteDefinition.FlipMode.None;
            float visualW = rotated ? regionH : regionW;
            float visualH = rotated ? regionW : regionH;
            float fit = Mathf.Min(box.width / visualW, box.height / visualH);
            float scale = fit >= 1f ? Mathf.Floor(fit) : fit;
            if (scale <= 0f) return false;

            Vector2 center = box.center;
            Rect target = new Rect(center.x - regionW * scale / 2f, center.y - regionH * scale / 2f, regionW * scale, regionH * scale);

            Matrix4x4 oldMatrix = GUI.matrix;
            try
            {
                if (rotated)
                    GUIUtility.RotateAroundPivot(def.flipped == tk2dSpriteDefinition.FlipMode.TPackerCW ? 90f : -90f, center);
                GUI.DrawTextureWithTexCoords(target, texture, uv, true);
            }
            finally
            {
                GUI.matrix = oldMatrix;
            }
            return true;
        }

        private void EnsureResources()
        {
            if (_white == null)
            {
                _white = new Texture2D(1, 1, TextureFormat.RGBA32, false);
                _white.SetPixel(0, 0, Color.white);
                _white.Apply();
                _white.hideFlags = HideFlags.HideAndDontSave;
            }
            if (_label == null)
            {
                _label = new GUIStyle(GUI.skin.label);
                _label.wordWrap = false;
            }
            if (_heartFull != null) return;

            string[] heart =
            {
                ".##.##.",
                "#######",
                "#######",
                ".#####.",
                "..###..",
                "...#..."
            };
            string[] shield =
            {
                "#######",
                "#######",
                "#######",
                "#######",
                ".#####.",
                "..###..",
                "...#..."
            };
            string[] disc =
            {
                "..###..",
                ".#####.",
                "#######",
                "#######",
                "#######",
                ".#####.",
                "..###.."
            };
            string[] key =
            {
                ".###.....",
                "##.######",
                ".###.#.#.",
                "........."
            };

            Color red = new Color(0.86f, 0.12f, 0.14f);
            Color grey = new Color(0.22f, 0.2f, 0.24f);
            _heartFull = MakeIcon(heart, delegate (int x, int y) { return red; }, new Color(1f, 0.6f, 0.62f));
            _heartHalf = MakeIcon(heart, delegate (int x, int y) { return x <= 3 ? red : grey; }, new Color(1f, 0.6f, 0.62f));
            _heartEmpty = MakeIcon(heart, delegate (int x, int y) { return grey; }, new Color(0.38f, 0.36f, 0.4f));
            _shield = MakeIcon(shield, delegate (int x, int y) { return new Color(0.3f, 0.62f, 1f); }, new Color(0.75f, 0.9f, 1f));
            _blank = MakeIcon(disc, delegate (int x, int y) { return new Color(0.35f, 0.75f, 1f); }, new Color(0.85f, 0.95f, 1f));
            _key = MakeIcon(key, delegate (int x, int y) { return new Color(0.78f, 0.8f, 0.86f); }, new Color(1f, 1f, 1f));
            _coin = MakeIcon(disc, delegate (int x, int y) { return new Color(1f, 0.82f, 0.2f); }, new Color(1f, 0.97f, 0.7f));
        }

        private delegate Color PixelColor(int x, int y);

        // Builds a tiny pixel-art icon from a '#' mask. Edge pixels are
        // darkened and one pixel near the top-left is highlighted.
        private static Texture2D MakeIcon(string[] mask, PixelColor color, Color highlight)
        {
            int w = mask[0].Length;
            int h = mask.Length;
            Texture2D tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Point;
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.hideFlags = HideFlags.HideAndDontSave;
            Color32[] pixels = new Color32[w * h];
            for (int row = 0; row < h; row++)
            {
                for (int x = 0; x < w; x++)
                {
                    bool on = mask[row][x] == '#';
                    Color c = new Color(0f, 0f, 0f, 0f);
                    if (on)
                    {
                        c = color(x, row);
                        bool edge = x == 0 || row == h - 1 || x == w - 1 || row == 0 ||
                                    mask[row][x - 1] != '#' || mask[row][x + 1] != '#' ||
                                    mask[row - 1][x] != '#' || (row + 1 < h && mask[row + 1][x] != '#');
                        if (edge) c = Color.Lerp(c, Color.black, 0.35f);
                        if (x == 1 && row == 1) c = highlight;
                    }
                    // Texture rows run bottom to top.
                    pixels[(h - 1 - row) * w + x] = c;
                }
            }
            tex.SetPixels32(pixels);
            tex.Apply();
            return tex;
        }

        private void LogErrorThrottled(string where, Exception e)
        {
            if (Time.realtimeSinceStartup < _nextErrorLog) return;
            _nextErrorLog = Time.realtimeSinceStartup + 10f;
            Debug.LogWarning("[Hud] " + where + " failed: " + e);
        }
    }
}

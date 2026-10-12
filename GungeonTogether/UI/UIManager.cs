using UnityEngine;
using GungeonTogether.Networking;
using GungeonTogether.Networking.Steam;
using GungeonTogether.Systems.Logging;
using Debug = GungeonTogether.Systems.Logging.Debug;
using Exception = System.Exception;
using System.Collections.Generic;

namespace GungeonTogether.UI
{
	public static class UIManager
	{
		private static GameObject _root;
		private static dfPanel _panel;
		private static dfLabel _statusLabel;
		private static dfButton _inviteButton;
		private static dfButton _leaveButton;
		private static dfButton _mainMenuHostButton;
		private static MainMenuFoyerController _mainMenuController;
		private static bool _singlePlayerHandlerAttached;
		private static bool _launchingHostedGame;
		private static bool _pendingNetworkGameEntry;
		private static float _nextGameEntryAttempt;
		private static float _nextUiErrorLog;
		
		private static dfPanel _playerListPanel;
		private static dfScrollPanel _playerScrollPanel;
		private static List<dfLabel> _playerLabels = new List<dfLabel>();
		private static bool _subscribed = false;

		public static void Initialise()
		{
			// Lazy: build only when in foyer and UI system exists.
		}

		public static void Update()
		{
			try
			{
				GameManager gameManager = Object.FindObjectOfType<GameManager>();
				if (gameManager == null) return;

				EnsureMainMenuHostButton();
				bool onMainMenu = Foyer.DoMainMenu;
				SetMainMenuHostVisible(onMainMenu);
				if (onMainMenu)
				{
					SetVisible(false);
					if (SteamLobbyManager.Instance.IsInLobby && NetworkManager.Instance.IsClient)
						_pendingNetworkGameEntry = true;
					TryEnterNetworkGameFromMainMenu();
					return;
				}
				_pendingNetworkGameEntry = false;
				_launchingHostedGame = false;

				if (!gameManager.IsFoyer)
				{
					SetVisible(false);
					return;
				}

				EnsureBuilt();
				bool showLobbyPanel = SteamLobbyManager.Instance.IsInLobby || NetworkManager.Instance.IsConnected;
				SetVisible(showLobbyPanel);
				if (!showLobbyPanel) return;
				UpdateStatus();
				SubscribeEvents();
			}
			catch (Exception e)
			{
				if (Time.realtimeSinceStartup >= _nextUiErrorLog)
				{
					_nextUiErrorLog = Time.realtimeSinceStartup + 2f;
					Debug.LogWarning("[UI] Update failed: " + e);
				}
			}
		}

		private static void EnsureMainMenuHostButton()
		{
			var mm = Object.FindObjectOfType<MainMenuFoyerController>();
			if (mm == null || mm.NewGameButton == null) return;

			if (_mainMenuController != mm || _mainMenuHostButton == null)
			{
				if (_mainMenuController != mm)
					_singlePlayerHandlerAttached = false;
				_mainMenuController = mm;
				dfControl parent = mm.NewGameButton.Parent;
				if (parent == null && mm.NewGameButton.transform.parent != null)
					parent = mm.NewGameButton.transform.parent.GetComponent<dfControl>();
				if (parent == null) return;

				_mainMenuHostButton = CreateButtonFromTemplate(
					mm.NewGameButton.GUIManager,
					parent,
					mm.NewGameButton,
					"GT_MainMenuHostButton",
					"HOST LOBBY",
					mm.NewGameButton.RelativePosition.x,
					mm.NewGameButton.RelativePosition.y,
					mm.NewGameButton.Width,
					mm.NewGameButton.Height);
				_mainMenuHostButton.Click += OnMainMenuHostClicked;
				_mainMenuHostButton.GotFocus += OnMainMenuHostFocused;

				var keyControls = _mainMenuHostButton.gameObject.AddComponent<UIKeyControls>();
				keyControls.selectOnAction = true;
				keyControls.clearRepeatingOnSelect = true;

				if (!_singlePlayerHandlerAttached)
				{
					mm.NewGameButton.Click += OnSinglePlayerClicked;
					_singlePlayerHandlerAttached = true;
				}
				Debug.Log("[UI] Main-menu HOST LOBBY button created.");
			}

			LayoutMainMenuButtons(mm);
			_mainMenuHostButton.IsEnabled = !NetworkManager.Instance.IsConnected && !SteamLobbyManager.Instance.IsInLobby;
		}

		private static void LayoutMainMenuButtons(MainMenuFoyerController mm)
		{
			if (_mainMenuHostButton == null || mm.QuitGameButton == null) return;
			float x = mm.QuitGameButton.RelativePosition.x;
			float quitY = mm.QuitGameButton.RelativePosition.y;

			mm.ContinueGameButton.RelativePosition = mm.ContinueGameButton.RelativePosition.WithX(x).WithY(quitY - 204f);
			mm.NewGameButton.RelativePosition = mm.NewGameButton.RelativePosition.WithX(x).WithY(quitY - 153f);
			_mainMenuHostButton.RelativePosition = _mainMenuHostButton.RelativePosition.WithX(x).WithY(quitY - 102f);
			mm.ControlsButton.RelativePosition = mm.ControlsButton.RelativePosition.WithX(x).WithY(quitY - 51f);

			var buttons = new List<dfButton>();
			if (mm.ContinueGameButton.IsEnabled && mm.ContinueGameButton.IsVisible)
				buttons.Add(mm.ContinueGameButton);
			buttons.Add(mm.NewGameButton);
			buttons.Add(_mainMenuHostButton);
			buttons.Add(mm.ControlsButton);
			buttons.Add(mm.QuitGameButton);

			for (int i = 0; i < buttons.Count; i++)
			{
				var current = buttons[i];
				var previous = buttons[(i + buttons.Count - 1) % buttons.Count];
				var next = buttons[(i + 1) % buttons.Count];
				var controls = current.GetComponent<UIKeyControls>();
				if (controls == null) controls = current.gameObject.AddComponent<UIKeyControls>();
				controls.up = previous;
				controls.down = next;
			}
		}

		private static void SetMainMenuHostVisible(bool visible)
		{
			if (_mainMenuHostButton != null)
			{
				_mainMenuHostButton.IsVisible = visible;
				_mainMenuHostButton.IsInteractive = visible;
			}
		}

		private static void EnsureBuilt()
		{
			if (_panel != null) return;
			GameUIRoot uiRoot = Object.FindObjectOfType<GameUIRoot>();
			if (uiRoot == null) return;
			if (uiRoot.Manager == null) return;

			dfGUIManager gui = uiRoot.Manager;

			// Find a button template for consistent styling.
			dfButton template = null;
			var mm = Object.FindObjectOfType<MainMenuFoyerController>();
			if (mm != null && mm.NewGameButton != null)
			{
				template = mm.NewGameButton;
			}
			if (template == null)
			{
				Debug.LogWarning("[UI] Could not find MainMenuFoyerController.NewGameButton as template.");
			}

			_root = new GameObject("GungeonTogether_UI");
			_root.transform.parent = gui.transform;
			_root.transform.localPosition = Vector3.zero;
			_root.transform.localScale = Vector3.one;

			_panel = _root.AddComponent<dfPanel>();
			if (gui != null)
			{
				gui.AddControl(_panel);
			}
			_panel.Anchor = dfAnchorStyle.Top | dfAnchorStyle.Left;
			_panel.RelativePosition = new Vector3(25f, 25f, 0f);
			_panel.Width = 420f;
			_panel.Height = 300f;
			_panel.IsVisible = true;
			_panel.Opacity = 1f;

			Debug.Log($"[UI] Panel created: Position={_panel.RelativePosition}, Size={_panel.Width}x{_panel.Height}");

			if (template != null)
			{
				_panel.Atlas = template.Atlas;
				Debug.Log($"[UI] Panel atlas set from template");
			}

			_statusLabel = CreateLabel(gui, _panel, template);
			_statusLabel.RelativePosition = new Vector3(10f, 10f, 0f);
			_statusLabel.Width = _panel.Width - 20f;
			_statusLabel.Height = 70f;
			_statusLabel.VerticalAlignment = dfVerticalAlignment.Top;
			_statusLabel.TextScale = 1.0f;
			_statusLabel.ProcessMarkup = true;
			_statusLabel.ColorizeSymbols = true;

			Debug.Log($"[UI] Status label created: Position={_statusLabel.RelativePosition}, Size={_statusLabel.Width}x{_statusLabel.Height}");

			// Lobby controls are only shown after hosting or joining.
			float currentY = _statusLabel.RelativePosition.y + _statusLabel.Height + 10f;
			float buttonWidth = (_panel.Width - 30f) / 2f;
			float buttonHeight = 40f;
			float buttonSpacing = 10f;

			Debug.Log($"[UI] Button layout: currentY={currentY}, buttonWidth={buttonWidth}, buttonHeight={buttonHeight}, spacing={buttonSpacing}");

		_inviteButton = CreateButtonFromTemplate(gui, _panel, template, "GT_InviteButton", "INVITE", 10f, currentY, buttonWidth, buttonHeight);
		_inviteButton.Click += OnInviteClicked;
		Debug.Log($"[UI] Invite button created: Position={_inviteButton.RelativePosition}, Size={_inviteButton.Width}x{_inviteButton.Height}, Visible={_inviteButton.IsVisible}");

		_leaveButton = CreateButtonFromTemplate(gui, _panel, template, "GT_LeaveButton", "LEAVE", 10f + buttonWidth + buttonSpacing, currentY, buttonWidth, buttonHeight);
		_leaveButton.Click += OnLeaveClicked;

			// Player list panel (inside the main panel)
			GameObject playerListObject = new GameObject("GT_PlayerListPanel");
			playerListObject.transform.parent = _panel.transform;
			playerListObject.transform.localScale = Vector3.one;
			_playerListPanel = playerListObject.AddComponent<dfPanel>();
			_panel.AddControl(_playerListPanel);
			_playerListPanel.RelativePosition = new Vector3(10f, 130f, 0f);
			_playerListPanel.Width = _panel.Width - 20f;
			_playerListPanel.Height = 120f;
			_playerListPanel.Atlas = template != null ? template.Atlas : null;
			_playerListPanel.BackgroundSprite = "blank";
			_playerListPanel.Color = new Color32(0, 0, 0, 150);

			// Scrollable area
			GameObject scrollObject = new GameObject("GT_PlayerScrollPanel");
			scrollObject.transform.parent = _playerListPanel.transform;
			scrollObject.transform.localScale = Vector3.one;
			_playerScrollPanel = scrollObject.AddComponent<dfScrollPanel>();
			_playerListPanel.AddControl(_playerScrollPanel);
			_playerScrollPanel.RelativePosition = Vector3.zero;
			_playerScrollPanel.Width = _playerListPanel.Width;
			_playerScrollPanel.Height = _playerListPanel.Height;
		}

		private static void SubscribeEvents()
		{
			if (_subscribed) return;
			SteamLobbyManager.Instance.OnPlayerListChanged += RefreshPlayerList;
			_subscribed = true;
			RefreshPlayerList();
		}

		private static void RefreshPlayerList()
		{
			if (_playerScrollPanel == null) return;

			// Clear old labels
			foreach (var label in _playerLabels)
				UnityEngine.Object.Destroy(label.gameObject);
			_playerLabels.Clear();

			var members = SteamLobbyManager.Instance.GetLobbyMembers();
			float yOffset = 0f;
			float labelHeight = 20f;
			foreach (var id in members)
			{
				string name = SteamReflectionHelper.GetPlayerName(id);
				var label = CreateLabel(null, _playerScrollPanel, null);
				label.Text = name;
				label.RelativePosition = new Vector3(5f, yOffset, 0f);
				label.Width = _playerScrollPanel.Width - 10f;
				label.Height = labelHeight;
				label.TextScale = 0.8f;
				label.Color = Color.white;
				label.VerticalAlignment = dfVerticalAlignment.Middle;
				_playerLabels.Add(label);
				yOffset += labelHeight + 2f;
			}
		}



		private static void UpdateStatus()
		{
			if (_statusLabel == null) return;

			var lobby = SteamLobbyManager.Instance;
			string lobbyText = lobby.IsInLobby ? ("Lobby: " + lobby.CurrentLobbyId) : "Lobby: (none)";
			string roleText = NetworkManager.Instance.IsHost ? "Role: Host" : (NetworkManager.Instance.IsClient ? "Role: Client" : "Role: (none)");
			string connText = NetworkManager.Instance.IsConnected ? "Net: Connected" : "Net: Disconnected";

			_statusLabel.ModifyLocalizedText("GUNGEON TOGETHER\n" + lobbyText + "\n" + roleText + " | " + connText);

			// Button enable states
			if (_inviteButton != null) _inviteButton.IsEnabled = lobby.IsInLobby;
			if (_leaveButton != null) _leaveButton.IsEnabled = lobby.IsInLobby || NetworkManager.Instance.IsConnected;
		}

		private static void SetVisible(bool visible)
		{
			if (_panel != null) _panel.IsVisible = visible;
		}

		private static void OnMainMenuHostClicked(dfControl control, dfMouseEventArgs mouseEvent)
		{
			if (_mainMenuController == null || NetworkManager.Instance.IsConnected || SteamLobbyManager.Instance.IsInLobby)
				return;

			Debug.Log("[UI] Main-menu Host Lobby clicked.");
			_pendingNetworkGameEntry = true;
			SteamLobbyManager.Instance.CreateLobby(NetworkManager.MaxPlayers);
		}

		private static void OnSinglePlayerClicked(dfControl control, dfMouseEventArgs mouseEvent)
		{
			if (_launchingHostedGame)
			{
				_launchingHostedGame = false;
				return;
			}

			// PLAY always means a clean single-player session.
			if (SteamLobbyManager.Instance.IsInLobby)
				SteamLobbyManager.Instance.LeaveLobby();
			if (NetworkManager.Instance.IsConnected)
				NetworkManager.Instance.Shutdown();
		}

		private static void TryEnterNetworkGameFromMainMenu()
		{
			if (!_pendingNetworkGameEntry || _mainMenuController == null || _launchingHostedGame) return;
			if (!SteamLobbyManager.Instance.IsInLobby || !NetworkManager.Instance.IsConnected) return;
			if (Time.realtimeSinceStartup < _nextGameEntryAttempt) return;
			_nextGameEntryAttempt = Time.realtimeSinceStartup + 0.75f;
			Debug.Log("[UI] Entering the foyer for the network lobby.");
			_launchingHostedGame = true;
			_mainMenuController.NewGameButton.DoClick();
			_launchingHostedGame = false;
		}

		private static void OnMainMenuHostFocused(dfControl control, dfFocusEventArgs args)
		{
			if (Foyer.DoMainMenu && GameManager.Instance != null)
				AkSoundEngine.PostEvent("Play_UI_menu_select_01", GameManager.Instance.gameObject);
		}

		private static void OnInviteClicked(dfControl control, dfMouseEventArgs mouseEvent)
		{
			Debug.Log("[UI] Invite clicked.");
			SteamLobbyManager.Instance.OpenInviteDialog();
		}

		private static void OnLeaveClicked(dfControl control, dfMouseEventArgs mouseEvent)
		{
			Debug.Log("[UI] Leave clicked.");
			SteamLobbyManager.Instance.LeaveLobby();
			NetworkManager.Instance.Shutdown();
		}

		private static dfLabel CreateLabel(dfGUIManager gui, dfControl parent, dfButton template)
		{
			GameObject go = new GameObject("GT_StatusLabel");
			go.transform.parent = parent != null ? parent.transform : null;
			go.transform.localScale = Vector3.one;
			var lbl = go.AddComponent<dfLabel>();
			if (parent != null)
			{
				parent.AddControl(lbl);
			}
			if (template != null)
			{
				lbl.Atlas = template.Atlas;
				lbl.Font = template.Font;
				lbl.TextScale = template.TextScale;
				lbl.Color = template.TextColor;
			}
			else
			{
				lbl.Color = Color.white;
			}
			lbl.WordWrap = true;
			lbl.IsVisible = true;
			return lbl;
		}

		private static dfButton CreateButtonFromTemplate(dfGUIManager gui, dfControl parent, dfButton template, string name, string text, float posX, float posY, float width, float height)
		{
			// Create button from scratch instead of cloning to avoid parent hierarchy issues
			GameObject go = new GameObject(name);
			go.transform.parent = parent != null ? parent.transform : null;
			go.transform.localScale = Vector3.one;
			go.transform.localPosition = Vector3.zero;

			dfButton btn = go.AddComponent<dfButton>();
			if (parent != null)
			{
				parent.AddControl(btn);
			}
			
			// Apply styling from template if available
			if (template != null)
			{
				btn.Atlas = template.Atlas;
				btn.Font = template.Font;
				btn.TextScale = template.TextScale;
				btn.TextColor = template.TextColor;
				btn.BackgroundSprite = template.BackgroundSprite;
				btn.FocusSprite = template.FocusSprite;
				btn.HoverSprite = template.HoverSprite;
				btn.PressedSprite = template.PressedSprite;
				btn.DisabledSprite = template.DisabledSprite;
			}
			
			btn.Text = text;
			btn.forceUpperCase = true;
			btn.IsInteractive = true;
			btn.IsVisible = true;
			btn.IsEnabled = true;
			
			// Set position and size
			btn.RelativePosition = new Vector3(posX, posY, 0f);
			btn.Width = width;
			btn.Height = height;
			
			Debug.Log($"[UI] Button '{name}' created from scratch: Position={btn.RelativePosition}, Size={btn.Width}x{btn.Height}");
			
			return btn;
		}
	}
}


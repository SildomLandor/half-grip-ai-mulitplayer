using System;
using System.Globalization;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace HalfGripMultiplayer;

/// <summary>
/// Fullscreen multiplayer panel built with the game's UiKit + ProgressionMenuSkin
/// so it matches Collection / Loadout chrome (paper panel, gold accents, TMP).
/// </summary>
internal sealed class MpMenuUi : MonoBehaviour
{
	public static MpMenuUi? Instance { get; private set; }

	private ProgressionMenuSkin? _skin;
	private Canvas? _canvas;
	private RectTransform? _root;
	private TMP_Text? _status;
	private TMP_Text? _peers;
	private TMP_Text? _chat;
	private TMP_InputField? _lobbyId;
	private TMP_InputField? _lanHost;
	private TMP_InputField? _lanPort;
	private TMP_InputField? _chatInput;
	private Toggle? _freePlay;
	private Toggle? _noBots;
	private Button? _readyBtn;
	private Button? _forceBtn;
	private RectTransform? _mapContent;
	private float _nextRefresh;
	private bool _built;
	private MainMenu? _boundMain;
	private TMP_FontAsset? _font;

	/// <summary>True only when the panel is built on a real Canvas and active.</summary>
	public bool IsOpen =>
		_built && _canvas != null && _root != null && _root.gameObject.activeInHierarchy;

	public static MpMenuUi Ensure()
	{
		if (Instance != null) return Instance;
		var go = new GameObject("HGMP_MenuUi");
		DontDestroyOnLoad(go);
		Instance = go.AddComponent<MpMenuUi>();
		return Instance;
	}

	private void Awake()
	{
		Instance = this;
	}

	private void Update()
	{
		if (!IsOpen) return;
		if (Time.unscaledTime < _nextRefresh) return;
		_nextRefresh = Time.unscaledTime + 0.35f;
		RefreshDynamic();
	}

	public void PrepareForMainMenu(MainMenu menu)
	{
		_boundMain = menu;
		EnsureBuilt(menu != null ? menu.Skin : null);
		if (menu != null && _root != null)
			menu.MultiplayerSection = _root.gameObject;
	}

	public void OpenForMainMenu(MainMenu menu)
	{
		PrepareForMainMenu(menu);
		Show(true);
		try { menu?.ShowMultiplayer(); } catch { /* view switch optional */ }
	}

	public void OpenOverlay(ProgressionMenuSkin? skin = null, Transform? parent = null)
	{
		_ = parent;
		_boundMain = null;
		EnsureBuilt(skin);
		Show(true);
	}

	public void Close()
	{
		Show(false);
		if (_boundMain != null)
		{
			try { _boundMain.CloseMultiplayer(); } catch { /* */ }
			_boundMain = null;
		}
		var rt = MpRuntime.Instance;
		if (rt != null && rt.UiVisible)
			rt.SetUiVisibleFromMenu(false);
	}

	public void Show(bool open)
	{
		EnsureBuilt(_skin);
		if (_root == null || _canvas == null)
		{
			Plugin.Log.LogWarning("MpMenuUi.Show: panel not built");
			return;
		}
		_canvas.gameObject.SetActive(open);
		_root.gameObject.SetActive(open);
		if (open)
		{
			_canvas.sortingOrder = 500;
			EnsureEventSystem();
			RefreshDynamic();
			Plugin.Log.LogInfo("MpMenuUi opened");
		}
	}

	private static void EnsureEventSystem()
	{
		if (UnityEngine.EventSystems.EventSystem.current != null) return;
		var go = new GameObject("HGMP_EventSystem",
			typeof(UnityEngine.EventSystems.EventSystem),
			typeof(UnityEngine.EventSystems.StandaloneInputModule));
		DontDestroyOnLoad(go);
	}

	public void ToggleOverlay()
	{
		if (IsOpen) Close();
		else OpenOverlay(FindSkin());
	}

	private void EnsureBuilt(ProgressionMenuSkin? skin)
	{
		_skin = skin ?? _skin ?? FindSkin();
		if (_built && _root != null && _canvas != null) return;

		EnsureCanvas();
		_font = _skin != null ? _skin.font : null;
		if (_font == null)
		{
			try { _font = TMP_Settings.defaultFontAsset; } catch { /* */ }
		}

		var parent = _canvas!.transform;
		_root = UiKit.Rect("HGMP_Multiplayer", parent, new Vector2(1920f, 1080f));
		StretchFull(_root);
		_root.gameObject.layer = LayerMask.NameToLayer("UI");

		var titleSize = _skin != null ? _skin.titleFontSize : 48f;
		var btnSize = _skin != null ? _skin.buttonFontSize : 32f;
		var small = _skin != null ? _skin.smallFontSize : 22f;
		var textCol = _skin != null ? _skin.textColor : Color.white;
		var dimCol = _skin != null ? _skin.dimTextColor : new Color(0.7f, 0.7f, 0.75f);
		var gold = _skin != null ? _skin.goldColor : new Color(1f, 0.83f, 0.35f);
		var menuBtn = _skin != null ? _skin.menuButton : null;
		var menuBtnPressed = _skin != null ? _skin.menuButtonPressed : null;
		var closeSpr = _skin != null ? _skin.closeButton : null;
		var panelBg = _skin != null ? _skin.panelBackground : null;

		var bg = UiKit.Fill("Background", _root, panelBg);
		bg.raycastTarget = true;
		bg.color = panelBg != null ? new Color(1f, 1f, 1f, 0.98f) : new Color(0.06f, 0.06f, 0.08f, 0.96f);

		var header = UiKit.Label("Header", _root, new Vector2(900f, 70f), "MULTIPLAYER",
			_font, titleSize, gold, TextAlignmentOptions.Left);
		header.rectTransform.anchoredPosition = new Vector2(-480f, 430f);

		_status = UiKit.Label("Status", _root, new Vector2(700f, 40f), "Offline",
			_font, small, dimCol, TextAlignmentOptions.Left);
		_status.rectTransform.anchoredPosition = new Vector2(-480f, 370f);

		((RectTransform)UiKit.Btn("Close", _root, closeSpr, new Vector2(64f, 64f),
			"X", _font, 28f, textCol, null, () =>
			{
				PlayClick();
				Close();
			}, sliced: false).transform).anchoredPosition = new Vector2(860f, 430f);

		((RectTransform)UiKit.Btn("Back", _root, menuBtn, new Vector2(240f, 80f),
			"BACK", _font, btnSize, textCol, menuBtnPressed, () =>
			{
				PlayClick();
				Close();
			}).transform).anchoredPosition = new Vector2(-700f, -460f);

		BuildSteamColumn();
		BuildLanColumn();
		BuildLobbyColumn();
		BuildSandboxColumn();
		BuildChatColumn();

		_built = true;
		_root.gameObject.SetActive(false);
		if (_canvas != null) _canvas.gameObject.SetActive(false);
		Plugin.Log.LogInfo("MpMenuUi panel built (canvas overlay)");
	}

	private void EnsureCanvas()
	{
		if (_canvas != null) return;
		var go = new GameObject("HGMP_MenuCanvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
		go.transform.SetParent(transform, false);
		go.layer = LayerMask.NameToLayer("UI");
		_canvas = go.GetComponent<Canvas>();
		_canvas.renderMode = RenderMode.ScreenSpaceOverlay;
		_canvas.sortingOrder = 500;
		var scaler = go.GetComponent<CanvasScaler>();
		scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
		scaler.referenceResolution = new Vector2(1920f, 1080f);
		scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
		scaler.matchWidthOrHeight = 0.5f;
	}

	private float CardFont => _skin != null ? _skin.cardFontSize : 26f;
	private float SmallFont => _skin != null ? _skin.smallFontSize : 22f;
	private float BtnFont => _skin != null ? Mathf.Min(_skin.buttonFontSize, 32f) : 28f;
	private Color TextCol => _skin != null ? _skin.textColor : Color.white;
	private Color DimCol => _skin != null ? _skin.dimTextColor : new Color(0.7f, 0.7f, 0.75f);
	private Color GoldCol => _skin != null ? _skin.goldColor : new Color(1f, 0.83f, 0.35f);
	private Sprite? MenuBtnSpr => _skin != null ? _skin.menuButton : null;
	private Sprite? MenuBtnPressedSpr => _skin != null ? _skin.menuButtonPressed : null;
	private Sprite? CellSpr => _skin != null ? _skin.itemCell : null;
	private Sprite? BadgeSpr => _skin != null ? _skin.counterBadge : null;

	private void BuildSteamColumn()
	{
		var col = UiKit.Rect("Steam", _root, new Vector2(420f, 280f));
		col.anchoredPosition = new Vector2(-520f, 120f);
		UiKit.Label("SteamTitle", col, new Vector2(400f, 36f), "STEAM",
			_font, CardFont, GoldCol, TextAlignmentOptions.Left)
			.rectTransform.anchoredPosition = new Vector2(0f, 110f);

		PlaceBtn(col, "HostSteam", "HOST LOBBY", new Vector2(0f, 40f), () =>
		{
			PlayClick();
			MpRuntime.Instance?.Session.HostSteam();
		});
		PlaceBtn(col, "Invite", "INVITE", new Vector2(0f, -40f), () =>
		{
			PlayClick();
			MpRuntime.Instance?.Session.InviteSteam();
		}, width: 200f);
		PlaceBtn(col, "LeaveSteam", "LEAVE", new Vector2(210f, -40f), () =>
		{
			PlayClick();
			var rt = MpRuntime.Instance;
			if (rt == null) return;
			rt.Session.Leave();
			rt.ClearGhostsPublic();
		}, width: 190f);

		_lobbyId = MakeInput(col, "LobbyId", "Steam lobby ID", new Vector2(0f, -110f), 280f);
		PlaceBtn(col, "JoinSteam", "JOIN", new Vector2(160f, -110f), () =>
		{
			PlayClick();
			var text = _lobbyId != null ? _lobbyId.text : "";
			if (ulong.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var lid))
				MpRuntime.Instance?.Session.JoinSteam(lid);
		}, width: 120f, height: 56f);
	}

	private void BuildLanColumn()
	{
		var col = UiKit.Rect("LAN", _root, new Vector2(420f, 280f));
		col.anchoredPosition = new Vector2(-60f, 120f);
		UiKit.Label("LanTitle", col, new Vector2(400f, 36f), "LAN",
			_font, CardFont, GoldCol, TextAlignmentOptions.Left)
			.rectTransform.anchoredPosition = new Vector2(0f, 110f);

		_lanHost = MakeInput(col, "LanHost", "127.0.0.1", new Vector2(-60f, 40f), 220f);
		_lanPort = MakeInput(col, "LanPort", NetProtocol.DefaultLanPort.ToString(), new Vector2(130f, 40f), 100f);
		if (MpRuntime.Instance != null)
		{
			_lanHost.text = MpRuntime.Instance.LanHostText;
			_lanPort.text = MpRuntime.Instance.LanPortText;
		}

		PlaceBtn(col, "HostLan", "HOST LAN", new Vector2(0f, -40f), () =>
		{
			PlayClick();
			var rt = MpRuntime.Instance;
			if (rt == null) return;
			int.TryParse(_lanPort != null ? _lanPort.text : "27777", out var port);
			rt.LanPortText = port > 0 ? port.ToString() : NetProtocol.DefaultLanPort.ToString();
			rt.Session.HostLan(port > 0 ? port : NetProtocol.DefaultLanPort);
		}, width: 200f);
		PlaceBtn(col, "JoinLan", "JOIN LAN", new Vector2(210f, -40f), () =>
		{
			PlayClick();
			var rt = MpRuntime.Instance;
			if (rt == null) return;
			rt.LanHostText = _lanHost != null ? _lanHost.text.Trim() : "127.0.0.1";
			int.TryParse(_lanPort != null ? _lanPort.text : "27777", out var port);
			rt.LanPortText = port > 0 ? port.ToString() : NetProtocol.DefaultLanPort.ToString();
			rt.Session.JoinLan(rt.LanHostText, port > 0 ? port : NetProtocol.DefaultLanPort);
		}, width: 190f);
	}

	private void BuildLobbyColumn()
	{
		var col = UiKit.Rect("Lobby", _root, new Vector2(420f, 280f));
		col.anchoredPosition = new Vector2(400f, 120f);
		UiKit.Label("LobbyTitle", col, new Vector2(400f, 36f), "LOBBY",
			_font, CardFont, GoldCol, TextAlignmentOptions.Left)
			.rectTransform.anchoredPosition = new Vector2(0f, 110f);

		_peers = UiKit.Label("Peers", col, new Vector2(400f, 120f), "Not connected",
			_font, SmallFont, TextCol, TextAlignmentOptions.TopLeft);
		_peers.rectTransform.anchoredPosition = new Vector2(0f, 20f);

		_readyBtn = PlaceBtn(col, "Ready", "READY", new Vector2(0f, -90f), () =>
		{
			PlayClick();
			var s = MpRuntime.Instance?.Session;
			if (s == null || !s.Linked) return;
			s.SetReady(!s.LocalReady);
		}, width: 200f);
		_forceBtn = PlaceBtn(col, "Force", "FORCE START", new Vector2(210f, -90f), () =>
		{
			PlayClick();
			var s = MpRuntime.Instance?.Session;
			if (s == null || !s.Linked || !s.IsHost) return;
			s.UnlockForceStart();
		}, width: 190f);
	}

	private void BuildSandboxColumn()
	{
		var col = UiKit.Rect("Sandbox", _root, new Vector2(860f, 260f));
		col.anchoredPosition = new Vector2(-290f, -200f);
		UiKit.Label("SandTitle", col, new Vector2(400f, 36f), "SANDBOX",
			_font, CardFont, GoldCol, TextAlignmentOptions.Left)
			.rectTransform.anchoredPosition = new Vector2(-200f, 100f);

		_freePlay = MakeToggle(col, "FreePlay", "Free play (no auto-finish)", new Vector2(-200f, 50f), RaceSync.FreePlay,
			v => RaceSync.FreePlay = v);
		_noBots = MakeToggle(col, "NoBots", "No bots (humans only)", new Vector2(-200f, 10f), RaceSync.NoBots,
			v => RaceSync.NoBots = v);

		PlaceBtn(col, "Playground", "PLAYGROUND", new Vector2(220f, 40f), () =>
		{
			PlayClick();
			MpRuntime.Instance?.HostOpenPlaygroundPublic();
		}, width: 220f, height: 64f);

		var scroll = UiKit.Scroll("Maps", col, new Vector2(820f, 140f), out _mapContent);
		((RectTransform)scroll.transform).anchoredPosition = new Vector2(0f, -50f);
		RebuildMapList();
	}

	private void BuildChatColumn()
	{
		var col = UiKit.Rect("Chat", _root, new Vector2(420f, 260f));
		col.anchoredPosition = new Vector2(400f, -200f);
		UiKit.Label("ChatTitle", col, new Vector2(400f, 36f), "CHAT",
			_font, CardFont, GoldCol, TextAlignmentOptions.Left)
			.rectTransform.anchoredPosition = new Vector2(0f, 100f);

		_chat = UiKit.Label("ChatLog", col, new Vector2(400f, 140f), "",
			_font, SmallFont, DimCol, TextAlignmentOptions.TopLeft);
		_chat.rectTransform.anchoredPosition = new Vector2(0f, 10f);
		_chat.textWrappingMode = TextWrappingModes.Normal;
		_chat.overflowMode = TextOverflowModes.Ellipsis;

		_chatInput = MakeInput(col, "ChatIn", "", new Vector2(-50f, -100f), 280f);
		PlaceBtn(col, "Send", "SEND", new Vector2(150f, -100f), () =>
		{
			PlayClick();
			var s = MpRuntime.Instance?.Session;
			if (s == null || _chatInput == null || string.IsNullOrWhiteSpace(_chatInput.text)) return;
			s.SendChat(_chatInput.text);
			_chatInput.text = "";
		}, width: 110f, height: 56f);
	}

	private void RebuildMapList()
	{
		if (_mapContent == null) return;
		for (int i = _mapContent.childCount - 1; i >= 0; i--)
			Destroy(_mapContent.GetChild(i).gameObject);

		var cfg = RunConfig.Instance;
		float y = 0f;
		if (cfg?.maps == null)
		{
			UiKit.Label("NoMaps", _mapContent, new Vector2(780f, 32f),
				"(Open a run once to load maps)", _font, SmallFont, DimCol,
				TextAlignmentOptions.Left).rectTransform.anchoredPosition = new Vector2(0f, -10f);
			_mapContent.sizeDelta = new Vector2(780f, 40f);
			return;
		}
		foreach (var map in cfg.maps)
		{
			if (map == null) continue;
			string label = !string.IsNullOrEmpty(map.displayName) ? map.displayName : map.name;
			var captured = map;
			var btn = PlaceBtn(_mapContent, "Map_" + label, $"{label}  #{map.trackIndex}", new Vector2(0f, y), () =>
			{
				PlayClick();
				MpRuntime.Instance?.HostLoadMapPublic(captured);
			}, width: 780f, height: 44f);
			y -= 50f;
			_ = btn;
		}
		_mapContent.sizeDelta = new Vector2(780f, Mathf.Max(140f, -y + 10f));
		_mapContent.anchoredPosition = new Vector2(0f, 0f);
	}

	private void RefreshDynamic()
	{
		var rt = MpRuntime.Instance;
		var s = rt?.Session;
		if (_status != null)
		{
			if (s == null || !s.Linked)
				_status.text = "Offline — host a Steam lobby or LAN session";
			else
			{
				string phase = s.InLobbyPhase ? "LOBBY" : "RACE";
				_status.text =
					$"{s.Status}  ·  {phase}  ·  RTT {s.LastRttMs:0}ms  ·  slot {s.LocalSlot}  ·  v{PluginInfo.Version}";
			}
		}

		if (_peers != null)
		{
			if (s == null || !s.Linked)
				_peers.text = "Not connected";
			else
			{
				var sb = new StringBuilder();
				sb.AppendLine($"[{s.LocalSlot}] YOU  {(s.LocalReady ? "READY" : "…")}");
				foreach (var kv in s.Peers)
				{
					int slot = s.SlotFor(kv.Key);
					sb.AppendLine($"[{slot}] {kv.Value.Name}  {(kv.Value.Ready ? "READY" : "…")}");
				}
				_peers.text = sb.ToString().TrimEnd();
			}
		}

		if (_chat != null && s != null)
		{
			var sb = new StringBuilder();
			int skip = Math.Max(0, s.ChatLog.Count - 10);
			int i = 0;
			foreach (var line in s.ChatLog)
			{
				if (i++ < skip) continue;
				sb.AppendLine(line);
			}
			_chat.text = sb.ToString().TrimEnd();
		}

		if (_readyBtn != null)
		{
			var label = _readyBtn.GetComponentInChildren<TMP_Text>(true);
			if (label != null && s != null && s.Linked)
				label.text = s.LocalReady ? "UNREADY" : "READY";
		}
		if (_forceBtn != null)
			_forceBtn.gameObject.SetActive(s != null && s.Linked && s.IsHost && s.InLobbyPhase);

		if (_freePlay != null) _freePlay.isOn = RaceSync.FreePlay;
		if (_noBots != null) _noBots.isOn = RaceSync.NoBots;
	}

	private Button PlaceBtn(Transform parent, string name, string text, Vector2 pos, Action onClick,
		float width = 400f, float height = 72f)
	{
		var btn = UiKit.Btn(name, parent, MenuBtnSpr, new Vector2(width, height), text,
			_font, BtnFont, TextCol, MenuBtnPressedSpr, onClick);
		((RectTransform)btn.transform).anchoredPosition = pos;
		return btn;
	}

	private TMP_InputField MakeInput(Transform parent, string name, string placeholder, Vector2 pos, float width)
	{
		var rect = UiKit.Rect(name, parent, new Vector2(width, 56f));
		rect.anchoredPosition = pos;
		var img = rect.gameObject.AddComponent<Image>();
		img.sprite = CellSpr;
		img.type = Image.Type.Sliced;
		img.color = new Color(0.12f, 0.12f, 0.14f, 0.92f);

		var text = UiKit.Label("Text", rect, new Vector2(width - 16f, 48f), "",
			_font, SmallFont, TextCol, TextAlignmentOptions.Left);
		text.raycastTarget = false;
		var ph = UiKit.Label("Placeholder", rect, new Vector2(width - 16f, 48f), placeholder,
			_font, SmallFont, DimCol, TextAlignmentOptions.Left);
		ph.fontStyle = FontStyles.Italic;
		ph.raycastTarget = false;

		var field = rect.gameObject.AddComponent<TMP_InputField>();
		field.textViewport = rect;
		field.textComponent = text;
		field.placeholder = ph;
		if (_font != null) field.fontAsset = _font;
		field.pointSize = SmallFont;
		field.caretColor = GoldCol;
		field.selectionColor = new Color(GoldCol.r, GoldCol.g, GoldCol.b, 0.35f);
		return field;
	}

	private Toggle MakeToggle(Transform parent, string name, string label, Vector2 pos, bool on, Action<bool> changed)
	{
		var rect = UiKit.Rect(name, parent, new Vector2(420f, 36f));
		rect.anchoredPosition = pos;
		var toggle = rect.gameObject.AddComponent<Toggle>();
		var bg = UiKit.Img("Box", rect, CellSpr, new Vector2(28f, 28f), sliced: true);
		bg.rectTransform.anchoredPosition = new Vector2(-190f, 0f);
		bg.raycastTarget = true;
		bg.enabled = true;
		var check = UiKit.Img("Check", bg.rectTransform, BadgeSpr, new Vector2(18f, 18f), sliced: true);
		check.color = GoldCol;
		check.enabled = true;
		UiKit.Label("Lbl", rect, new Vector2(360f, 36f), label,
			_font, SmallFont, TextCol, TextAlignmentOptions.Left)
			.rectTransform.anchoredPosition = new Vector2(30f, 0f);
		toggle.targetGraphic = bg;
		toggle.graphic = check;
		toggle.isOn = on;
		toggle.onValueChanged.AddListener(v => changed(v));
		return toggle;
	}

	private static void StretchFull(RectTransform rt)
	{
		rt.anchorMin = Vector2.zero;
		rt.anchorMax = Vector2.one;
		rt.pivot = new Vector2(0.5f, 0.5f);
		rt.offsetMin = Vector2.zero;
		rt.offsetMax = Vector2.zero;
		rt.anchoredPosition = Vector2.zero;
		rt.sizeDelta = Vector2.zero;
	}

	private static ProgressionMenuSkin? FindSkin()
	{
		var menu = UnityEngine.Object.FindFirstObjectByType<MainMenu>();
		if (menu?.Skin != null) return menu.Skin;
		var start = UnityEngine.Object.FindFirstObjectByType<StartMenuProgression>();
		if (start?.Skin != null) return start.Skin;
		return Resources.Load<ProgressionMenuSkin>("ProgressionMenuSkin");
	}

	private void PlayClick()
	{
		try
		{
			var menu = _boundMain ?? (ProgressionMenu?)UnityEngine.Object.FindFirstObjectByType<MainMenu>()
			           ?? UnityEngine.Object.FindFirstObjectByType<StartMenuProgression>();
			menu?.PlayClick();
		}
		catch { /* */ }
	}
}

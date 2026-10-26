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
	public bool IsOpen => _root != null && _root.gameObject.activeSelf;

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
		if (!_built || _root == null || !_root.gameObject.activeSelf) return;
		if (Time.unscaledTime < _nextRefresh) return;
		_nextRefresh = Time.unscaledTime + 0.35f;
		RefreshDynamic();
	}

	public void OpenForMainMenu(MainMenu menu)
	{
		_boundMain = menu;
		RectTransform? parent = null;
		var canvas = menu.GetComponentInParent<Canvas>();
		if (canvas != null) parent = canvas.transform as RectTransform;
		if (parent == null && menu.Root != null) parent = menu.Root;
		EnsureBuilt(menu.Skin, parent);
		AttachAsMainSection(menu);
		Show(true);
	}

	public void OpenOverlay(ProgressionMenuSkin? skin = null, Transform? parent = null)
	{
		_boundMain = null;
		EnsureBuilt(skin, parent as RectTransform);
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
		if (_root == null) return;
		_root.gameObject.SetActive(open);
		if (open) RefreshDynamic();
	}

	public void ToggleOverlay()
	{
		if (!_built) OpenOverlay(FindSkin());
		else Show(!IsOpen);
	}

	private void AttachAsMainSection(MainMenu menu)
	{
		if (_root == null) return;
		menu.MultiplayerSection = _root.gameObject;
		if (_root.parent != menu.transform)
			_root.SetParent(menu.transform, false);
		StretchFull(_root);
	}

	private void EnsureBuilt(ProgressionMenuSkin? skin, RectTransform? preferParent)
	{
		_skin = skin ?? FindSkin();
		if (_skin == null)
		{
			Plugin.Log.LogWarning("MpMenuUi: ProgressionMenuSkin missing — panel deferred");
			return;
		}
		if (_built && _root != null) return;

		Transform parent = preferParent != null ? preferParent : transform;
		_root = UiKit.Rect("HGMP_Multiplayer", parent, new Vector2(1920f, 1080f));
		StretchFull(_root);
		_root.gameObject.layer = LayerMask.NameToLayer("UI");

		var bg = UiKit.Fill("Background", _root, _skin.panelBackground);
		bg.raycastTarget = true;
		bg.color = new Color(1f, 1f, 1f, 0.98f);

		var header = UiKit.Label("Header", _root, new Vector2(900f, 70f), "MULTIPLAYER",
			_skin.font, _skin.titleFontSize, _skin.textColor, TextAlignmentOptions.Left);
		header.rectTransform.anchoredPosition = new Vector2(-480f, 430f);

		_status = UiKit.Label("Status", _root, new Vector2(700f, 40f), "Offline",
			_skin.font, _skin.smallFontSize, _skin.dimTextColor, TextAlignmentOptions.Left);
		_status.rectTransform.anchoredPosition = new Vector2(-480f, 370f);

		((RectTransform)UiKit.Btn("Close", _root, _skin.closeButton, new Vector2(64f, 64f),
			null, _skin.font, 0f, _skin.textColor, null, () =>
			{
				PlayClick();
				Close();
			}, sliced: false).transform).anchoredPosition = new Vector2(860f, 430f);

		((RectTransform)UiKit.Btn("Back", _root, _skin.menuButton, new Vector2(240f, 80f),
			Loc.T("BACK"), _skin.font, _skin.buttonFontSize, _skin.textColor, _skin.menuButtonPressed, () =>
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
		Plugin.Log.LogInfo("MpMenuUi panel built");
	}

	private void BuildSteamColumn()
	{
		var col = UiKit.Rect("Steam", _root, new Vector2(420f, 280f));
		col.anchoredPosition = new Vector2(-520f, 120f);
		UiKit.Label("SteamTitle", col, new Vector2(400f, 36f), "STEAM",
			_skin!.font, _skin.cardFontSize, _skin.goldColor, TextAlignmentOptions.Left)
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
			_skin!.font, _skin.cardFontSize, _skin.goldColor, TextAlignmentOptions.Left)
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
			_skin!.font, _skin.cardFontSize, _skin.goldColor, TextAlignmentOptions.Left)
			.rectTransform.anchoredPosition = new Vector2(0f, 110f);

		_peers = UiKit.Label("Peers", col, new Vector2(400f, 120f), "Not connected",
			_skin.font, _skin.smallFontSize, _skin.textColor, TextAlignmentOptions.TopLeft);
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
			_skin!.font, _skin.cardFontSize, _skin.goldColor, TextAlignmentOptions.Left)
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
			_skin!.font, _skin.cardFontSize, _skin.goldColor, TextAlignmentOptions.Left)
			.rectTransform.anchoredPosition = new Vector2(0f, 100f);

		_chat = UiKit.Label("ChatLog", col, new Vector2(400f, 140f), "",
			_skin.font, _skin.smallFontSize, _skin.dimTextColor, TextAlignmentOptions.TopLeft);
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
		if (_mapContent == null || _skin == null) return;
		for (int i = _mapContent.childCount - 1; i >= 0; i--)
			Destroy(_mapContent.GetChild(i).gameObject);

		var cfg = RunConfig.Instance;
		float y = 0f;
		if (cfg?.maps == null)
		{
			UiKit.Label("NoMaps", _mapContent, new Vector2(780f, 32f),
				"(Open a run once to load maps)", _skin.font, _skin.smallFontSize, _skin.dimTextColor,
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
		var btn = UiKit.Btn(name, parent, _skin!.menuButton, new Vector2(width, height), text,
			_skin.font, Mathf.Min(_skin.buttonFontSize, 32f), _skin.textColor, _skin.menuButtonPressed, onClick);
		((RectTransform)btn.transform).anchoredPosition = pos;
		return btn;
	}

	private TMP_InputField MakeInput(Transform parent, string name, string placeholder, Vector2 pos, float width)
	{
		var rect = UiKit.Rect(name, parent, new Vector2(width, 56f));
		rect.anchoredPosition = pos;
		var img = rect.gameObject.AddComponent<Image>();
		img.sprite = _skin!.itemCell;
		img.type = Image.Type.Sliced;
		img.color = new Color(0.12f, 0.12f, 0.14f, 0.92f);

		var text = UiKit.Label("Text", rect, new Vector2(width - 16f, 48f), "",
			_skin.font, _skin.smallFontSize, _skin.textColor, TextAlignmentOptions.Left);
		text.raycastTarget = false;
		var ph = UiKit.Label("Placeholder", rect, new Vector2(width - 16f, 48f), placeholder,
			_skin.font, _skin.smallFontSize, _skin.dimTextColor, TextAlignmentOptions.Left);
		ph.fontStyle = FontStyles.Italic;
		ph.raycastTarget = false;

		var field = rect.gameObject.AddComponent<TMP_InputField>();
		field.textViewport = rect;
		field.textComponent = text;
		field.placeholder = ph;
		field.fontAsset = _skin.font;
		field.pointSize = _skin.smallFontSize;
		field.caretColor = _skin.goldColor;
		field.selectionColor = new Color(_skin.goldColor.r, _skin.goldColor.g, _skin.goldColor.b, 0.35f);
		return field;
	}

	private Toggle MakeToggle(Transform parent, string name, string label, Vector2 pos, bool on, Action<bool> changed)
	{
		var rect = UiKit.Rect(name, parent, new Vector2(420f, 36f));
		rect.anchoredPosition = pos;
		var toggle = rect.gameObject.AddComponent<Toggle>();
		var bg = UiKit.Img("Box", rect, _skin!.itemCell, new Vector2(28f, 28f), sliced: true);
		bg.rectTransform.anchoredPosition = new Vector2(-190f, 0f);
		bg.raycastTarget = true;
		var check = UiKit.Img("Check", bg.rectTransform, _skin.counterBadge, new Vector2(18f, 18f), sliced: true);
		check.color = _skin.goldColor;
		UiKit.Label("Lbl", rect, new Vector2(360f, 36f), label,
			_skin.font, _skin.smallFontSize, _skin.textColor, TextAlignmentOptions.Left)
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

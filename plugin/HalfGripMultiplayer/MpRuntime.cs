using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace HalfGripMultiplayer;

internal sealed class MpRuntime : MonoBehaviour
{
	public static MpRuntime? Instance { get; private set; }

	public readonly MpSession Session = new();
	public bool UiVisible;
	public string ChatDraft = "";
	public string JoinLobbyIdText = "";
	public string LanHostText = "127.0.0.1";
	public string LanPortText = NetProtocol.DefaultLanPort.ToString();

	private readonly Dictionary<ulong, GhostCar> _ghosts = new();
	private float _nextPose;
	private float _nextBotPose;
	private float _nextCarLook;
	private readonly Dictionary<ulong, float> _lastDamageSent = new();
	private string? _loadingHostScene;
	private GUIStyle? _box;
	private GUIStyle? _label;
	private GUIStyle? _title;
	private GUIStyle? _hint;
	private Vector2 _chatScroll;
	private Vector2 _mapScroll;
	private float _hintUntil;
	private bool _unfocusGui;

	internal IEnumerable<GhostCar> AllGhosts() => _ghosts.Values;

	internal GhostCar? GetGhost(ulong peerId) =>
		_ghosts.TryGetValue(peerId, out var g) ? g : null;

	internal void ApplyPeerLoadoutVisual(ulong peer, List<string> ids)
	{
		if (!_ghosts.TryGetValue(peer, out var ghost) || ghost?.BoundEnemy == null) return;
		WorldSync.AttachLoadoutVisuals(peer, ghost.BoundEnemy.transform, ids);
	}

	private void Awake()
	{
		Instance = this;
		UiVisible = Plugin.Instance.UiStartVisible.Value;
		_hintUntil = Time.unscaledTime + 8f;
		SceneManager.sceneLoaded += OnUnitySceneLoaded;
		TryAutoConnectFromEnv();
	}

	private void OnDestroy()
	{
		SceneManager.sceneLoaded -= OnUnitySceneLoaded;
		if (Instance == this) Instance = null;
		Session.Leave();
	}

	private void OnUnitySceneLoaded(Scene scene, LoadSceneMode mode)
	{
		_ = mode;
		bool isTrack = !string.IsNullOrEmpty(scene.name) &&
		               scene.name.StartsWith("SCENE_TRACK_", StringComparison.Ordinal);
		bool isUi = !string.IsNullOrEmpty(scene.name) &&
		            scene.name.StartsWith("SCENE_UI_", StringComparison.Ordinal);

		if (isTrack)
			ClearGhosts();
		if (isUi || (!isTrack && Session.Linked))
		{
			Session.InLobbyPhase = true;
			Session.RaceStartReceived = false;
			WorldSync.RestoreMenuCursor();
		}

		if (!Session.Linked)
		{
			if (isUi) WorldSync.RestoreMenuCursor();
			return;
		}

		if (Session.IsHost && (isTrack || isUi))
			Session.BroadcastHostScene(scene.name);

		if (isTrack)
		{
			Session.InLobbyPhase = false;
			// Guests already received RaceSetup; re-apply weather after scene objects exist.
			if (RaceSync.HasSetup)
				RaceSync.ApplyRunContext();
			// Before LoadoutSpawner logic finishes late — restore gear from this client's save.
			WorldSync.RestoreLocalLoadoutFromSave();
			CarLookSync.MarkDirty();
		}

		if (isUi)
			WorldSync.RestoreMenuCursor();

		if (!string.IsNullOrEmpty(_loadingHostScene) &&
		    string.Equals(scene.name, _loadingHostScene, StringComparison.OrdinalIgnoreCase))
			_loadingHostScene = null;
	}

	/// <summary>Called after TrackManager.Start — grid exists, enemies spawned.</summary>
	public void OnTrackReady(TrackManager tm)
	{
		if (tm == null || !Session.Linked) return;
		WorldSync.RestoreLocalLoadoutFromSave();
		RaceSync.ApplyLapsToTrack(tm);
		RaceSync.ApplyRunContext();
		RaceSync.StripBotsIfNeeded(tm);
		RaceSync.PlaceLocalPlayer(tm, Session.LocalSlot);
		if (!Session.IsHost && !RaceSync.NoBots)
			RaceSync.FreezeGuestBots(tm);
		// Fit after place — LoadoutSpawner may have Awoke with empty PlayerLoadout.
		WorldSync.EnsureLocalLoadoutFitted();
		BroadcastLocalCarLook(force: true);
		WorldSync.ForceBroadcastLoadout(Session);
		Plugin.Log.LogInfo($"Track ready: localSlot={Session.LocalSlot} peers={Session.Peers.Count} free={RaceSync.FreePlay} noBots={RaceSync.NoBots} loadout={(PlayerLoadout.Known ? PlayerLoadout.Equipped.Count : 0)}");
	}

	/// <summary>Guest: jump onto the host's track or lobby UI.</summary>
	public void OnHostScene(string sceneName)
	{
		if (string.IsNullOrWhiteSpace(sceneName) || Session.IsHost) return;
		if (string.Equals(SceneManager.GetActiveScene().name, sceneName, StringComparison.OrdinalIgnoreCase))
			return;
		if (string.Equals(_loadingHostScene, sceneName, StringComparison.OrdinalIgnoreCase))
			return;
		_loadingHostScene = sceneName;
		Plugin.Log.LogInfo($"Syncing to host scene: {sceneName}");
		Session.AddChat($"* Joining host: {sceneName}");
		try
		{
			if (!Application.CanStreamedLevelBeLoaded(sceneName))
			{
				Plugin.Log.LogWarning($"Host scene not in build: {sceneName}");
				_loadingHostScene = null;
				return;
			}
			SceneManager.LoadScene(sceneName);
		}
		catch (Exception ex)
		{
			Plugin.Log.LogWarning($"Host scene load failed: {ex.Message}");
			_loadingHostScene = null;
		}
	}

	private void Update()
	{
		try { Session.Pump(); } catch { /* steam/lan */ }

		var p = Plugin.Instance;
		if (Input.GetKeyDown(p.ToggleUiKey.Value))
			SetUiVisible(!UiVisible);
		if (Input.GetKeyDown(KeyCode.Escape) && UiVisible)
			SetUiVisible(false);
		// F7 always tries Steam host (do not require Kind==Steam — offline default blocked hosting).
		if (Input.GetKeyDown(p.HostKey.Value))
			Session.HostSteam();
		if (Input.GetKeyDown(p.LeaveKey.Value))
		{
			Session.Leave();
			ClearGhosts();
		}

		if (!Session.Linked) return;
		SendLocalPose();
		SendBotPoses();
		SendLocalCarLook();
		WorldSync.BroadcastLocalLoadoutIfChanged(Session);
		WorldSync.PulseLocalNitroOff(Session);
		WorldSync.PulsePeerCollisions(Session);
		WorldSync.PulseCutoffSync(Session);
		if (Time.frameCount % 20 == 0)
			WorldSync.BroadcastLocalLap(Session);
		foreach (var g in _ghosts.Values) g?.Tick(Time.deltaTime);
	}

	private void TryAutoConnectFromEnv()
	{
		var role = Environment.GetEnvironmentVariable("HGMP_ROLE");
		if (string.IsNullOrWhiteSpace(role))
		{
			try
			{
				var dir = Path.GetDirectoryName(typeof(Plugin).Assembly.Location);
				var f = Path.Combine(dir ?? ".", "hgmp.role");
				if (File.Exists(f)) role = File.ReadAllText(f).Trim();
			}
			catch { /* ignore */ }
		}
		if (string.IsNullOrWhiteSpace(role)) return;

		var host = Environment.GetEnvironmentVariable("HGMP_LAN_HOST") ?? "127.0.0.1";
		var portStr = Environment.GetEnvironmentVariable("HGMP_LAN_PORT") ?? LanPortText;
		int.TryParse(portStr, out var port);
		if (port <= 0) port = NetProtocol.DefaultLanPort;

		role = role.Trim().ToLowerInvariant();
		Plugin.Log.LogInfo($"Auto role: {role}");
		switch (role)
		{
			case "host-lan":
			case "lan-host":
				Session.HostLan(port);
				break;
			case "join-lan":
			case "lan-join":
				Session.JoinLan(host, port);
				break;
			case "host-steam":
			case "steam-host":
				Session.HostSteam();
				break;
		}
	}

	private void SendLocalPose()
	{
		if (Time.unscaledTime < _nextPose) return;
		var hz = Mathf.Clamp(Plugin.Instance.PoseHz.Value, 5f, 60f);
		_nextPose = Time.unscaledTime + 1f / hz;

		var car = FindLocalPlayerCar();
		if (car == null) return;
		float hp = car.carLink != null ? car.carLink.hp : 100f;
		var t = car.transform;
		Session.Broadcast(NetProtocol.Pose(Session.LocalId, t.position, t.rotation, car.forwardSpeed, hp, Time.unscaledTime), false);
	}

	private void SendBotPoses()
	{
		if (!Session.IsHost) return;
		if (Time.unscaledTime < _nextBotPose) return;
		_nextBotPose = Time.unscaledTime + 1f / 15f;
		var tm = UnityEngine.Object.FindFirstObjectByType<TrackManager>();
		if (tm == null || !tm.raceStarted && tm.playerCarController == null) return;
		// Only while a real track with enemies is up.
		if (tm.Enemies == null || tm.Enemies.Count == 0) return;
		var bots = RaceSync.CollectHostBotPoses(tm);
		if (bots.Count == 0) return;
		Session.Broadcast(NetProtocol.BotPose(bots), false);
	}

	private void SendLocalCarLook()
	{
		if (Time.unscaledTime < _nextCarLook) return;
		_nextCarLook = Time.unscaledTime + 1f; // 1 Hz + dirty from ApplySkin patches
		if (!CarLookSync.NeedsSend(out var look)) return;
		Session.Broadcast(NetProtocol.CarLook(look), true);
		CarLookSync.RememberSent(look);
	}

	/// <summary>Force a CarLook packet (on Hello / track ready).</summary>
	public void BroadcastLocalCarLook(bool force = true)
	{
		if (!Session.Linked) return;
		if (force) CarLookSync.MarkDirty();
		if (!CarLookSync.TryCaptureLocal(out var look)) return;
		Session.Broadcast(NetProtocol.CarLook(look), true);
		CarLookSync.RememberSent(look);
	}

	public void ApplyRemoteCarLook(ulong from, CarLookData look)
	{
		CarLookSync.StorePending(from, look);
		if (_ghosts.TryGetValue(from, out var ghost) && ghost != null)
			ghost.ApplyLook(look);
	}

	public void ApplyRemotePose(ulong from, ulong ownerId, Vector3 pos, Quaternion rot, float speed, float hp, float remoteTime)
	{
		_ = ownerId;
		if (!_ghosts.TryGetValue(from, out var ghost) || ghost == null || ghost.Root == null)
		{
			if (ghost != null)
			{
				try { ghost.Destroy(); } catch { /* already gone */ }
				_ghosts.Remove(from);
			}
			var name = Session.Peers.TryGetValue(from, out var p) ? p.Name : from.ToString();
			int slot = Session.SlotFor(from);
			ghost = GhostCar.Spawn(from, name, Plugin.Instance.GhostTransparent.Value, slot);
			_ghosts[from] = ghost;
			if (CarLookSync.TryGetPending(from, out var pending))
				ghost.ApplyLook(pending);
		}
		else if (ghost.PreferredSlot < 0)
			ghost.PreferredSlot = Session.SlotFor(from);
		ghost.PushTarget(pos, rot, speed, hp, remoteTime);
	}

	public void DealDamageToPeer(ulong peerId, float amount, Vector3 point)
	{
		if (amount <= 0f) return;
		if (_lastDamageSent.TryGetValue(peerId, out var last) && Time.unscaledTime - last < 0.03f) return;
		_lastDamageSent[peerId] = Time.unscaledTime;
		// Broadcast with targetPeer — more reliable than SendTo alone on LAN remap.
		Session.Broadcast(NetProtocol.Damage(amount, point, peerId), true);
		Session.AddChat($"* Hit {peerId} for {amount:0}");
	}

	public void ApplyIncomingDamage(float amount, Vector3 point)
	{
		var car = FindLocalPlayerCar();
		if (car?.carLink == null) return;
		car.carLink.TakeCrashDamage(amount, shake: true, DamageKind.Other);
		Session.AddChat($"* Took {amount:0} dmg");
		_ = point;
	}

	public void OnPeerWrecked(ulong peerId)
	{
		if (_ghosts.TryGetValue(peerId, out var g))
			g.PushTarget(g.Root.transform.position, g.Root.transform.rotation, 0f, 0f, Time.unscaledTime);
	}

	public void DespawnGhost(ulong id)
	{
		if (_ghosts.TryGetValue(id, out var g))
		{
			g.Destroy();
			_ghosts.Remove(id);
		}
	}

	private void ClearGhosts()
	{
		foreach (var g in _ghosts.Values) g.Destroy();
		_ghosts.Clear();
	}

	private static CarControllerNew? FindLocalPlayerCar()
	{
		var all = UnityEngine.Object.FindObjectsByType<CarControllerNew>(FindObjectsSortMode.None);
		foreach (var c in all)
			if (c != null && c.isPlayer && c.isActiveAndEnabled) return c;
		var tm = UnityEngine.Object.FindFirstObjectByType<TrackManager>();
		return tm != null ? tm.playerCarController : null;
	}

	private void SetUiVisible(bool visible)
	{
		UiVisible = visible;
		if (!visible)
			_unfocusGui = true;
	}

	private void OnGUI()
	{
		EnsureStyles();
		if (_unfocusGui)
		{
			GUI.FocusControl(null);
			_unfocusGui = false;
		}
		if (!UiVisible)
		{
			// No TextFields while hidden — keyboard / Steam overlay stay free.
			if (Time.unscaledTime < _hintUntil)
			{
				var key = Plugin.Instance.ToggleUiKey.Value;
				GUI.Label(new Rect(12f, 12f, 420f, 22f),
					$"Half Grip MP — press {key} for menu (hidden so Steam works)", _hint);
			}
			return;
		}

		const float w = 420f;
		var rect = new Rect(12f, 12f, w, Mathf.Min(640f, Screen.height - 24f));
		GUI.Box(rect, GUIContent.none, _box);
		GUILayout.BeginArea(new Rect(rect.x + 10, rect.y + 8, w - 20, rect.height - 16));

		GUILayout.BeginHorizontal();
		GUILayout.Label("HALF GRIP MULTIPLAYER", _title);
		if (GUILayout.Button("Hide", GUILayout.Width(52), GUILayout.Height(22)))
			SetUiVisible(false);
		GUILayout.EndHorizontal();
		GUILayout.Label($"v{PluginInfo.Version}  PvP  |  {Session.Status}", _label);
		if (Session.Linked)
		{
			GUILayout.Label(
				$"RTT {Session.LastRttMs:0}ms  peers {Session.Peers.Count}  slot {Session.LocalSlot}  " +
				$"ready {(Session.LocalReady ? "YES" : "no")}/{(Session.AllPeersReady() ? "ALL" : "wait")}",
				_label);
			GUILayout.Label(Session.InLobbyPhase ? "Phase: LOBBY — press Ready, host starts race" : "Phase: RACE", _label);
		}

		GUILayout.Space(4);
		GUILayout.Label("— Steam —", _label);
		GUILayout.BeginHorizontal();
		if (GUILayout.Button("Host Steam", GUILayout.Height(26))) Session.HostSteam();
		if (GUILayout.Button("Invite", GUILayout.Height(26))) Session.InviteSteam();
		if (GUILayout.Button("Leave", GUILayout.Height(26))) { Session.Leave(); ClearGhosts(); }
		GUILayout.EndHorizontal();
		GUILayout.BeginHorizontal();
		GUILayout.Label("Lobby ID", GUILayout.Width(60));
		JoinLobbyIdText = GUILayout.TextField(JoinLobbyIdText);
		if (GUILayout.Button("Join", GUILayout.Width(50)) && ulong.TryParse(JoinLobbyIdText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var lid))
			Session.JoinSteam(lid);
		GUILayout.EndHorizontal();

		GUILayout.Space(4);
		GUILayout.Label("— LAN / localhost —", _label);
		GUILayout.BeginHorizontal();
		GUILayout.Label("IP", GUILayout.Width(20));
		LanHostText = GUILayout.TextField(LanHostText, GUILayout.Width(110));
		GUILayout.Label("Port", GUILayout.Width(30));
		LanPortText = GUILayout.TextField(LanPortText, GUILayout.Width(50));
		GUILayout.EndHorizontal();
		GUILayout.BeginHorizontal();
		if (GUILayout.Button("Host LAN", GUILayout.Height(26)))
		{
			var role = Environment.GetEnvironmentVariable("HGMP_ROLE") ?? "";
			if (role.IndexOf("join", StringComparison.OrdinalIgnoreCase) >= 0)
				Session.AddChat("* Host LAN blocked (join role)");
			else
			{
				int.TryParse(LanPortText, out var port);
				Session.HostLan(port > 0 ? port : NetProtocol.DefaultLanPort);
			}
		}
		if (GUILayout.Button("Join LAN", GUILayout.Height(26)))
		{
			int.TryParse(LanPortText, out var port);
			Session.JoinLan(LanHostText.Trim(), port > 0 ? port : NetProtocol.DefaultLanPort);
		}
		GUILayout.EndHorizontal();

		if (Session.Linked)
		{
			GUILayout.Space(6);
			GUILayout.Label("— Lobby ready —", _label);
			foreach (var kv in Session.Peers)
			{
				var slot = Session.SlotFor(kv.Key);
				GUILayout.Label($"  [{slot}] {kv.Value.Name}: {(kv.Value.Ready ? "READY" : "…")}", _label);
			}
			GUILayout.Label($"  [{Session.LocalSlot}] YOU: {(Session.LocalReady ? "READY" : "…")}", _label);
			GUILayout.BeginHorizontal();
			if (GUILayout.Button(Session.LocalReady ? "Unready" : "Ready", GUILayout.Height(28)))
				Session.SetReady(!Session.LocalReady);
			if (Session.IsHost && Session.InLobbyPhase)
			{
				GUI.enabled = Session.AllPeersReady() && Session.Peers.Count > 0;
				if (GUILayout.Button("Host: allow start", GUILayout.Height(28)))
					Session.AddChat("* All ready — use the game Start Race button");
				GUI.enabled = true;
			}
			GUILayout.EndHorizontal();
			if (Session.IsHost && Session.InLobbyPhase && !Session.AllPeersReady())
				GUILayout.Label("Waiting for every driver to press Ready before Start Race.", _label);
		}

		GUILayout.Space(4);
		GUILayout.Label("— Free play / sandbox —", _label);
		RaceSync.FreePlay = GUILayout.Toggle(RaceSync.FreePlay, "Free play (no auto-finish)");
		RaceSync.NoBots = GUILayout.Toggle(RaceSync.NoBots, "No bots (humans only)");
		if (Session.IsHost || !Session.Linked)
		{
			if (GUILayout.Button("Open Playground", GUILayout.Height(24)))
				HostOpenPlayground();
			_mapScroll = GUILayout.BeginScrollView(_mapScroll, GUILayout.Height(110));
			var cfg = RunConfig.Instance;
			if (cfg?.maps != null)
			{
				foreach (var map in cfg.maps)
				{
					if (map == null) continue;
					string label = !string.IsNullOrEmpty(map.displayName) ? map.displayName : map.name;
					if (GUILayout.Button($"{label}  [#{map.trackIndex}]", GUILayout.Height(22)))
						HostLoadMap(map);
				}
			}
			else
				GUILayout.Label("(RunConfig maps unavailable — open a run once)", _label);
			GUILayout.EndScrollView();
		}
		else
			GUILayout.Label("Guest: host picks the map.", _label);

		GUILayout.Space(4);
		GUILayout.Label("Chat", _label);
		_chatScroll = GUILayout.BeginScrollView(_chatScroll, GUILayout.Height(80));
		foreach (var line in Session.ChatLog) GUILayout.Label(line, _label);
		GUILayout.EndScrollView();
		GUILayout.BeginHorizontal();
		ChatDraft = GUILayout.TextField(ChatDraft);
		if (GUILayout.Button("Send", GUILayout.Width(50)) && !string.IsNullOrWhiteSpace(ChatDraft))
		{
			Session.SendChat(ChatDraft);
			ChatDraft = "";
		}
		GUILayout.EndHorizontal();

		GUILayout.Space(4);
		GUILayout.Label("Host: Free play + map button, or normal run → Ready → Start Race.", _label);
		GUILayout.Label($"Hotkeys: {Plugin.Instance.ToggleUiKey.Value}/Esc hide · F7 Steam Host · F8 Leave", _label);
		GUILayout.EndArea();
	}

	private void HostOpenPlayground()
	{
		RaceSync.FreePlay = true;
		RaceSync.NoBots = true;
		try
		{
			if (PlayerLoadout.Known)
			{
				foreach (var up in PlayerLoadout.Equipped)
					up?.Apply();
			}
		}
		catch { /* */ }
		Playground.Open();
		if (Session.Linked && Session.IsHost)
		{
			Session.BroadcastHostScene(Playground.SCENE);
			Session.BroadcastRaceSetup();
		}
		Session.AddChat("* Playground (free / no bots)");
	}

	private void HostLoadMap(MapDefinition map)
	{
		if (map == null) return;
		RaceSync.FreePlay = true;
		try
		{
			if (SceneLoader.Instance == null)
			{
				Session.AddChat("* SceneLoader missing");
				return;
			}
			Session.InLobbyPhase = false;
			Session.BroadcastRaceSetup();
			SceneLoader.Instance.LoadTrackScene(map.trackIndex, 1f);
			Session.AddChat($"* Loading map {map.displayName ?? map.name}");
			Plugin.Log.LogInfo($"Host free-play map trackIndex={map.trackIndex} noBots={RaceSync.NoBots}");
		}
		catch (Exception ex)
		{
			Session.AddChat($"* Map load failed: {ex.Message}");
			Plugin.Log.LogWarning($"HostLoadMap: {ex.Message}");
		}
	}

	private void EnsureStyles()
	{
		if (_box != null) return;
		_box = new GUIStyle(GUI.skin.box);
		var tex = new Texture2D(1, 1);
		tex.SetPixel(0, 0, new Color(0.04f, 0.06f, 0.09f, 0.88f));
		tex.Apply();
		_box.normal.background = tex;
		_label = new GUIStyle(GUI.skin.label) { fontSize = 12, wordWrap = true };
		_title = new GUIStyle(GUI.skin.label) { fontSize = 14, fontStyle = FontStyle.Bold };
		_hint = new GUIStyle(GUI.skin.label)
		{
			fontSize = 12,
			normal = { textColor = new Color(0.85f, 0.9f, 1f, 0.75f) },
		};
	}
}

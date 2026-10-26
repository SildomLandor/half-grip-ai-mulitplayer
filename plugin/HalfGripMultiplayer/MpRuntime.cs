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
	public bool UiVisible = true;
	public string ChatDraft = "";
	public string JoinLobbyIdText = "";
	public string LanHostText = "127.0.0.1";
	public string LanPortText = NetProtocol.DefaultLanPort.ToString();

	private readonly Dictionary<ulong, GhostCar> _ghosts = new();
	private float _nextPose;
	private float _nextBotPose;
	private float _lastDamageSent;
	private string? _loadingHostScene;
	private GUIStyle? _box;
	private GUIStyle? _label;
	private GUIStyle? _title;
	private Vector2 _chatScroll;

	internal IEnumerable<GhostCar> AllGhosts() => _ghosts.Values;

	private void Awake()
	{
		Instance = this;
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
		}

		if (!Session.Linked) return;

		if (Session.IsHost && (isTrack || isUi))
			Session.BroadcastHostScene(scene.name);

		if (isTrack)
		{
			Session.InLobbyPhase = false;
			// Guests already received RaceSetup; re-apply weather after scene objects exist.
			if (RaceSync.HasSetup)
				RaceSync.ApplyRunContext();
		}

		if (!string.IsNullOrEmpty(_loadingHostScene) &&
		    string.Equals(scene.name, _loadingHostScene, StringComparison.OrdinalIgnoreCase))
			_loadingHostScene = null;
	}

	/// <summary>Called after TrackManager.Start — grid exists, enemies spawned.</summary>
	public void OnTrackReady(TrackManager tm)
	{
		if (tm == null || !Session.Linked) return;
		RaceSync.ApplyLapsToTrack(tm);
		RaceSync.ApplyRunContext();
		RaceSync.PlaceLocalPlayer(tm, Session.LocalSlot);
		if (!Session.IsHost)
			RaceSync.FreezeGuestBots(tm);
		Plugin.Log.LogInfo($"Track ready: localSlot={Session.LocalSlot} peers={Session.Peers.Count}");
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
		if (Input.GetKeyDown(p.ToggleUiKey.Value)) UiVisible = !UiVisible;
		if (Input.GetKeyDown(p.HostKey.Value) && Session.Kind == TransportKind.Steam)
			Session.HostSteam();
		if (Input.GetKeyDown(p.LeaveKey.Value))
		{
			Session.Leave();
			ClearGhosts();
		}

		if (!Session.Linked) return;
		SendLocalPose();
		SendBotPoses();
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
		}
		else if (ghost.PreferredSlot < 0)
			ghost.PreferredSlot = Session.SlotFor(from);
		ghost.PushTarget(pos, rot, speed, hp, remoteTime);
	}

	public void DealDamageToPeer(ulong peerId, float amount, Vector3 point)
	{
		if (amount <= 0f) return;
		if (Time.unscaledTime - _lastDamageSent < 0.03f) return;
		_lastDamageSent = Time.unscaledTime;
		Session.SendTo(peerId, NetProtocol.Damage(amount, point), true);
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

	private void OnGUI()
	{
		if (!UiVisible) return;
		EnsureStyles();
		const float w = 400f;
		var rect = new Rect(12f, 12f, w, 560f);
		GUI.Box(rect, GUIContent.none, _box);
		GUILayout.BeginArea(new Rect(rect.x + 10, rect.y + 8, w - 20, rect.height - 16));

		GUILayout.Label("HALF GRIP MULTIPLAYER", _title);
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
		GUILayout.Label("Chat", _label);
		_chatScroll = GUILayout.BeginScrollView(_chatScroll, GUILayout.Height(100));
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
		GUILayout.Label("Host opens new/continue run → guests join lobby. All Ready → host Start Race.", _label);
		GUILayout.Label($"Hotkeys: {Plugin.Instance.ToggleUiKey.Value} UI · F7 Steam Host · F8 Leave", _label);
		GUILayout.EndArea();
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
	}
}

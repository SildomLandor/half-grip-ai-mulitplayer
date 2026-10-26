using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace HalfGripMultiplayer;

internal sealed class MpRuntime : MonoBehaviour
{
	public static MpRuntime? Instance { get; private set; }

	/// <summary>Create or return the runtime. Prefer hosting on the BepInEx plugin GO.</summary>
	public static MpRuntime Ensure(GameObject? host = null)
	{
		if (Instance != null) return Instance;
		GameObject go = host != null ? host : new GameObject("HalfGripMultiplayer");
		if (host == null) UnityEngine.Object.DontDestroyOnLoad(go);
		var existing = go.GetComponent<MpRuntime>();
		if (existing != null)
		{
			Instance = existing;
			return existing;
		}
		var rt = go.AddComponent<MpRuntime>();
		Plugin.Log.LogInfo("MpRuntime created/restored");
		return rt;
	}

	public readonly MpSession Session = new();
	public bool UiVisible;
	public string ChatDraft = "";
	public string JoinLobbyIdText = "";
	public string LanHostText = "127.0.0.1";
	public string LanPortText = NetProtocol.DefaultLanPort.ToString();

	private readonly Dictionary<ulong, GhostCar> _ghosts = new();
	private readonly Dictionary<ulong, (Vector3 pos, Quaternion rot, float speed, float hp, float t, float steer, byte flags)> _pendingPoses = new();
	private bool _trackGhostsReady;
	private float _nextPose;
	private float _nextBotPose;
	private float _nextCarLook;
	private readonly Dictionary<ulong, float> _lastDamageSent = new();
	private readonly Dictionary<ulong, float> _lastDamageToPeer = new();
	private readonly Dictionary<ulong, float> _lastHitChat = new();
	private bool _lastBurnSent;
	private string? _loadingHostScene;
	private GUIStyle? _box;
	private GUIStyle? _label;
	private GUIStyle? _title;
	private GUIStyle? _hint;
	private Vector2 _chatScroll;
	private Vector2 _mapScroll;
	private float _hintUntil;
	private bool _unfocusGui;
	private int _loadoutRefitFrames;
	private float _nextPingSend;
	private bool _quickChat;
	private string _quickChatDraft = "";
	private readonly List<string> _toastChat = new();
	private float _toastUntil;
	private bool _spectating;
	private int _spectateIndex;
	private Transform? _spectateCamParent;
	private Vector3 _spectateCamLocalPos;
	private Quaternion _spectateCamLocalRot;
	private float _nextTaunt;

	internal IEnumerable<GhostCar> AllGhosts() => _ghosts.Values;

	internal GhostCar? GetGhost(ulong peerId) =>
		_ghosts.TryGetValue(peerId, out var g) ? g : null;

	/// <summary>Late-join / first Hello while already on a track — spawn ghost immediately.</summary>
	internal void EnsurePeerGhost(ulong peerId, string? name = null)
	{
		if (!_trackGhostsReady) return;
		if (peerId == 0 || peerId == Session.LocalId) return;
		if (_ghosts.TryGetValue(peerId, out var existing) && existing?.Root != null)
		{
			if (existing.BoundEnemy == null) existing.ForceRebind();
			return;
		}
		if (existing != null)
		{
			try { existing.Destroy(); } catch { /* */ }
			_ghosts.Remove(peerId);
		}
		string label = name ?? (Session.Peers.TryGetValue(peerId, out var p) ? p.Name : peerId.ToString());
		int slot = Session.SlotFor(peerId);
		var ghost = GhostCar.Spawn(peerId, label, Plugin.Instance.GhostTransparent.Value, slot);
		_ghosts[peerId] = ghost;
		if (CarLookSync.TryGetPending(peerId, out var pending))
			ghost.ApplyLook(pending);
		if (WorldSync.TryGetLoadout(peerId, out var ids))
			WorldSync.AttachLoadoutVisuals(peerId, ghost.BoundEnemy != null
				? ghost.BoundEnemy.transform
				: ghost.Root.transform, ids);
	}

	/// <summary>LAN Hello remaps temp EP peer → guest LocalId; keep ghosts/FX keyed correctly.</summary>
	internal void RemapPeerIdentity(ulong fromId, ulong toId)
	{
		if (fromId == 0 || toId == 0 || fromId == toId) return;
		if (_ghosts.TryGetValue(fromId, out var ghost))
		{
			_ghosts.Remove(fromId);
			if (!_ghosts.ContainsKey(toId))
			{
				_ghosts[toId] = ghost;
				if (ghost.Hitbox != null) ghost.Hitbox.PeerId = toId;
			}
			else
			{
				try { ghost.Destroy(); } catch { /* */ }
			}
		}
		if (_lastDamageSent.TryGetValue(fromId, out var dmg))
		{
			_lastDamageSent.Remove(fromId);
			_lastDamageSent[toId] = dmg;
		}
		if (_lastDamageToPeer.TryGetValue(fromId, out var hit))
		{
			_lastDamageToPeer.Remove(fromId);
			_lastDamageToPeer[toId] = hit;
		}
		WorldSync.RemapPeer(fromId, toId);
		CarLookSync.RemapPeer(fromId, toId);
		Plugin.Log.LogInfo($"Remapped peer identity {fromId} → {toId}");
	}

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
		{
			_trackGhostsReady = false;
			ClearGhosts();
			IceSpikeSyncPatches.ClearFired();
		}
		if (isUi || (!isTrack && Session.Linked))
		{
			_trackGhostsReady = false;
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
		RaceSync.PlaceLocalPlayer(tm, Session.LocalSlot);
		// Bind ghosts to slot cars BEFORE NoBots strip so remotes keep real bodies.
		_trackGhostsReady = true;
		PrebindPeerGhosts();
		RaceSync.StripBotsIfNeeded(tm);
		if (!Session.IsHost && !RaceSync.NoBots)
			RaceSync.FreezeGuestBots(tm);
		// Fit after place — LoadoutSpawner may have Awoke with empty PlayerLoadout.
		WorldSync.EnsureLocalLoadoutFitted();
		_loadoutRefitFrames = 90; // retry while spawner/Awake settle
		BroadcastLocalCarLook(force: true);
		WorldSync.ForceBroadcastLoadout(Session);
		FlushPendingPoses();
		Plugin.Log.LogInfo($"Track ready: localSlot={Session.LocalSlot} peers={Session.Peers.Count} free={RaceSync.FreePlay} noBots={RaceSync.NoBots} loadout={(PlayerLoadout.Known ? PlayerLoadout.Equipped.Count : 0)}");
	}

	/// <summary>Spawn/claim ghost cars for known peers as soon as the track exists.</summary>
	private void PrebindPeerGhosts()
	{
		foreach (var kv in Session.Peers)
		{
			if (_ghosts.TryGetValue(kv.Key, out var existing) && existing?.Root != null)
			{
				if (existing.PreferredSlot < 0)
					existing.PreferredSlot = Session.SlotFor(kv.Key);
				// Mid-load poses often left a ghost with no bot — rebind now that enemies exist.
				if (existing.BoundEnemy == null)
					existing.ForceRebind();
				existing.PushTarget(existing.Root.transform.position, existing.Root.transform.rotation,
					0f, existing.Hp, Time.unscaledTime);
				continue;
			}
			if (existing != null)
			{
				try { existing.Destroy(); } catch { /* */ }
				_ghosts.Remove(kv.Key);
			}
			int slot = Session.SlotFor(kv.Key);
			var ghost = GhostCar.Spawn(kv.Key, kv.Value.Name, Plugin.Instance.GhostTransparent.Value, slot);
			_ghosts[kv.Key] = ghost;
			if (CarLookSync.TryGetPending(kv.Key, out var pending))
				ghost.ApplyLook(pending);
			if (WorldSync.TryGetLoadout(kv.Key, out var ids))
				WorldSync.AttachLoadoutVisuals(kv.Key, ghost.BoundEnemy != null
					? ghost.BoundEnemy.transform
					: ghost.Root.transform, ids);
		}
	}

	private void FlushPendingPoses()
	{
		if (_pendingPoses.Count == 0) return;
		var snap = new List<KeyValuePair<ulong, (Vector3 pos, Quaternion rot, float speed, float hp, float t, float steer, byte flags)>>(_pendingPoses);
		_pendingPoses.Clear();
		foreach (var kv in snap)
		{
			var p = kv.Value;
			ApplyRemotePose(kv.Key, kv.Key, p.pos, p.rot, p.speed, p.hp, p.t, p.steer, p.flags);
		}
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

	private static bool KeyDown(KeyCode key)
	{
		if (key == KeyCode.None) return false;
		// Prefer the game's input path (works with muted/pad/physical remap).
		try
		{
			if (GameInput.KeyDown(key)) return true;
		}
		catch { /* */ }
		try
		{
			if (Input.GetKeyDown(key)) return true;
		}
		catch { /* legacy input disabled */ }
		try
		{
			var kb = UnityEngine.InputSystem.Keyboard.current;
			if (kb == null) return false;
			return key switch
			{
				KeyCode.F4 => kb.f4Key.wasPressedThisFrame,
				KeyCode.F5 => kb.f5Key.wasPressedThisFrame,
				KeyCode.F6 => kb.f6Key.wasPressedThisFrame,
				KeyCode.F7 => kb.f7Key.wasPressedThisFrame,
				KeyCode.F8 => kb.f8Key.wasPressedThisFrame,
				KeyCode.Escape => kb.escapeKey.wasPressedThisFrame,
				KeyCode.Return => kb.enterKey.wasPressedThisFrame,
				KeyCode.KeypadEnter => kb.numpadEnterKey.wasPressedThisFrame,
				KeyCode.Tab => kb.tabKey.wasPressedThisFrame,
				KeyCode.Mouse2 => UnityEngine.InputSystem.Mouse.current != null &&
				                  UnityEngine.InputSystem.Mouse.current.middleButton.wasPressedThisFrame,
				_ => false,
			};
		}
		catch { return false; }
	}

	private void Update()
	{
		try { Session.Pump(); } catch { /* steam/lan */ }

		var p = Plugin.Instance;
		if (KeyDown(p.ToggleUiKey.Value))
		{
			Plugin.Log.LogInfo($"Hotkey {p.ToggleUiKey.Value} → toggle menu (was {UiVisible})");
			SetUiVisible(!UiVisible);
		}
		if (KeyDown(KeyCode.Escape) && UiVisible)
			SetUiVisible(false);
		// F7 always tries Steam host (do not require Kind==Steam — offline default blocked hosting).
		if (KeyDown(p.HostKey.Value))
			Session.HostSteam();
		if (KeyDown(p.LeaveKey.Value))
		{
			ExitSpectate();
			Session.Leave();
			ClearGhosts();
		}
		if (Session.Linked && KeyDown(p.PingKey.Value) && Time.unscaledTime >= _nextPingSend && !_quickChat)
			TrySendWorldPing();
		if (Session.Linked && Session.InLobbyPhase && !_quickChat && !UiVisible &&
		    KeyDown(p.ReadyKey.Value))
		{
			Session.SetReady(!Session.LocalReady);
			PushToast(Session.LocalReady ? "* Ready" : "* Unready");
		}
		if (Session.Linked && Session.IsHost && Session.InLobbyPhase && !_quickChat && !UiVisible &&
		    KeyDown(p.ForceStartKey.Value))
		{
			Session.UnlockForceStart();
			PushToast("* Force start ON — press Start Race");
		}
		if (Session.Linked && !UiVisible)
			TickQuickChat();

		if (!Session.Linked) return;
		if (Time.frameCount % 60 == 0)
		{
			PruneStalePeers();
			if (Session.Kind == TransportKind.Steam)
				Session.PruneSteamLobbyDropouts();
		}
		if (_loadoutRefitFrames > 0)
		{
			_loadoutRefitFrames--;
			if (_loadoutRefitFrames % 30 == 0)
				WorldSync.EnsureLocalLoadoutFitted();
		}
		if (Time.frameCount % 45 == 0)
			RaceSync.EnsureLocalLapTracking();
		WorldSync.PulseLocalNitroOff(Session);
		SendLocalPose();
		SendBotPoses();
		SendLocalCarLook();
		WorldSync.BroadcastLocalLoadoutIfChanged(Session);
		WorldSync.PulsePeerCollisions(Session);
		WorldSync.PulseCutoffSync(Session);
		WorldSync.PulseTrainSync(Session);
		WorldSync.PulseTornadoSync(Session);
		if (Time.frameCount % 20 == 0)
			WorldSync.BroadcastLocalLap(Session);
		TickSpectate();
		TickTauntKeys();
		TickDrivingHints();
		foreach (var g in _ghosts.Values) g?.Tick(Time.deltaTime);
	}

	private bool _lastWrongWay;
	private bool _lastStuck;
	private float _nextHintPoll;

	private void TickDrivingHints()
	{
		if (Time.unscaledTime < _nextHintPoll) return;
		_nextHintPoll = Time.unscaledTime + 0.4f;
		var tm = UnityEngine.Object.FindFirstObjectByType<TrackManager>();
		if (tm == null) return;
		object? logic = null;
		try
		{
			var f = AccessTools.Field(typeof(TrackManager), "playerReset")
			        ?? AccessTools.Field(typeof(TrackManager), "resetLogic")
			        ?? AccessTools.Field(typeof(TrackManager), "PlayerReset");
			if (f == null)
			{
				foreach (var field in typeof(TrackManager).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
				{
					if (field.FieldType == typeof(PlayerResetLogic)) { f = field; break; }
				}
			}
			logic = f?.GetValue(tm);
		}
		catch { return; }
		if (logic == null) return;
		bool wrong = false;
		bool stuck = false;
		try
		{
			var backwards = AccessTools.Method(typeof(PlayerResetLogic), "IsGoingBackwards");
			if (backwards != null) wrong = (bool)backwards.Invoke(logic, null)!;
			var far = AccessTools.Method(typeof(PlayerResetLogic), "FarFromTrack");
			if (far != null) stuck = (bool)far.Invoke(logic, null)!;
		}
		catch { return; }
		if (wrong != _lastWrongWay)
		{
			_lastWrongWay = wrong;
			Session.Broadcast(NetProtocol.HintFx(0, wrong), false);
			if (wrong) PushToast("* wrong way");
		}
		if (stuck != _lastStuck)
		{
			_lastStuck = stuck;
			Session.Broadcast(NetProtocol.HintFx(1, stuck), false);
			if (stuck) PushToast("* off track / stuck");
		}
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
		byte flags = 0;
		if (car.brakeAccel > 0.05f) flags |= 1;
		WorldSync.PulseLocalHandbrake(Session);
		if (WorldSync.LocalHandbrakeOn) flags |= 2;
		if (car.forwardSpeed < -1.5f) flags |= 4; // reverse
		if (car.engineAccel > 0.25f || car.forwardSpeed > 25f) flags |= 8; // exhaust
		bool trails = (flags & 2) != 0; // handbrake trails
		if (trails) flags |= 16;
		try
		{
			bool airborne = true;
			foreach (var probe in car.GetComponentsInChildren<AirContactProbe>(true))
			{
				if (probe != null && probe.Touching) { airborne = false; break; }
			}
			// No probes → use suspension heuristic.
			if (car.GetComponentInChildren<AirContactProbe>(true) == null)
				airborne = Mathf.Abs(car.forwardSpeed) > 2f && t.up.y < 0.85f;
			else if (airborne && Mathf.Abs(car.forwardSpeed) < 0.5f)
				airborne = false;
			if (airborne) flags |= 32;
		}
		catch { /* */ }
		if (WorldSync.LocalNitroOn) flags |= 64;
		float engineInd = car.carLink != null ? car.carLink.engineIndicator : -1f;
		Session.Broadcast(NetProtocol.Pose(
			Session.LocalId, t.position, t.rotation, car.forwardSpeed, hp, Time.unscaledTime,
			car.steeringAngleDeg, flags, engineInd), false);
		TickLocalGearFxEdges(trails);
		bool burning = hp < 35f;
		if (burning != _lastBurnSent)
		{
			_lastBurnSent = burning;
			Session.Broadcast(NetProtocol.BurnFx(burning), false);
		}
	}

	private bool _lastTrailSent;
	private bool _lastMagnetSent;
	private bool _lastGlueSent;

	/// <summary>Edge-trigger Magnet/Glue/Trail packets so ghosts update without waiting for Pose alone.</summary>
	private void TickLocalGearFxEdges(bool trails)
	{
		if (trails != _lastTrailSent)
		{
			_lastTrailSent = trails;
			Session.Broadcast(NetProtocol.TrailFx(trails), false);
		}
		bool magnet = false;
		bool glue = false;
		try
		{
			foreach (var m in UnityEngine.Object.FindObjectsByType<NMagnetLever>(FindObjectsSortMode.None))
			{
				if (m != null && m.IsActive) { magnet = true; break; }
			}
			foreach (var g in UnityEngine.Object.FindObjectsByType<NGlueLever>(FindObjectsSortMode.None))
			{
				if (g != null && g.Active) { glue = true; break; }
			}
		}
		catch { /* */ }
		if (magnet != _lastMagnetSent)
		{
			_lastMagnetSent = magnet;
			Session.Broadcast(NetProtocol.MagnetFx(magnet), false);
		}
		if (glue != _lastGlueSent)
		{
			_lastGlueSent = glue;
			Session.Broadcast(NetProtocol.GlueFx(glue), false);
		}
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

	public void ApplyRemotePose(ulong from, ulong ownerId, Vector3 pos, Quaternion rot, float speed, float hp,
		float remoteTime, float steerDeg = 0f, byte flags = 0, float engineIndicator = -1f)
	{
		_ = ownerId;
		// Avoid spawning cube ghosts while the track/enemies are still loading.
		if (!_trackGhostsReady)
		{
			_pendingPoses[from] = (pos, rot, speed, hp, remoteTime, steerDeg, flags);
			return;
		}
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
		ghost.ApplyAnim(steerDeg, (flags & 1) != 0, (flags & 2) != 0, (flags & 4) != 0, (flags & 8) != 0, (flags & 16) != 0, (flags & 32) != 0);
		ghost.SetNitroLit((flags & 64) != 0);
		ghost.SetEngineIndicator(engineIndicator);
	}

	public void DealDamageToPeer(ulong peerId, float amount, Vector3 point)
	{
		if (amount <= 0f) return;
		if (_lastDamageSent.TryGetValue(peerId, out var last) && Time.unscaledTime - last < 0.03f) return;
		_lastDamageSent[peerId] = Time.unscaledTime;
		_lastDamageToPeer[peerId] = Time.unscaledTime;
		// Broadcast with targetPeer — more reliable than SendTo alone on LAN remap.
		Session.Broadcast(NetProtocol.Damage(amount, point, peerId), true);
		if (!_lastHitChat.TryGetValue(peerId, out var chatAt) || Time.unscaledTime - chatAt > 0.85f)
		{
			_lastHitChat[peerId] = Time.unscaledTime;
			var name = Session.Peers.TryGetValue(peerId, out var p) ? p.Name : peerId.ToString();
			Session.AddChat($"* Hit {name} for {amount:0}");
		}
	}

	public void ApplyIncomingDamage(float amount, Vector3 point)
	{
		var car = FindLocalPlayerCar();
		if (car?.carLink == null) return;
		car.carLink.TakeCrashDamage(amount, shake: true, DamageKind.Other);
		Session.AddChat($"* Took {amount:0} dmg");
		try
		{
			foreach (var shaker in UnityEngine.Object.FindObjectsByType<Shaker>(FindObjectsSortMode.None))
			{
				if (shaker == null) continue;
				shaker.KickSpring(Mathf.Clamp(amount * 0.08f, 0.15f, 1.4f));
			}
		}
		catch { /* camera */ }
		_ = point;
	}

	public void OnPeerWrecked(ulong peerId)
	{
		if (!_ghosts.TryGetValue(peerId, out var g) || g?.Root == null) return;
		g.PushTarget(g.Root.transform.position, g.Root.transform.rotation, 0f, 0f, Time.unscaledTime);
		g.SetBurning(true);
		g.PlayExplode(1);
		try
		{
			var go = g.BoundEnemy != null ? g.BoundEnemy.gameObject : g.Root;
			var vis = go.GetComponent<CarDamageVisuals>() ?? go.GetComponentInChildren<CarDamageVisuals>(true);
			vis?.SetWrecked();
		}
		catch { /* fx */ }
		if (_lastDamageToPeer.TryGetValue(peerId, out var when) && Time.unscaledTime - when < 4f)
		{
			var name = Session.Peers.TryGetValue(peerId, out var p) ? p.Name : peerId.ToString();
			NotifyChatToast($"* YOU wrecked {name}");
			Session.AddChat($"* YOU wrecked {name}");
		}
		else
		{
			var name = Session.Peers.TryGetValue(peerId, out var p) ? p.Name : peerId.ToString();
			NotifyChatToast($"* {name} wrecked");
		}
	}

	private void TickQuickChat()
	{
		if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
		{
			if (!_quickChat)
			{
				_quickChat = true;
				_quickChatDraft = "";
				return;
			}
			if (!string.IsNullOrWhiteSpace(_quickChatDraft))
			{
				Session.SendChat(_quickChatDraft.Trim());
				PushToast($"YOU: {_quickChatDraft.Trim()}");
			}
			_quickChat = false;
			_quickChatDraft = "";
			return;
		}
		if (_quickChat && Input.GetKeyDown(KeyCode.Escape))
		{
			_quickChat = false;
			_quickChatDraft = "";
		}
	}

	private string _lastToast = "";
	private float _lastToastAt = -999f;

	private void PushToast(string line)
	{
		if (string.IsNullOrEmpty(line)) return;
		if (line == _lastToast && Time.unscaledTime - _lastToastAt < 0.85f) return;
		_lastToast = line;
		_lastToastAt = Time.unscaledTime;
		_toastChat.Add(line);
		while (_toastChat.Count > 6) _toastChat.RemoveAt(0);
		_toastUntil = Time.unscaledTime + 6f;
	}

	private void DrawToasts()
	{
		if (Time.unscaledTime > _toastUntil || _toastChat.Count == 0) return;
		float y = Screen.height - 24f - _toastChat.Count * 18f;
		foreach (var line in _toastChat)
		{
			GUI.Label(new Rect(12f, y, Screen.width - 24f, 18f), line, _hint ?? GUI.skin.label);
			y += 18f;
		}
	}

	private void DrawQuickChatBar()
	{
		if (!_quickChat) return;
		var r = new Rect(12f, Screen.height - 48f, Mathf.Min(480f, Screen.width - 24f), 28f);
		GUI.Box(r, GUIContent.none);
		GUI.SetNextControlName("HGMP_QuickChat");
		_quickChatDraft = GUI.TextField(new Rect(r.x + 6, r.y + 4, r.width - 12, 20f), _quickChatDraft);
		GUI.FocusControl("HGMP_QuickChat");
	}

	public void NotifyChatToast(string line) => PushToast(line);

	private void TickTauntKeys()
	{
		if (_quickChat || UiVisible) return;
		if (Time.unscaledTime < _nextTaunt) return;
		string? msg = null;
		if (Input.GetKeyDown(KeyCode.Alpha1) || Input.GetKeyDown(KeyCode.Keypad1))
			msg = "Nice!";
		else if (Input.GetKeyDown(KeyCode.Alpha2) || Input.GetKeyDown(KeyCode.Keypad2))
			msg = "Incoming!";
		else if (Input.GetKeyDown(KeyCode.Alpha3) || Input.GetKeyDown(KeyCode.Keypad3))
			msg = "Sorry!";
		else if (Input.GetKeyDown(KeyCode.Alpha4) || Input.GetKeyDown(KeyCode.Keypad4))
			msg = "GG";
		if (msg == null) return;
		_nextTaunt = Time.unscaledTime + 1.5f;
		Session.SendChat(msg);
		PushToast($"YOU: {msg}");
	}

	private void TickSpectate()
	{
		bool wrecked = false;
		var car = FindLocalPlayerCar();
		if (car?.carLink != null && car.carLink.hp <= 0f) wrecked = true;

		// Auto-enter spectate once after wreck so you keep watching the race.
		if (wrecked && !_spectating && Session.Peers.Count > 0 && !_autoSpectateTried)
		{
			_autoSpectateTried = true;
			EnterSpectate();
		}
		if (!wrecked) _autoSpectateTried = false;

		if (Input.GetKeyDown(KeyCode.Tab) && Session.Peers.Count > 0)
		{
			int dir = (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)) ? -1 : +1;
			if (!_spectating && wrecked)
				EnterSpectate();
			else if (_spectating)
				CycleSpectate(dir);
		}
		if (_spectating && (!wrecked || Session.Peers.Count == 0))
			ExitSpectate();
		if (_spectating)
			FollowSpectateTarget();
	}

	private bool _autoSpectateTried;

	internal void EnterSpectate()
	{
		if (_spectating) return;
		var cam = Camera.main;
		if (cam == null) return;
		_spectating = true;
		_spectateIndex = 0;
		_spectateCamParent = cam.transform.parent;
		_spectateCamLocalPos = cam.transform.localPosition;
		_spectateCamLocalRot = cam.transform.localRotation;
		PushToast("* Spectate — Tab / Shift+Tab cycle peers");
		FollowSpectateTarget();
	}

	private void CycleSpectate(int delta)
	{
		var ids = new List<ulong>(Session.Peers.Keys);
		if (ids.Count == 0) return;
		_spectateIndex = ((_spectateIndex + delta) % ids.Count + ids.Count) % ids.Count;
		string name = Session.Peers.TryGetValue(ids[_spectateIndex], out var peer) ? peer.Name : "?";
		PushToast($"* Spectating {name}");
		FollowSpectateTarget();
	}

	private void FollowSpectateTarget()
	{
		var cam = Camera.main;
		if (cam == null) return;
		var ids = new List<ulong>(Session.Peers.Keys);
		if (ids.Count == 0) return;
		_spectateIndex = Mathf.Clamp(_spectateIndex, 0, ids.Count - 1);
		var id = ids[_spectateIndex];
		if (!_ghosts.TryGetValue(id, out var g) || g?.Root == null) return;
		cam.transform.SetParent(null, true);
		var target = g.BoundEnemy != null ? g.BoundEnemy.transform : g.Root.transform;
		var desired = target.position - target.forward * 8f + Vector3.up * 3.2f;
		cam.transform.position = Vector3.Lerp(cam.transform.position, desired, 1f - Mathf.Exp(-10f * Time.unscaledDeltaTime));
		var look = target.position + Vector3.up * 1.2f;
		var wantRot = Quaternion.LookRotation((look - cam.transform.position).normalized, Vector3.up);
		cam.transform.rotation = Quaternion.Slerp(cam.transform.rotation, wantRot, 1f - Mathf.Exp(-12f * Time.unscaledDeltaTime));
	}

	private void ExitSpectate()
	{
		if (!_spectating) return;
		_spectating = false;
		var cam = Camera.main;
		if (cam != null && _spectateCamParent != null)
		{
			cam.transform.SetParent(_spectateCamParent, false);
			cam.transform.localPosition = _spectateCamLocalPos;
			cam.transform.localRotation = _spectateCamLocalRot;
		}
		_spectateCamParent = null;
	}

	private void TrySendWorldPing()
	{
		_nextPingSend = Time.unscaledTime + 1.2f;
		Vector3 pos;
		var car = FindLocalPlayerCar();
		if (car != null)
			pos = car.transform.position;
		else if (Camera.main != null)
			pos = Camera.main.transform.position + Camera.main.transform.forward * 40f;
		else
			return;
		Session.Broadcast(NetProtocol.WorldPing(pos), true);
		WorldSync.SpawnWorldPing(pos, "YOU");
		Session.AddChat("* Pinged");
	}

	private void DrawPeerRadar()
	{
		if (Session.InLobbyPhase || _ghosts.Count == 0) return;
		var car = FindLocalPlayerCar();
		if (car == null) return;
		float size = 110f;
		var box = new Rect(Screen.width - size - 16f, 40f, size, size);
		GUI.Box(box, GUIContent.none);
		Vector2 center = new(box.x + size * 0.5f, box.y + size * 0.5f);
		GUI.Label(new Rect(center.x - 3f, center.y - 6f, 12f, 12f), "+", _hint ?? GUI.skin.label);
		float range = 220f;
		var forward = car.transform.forward;
		forward.y = 0f;
		if (forward.sqrMagnitude < 0.01f) forward = Vector3.forward;
		forward.Normalize();
		var right = car.transform.right;
		right.y = 0f;
		right.Normalize();
		var prev = GUI.color;
		foreach (var kv in _ghosts)
		{
			if (kv.Value?.Root == null) continue;
			var delta = kv.Value.Root.transform.position - car.transform.position;
			delta.y = 0f;
			float dist = delta.magnitude;
			float fx = Vector3.Dot(delta, right) / range;
			float fz = Vector3.Dot(delta, forward) / range;
			fx = Mathf.Clamp(fx, -0.9f, 0.9f);
			fz = Mathf.Clamp(fz, -0.9f, 0.9f);
			float px = center.x + fx * (size * 0.42f);
			float py = center.y - fz * (size * 0.42f);
			float hp = kv.Value.Hp;
			GUI.color = hp <= 0.1f ? new Color(0.4f, 0.4f, 0.4f, 1f)
				: hp < 35f ? new Color(1f, 0.35f, 0.2f, 1f)
				: hp < 70f ? new Color(1f, 0.85f, 0.2f, 1f)
				: new Color(0.4f, 1f, 0.45f, 1f);
			string tag = Session.Peers.TryGetValue(kv.Key, out var p)
				? p.Name.Substring(0, Mathf.Min(3, p.Name.Length))
				: "?";
			GUI.Label(new Rect(px - 10f, py - 8f, 36f, 16f), tag, _hint ?? GUI.skin.label);
			if (dist > 5f)
				GUI.Label(new Rect(px - 12f, py + 6f, 40f, 14f), $"{dist:0}m", _hint ?? GUI.skin.label);
		}
		GUI.color = prev;
	}

	private void DrawRaceScoreboard()
	{
		if (Session.InLobbyPhase) return;
		float y = 40f;
		GUI.Label(new Rect(12f, y, 360f, 20f), "— MP laps —", _hint ?? GUI.skin.label);
		y += 18f;
		var tm = UnityEngine.Object.FindFirstObjectByType<TrackManager>();
		var lm = tm != null ? tm.lapManager : null;
		if (lm?.PlayerCar != null && lm.leaderboard != null &&
		    lm.leaderboard.TryGetValue(lm.PlayerCar, out var local))
		{
			GUI.Label(new Rect(12f, y, 360f, 18f),
				$"YOU  lap {local.currentLap}  #{local.position}" + (local.isFinished ? " FIN" : ""),
				_hint ?? GUI.skin.label);
			y += 16f;
		}
		foreach (var kv in Session.Peers)
		{
			string hpBit = "";
			if (_ghosts.TryGetValue(kv.Key, out var g) && g != null)
				hpBit = $"  {Mathf.CeilToInt(g.Hp)}hp";
			if (!WorldSync.TryGetPeerLap(kv.Key, out var lap))
			{
				if (!string.IsNullOrEmpty(hpBit))
				{
					GUI.Label(new Rect(12f, y, 360f, 18f), $"{kv.Value.Name}{hpBit}", _hint ?? GUI.skin.label);
					y += 16f;
				}
				continue;
			}
			GUI.Label(new Rect(12f, y, 360f, 18f),
				$"{kv.Value.Name}  lap {lap.lap}  #{lap.place}{hpBit}" + (lap.finished ? " FIN" : ""),
				_hint ?? GUI.skin.label);
			y += 16f;
		}
	}

	private void PruneStalePeers()
	{
		if (!Session.Linked) return;
		const float timeout = 20f;
		float now = Time.unscaledTime;
		List<ulong>? gone = null;
		foreach (var kv in Session.Peers)
		{
			if (now - kv.Value.LastSeen > timeout)
				(gone ??= new List<ulong>()).Add(kv.Key);
		}
		if (gone == null) return;
		foreach (var id in gone)
			Session.DropPeer(id, "timed out");
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
		_pendingPoses.Clear();
		_trackGhostsReady = false;
		_lastBurnSent = false;
	}

	private static CarControllerNew? FindLocalPlayerCar()
	{
		var all = UnityEngine.Object.FindObjectsByType<CarControllerNew>(FindObjectsSortMode.None);
		foreach (var c in all)
			if (c != null && c.isPlayer && c.isActiveAndEnabled) return c;
		var tm = UnityEngine.Object.FindFirstObjectByType<TrackManager>();
		return tm != null ? tm.playerCarController : null;
	}

	internal void SetUiVisible(bool visible)
	{
		UiVisible = visible;
		if (!visible)
		{
			_unfocusGui = true;
			try { MpMenuUi.Instance?.Show(false); } catch { /* */ }
			Plugin.Log.LogInfo("MP menu closed (IMGUI)");
			return;
		}
		// IMGUI is the reliable path. uGUI overlay is optional and must not block IMGUI.
		try { MpMenuUi.Instance?.Show(false); } catch { /* */ }
		Plugin.Log.LogInfo($"MP menu opened (IMGUI) v{PluginInfo.Version}");
	}

	internal void SetUiVisibleFromMenu(bool visible) => SetUiVisible(visible);

	/// <summary>Public entry for injected buttons / patches.</summary>
	internal void ForceOpenMenu()
	{
		Plugin.Log.LogInfo("ForceOpenMenu");
		SetUiVisible(true);
	}

	internal void ClearGhostsPublic() => ClearGhosts();
	internal void HostOpenPlaygroundPublic() => HostOpenPlayground();
	internal void HostLoadMapPublic(MapDefinition map) => HostLoadMap(map);

	private void OnGUI()
	{
		EnsureStyles();
		if (_unfocusGui)
		{
			GUI.FocusControl(null);
			_unfocusGui = false;
		}

		// Always-visible open affordance — does not depend on F6 or scene buttons.
		if (!UiVisible)
		{
			if (GUI.Button(new Rect(12f, 12f, 160f, 40f), "MP MENU (F6)"))
			{
				Plugin.Log.LogInfo("MP MENU corner button clicked");
				SetUiVisible(true);
			}
			if (Time.unscaledTime < _hintUntil && !_quickChat)
			{
				GUI.Label(new Rect(180f, 18f, 520f, 28f),
					"Half Grip Multiplayer — click MP MENU or press F6", _hint);
			}
			if (Session.Linked)
			{
				DrawRaceScoreboard();
				DrawPeerRadar();
				DrawToasts();
				DrawQuickChatBar();
			}
			return;
		}

		if (Session.Linked)
		{
			DrawToasts();
			DrawQuickChatBar();
		}

		const float w = 440f;
		var rect = new Rect(12f, 12f, w, Mathf.Min(660f, Screen.height - 24f));
		GUI.Box(rect, GUIContent.none, _box);
		GUILayout.BeginArea(new Rect(rect.x + 10, rect.y + 8, w - 20, rect.height - 16));

		GUILayout.BeginHorizontal();
		GUILayout.Label("HALF GRIP MULTIPLAYER", _title);
		if (GUILayout.Button("Hide", GUILayout.Width(52), GUILayout.Height(22)))
			SetUiVisible(false);
		GUILayout.EndHorizontal();
		GUILayout.Label($"v{PluginInfo.Version}  PvP  |  {Session.Status}  |  MMB=ping", _label);
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
				if (Session.AllPeersReady() && Session.Peers.Count > 0)
				{
					if (GUILayout.Button("Host: allow start", GUILayout.Height(28)))
						Session.AddChat("* All ready — use the game Start Race button");
				}
				else if (Session.Peers.Count > 0)
				{
					if (GUILayout.Button("Force start", GUILayout.Height(28)))
						Session.UnlockForceStart();
				}
			}
			GUILayout.EndHorizontal();
			if (Session.IsHost && Session.InLobbyPhase && !Session.AllPeersReady() && !Session.ForceStartUnlocked)
				GUILayout.Label("Waiting for Ready — or press Force start.", _label);
			if (Session.ForceStartUnlocked)
				GUILayout.Label("Force start ON — press game Start Race.", _label);
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
		GUILayout.Label($"Hotkeys: F4 Force · F5 Ready · {Plugin.Instance.ToggleUiKey.Value}/Esc · F7 Host · F8 Leave", _label);
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
		// Match ProgressionMenuSkin palette: dark paper + warm gold titles.
		_box = new GUIStyle(GUI.skin.box);
		var tex = new Texture2D(1, 1);
		tex.SetPixel(0, 0, new Color(0.08f, 0.08f, 0.09f, 0.94f));
		tex.Apply();
		_box.normal.background = tex;
		_box.border = new RectOffset(8, 8, 8, 8);
		_box.padding = new RectOffset(10, 10, 8, 8);
		_label = new GUIStyle(GUI.skin.label)
		{
			fontSize = 13,
			wordWrap = true,
			normal = { textColor = new Color(0.86f, 0.86f, 0.88f, 1f) },
		};
		_title = new GUIStyle(GUI.skin.label)
		{
			fontSize = 16,
			fontStyle = FontStyle.Bold,
			normal = { textColor = new Color(1f, 0.83f, 0.35f, 1f) },
		};
		_hint = new GUIStyle(GUI.skin.label)
		{
			fontSize = 12,
			normal = { textColor = new Color(0.72f, 0.72f, 0.76f, 0.85f) },
		};
	}
}

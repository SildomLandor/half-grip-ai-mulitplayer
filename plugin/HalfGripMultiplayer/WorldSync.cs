using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace HalfGripMultiplayer;

/// <summary>
/// Cross-cutting sync: loadout, gun/nitro/ammo/reset FX, race end, map/shop, laps, boss.
/// </summary>
internal static class WorldSync
{
	private static readonly Dictionary<ulong, List<string>> PeerLoadouts = new();
	private static readonly Dictionary<ulong, (int lap, float progress, int place, bool finished)> PeerLaps = new();
	private static readonly Dictionary<ulong, List<GameObject>> GhostExtras = new();
	private static readonly Dictionary<ulong, float> CollideCooldown = new();
	private static bool _applyingRemoteRaceEnd;
	private static bool _applyingRemoteMapSelect;
	private static bool _applyingRemoteTire;
	private static bool _applyingRemoteShield;
	private static bool _applyingRemoteCutoff;
	private static bool _applyingRemoteBlast;
	private static bool _applyingRemoteRocket;
	private static bool _applyingRemotePickup;
	private static bool _applyingRemoteDestructible;
	private static bool _applyingRemotePause;
	private static bool _applyingRemoteCutoffSay;
	private static string _lastLoadoutKey = "";
	private static bool _localNitroOn;
	/// <summary>True while local player nitro boost &gt; 0 (updated by PulseLocalNitroOff).</summary>
	public static bool LocalNitroOn => _localNitroOn;
	private static bool _localHandbrakeOn;
	public static bool LocalHandbrakeOn => _localHandbrakeOn;
	private static float _lastCutoffBroadcast = -999f;
	private static AudioClip[]? _cachedShootSounds;

	private static readonly FieldInfo? RaceCutoffInstance =
		AccessTools.Field(typeof(RaceCutoff), "instance");
	private static readonly FieldInfo? RaceCutoffEndsAt =
		AccessTools.Field(typeof(RaceCutoff), "endsAt");
	private static readonly MethodInfo? RaceCutoffSay =
		AccessTools.Method(typeof(RaceCutoff), "Say");
	private static readonly MethodInfo? TireBurstMethod =
		AccessTools.Method(typeof(CarTireDamage), "Burst");

	private static readonly PropertyInfo? SelectedIndexProp =
		AccessTools.Property(typeof(RunState), nameof(RunState.SelectedIndex));
	private static readonly PropertyInfo? CurrentIndexProp =
		AccessTools.Property(typeof(RunState), nameof(RunState.CurrentIndex));
	private static readonly PropertyInfo? LivesProp =
		AccessTools.Property(typeof(RunState), nameof(RunState.Lives));
	private static readonly PropertyInfo? RacesProp =
		AccessTools.Property(typeof(RunState), nameof(RunState.Races));

	public static bool SuppressLocalRaceEndBroadcast { get; set; }
	public static bool SuppressPauseBroadcast => _applyingRemotePause;
	public static bool SuppressCutoffSayBroadcast => _applyingRemoteCutoffSay;

	public static List<string> CaptureLocalLoadout()
	{
		var ids = new List<string>();
		try
		{
			if (!PlayerLoadout.Known) return ids;
			foreach (var up in PlayerLoadout.Equipped)
			{
				if (up == null) continue;
				var id = !string.IsNullOrEmpty(up.id) ? up.id : up.name;
				if (!string.IsNullOrEmpty(id)) ids.Add(id);
			}
		}
		catch { /* menu */ }
		return ids;
	}

	public static void BroadcastLocalLoadoutIfChanged(MpSession session)
	{
		if (session == null || !session.Linked) return;
		var ids = CaptureLocalLoadout();
		var key = string.Join("|", ids);
		if (key == _lastLoadoutKey) return;
		_lastLoadoutKey = key;
		session.Broadcast(NetProtocol.Loadout(ids), true);
	}

	public static void ForceBroadcastLoadout(MpSession session)
	{
		_lastLoadoutKey = "";
		BroadcastLocalLoadoutIfChanged(session);
	}

	public static void StorePeerLoadout(ulong peer, List<string> ids)
	{
		PeerLoadouts[peer] = ids ?? new List<string>();
		MpRuntime.Instance?.ApplyPeerLoadoutVisual(peer, PeerLoadouts[peer]);
	}

	public static bool TryGetLoadout(ulong peer, out List<string> ids) =>
		PeerLoadouts.TryGetValue(peer, out ids!);

	public static void ForgetPeer(ulong peer)
	{
		PeerLoadouts.Remove(peer);
		PeerLaps.Remove(peer);
		ClearGhostExtras(peer);
	}

	public static void RemapPeer(ulong fromId, ulong toId)
	{
		if (fromId == toId) return;
		if (PeerLoadouts.TryGetValue(fromId, out var loadout))
		{
			PeerLoadouts.Remove(fromId);
			if (!PeerLoadouts.ContainsKey(toId))
				PeerLoadouts[toId] = loadout;
		}
		if (PeerLaps.TryGetValue(fromId, out var lap))
		{
			PeerLaps.Remove(fromId);
			PeerLaps[toId] = lap;
		}
		if (GhostExtras.TryGetValue(fromId, out var extras))
		{
			GhostExtras.Remove(fromId);
			if (!GhostExtras.ContainsKey(toId))
				GhostExtras[toId] = extras;
		}
	}

	public static void ForgetAllPeers()
	{
		foreach (var id in new List<ulong>(GhostExtras.Keys))
			ClearGhostExtras(id);
		PeerLoadouts.Clear();
		PeerLaps.Clear();
		_lastLoadoutKey = "";
		_lastAmmoSent = -1;
	}

	public static UpgradeComponent? FindUpgrade(string idOrName)
	{
		if (string.IsNullOrWhiteSpace(idOrName)) return null;
		try
		{
			var mapper = UpgradeMapper.Resolve();
			if (mapper != null)
			{
				foreach (var up in mapper.All)
				{
					if (up == null) continue;
					if (string.Equals(up.id, idOrName, StringComparison.OrdinalIgnoreCase) ||
					    string.Equals(up.name, idOrName, StringComparison.OrdinalIgnoreCase) ||
					    string.Equals(up.Name, idOrName, StringComparison.OrdinalIgnoreCase))
						return up;
				}
			}
		}
		catch { /* */ }

		foreach (var up in Resources.FindObjectsOfTypeAll<UpgradeComponent>())
		{
			if (up == null) continue;
			if (string.Equals(up.id, idOrName, StringComparison.OrdinalIgnoreCase) ||
			    string.Equals(up.name, idOrName, StringComparison.OrdinalIgnoreCase))
				return up;
		}
		return null;
	}

	public static void AttachLoadoutVisuals(ulong peer, Transform root, List<string> ids)
	{
		ClearGhostExtras(peer);
		if (root == null || ids == null || ids.Count == 0) return;
		var spawned = new List<GameObject>();
		foreach (var id in ids)
		{
			var up = FindUpgrade(id);
			if (up?.exteriorPrefab == null) continue;
			try
			{
				var go = UnityEngine.Object.Instantiate(up.exteriorPrefab, root);
				go.name = $"HGMP_Up_{up.name}";
				spawned.Add(go);
			}
			catch { /* prefab */ }
		}
		GhostExtras[peer] = spawned;
	}

	public static void ClearGhostExtras(ulong peer)
	{
		if (!GhostExtras.TryGetValue(peer, out var list)) return;
		foreach (var go in list)
			if (go != null) UnityEngine.Object.Destroy(go);
		GhostExtras.Remove(peer);
	}

	public static void BroadcastRaceEnd(MpSession session, int place, bool survived, bool objectiveMet, bool wrecked, bool quit)
	{
		if (session == null || !session.Linked || !session.IsHost) return;
		if (SuppressLocalRaceEndBroadcast) return;
		session.Broadcast(NetProtocol.RaceEnd(place, survived, objectiveMet, wrecked, quit), true);
		session.AddChat($"* Race END place={place}");
		Plugin.Log.LogInfo($"RaceEnd broadcast place={place} survived={survived}");
	}

	public static void ApplyRemoteRaceEnd(int place, bool survived, bool objectiveMet, bool wrecked, bool quit)
	{
		if (_applyingRemoteRaceEnd) return;
		if (GameManager.raceOver) return; // already ended locally
		_applyingRemoteRaceEnd = true;
		SuppressLocalRaceEndBroadcast = true;
		try
		{
			// Per-player economy: each client banks THEIR own race result — never host money.
			try
			{
				if (!RaceTracker.Ended)
					RaceTracker.Finish();
				ShopManager.addRaceGain(RaceTracker.Last);
			}
			catch (Exception ex)
			{
				Plugin.Log.LogWarning($"Local race payout: {ex.Message}");
			}

			GameManager.raceOver = true;
			MpRuntime.Instance?.Session.AddChat($"* Race over (host signal) — local payout kept");
			Plugin.Log.LogInfo($"RaceEnd apply hostPlace={place} localBanked wrecked={wrecked}");
			try
			{
				if (SceneLoader.Instance != null)
					SceneLoader.Instance.LoadUIScene(1, 1.5f);
			}
			catch (Exception ex)
			{
				Plugin.Log.LogWarning($"RaceEnd UI load: {ex.Message}");
			}
			_ = survived;
			_ = objectiveMet;
			_ = quit;
		}
		finally
		{
			SuppressLocalRaceEndBroadcast = false;
			_applyingRemoteRaceEnd = false;
		}
	}

	public static void BroadcastMapSelect(MpSession session)
	{
		if (session == null || !session.Linked || !session.IsHost) return;
		if (!RunState.Active) return;
		session.Broadcast(NetProtocol.MapSelect(
			RunState.HasSelection ? RunState.SelectedIndex : -1,
			RunState.CurrentIndex,
			RunState.Races,
			RunState.Lives), true);
	}

	public static void ApplyMapSelect(int selected, int current, int races, int lives)
	{
		if (_applyingRemoteMapSelect) return;
		_applyingRemoteMapSelect = true;
		try
		{
			if (!RunState.Active) return;
			CurrentIndexProp?.SetValue(null, current);
			RacesProp?.SetValue(null, races);
			LivesProp?.SetValue(null, lives);
			if (selected >= 0)
			{
				if (RunState.Choices.Contains(selected))
					RunState.Select(selected);
				else
					SelectedIndexProp?.SetValue(null, selected);
			}
			Plugin.Log.LogInfo($"MapSelect apply selected={selected} current={current}");
		}
		catch (Exception ex)
		{
			Plugin.Log.LogWarning($"MapSelect apply: {ex.Message}");
		}
		finally
		{
			_applyingRemoteMapSelect = false;
		}
	}

	/// <summary>
	/// Shop catalog, wallets, and inventory are strictly per-player — never sync.
	/// Kept as no-ops so older peers' ShopState packets are ignored safely.
	/// </summary>
	public static void PulseShopOfferCatalog(MpSession? session)
	{
		_ = session;
	}

	public static void ApplyShopState(float money, string offerKey)
	{
		_ = money;
		_ = offerKey;
		// Intentionally ignore — each client's ShopManager / SaveData money & offers stay private.
	}

	public static void BroadcastRunSnap(MpSession session)
	{
		if (session == null || !session.Linked || !session.IsHost) return;
		if (!RunState.Active) return;
		try
		{
			var save = RunSave.Capture(
				RunState.Map,
				RunState.Lives,
				RunState.Races,
				RunState.CurrentIndex,
				RunState.HasSelection ? RunState.SelectedIndex : -1,
				RunState.PendingObjective,
				RunState.Outcome);
			var json = UnityEngine.JsonUtility.ToJson(save);
			// Chunk so LAN/Steam P2P does not drop oversized UDP datagrams.
			uint id = (uint)(Time.frameCount ^ (json.Length * 397));
			var parts = NetProtocol.RunSnapParts(json, id);
			foreach (var p in parts)
				session.Broadcast(p, true);
			Plugin.Log.LogInfo($"RunSnap broadcast ({json.Length} chars, {parts.Count} parts) — graph only");
		}
		catch (Exception ex)
		{
			Plugin.Log.LogWarning($"RunSnap capture: {ex.Message}");
		}
	}

	public static void ApplyRunSnap(string json)
	{
		if (string.IsNullOrWhiteSpace(json)) return;
		try
		{
			// Rebuild shared map graph; does not touch ShopManager / inventory / loadout.
			SaveData.SetString("run", json);
			if (!RunState.Load())
				Plugin.Log.LogWarning("RunSnap: RunState.Load failed");
			else
				Plugin.Log.LogInfo("RunSnap applied (run graph)");
		}
		catch (Exception ex)
		{
			Plugin.Log.LogWarning($"RunSnap apply: {ex.Message}");
		}
	}

	public static void ApplyDentFx(byte mode, ulong id, Vector3 point, Vector3 normal, float strength, byte kind)
	{
		try
		{
			CarDamageVisuals? vis = null;
			if (mode == 0)
			{
				var ghost = MpRuntime.Instance?.GetGhost(id);
				if (ghost?.BoundEnemy != null)
					vis = ghost.BoundEnemy.GetComponent<CarDamageVisuals>()
					      ?? CarDamageVisuals.Ensure(ghost.BoundEnemy.gameObject);
			}
			else
			{
				var tm = UnityEngine.Object.FindFirstObjectByType<TrackManager>();
				if (tm != null && (int)id < tm.Enemies.Count)
				{
					var e = tm.Enemies[(int)id];
					if (e != null)
						vis = e.GetComponent<CarDamageVisuals>() ?? CarDamageVisuals.Ensure(e.gameObject);
				}
			}
			if (vis == null) return;
			DentFxPatches.ApplyRemote();
			vis.ReportHit(point, normal, strength, (DamageMarkKind)kind);
		}
		catch (Exception ex)
		{
			Plugin.Log.LogWarning($"DentFx: {ex.Message}");
		}
	}

	/// <summary>
	/// Do NOT call LapManager.RegisterCar here. LapManager only bulk-registers player+bots
	/// when leaderboard.Count==0; an early RegisterCar (ghost claim) permanently skips
	/// the player → KeyNotFound in GetCarProjectedPoint → laps stop for everyone.
	/// Ghosts claim existing enemies that LapManager already registered.
	/// </summary>
	public static void TryRegisterGhostOnLeaderboard(GhostCar ghost)
	{
		_ = ghost;
	}

	public static void BroadcastLocalLap(MpSession session)
	{
		if (session == null || !session.Linked) return;
		var tm = UnityEngine.Object.FindFirstObjectByType<TrackManager>();
		var lm = tm != null ? tm.lapManager : null;
		if (lm == null || lm.PlayerCar == null) return;
		if (lm.leaderboard == null || !lm.leaderboard.TryGetValue(lm.PlayerCar, out var e)) return;
		session.Broadcast(NetProtocol.LapState(e.currentLap, e.lapProgress, e.position, e.isFinished), false);
	}

	/// <summary>
	/// Cache peer lap for UI only. Do not write into LapManager.leaderboard —
	/// that fights GetCarProjectedPoint / LapCounter and can freeze race progress.
	/// </summary>
	public static void StorePeerLap(ulong peer, int lap, float progress, int place, bool finished)
	{
		PeerLaps[peer] = (lap, progress, place, finished);
	}

	public static bool TryGetPeerLap(ulong peer, out (int lap, float progress, int place, bool finished) lap) =>
		PeerLaps.TryGetValue(peer, out lap);

	public static void SpawnGunTracer(Vector3 origin, Vector3 target, ulong shooterPeer = 0)
	{
		// Prefer muzzle at the shooter's ghost if we have one (avoids host-only origin).
		if (shooterPeer != 0)
		{
			var ghost = MpRuntime.Instance?.GetGhost(shooterPeer);
			if (ghost?.BoundEnemy != null)
				origin = ghost.BoundEnemy.transform.position + Vector3.up * 1.2f;
			else if (ghost?.Root != null)
				origin = ghost.Root.transform.position + Vector3.up * 1.2f;
		}
		var go = new GameObject("HGMP_Tracer");
		var lr = go.AddComponent<LineRenderer>();
		lr.positionCount = 2;
		lr.SetPosition(0, origin);
		lr.SetPosition(1, target);
		lr.startWidth = 0.06f;
		lr.endWidth = 0.02f;
		lr.material = new Material(Shader.Find("Sprites/Default") ?? Shader.Find("Legacy Shaders/Particles/Alpha Blended"));
		lr.startColor = new Color(1f, 0.85f, 0.3f, 0.9f);
		lr.endColor = new Color(1f, 0.4f, 0.1f, 0.2f);
		UnityEngine.Object.Destroy(go, 0.18f);
		SpawnMuzzleBurst(origin, target);
		PlayRemoteGunSound(origin);
	}

	private static void SpawnMuzzleBurst(Vector3 origin, Vector3 target)
	{
		try
		{
			var dir = (target - origin);
			if (dir.sqrMagnitude < 0.01f) dir = Vector3.forward;
			dir.Normalize();
			var burst = new GameObject("HGMP_Muzzle");
			burst.transform.position = origin;
			var lr = burst.AddComponent<LineRenderer>();
			lr.positionCount = 2;
			lr.SetPosition(0, origin);
			lr.SetPosition(1, origin + dir * 0.55f);
			lr.startWidth = 0.22f;
			lr.endWidth = 0.02f;
			lr.material = new Material(Shader.Find("Sprites/Default") ?? Shader.Find("Legacy Shaders/Particles/Alpha Blended"));
			lr.startColor = new Color(1f, 0.95f, 0.55f, 1f);
			lr.endColor = new Color(1f, 0.4f, 0.05f, 0.1f);
			UnityEngine.Object.Destroy(burst, 0.08f);
		}
		catch { /* fx */ }
	}

	private static void PlayRemoteGunSound(Vector3 origin)
	{
		try
		{
			if (_cachedShootSounds == null || _cachedShootSounds.Length == 0)
			{
				foreach (var gun in Resources.FindObjectsOfTypeAll<Gun>())
				{
					if (gun?.shootSounds == null || gun.shootSounds.Length == 0) continue;
					_cachedShootSounds = gun.shootSounds;
					break;
				}
			}
			if (_cachedShootSounds == null || _cachedShootSounds.Length == 0) return;
			SoundController.PlayRandomSoundAtPosition(
				_cachedShootSounds, origin, 0.45f, 5f, 180f, 0.08f);
		}
		catch { /* audio optional */ }
	}

	public static void ApplyAmmoFx(Vector3 pos, int magazines)
	{
		_ = magazines;
		// Remove nearest AmmoDrop so world pickups stay in sync.
		AmmoDrop? best = null;
		float bestD = 64f;
		foreach (var drop in UnityEngine.Object.FindObjectsByType<AmmoDrop>(FindObjectsSortMode.None))
		{
			if (drop == null) continue;
			float d = (drop.transform.position - pos).sqrMagnitude;
			if (d < bestD)
			{
				bestD = d;
				best = drop;
			}
		}
		if (best != null)
			UnityEngine.Object.Destroy(best.gameObject);
	}

	public static void ApplyNitroFx(ulong peer, bool on, float power)
	{
		_ = power;
		var ghost = MpRuntime.Instance?.GetGhost(peer);
		if (ghost == null) return;
		ghost.SetNitroLit(on);
		if (ghost.BoundEnemy != null)
		{
			var flames = ghost.BoundEnemy.GetComponentInChildren<NitroFlames>(true);
			if (flames?.flames != null)
			{
				foreach (var sr in flames.flames)
					if (sr != null) sr.enabled = on;
			}
		}
		if (on)
			PlayRemoteNitroSound(ghost.Root != null ? ghost.Root.transform.position : Vector3.zero);
	}

	private static AudioClip? _cachedNitroClip;
	private static readonly FieldInfo? NitroSound1Field =
		AccessTools.Field(typeof(NNitroLever), "nitroSound1");
	private static AudioClip[]? _breakOpenSounds;
	private static AudioClip[]? _breakCloseSounds;
	private static AudioClip[]? _ejectSounds;

	private static void PlayRemoteNitroSound(Vector3 origin)
	{
		try
		{
			if (_cachedNitroClip == null && NitroSound1Field != null)
			{
				foreach (var lever in Resources.FindObjectsOfTypeAll<NNitroLever>())
				{
					if (lever == null) continue;
					_cachedNitroClip = NitroSound1Field.GetValue(lever) as AudioClip;
					if (_cachedNitroClip != null) break;
				}
			}
			if (_cachedNitroClip == null) return;
			SoundController.PlaySoundAtPosition(_cachedNitroClip, origin, 0.55f, 4f, 90f);
		}
		catch { /* audio optional */ }
	}

	public static void ApplyGunActFx(ulong peer, byte kind, float param)
	{
		var ghost = MpRuntime.Instance?.GetGhost(peer);
		ghost?.PulseGunAct(kind, param);
		var pos = ghost?.Root != null
			? ghost.Root.transform.position + Vector3.up * 1.1f
			: Vector3.zero;
		PlayGunActSound(kind, pos);
		_ = param;
	}

	private static void PlayGunActSound(byte kind, Vector3 origin)
	{
		try
		{
			CacheGunActSounds();
			AudioClip[]? clips = kind switch
			{
				1 => _breakOpenSounds,
				2 => _breakCloseSounds,
				3 => _ejectSounds,
				_ => null
			};
			if (clips == null || clips.Length == 0) return;
			SoundController.PlayRandomSoundAtPosition(clips, origin, 0.4f, 3f, 60f, 0.05f);
		}
		catch { /* audio optional */ }
	}

	private static void CacheGunActSounds()
	{
		if (_breakOpenSounds != null) return;
		foreach (var gun in Resources.FindObjectsOfTypeAll<NBreakActionGun>())
		{
			if (gun == null) continue;
			if (_breakOpenSounds == null && gun.breakOpenSounds != null && gun.breakOpenSounds.Length > 0)
				_breakOpenSounds = gun.breakOpenSounds;
			if (_breakCloseSounds == null && gun.breakCloseSounds != null && gun.breakCloseSounds.Length > 0)
				_breakCloseSounds = gun.breakCloseSounds;
			if (_ejectSounds == null)
			{
				// eject often shares empty click / casing; prefer pump eject if present
			}
			if (_breakOpenSounds != null && _breakCloseSounds != null) break;
		}
		if (_ejectSounds == null)
		{
			foreach (var pump in Resources.FindObjectsOfTypeAll<NPumpActionGun>())
			{
				if (pump?.ejectShellSounds == null || pump.ejectShellSounds.Length == 0) continue;
				_ejectSounds = pump.ejectShellSounds;
				break;
			}
		}
		_breakOpenSounds ??= Array.Empty<AudioClip>();
		_breakCloseSounds ??= Array.Empty<AudioClip>();
		_ejectSounds ??= Array.Empty<AudioClip>();
	}

	public static void ApplySmokeFx(ulong peer, bool on)
	{
		MpRuntime.Instance?.GetGhost(peer)?.SetForcedSmoke(on);
	}

	public static void ApplyGlassCrackFx(ulong peer, float strength)
	{
		if (_applyingRemoteGlass) return;
		_applyingRemoteGlass = true;
		try
		{
			var ghost = MpRuntime.Instance?.GetGhost(peer);
			GlassDamage? glass = null;
			if (ghost?.BoundEnemy != null)
				glass = ghost.BoundEnemy.GetComponentInChildren<GlassDamage>(true);
			if (glass == null) return;
			if (glass.Shattered) return;
			glass.ReportHit(strength, DamageMarkKind.Impact);
			ghost?.PulseBlink(1);
		}
		catch (Exception ex)
		{
			Plugin.Log.LogWarning($"GlassCrackFx: {ex.Message}");
		}
		finally
		{
			_applyingRemoteGlass = false;
		}
	}

	private static bool _applyingRemoteWeather;

	public static bool SuppressWeatherBroadcast => _applyingRemoteWeather;

	public static void ApplyWeatherFx(string profileName)
	{
		if (string.IsNullOrWhiteSpace(profileName)) return;
		if (_applyingRemoteWeather) return;
		_applyingRemoteWeather = true;
		try
		{
			WeatherProfile? profile = null;
			foreach (var w in Resources.FindObjectsOfTypeAll<WeatherProfile>())
			{
				if (w != null && w.name == profileName) { profile = w; break; }
			}
			if (profile == null) return;
			AccessTools.Property(typeof(RunState), nameof(RunState.ActiveWeather))?.SetValue(null, profile);
			WeatherGrip.Current = profile;
			var wc = UnityEngine.Object.FindFirstObjectByType<WeatherController>();
			if (wc != null)
			{
				wc.activeProfile = profile;
				wc.ApplyProfile();
			}
			MpRuntime.Instance?.NotifyChatToast($"* weather: {profileName}");
		}
		catch (Exception ex)
		{
			Plugin.Log.LogWarning($"WeatherFx: {ex.Message}");
		}
		finally
		{
			_applyingRemoteWeather = false;
		}
	}

	public static void ApplyHbGrabFx(ulong peer)
	{
		MpRuntime.Instance?.GetGhost(peer)?.PulseBlink(2);
	}

	public static void ApplyHealFx(ulong peer, float amount)
	{
		var ghost = MpRuntime.Instance?.GetGhost(peer);
		ghost?.ApplyHeal(amount);
		string who = peer.ToString();
		var rt = MpRuntime.Instance;
		if (rt != null && rt.Session.Peers.TryGetValue(peer, out var p))
			who = p.Name;
		if (amount > 0.5f)
			rt?.NotifyChatToast($"* {who} healed +{amount:0}");
	}

	public static void ApplyShiftHitFx(ulong peer, byte kind)
	{
		var ghost = MpRuntime.Instance?.GetGhost(peer);
		ghost?.PulseShiftHit(kind);
		string who = peer.ToString();
		var rt = MpRuntime.Instance;
		if (rt != null && rt.Session.Peers.TryGetValue(peer, out var p))
			who = p.Name;
		rt?.NotifyChatToast(kind == 1 ? $"* {who} stalled" : $"* {who} money-shifted");
	}

	private static bool _applyingRemoteTrainMove;
	public static bool SuppressTrainMoveBroadcast => _applyingRemoteTrainMove;

	public static void ApplyTrainMoveFx(byte index, bool moving)
	{
		if (_applyingRemoteTrainMove) return;
		_applyingRemoteTrainMove = true;
		try
		{
			var trains = UnityEngine.Object.FindObjectsByType<TrainController>(FindObjectsSortMode.None);
			if (trains == null || index >= trains.Length) return;
			var t = trains[index];
			if (t == null) return;
			if (moving) t.StartMoving();
			else t.StopMoving();
		}
		catch (Exception ex)
		{
			Plugin.Log.LogWarning($"TrainMoveFx: {ex.Message}");
		}
		finally
		{
			_applyingRemoteTrainMove = false;
		}
	}

	public static void ApplyKillAmmoFx(ulong peer, int magazines)
	{
		string who = peer.ToString();
		var rt = MpRuntime.Instance;
		if (rt != null && rt.Session.Peers.TryGetValue(peer, out var p))
			who = p.Name;
		rt?.NotifyChatToast(magazines > 0
			? $"* {who} +{magazines} ammo (kill)"
			: $"* {who} kill-ammo");
		var ghost = rt?.GetGhost(peer);
		ghost?.PulseBlink(4);
		if (magazines > 0 && ghost != null && ghost.AmmoCount >= 0)
			ghost.SetAmmo(ghost.AmmoCount + magazines);
		else if (magazines > 0)
			ghost?.SetAmmo(magazines);
	}

	public static void ApplyFeatFx(ulong peer, int featId, string label)
	{
		_ = featId;
		string who = peer.ToString();
		var rt = MpRuntime.Instance;
		if (rt != null && rt.Session.Peers.TryGetValue(peer, out var p))
			who = p.Name;
		string bit = string.IsNullOrEmpty(label) ? "feat" : label;
		rt?.NotifyChatToast($"* {who} unlocked: {bit}");
		AddChatSafe($"* feat {who}: {bit}");
		rt?.GetGhost(peer)?.PulseBlink(6);
	}

	public static void ApplyGunTypeFx(ulong peer, byte gunType)
	{
		MpRuntime.Instance?.GetGhost(peer)?.SetGunType(gunType);
	}

	public static void ApplyShiftTypeFx(ulong peer, byte shifterType)
	{
		MpRuntime.Instance?.GetGhost(peer)?.SetShifterType(shifterType);
	}

	public static void ApplyShellFx(ulong peer, bool grabbed)
	{
		var ghost = MpRuntime.Instance?.GetGhost(peer);
		ghost?.PulseGunAct(grabbed ? (byte)0 : (byte)3, 0f);
		ghost?.PulseBlink(grabbed ? 2 : 1);
	}

	public static void ApplySlideFx(ulong peer, byte kind)
	{
		MpRuntime.Instance?.GetGhost(peer)?.PulseSlide(kind);
	}

	public static void ApplyClipFx(ulong peer, int consumed)
	{
		var ghost = MpRuntime.Instance?.GetGhost(peer);
		ghost?.PulseBlink(2);
		if (consumed > 0 && ghost != null && ghost.AmmoCount > 0)
			ghost.SetAmmo(Mathf.Max(0, ghost.AmmoCount)); // visual pulse only; count via AmmoState
		_ = consumed;
	}

	public static void ApplyUnlockFx(ulong peer, string upgradeId, string displayName)
	{
		_ = upgradeId;
		string who = peer.ToString();
		var rt = MpRuntime.Instance;
		if (rt != null && rt.Session.Peers.TryGetValue(peer, out var p))
			who = p.Name;
		string bit = string.IsNullOrEmpty(displayName) ? "upgrade" : displayName;
		rt?.NotifyChatToast($"* {who} unlocked {bit}");
		AddChatSafe($"* unlock {who}: {bit}");
		rt?.GetGhost(peer)?.PulseBlink(5);
	}

	public static void ApplyCamLookFx(ulong peer, bool released)
	{
		MpRuntime.Instance?.GetGhost(peer)?.SetCamLook(released);
	}

	private static bool _applyingRemoteRepair;
	public static bool SuppressRepairBroadcast => _applyingRemoteRepair;

	public static void ApplyRepairFx(ulong peer)
	{
		if (_applyingRemoteRepair) return;
		_applyingRemoteRepair = true;
		try
		{
			var ghost = MpRuntime.Instance?.GetGhost(peer);
			if (ghost?.BoundEnemy == null)
			{
				ghost?.PulseBlink(4);
				ghost?.ApplyHeal(25f);
				return;
			}
			var vis = ghost.BoundEnemy.GetComponent<CarDamageVisuals>()
			          ?? ghost.BoundEnemy.GetComponentInChildren<CarDamageVisuals>(true);
			vis?.ClearDamage();
			ghost.ApplyHeal(20f);
			ghost.PulseBlink(4);
			string who = peer.ToString();
			var rt = MpRuntime.Instance;
			if (rt != null && rt.Session.Peers.TryGetValue(peer, out var p))
				who = p.Name;
			rt?.NotifyChatToast($"* {who} repaired");
		}
		catch (Exception ex)
		{
			Plugin.Log.LogWarning($"RepairFx: {ex.Message}");
		}
		finally
		{
			_applyingRemoteRepair = false;
		}
	}

	private static bool _applyingRemoteGrime;
	public static bool SuppressGrimeBroadcast => _applyingRemoteGrime;

	public static void ApplyGrimeFx(ulong peer, float amount)
	{
		if (_applyingRemoteGrime) return;
		_applyingRemoteGrime = true;
		try
		{
			var ghost = MpRuntime.Instance?.GetGhost(peer);
			if (ghost?.BoundEnemy == null) return;
			var vis = ghost.BoundEnemy.GetComponent<CarDamageVisuals>()
			          ?? ghost.BoundEnemy.GetComponentInChildren<CarDamageVisuals>(true);
			vis?.AddGrime(amount);
		}
		catch (Exception ex)
		{
			Plugin.Log.LogWarning($"GrimeFx: {ex.Message}");
		}
		finally
		{
			_applyingRemoteGrime = false;
		}
	}

	public static void ApplyMagShotFx(ulong peer)
	{
		MpRuntime.Instance?.GetGhost(peer)?.PulseMagShot();
	}

	private static bool _applyingRemoteWindshield;
	public static bool SuppressWindshieldBroadcast => _applyingRemoteWindshield;

	public static void ApplyWindshieldFx(ulong peer, bool visible)
	{
		if (_applyingRemoteWindshield) return;
		_applyingRemoteWindshield = true;
		try
		{
			var ghost = MpRuntime.Instance?.GetGhost(peer);
			if (ghost?.BoundEnemy == null) return;
			var skin = ghost.BoundEnemy.GetComponent<CarSkinController>()
			           ?? ghost.BoundEnemy.GetComponentInChildren<CarSkinController>(true);
			skin?.SetWindshield(visible);
		}
		catch (Exception ex)
		{
			Plugin.Log.LogWarning($"WindshieldFx: {ex.Message}");
		}
		finally
		{
			_applyingRemoteWindshield = false;
		}
	}

	private static bool _applyingRemoteObjSay;
	public static bool SuppressObjSayBroadcast => _applyingRemoteObjSay;

	public static void ApplyObjSayFx(string line, float seconds)
	{
		if (string.IsNullOrWhiteSpace(line)) return;
		MpRuntime.Instance?.NotifyChatToast($"* {line}");
		AddChatSafe($"* obj: {line}");
		if (_applyingRemoteObjSay) return;
		_applyingRemoteObjSay = true;
		try
		{
			var tc = UnityEngine.Object.FindAnyObjectByType<TextController>();
			tc?.EnqueueText(line, Mathf.Clamp(seconds, 0.5f, 6f));
		}
		catch (Exception ex)
		{
			Plugin.Log.LogWarning($"ObjSayFx: {ex.Message}");
		}
		finally
		{
			_applyingRemoteObjSay = false;
		}
	}

	public static void ApplySteerLockFx(ulong peer, bool locked)
	{
		MpRuntime.Instance?.GetGhost(peer)?.SetSteerLocked(locked);
	}

	public static void ApplyRecenterFx(ulong peer)
	{
		MpRuntime.Instance?.GetGhost(peer)?.PulseRecenter();
	}

	public static void ApplyMatchRevFx(ulong peer)
	{
		MpRuntime.Instance?.GetGhost(peer)?.PulseMatchRev();
	}

	public static void ApplyRecordFx(ulong peer, string label, float seconds)
	{
		string who = peer.ToString();
		var rt = MpRuntime.Instance;
		if (rt != null && rt.Session.Peers.TryGetValue(peer, out var p))
			who = p.Name;
		string bit = string.IsNullOrEmpty(label) ? "record" : label;
		string time = seconds > 0.01f ? $" {LifetimeStats.FormatTime(seconds)}" : "";
		rt?.NotifyChatToast($"* {who} PB {bit}{time}");
		AddChatSafe($"* record {who}: {bit}{time}");
		rt?.GetGhost(peer)?.PulseBlink(8);
	}

	public static void ApplyLapAheadFx(ulong peer, int lapsAhead)
	{
		string who = peer.ToString();
		var rt = MpRuntime.Instance;
		if (rt != null && rt.Session.Peers.TryGetValue(peer, out var p))
			who = p.Name;
		int n = Mathf.Max(1, lapsAhead);
		string line = n == 1
			? $"* {who}: target protected, 1 lap ahead"
			: $"* {who}: target protected, {n} laps ahead";
		rt?.NotifyChatToast(line);
		try
		{
			var tc = UnityEngine.Object.FindAnyObjectByType<TextController>();
			tc?.EnqueueText($"TARGET PROTECTED, {n} LAPS AHEAD", 1.5f);
		}
		catch { /* */ }
	}

	public static void ApplyNosUpFx(ulong peer)
	{
		string who = peer.ToString();
		var rt = MpRuntime.Instance;
		if (rt != null && rt.Session.Peers.TryGetValue(peer, out var p))
			who = p.Name;
		rt?.NotifyChatToast($"* {who} fitted NOS");
		rt?.GetGhost(peer)?.SetNitroLit(true);
		rt?.GetGhost(peer)?.PulseBlink(4);
	}

	public static void ApplyPartsFx(ulong peer)
	{
		MpRuntime.Instance?.GetGhost(peer)?.PulseBlink(3);
		MpRuntime.Instance?.GetGhost(peer)?.ForceRebind();
	}

	public static void ApplyShakeFx(ulong peer, float severity)
	{
		// Ghost wobble only — do not KickSpring locally (would shake every peer's camera).
		MpRuntime.Instance?.GetGhost(peer)?.PulseShake(severity);
	}

	public static void ApplyChickenFx(ulong peer, int count)
	{
		if (count <= 0) return;
		string who = peer.ToString();
		var rt = MpRuntime.Instance;
		if (rt != null && rt.Session.Peers.TryGetValue(peer, out var p))
			who = p.Name;
		rt?.NotifyChatToast(count == 1
			? $"* {who} hit a chicken"
			: $"* {who} chickens x{count}");
	}

	private static bool _applyingRemoteChicken;
	public static bool SuppressChickenBroadcast => _applyingRemoteChicken;

	private static readonly System.Reflection.MethodInfo? ChickenDestroy =
		AccessTools.Method(typeof(chicken), "DestroyObject");

	public static void ApplyChickenGoneFx(Vector3 pos)
	{
		if (_applyingRemoteChicken) return;
		_applyingRemoteChicken = true;
		try
		{
			chicken? best = null;
			float bestD = 36f;
			foreach (var c in UnityEngine.Object.FindObjectsByType<chicken>(FindObjectsSortMode.None))
			{
				if (c == null) continue;
				float d = (c.transform.position - pos).sqrMagnitude;
				if (d < bestD) { bestD = d; best = c; }
			}
			if (best != null)
			{
				// Prefer DestroyObject so smash VFX + audio play on remotes.
				if (ChickenDestroy != null)
					ChickenDestroy.Invoke(best, null);
				else
					UnityEngine.Object.Destroy(best.gameObject);
			}
		}
		catch (Exception ex)
		{
			Plugin.Log.LogWarning($"ChickenGoneFx: {ex.Message}");
		}
		finally
		{
			_applyingRemoteChicken = false;
		}
	}

	private static float _lastTornadoBroadcast;
	private static bool _applyingRemoteBarrel;
	public static bool SuppressBarrelBroadcast => _applyingRemoteBarrel;

	public static void PulseTornadoSync(MpSession session)
	{
		if (session == null || !session.Linked || !session.IsHost) return;
		if (Time.unscaledTime - _lastTornadoBroadcast < 0.22f) return;
		_lastTornadoBroadcast = Time.unscaledTime;
		var tornados = UnityEngine.Object.FindObjectsByType<Tornado>(FindObjectsSortMode.None);
		if (tornados.Length == 0) return;
		var items = new List<(byte idx, Vector3 pos)>(tornados.Length);
		for (byte i = 0; i < tornados.Length && i < 32; i++)
		{
			if (tornados[i] == null) continue;
			items.Add((i, tornados[i].transform.position));
		}
		if (items.Count == 0) return;
		session.Broadcast(NetProtocol.TornadoSnap(items), false);
	}

	public static void ApplyTornadoSnap(IReadOnlyList<(byte idx, Vector3 pos)> items)
	{
		try
		{
			var tornados = UnityEngine.Object.FindObjectsByType<Tornado>(FindObjectsSortMode.None);
			foreach (var (idx, pos) in items)
			{
				if (idx >= tornados.Length) continue;
				var t = tornados[idx];
				if (t == null) continue;
				t.transform.position = pos;
			}
		}
		catch (Exception ex)
		{
			Plugin.Log.LogWarning($"TornadoSnap: {ex.Message}");
		}
	}

	public static void ApplyBarrelFx(Vector3 pos)
	{
		if (_applyingRemoteBarrel) return;
		_applyingRemoteBarrel = true;
		try
		{
			GameObject? prefab = null;
			foreach (var sp in UnityEngine.Object.FindObjectsByType<BarrelSpawner>(FindObjectsSortMode.None))
			{
				if (sp?.barrelPrefab != null) { prefab = sp.barrelPrefab; break; }
			}
			if (prefab == null) return;
			UnityEngine.Object.Instantiate(prefab, pos, Quaternion.identity);
		}
		catch (Exception ex)
		{
			Plugin.Log.LogWarning($"BarrelFx: {ex.Message}");
		}
		finally
		{
			_applyingRemoteBarrel = false;
		}
	}

	public static void ApplyItemUseFx(ulong peer)
	{
		string who = peer.ToString();
		var rt = MpRuntime.Instance;
		if (rt != null && rt.Session.Peers.TryGetValue(peer, out var p))
			who = p.Name;
		rt?.NotifyChatToast($"* {who} used an item");
		rt?.GetGhost(peer)?.PulseBlink(2);
	}

	public static void ApplyModelFx(ulong peer)
	{
		var ghost = MpRuntime.Instance?.GetGhost(peer);
		ghost?.PulseBlink(3);
		ghost?.ForceRebind();
		string who = peer.ToString();
		var rt = MpRuntime.Instance;
		if (rt != null && rt.Session.Peers.TryGetValue(peer, out var p))
			who = p.Name;
		rt?.NotifyChatToast($"* {who} changed car model");
	}

	public static void ApplyDiceFx(ulong peer, byte kind)
	{
		string who = peer.ToString();
		var rt = MpRuntime.Instance;
		if (rt != null && rt.Session.Peers.TryGetValue(peer, out var p))
			who = p.Name;
		string outcome = kind switch
		{
			0 => "hurt",
			1 => "heal",
			_ => "money",
		};
		rt?.NotifyChatToast($"* {who} dice → {outcome}");
		var ghost = rt?.GetGhost(peer);
		if (kind == 0) ghost?.PulseDamaged(0.6f);
		else if (kind == 1) ghost?.PulseBlink(2);
		else ghost?.PulseBlink(1);
	}

	public static void ApplyBeerFx(ulong peer)
	{
		string who = peer.ToString();
		var rt = MpRuntime.Instance;
		if (rt != null && rt.Session.Peers.TryGetValue(peer, out var p))
			who = p.Name;
		rt?.NotifyChatToast($"* {who} cracked a beer");
		rt?.GetGhost(peer)?.PulseBlink(2);
	}

	public static void PulseLocalNitroOff(MpSession session)
	{
		if (session == null || !session.Linked) return;
		bool on = false;
		try
		{
			foreach (var lever in UnityEngine.Object.FindObjectsByType<NNitroLever>(FindObjectsSortMode.None))
			{
				if (lever == null) continue;
				var cc = lever.GetComponentInParent<CarControllerNew>();
				if (cc == null || !cc.isPlayer) continue;
				on = lever.activeBoost > 0.01f;
				break;
			}
		}
		catch { return; }

		if (_localNitroOn && !on)
			session.Broadcast(NetProtocol.NitroFx(false, 0f), false);
		_localNitroOn = on;
	}

	/// <summary>Cache handbrake for Pose flags — avoids FindObjectsByType every send.</summary>
	public static void PulseLocalHandbrake(MpSession session)
	{
		_ = session;
		bool on = false;
		try
		{
			foreach (var hb in UnityEngine.Object.FindObjectsByType<NHandBrake>(FindObjectsSortMode.None))
			{
				if (hb == null || hb.value <= 0.45f) continue;
				var cc = hb.GetComponentInParent<CarControllerNew>();
				if (cc != null && !cc.isPlayer) continue;
				on = true;
				break;
			}
		}
		catch { on = false; }
		_localHandbrakeOn = on;
	}

	public static void ApplyTireFx(ulong peer, byte wheelIndex, Vector3 point)
	{
		if (_applyingRemoteTire) return;
		_applyingRemoteTire = true;
		try
		{
			var ghost = MpRuntime.Instance?.GetGhost(peer);
			if (ghost?.BoundEnemy == null) return;
			var tire = ghost.BoundEnemy.GetComponent<CarTireDamage>()
			           ?? ghost.BoundEnemy.GetComponentInChildren<CarTireDamage>(true);
			if (tire == null) return;
			TireBurstMethod?.Invoke(tire, new object[] { (int)wheelIndex });
			_ = point;
		}
		catch (Exception ex)
		{
			Plugin.Log.LogWarning($"TireFx: {ex.Message}");
		}
		finally
		{
			_applyingRemoteTire = false;
		}
	}

	public static void ApplyShieldFx(ulong peer, Vector3 point)
	{
		if (_applyingRemoteShield) return;
		_applyingRemoteShield = true;
		try
		{
			var ghost = MpRuntime.Instance?.GetGhost(peer);
			Transform? root = ghost?.BoundEnemy != null ? ghost.BoundEnemy.transform : ghost?.Root?.transform;
			if (root == null) return;
			var shield = root.GetComponentInChildren<BulletShield>(true);
			shield?.Block(point);
		}
		catch (Exception ex)
		{
			Plugin.Log.LogWarning($"ShieldFx: {ex.Message}");
		}
		finally
		{
			_applyingRemoteShield = false;
		}
	}

	public static void ApplyCollideImpulse(Vector3 impulse, Vector3 atPoint)
	{
		try
		{
			var car = FindLocalPlayerCar();
			if (car == null) return;
			var rb = car.GetComponent<Rigidbody>();
			if (rb == null) return;
			rb.AddForceAtPosition(impulse, atPoint, ForceMode.VelocityChange);
		}
		catch (Exception ex)
		{
			Plugin.Log.LogWarning($"CollideImpulse: {ex.Message}");
		}
	}

	public static void PulsePeerCollisions(MpSession session)
	{
		if (session == null || !session.Linked) return;
		var car = FindLocalPlayerCar();
		if (car == null) return;
		var rb = car.GetComponent<Rigidbody>();
		if (rb == null) return;
		var localPos = car.transform.position;
		const float contactRadius = 3.4f;
		float now = Time.unscaledTime;

		foreach (var ghost in MpRuntime.Instance?.AllGhosts() ?? Array.Empty<GhostCar>())
		{
			if (ghost?.Root == null || ghost.Hitbox == null) continue;
			ulong peer = ghost.Hitbox.PeerId;
			var gPos = ghost.Root.transform.position;
			var delta = localPos - gPos;
			float dist = delta.magnitude;
			if (dist > contactRadius || dist < 0.05f) continue;
			float penetration = contactRadius - dist;
			var dir = delta / dist;
			Vector3 vel = rb.linearVelocity;
			float approach = Mathf.Max(0f, -Vector3.Dot(vel, dir));
			if (penetration < 0.15f && approach < 1.5f) continue;
			if (CollideCooldown.TryGetValue(peer, out var until) && now < until) continue;
			CollideCooldown[peer] = now + 0.12f;

			float mag = Mathf.Clamp(penetration * 6f + approach * 0.35f, 1f, 28f);
			var impulse = dir * mag;
			var at = Vector3.Lerp(localPos, gPos, 0.5f) + Vector3.up * 0.4f;
			rb.AddForceAtPosition(impulse, at, ForceMode.VelocityChange);
			// Broadcast+filter — LAN SendTo alone often missed remapped peer ids.
			session.Broadcast(NetProtocol.CollideImpulse(peer, -impulse, at), true);
			// Local crash tick so contact also hurts.
			try
			{
				var link = car.carLink;
				if (link != null && mag > 4f)
					link.TakeCrashDamage(mag * 0.35f, shake: true, DamageKind.Crash);
			}
			catch { /* */ }
		}
	}

	/// <summary>
	/// Guest skips MenuManager.StartRace, so PlayerLoadout is empty.
	/// Restore THIS client's equipped gear from GameManager / SaveData (last race), then fit it.
	/// </summary>
	public static void EnsureLocalLoadoutFitted()
	{
		try
		{
			RestoreLocalLoadoutFromSave();
			if (!PlayerLoadout.Known || PlayerLoadout.Equipped.Count == 0)
			{
				Plugin.Log.LogWarning("Local loadout empty after restore — no gear to fit");
				return;
			}
			var spawner = UnityEngine.Object.FindFirstObjectByType<LoadoutSpawner>();
			if (spawner == null)
			{
				Plugin.Log.LogWarning("LoadoutSpawner missing on track");
				return;
			}
			// Strip anything fitted that is not in this client's save (editor stubs).
			var want = new HashSet<UpgradeComponent>(PlayerLoadout.Equipped);
			var fittedCopy = new List<UpgradeSpawn>(spawner.Fitted);
			foreach (var s in fittedCopy)
			{
				if (s?.upgrade == null) continue;
				if (!want.Contains(s.upgrade))
					spawner.Swap(s.upgrade, null);
			}
			foreach (var up in PlayerLoadout.Equipped)
			{
				if (up == null) continue;
				bool already = false;
				foreach (var s in spawner.Fitted)
				{
					if (s != null && s.upgrade == up) { already = true; break; }
				}
				if (!already)
					spawner.Swap(null, up);
			}
			Plugin.Log.LogInfo($"Local loadout fitted: {PlayerLoadout.Equipped.Count} upgrades");
		}
		catch (Exception ex)
		{
			Plugin.Log.LogWarning($"EnsureLocalLoadoutFitted: {ex.Message}");
		}
	}

	public static void RestoreLocalLoadoutFromSave()
	{
		try
		{
			// Always prefer THIS client's save — LoadoutSpawner may have already Set() an editor stub.
			Dictionary<string, UpgradeComponent>? fromSave = null;
			try
			{
				fromSave = UpgradeRepository.LoadEquipped(Array.Empty<InventorySlot>());
			}
			catch (Exception ex)
			{
				Plugin.Log.LogWarning($"UpgradeRepository.LoadEquipped: {ex.Message}");
			}

			if (fromSave == null || fromSave.Count == 0)
			{
				if (GameManager.equippedUpgrades != null && GameManager.equippedUpgrades.Count > 0)
					fromSave = GameManager.equippedUpgrades;
			}

			if (fromSave == null || fromSave.Count == 0)
			{
				Plugin.Log.LogWarning("No equipped upgrades in save — client will have no gear");
				return;
			}

			GameManager.equippedUpgrades = fromSave;
			PlayerStats.ResetAllUpgrades();
			foreach (var kv in fromSave)
				kv.Value?.Apply();
			PlayerLoadout.Set(fromSave.Values);
			Plugin.Log.LogInfo($"Restored local loadout from save ({fromSave.Count} items)");
		}
		catch (Exception ex)
		{
			Plugin.Log.LogWarning($"RestoreLocalLoadoutFromSave: {ex.Message}");
		}
	}

	public static void RestoreMenuCursor()
	{
		try
		{
			Cursor.lockState = CursorLockMode.Confined;
			Cursor.visible = true;
			InteriorMath.Paused = false;
			TimeScale.ResetAll();
		}
		catch { /* */ }
	}

	public static void PulseCutoffSync(MpSession session)
	{
		if (session == null || !session.Linked || !session.IsHost) return;
		int left = RaceCutoff.SecondsLeft;
		if (left < 0) return;
		float now = Time.unscaledTime;
		if (now - _lastCutoffBroadcast < 0.45f) return;
		_lastCutoffBroadcast = now;
		session.Broadcast(NetProtocol.CutoffState(left), true);
	}

	public static void ApplyCutoffState(float secondsLeft)
	{
		if (_applyingRemoteCutoff) return;
		_applyingRemoteCutoff = true;
		try
		{
			var inst = RaceCutoffInstance?.GetValue(null) as RaceCutoff;
			if (inst == null || RaceCutoffEndsAt == null) return;
			float ends = Time.time + Mathf.Max(0f, secondsLeft);
			RaceCutoffEndsAt.SetValue(inst, ends);
		}
		catch (Exception ex)
		{
			Plugin.Log.LogWarning($"CutoffState: {ex.Message}");
		}
		finally
		{
			_applyingRemoteCutoff = false;
		}
	}

	public static bool SuppressTireBroadcast => _applyingRemoteTire;
	public static bool SuppressShieldBroadcast => _applyingRemoteShield;
	public static bool SuppressBlastBroadcast => _applyingRemoteBlast;
	public static bool SuppressRocketBroadcast => _applyingRemoteRocket;
	public static bool SuppressPickupBroadcast => _applyingRemotePickup;
	public static bool SuppressDestructibleBroadcast => _applyingRemoteDestructible;
	public static bool SuppressGlassBroadcast => _applyingRemoteGlass;
	public static bool SuppressTreeBroadcast => _applyingRemoteTree;
	public static bool SuppressSpikeBroadcast => _applyingRemoteSpike;

	private static bool _applyingRemoteGlass;
	private static bool _applyingRemoteTree;
	private static bool _applyingRemoteSpike;

	/// <summary>Damage peers whose ghost hitbox sits inside a blast sphere (ghosts are invincible to AreaBlast).</summary>
	public static void DamagePeersInBlast(Vector3 centre, float radius, float damageMax, float damageMin)
	{
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		if (radius <= 0.01f) return;
		foreach (var ghost in rt.AllGhosts())
		{
			if (ghost?.Hitbox == null || ghost.Root == null) continue;
			float dist = Vector3.Distance(ghost.Root.transform.position, centre);
			if (dist > radius + 1.5f) continue;
			float t = Mathf.Clamp01(dist / radius);
			float dmg = Mathf.Lerp(damageMax, damageMin, t);
			if (dmg > 0.5f)
				rt.DealDamageToPeer(ghost.Hitbox.PeerId, dmg, centre);
		}
	}

	public static void BroadcastBlast(MpSession session, Vector3 centre, float radius, float damageMax, float damageMin, byte source)
	{
		if (session == null || !session.Linked || SuppressBlastBroadcast) return;
		session.Broadcast(NetProtocol.BlastFx(centre, radius, damageMax, damageMin, source), true);
		DamagePeersInBlast(centre, radius, damageMax, damageMin);
	}

	public static void ApplyBlastFx(Vector3 centre, float radius, float damageMax, float damageMin, byte source)
	{
		if (_applyingRemoteBlast) return;
		_applyingRemoteBlast = true;
		try
		{
			// Visual/world: destroy nearest barrel in radius, then local-only AreaBlast (no rebroadcast).
			ExplosiveBarrel? best = null;
			float bestD = radius * radius + 4f;
			foreach (var b in UnityEngine.Object.FindObjectsByType<ExplosiveBarrel>(FindObjectsSortMode.None))
			{
				if (b == null) continue;
				float d = (b.transform.position - centre).sqrMagnitude;
				if (d < bestD) { bestD = d; best = b; }
			}
			if (best != null)
				UnityEngine.Object.Destroy(best.gameObject);

			AreaBlast.Apply(centre, radius, damageMax, damageMin, (BlastSource)source);
		}
		catch (Exception ex)
		{
			Plugin.Log.LogWarning($"BlastFx: {ex.Message}");
		}
		finally
		{
			_applyingRemoteBlast = false;
		}
	}

	public static void ApplyRocketFx(Vector3 origin, Vector3 landing, float speed, float arcHeight)
	{
		if (_applyingRemoteRocket) return;
		_applyingRemoteRocket = true;
		try
		{
			Rocket? prefab = null;
			foreach (var launcher in UnityEngine.Object.FindObjectsByType<EnemyRocketLauncher>(FindObjectsSortMode.None))
			{
				if (launcher?.rocketPrefab != null) { prefab = launcher.rocketPrefab; break; }
			}
			if (prefab == null)
			{
				foreach (var r in Resources.FindObjectsOfTypeAll<Rocket>())
				{
					if (r != null) { prefab = r; break; }
				}
			}
			if (prefab == null) return;
			var rocket = UnityEngine.Object.Instantiate(prefab, origin, Quaternion.identity);
			rocket.speed = speed;
			rocket.arcHeight = arcHeight;
			rocket.gameObject.AddComponent<RemoteRocketTag>();
			rocket.Launch(landing, null, ~0);
		}
		catch (Exception ex)
		{
			Plugin.Log.LogWarning($"RocketFx: {ex.Message}");
		}
		finally
		{
			_applyingRemoteRocket = false;
		}
	}

	public static void ApplyPickupFx(byte kind, Vector3 pos)
	{
		if (_applyingRemotePickup) return;
		_applyingRemotePickup = true;
		try
		{
			if (kind == 0)
			{
				ApplyAmmoFx(pos, 0);
				return;
			}
			MonoBehaviour? best = null;
			float bestD = 64f;
			if (kind == 1)
			{
				foreach (var drop in UnityEngine.Object.FindObjectsByType<HealthDrop>(FindObjectsSortMode.None))
				{
					if (drop == null) continue;
					float d = (drop.transform.position - pos).sqrMagnitude;
					if (d < bestD) { bestD = d; best = drop; }
				}
			}
			else if (kind == 3)
			{
				foreach (var drop in UnityEngine.Object.FindObjectsByType<RoadPickup>(FindObjectsSortMode.None))
				{
					if (drop == null) continue;
					float d = (drop.transform.position - pos).sqrMagnitude;
					if (d < bestD) { bestD = d; best = drop; }
				}
			}
			else
			{
				foreach (var drop in UnityEngine.Object.FindObjectsByType<EffectDrop>(FindObjectsSortMode.None))
				{
					if (drop == null) continue;
					float d = (drop.transform.position - pos).sqrMagnitude;
					if (d < bestD) { bestD = d; best = drop; }
				}
			}
			if (best != null)
				UnityEngine.Object.Destroy(best.gameObject);
			else
				SpawnFlashLine(pos + Vector3.up * 0.5f, new Color(0.9f, 0.85f, 0.3f, 0.9f), 1.1f, 0.4f);
		}
		catch (Exception ex)
		{
			Plugin.Log.LogWarning($"PickupFx: {ex.Message}");
		}
		finally
		{
			_applyingRemotePickup = false;
		}
	}

	public static void ApplySpikeFx(ulong peer, Vector3 point, Vector3 dir)
	{
		if (_applyingRemoteSpike) return;
		_applyingRemoteSpike = true;
		try
		{
			var ghost = MpRuntime.Instance?.GetGhost(peer);
			var origin = ghost?.Root != null ? ghost.Root.transform.position : point;
			SpawnFlashLine(point, new Color(0.95f, 0.2f, 0.35f, 0.95f), 0.9f, 0.35f);
			var tip = point + dir.normalized * 2.2f;
			var go = new GameObject("HGMP_SpikeLine");
			go.transform.position = point;
			var lr = go.AddComponent<LineRenderer>();
			lr.positionCount = 2;
			lr.SetPosition(0, origin + Vector3.up * 0.4f);
			lr.SetPosition(1, tip);
			lr.startWidth = 0.08f;
			lr.endWidth = 0.02f;
			lr.material = new Material(Shader.Find("Sprites/Default") ?? Shader.Find("Legacy Shaders/Particles/Alpha Blended"));
			lr.startColor = new Color(1f, 0.3f, 0.25f, 0.9f);
			lr.endColor = new Color(1f, 0.3f, 0.25f, 0.2f);
			UnityEngine.Object.Destroy(go, 0.45f);
		}
		catch (Exception ex)
		{
			Plugin.Log.LogWarning($"SpikeFx: {ex.Message}");
		}
		finally
		{
			_applyingRemoteSpike = false;
		}
	}

	public static void ApplyGlassFx(ulong peer, Vector3 pos)
	{
		if (_applyingRemoteGlass) return;
		_applyingRemoteGlass = true;
		try
		{
			var ghost = MpRuntime.Instance?.GetGhost(peer);
			GlassDamage? glass = null;
			if (ghost?.BoundEnemy != null)
				glass = ghost.BoundEnemy.GetComponentInChildren<GlassDamage>(true);
			if (glass == null)
			{
				float best = 36f;
				foreach (var g in UnityEngine.Object.FindObjectsByType<GlassDamage>(FindObjectsSortMode.None))
				{
					if (g == null) continue;
					float d = (g.transform.position - pos).sqrMagnitude;
					if (d < best) { best = d; glass = g; }
				}
			}
			if (glass != null && !glass.Shattered)
				glass.Shatter();
			SpawnFlashLine(pos + Vector3.up * 1.1f, new Color(0.7f, 0.9f, 1f, 0.9f), 1.2f, 0.4f);
		}
		catch (Exception ex)
		{
			Plugin.Log.LogWarning($"GlassFx: {ex.Message}");
		}
		finally
		{
			_applyingRemoteGlass = false;
		}
	}

	public static void ApplyTreeFx(byte terrainIndex, int treeIndex, Vector3 push)
	{
		if (_applyingRemoteTree) return;
		_applyingRemoteTree = true;
		try
		{
			var terrains = UnityEngine.Object.FindObjectsByType<FellableTerrain>(FindObjectsSortMode.None);
			if (terrainIndex >= terrains.Length) return;
			var ft = terrains[terrainIndex];
			if (ft == null) return;
			var away = push.sqrMagnitude > 0.01f ? push.normalized : Vector3.forward;
			var fell = AccessTools.Method(typeof(FellableTerrain), "Fell");
			fell?.Invoke(ft, new object[] { treeIndex, push, away });
		}
		catch (Exception ex)
		{
			Plugin.Log.LogWarning($"TreeFx: {ex.Message}");
		}
		finally
		{
			_applyingRemoteTree = false;
		}
	}

	public static void ApplyMudFx(ulong peer, Vector3 pos)
	{
		_ = peer;
		SpawnFlashLine(pos + Vector3.up * 0.2f, new Color(0.45f, 0.3f, 0.12f, 0.9f), 0.7f, 0.35f);
	}

	public static void ApplyTrapFx(ulong peer, Vector3 pos, float damage)
	{
		_ = peer;
		_ = damage;
		SpawnFlashLine(pos + Vector3.up * 0.4f, new Color(0.9f, 0.15f, 0.1f, 0.95f), 1.0f, 0.4f);
	}

	public static void ApplyHammerFx(ulong peer, Vector3 pos, float damage)
	{
		_ = peer;
		SpawnFlashLine(pos + Vector3.up * 0.8f, new Color(1f, 0.55f, 0.1f, 0.95f), 1.3f + damage * 0.02f, 0.45f);
	}

	public static void ApplyInvertFx(ulong peer)
	{
		var ghost = MpRuntime.Instance?.GetGhost(peer);
		ghost?.PulseInvert();
		if (ghost?.Root != null)
			SpawnFlashLine(ghost.Root.transform.position + Vector3.up * 1.2f,
				new Color(0.95f, 0.75f, 0.2f, 0.95f), 2f, 0.55f);
		MpRuntime.Instance?.NotifyChatToast("* invert!");
	}

	public static void ApplyComboFx(ulong peer, string name, float value)
	{
		string who = peer.ToString();
		var rt = MpRuntime.Instance;
		if (rt != null && rt.Session.Peers.TryGetValue(peer, out var p))
			who = p.Name;
		string label = string.IsNullOrEmpty(name) ? "combo" : name;
		rt?.NotifyChatToast($"* {who}: {label} {value:0}");
	}

	public static void ApplyCheckpointFx(ulong peer, byte index, bool starting)
	{
		string who = peer.ToString();
		var rt = MpRuntime.Instance;
		if (rt != null && rt.Session.Peers.TryGetValue(peer, out var p))
			who = p.Name;
		string text = starting ? $"* {who} crossed start/finish" : $"* {who} checkpoint {index}";
		rt?.NotifyChatToast(text);
	}

	public static void ApplyGuillotineFx(ulong peer, Vector3 pos)
	{
		_ = peer;
		SpawnFlashLine(pos + Vector3.up * 0.8f, new Color(0.7f, 0.05f, 0.1f, 0.95f), 1.5f, 0.45f);
	}

	public static void ApplyDrainFx(ulong peer, Vector3 pos)
	{
		MpRuntime.Instance?.GetGhost(peer)?.SetLeech(true);
		SpawnFlashLine(pos + Vector3.up * 0.6f, new Color(0.4f, 0.9f, 0.35f, 0.9f), 1.0f, 0.35f);
	}

	public static void ApplyTrampleFx(Vector3 pos, float strength, float radius)
	{
		try
		{
			var field = TrampleField.Instance;
			if (field == null) return;
			field.AddRadial(pos, Mathf.Max(0.5f, radius), Mathf.Clamp(strength, 0.05f, 2f), 6);
		}
		catch (Exception ex)
		{
			Plugin.Log.LogWarning($"TrampleFx: {ex.Message}");
		}
	}

	public static void ApplyExplodeFx(ulong peer, byte kind, Vector3 pos)
	{
		var ghost = MpRuntime.Instance?.GetGhost(peer);
		if (ghost != null)
		{
			ghost.PlayExplode(kind);
			ghost.SetBurning(true);
			return;
		}
		SpawnFlashLine(pos + Vector3.up * 0.5f,
			kind == 0 ? new Color(1f, 0.6f, 0.15f, 0.9f) : new Color(1f, 0.25f, 0.05f, 0.95f),
			kind == 0 ? 1.2f : 2.4f, 0.55f);
	}

	public static void ApplyBotFlameFx(byte botIndex, bool on)
	{
		try
		{
			var tm = UnityEngine.Object.FindFirstObjectByType<TrackManager>();
			if (tm?.Enemies == null || botIndex >= tm.Enemies.Count) return;
			var enemy = tm.Enemies[botIndex];
			if (enemy == null || enemy.GetComponent<RemotePlayerHitbox>() != null) return;
			var nitro = enemy.GetComponentInChildren<EnemyNitro>(true);
			if (nitro != null)
			{
				var set = AccessTools.Method(typeof(EnemyNitro), "SetFlames");
				set?.Invoke(nitro, new object[] { on });
				return;
			}
			var fx = enemy.GetComponentInChildren<EnemyCarEffectController>(true);
			if (fx == null) return;
			if (on) fx.StartFlamesEffect();
			else fx.StopFireEmission();
		}
		catch (Exception ex)
		{
			Plugin.Log.LogWarning($"BotFlameFx: {ex.Message}");
		}
	}

	public static void ApplyAmmoEatFx(ulong peer, Vector3 pos)
	{
		_ = peer;
		SpawnFlashLine(pos + Vector3.up * 0.5f, new Color(0.95f, 0.55f, 0.15f, 0.95f), 1.1f, 0.4f);
	}

	public static void ApplyTreeKickFx(Vector3 pos, Vector3 lean, float degPerSec)
	{
		try
		{
			LiveTree? best = null;
			float bestD = 64f;
			foreach (var tree in UnityEngine.Object.FindObjectsByType<LiveTree>(FindObjectsSortMode.None))
			{
				if (tree == null) continue;
				float d = (tree.transform.position - pos).sqrMagnitude;
				if (d < bestD) { bestD = d; best = tree; }
			}
			if (best != null)
				best.Kick(lean.sqrMagnitude > 0.01f ? lean : Vector3.forward, degPerSec);
			else
				SpawnFlashLine(pos + Vector3.up * 1f, new Color(0.35f, 0.7f, 0.25f, 0.85f), 1.6f, 0.4f);
		}
		catch (Exception ex)
		{
			Plugin.Log.LogWarning($"TreeKickFx: {ex.Message}");
		}
	}

	public static void ApplyRefillFx(ulong peer, byte kind, float amount)
	{
		_ = kind;
		_ = amount;
		string who = peer.ToString();
		var rt = MpRuntime.Instance;
		if (rt != null && rt.Session.Peers.TryGetValue(peer, out var p))
			who = p.Name;
		rt?.NotifyChatToast($"* {who} refilled");
		var ghost = rt?.GetGhost(peer);
		if (ghost?.Root != null)
			SpawnFlashLine(ghost.Root.transform.position + Vector3.up * 1.3f,
				new Color(0.4f, 0.95f, 1f, 0.9f), 1.4f, 0.45f);
	}

	public static void ApplyThreatFx(ulong peer, bool on)
	{
		string who = peer.ToString();
		var rt = MpRuntime.Instance;
		if (rt != null && rt.Session.Peers.TryGetValue(peer, out var p))
			who = p.Name;
		rt?.GetGhost(peer)?.SetThreatened(on);
		if (on)
			rt?.NotifyChatToast($"* AI locking on {who}");
	}

	public static void ApplyCanisterFx(ulong peer, float charge)
	{
		var ghost = MpRuntime.Instance?.GetGhost(peer);
		if (ghost?.Root != null)
			SpawnFlashLine(ghost.Root.transform.position + Vector3.up * 1f,
				new Color(0.55f, 0.35f, 1f, 0.9f), 0.8f + charge * 0.4f, 0.35f);
	}

	public static void ApplyCamFx(ulong peer, byte outside)
	{
		string who = peer.ToString();
		var rt = MpRuntime.Instance;
		if (rt != null && rt.Session.Peers.TryGetValue(peer, out var p))
			who = p.Name;
		rt?.NotifyChatToast(outside != 0 ? $"* {who}: exterior cam" : $"* {who}: cabin cam");
		rt?.GetGhost(peer)?.SetCamOutside(outside != 0);
	}

	public static void ApplyHbLockFx(ulong peer, bool locked)
	{
		MpRuntime.Instance?.GetGhost(peer)?.SetHandbrakeLocked(locked);
	}

	public static void ApplyShortcutFx(ulong peer, float metres)
	{
		string who = peer.ToString();
		var rt = MpRuntime.Instance;
		if (rt != null && rt.Session.Peers.TryGetValue(peer, out var p))
			who = p.Name;
		rt?.NotifyChatToast($"* {who} shortcut {metres:0}m");
		AddChatSafe($"* {who} shortcut {metres:0}m");
	}

	public static void ApplyEarnFx(ulong peer, string label, float amount)
	{
		string who = peer.ToString();
		var rt = MpRuntime.Instance;
		if (rt != null && rt.Session.Peers.TryGetValue(peer, out var p))
			who = p.Name;
		string bit = string.IsNullOrEmpty(label) ? $"${amount:0}" : $"{label} ${amount:0}";
		rt?.NotifyChatToast($"* {who} +{bit}");
	}

	public static void ApplyHintFx(ulong peer, byte kind, bool on)
	{
		if (!on) return;
		string who = peer.ToString();
		var rt = MpRuntime.Instance;
		if (rt != null && rt.Session.Peers.TryGetValue(peer, out var p))
			who = p.Name;
		string text = kind == 0 ? $"* {who}: wrong way" : $"* {who}: stuck / off track";
		rt?.NotifyChatToast(text);
		rt?.GetGhost(peer)?.SetThreatened(true);
	}

	public static void ApplyCabinAmmoFx(ulong peer, bool picked, int magazines)
	{
		string who = peer.ToString();
		var rt = MpRuntime.Instance;
		if (rt != null && rt.Session.Peers.TryGetValue(peer, out var p))
			who = p.Name;
		rt?.NotifyChatToast(picked
			? $"* {who} grabbed ammo x{magazines}"
			: $"* {who} dropped ammo");
		rt?.GetGhost(peer)?.PulseBlink(picked ? 4 : 2);
	}

	public static void ApplyAimFx(ulong peer, bool aiming)
	{
		MpRuntime.Instance?.GetGhost(peer)?.SetAiming(aiming);
	}

	public static void ApplyMagFx(ulong peer, bool seated)
	{
		string who = peer.ToString();
		var rt = MpRuntime.Instance;
		if (rt != null && rt.Session.Peers.TryGetValue(peer, out var p))
			who = p.Name;
		rt?.NotifyChatToast(seated ? $"* {who} seated mag" : $"* {who} ejected mag");
		rt?.GetGhost(peer)?.PulseMag(seated);
	}

	public static void ApplyPauseFx(ulong peer, bool paused)
	{
		string who = peer.ToString();
		var rt = MpRuntime.Instance;
		if (rt != null && rt.Session.Peers.TryGetValue(peer, out var p))
			who = p.Name;
		rt?.NotifyChatToast(paused ? $"* {who} paused" : $"* {who} resumed");
		if (_applyingRemotePause) return;
		_applyingRemotePause = true;
		try
		{
			if (paused)
			{
				PauseController.PauseRace();
			}
			else
			{
				var pc = UnityEngine.Object.FindFirstObjectByType<PauseController>();
				pc?.Resume();
			}
		}
		catch (Exception ex)
		{
			Plugin.Log.LogWarning($"PauseFx: {ex.Message}");
		}
		finally
		{
			_applyingRemotePause = false;
		}
	}

	public static void ApplyCutoffSay(string line)
	{
		if (string.IsNullOrWhiteSpace(line)) return;
		MpRuntime.Instance?.NotifyChatToast($"* {line}");
		AddChatSafe($"* cutoff: {line}");
		if (_applyingRemoteCutoffSay) return;
		_applyingRemoteCutoffSay = true;
		try
		{
			var inst = RaceCutoffInstance?.GetValue(null) as RaceCutoff;
			if (inst == null || RaceCutoffSay == null) return;
			RaceCutoffSay.Invoke(inst, new object[] { line, 2.5f });
		}
		catch (Exception ex)
		{
			Plugin.Log.LogWarning($"CutoffSay: {ex.Message}");
		}
		finally
		{
			_applyingRemoteCutoffSay = false;
		}
	}

	private static void AddChatSafe(string line)
	{
		try { MpRuntime.Instance?.Session.AddChat(line); } catch { /* */ }
	}

	private static CarControllerNew? FindLocalPlayerCar()
	{
		foreach (var c in UnityEngine.Object.FindObjectsByType<CarControllerNew>(FindObjectsSortMode.None))
			if (c != null && c.isPlayer && c.isActiveAndEnabled) return c;
		return UnityEngine.Object.FindFirstObjectByType<TrackManager>()?.playerCarController;
	}

	public static void ApplyResetFx(ulong peer, Vector3 pos, Quaternion rot)
	{
		var ghost = MpRuntime.Instance?.GetGhost(peer);
		if (ghost == null) return;
		ghost.PushTarget(pos, rot, 0f, ghost.Hp, Time.unscaledTime);
	}

	public static void ApplyHornFx(ulong peer, bool on)
	{
		var ghost = MpRuntime.Instance?.GetGhost(peer);
		var origin = ghost?.Root != null
			? ghost.Root.transform.position
			: Vector3.zero;
		try
		{
			AudioClip? clip = null;
			foreach (var c in Resources.FindObjectsOfTypeAll<NClaxon>())
			{
				if (c?.hornClip != null) { clip = c.hornClip; break; }
			}
			if (clip == null) return;
			if (on)
				SoundController.PlaySoundAtPosition(clip, origin, 0.7f, 5f, 80f);
		}
		catch { /* audio */ }
		_ = on;
	}

	public static void SpawnWorldPing(Vector3 pos, string label)
	{
		var go = new GameObject("HGMP_Ping");
		go.transform.position = pos + Vector3.up * 2.5f;
		var tm = go.AddComponent<TextMesh>();
		tm.text = string.IsNullOrEmpty(label) ? "!" : $"! {label}";
		tm.characterSize = 0.35f;
		tm.anchor = TextAnchor.MiddleCenter;
		tm.color = new Color(1f, 0.85f, 0.2f, 1f);
		var pulse = go.AddComponent<MpPingPulse>();
		pulse.Life = 4f;
		UnityEngine.Object.Destroy(go, 4.2f);
	}

	public static void ApplyMoneyFx(Vector3 pos)
	{
		if (_applyingRemotePickup) return;
		_applyingRemotePickup = true;
		try
		{
			MoneyDrop? best = null;
			float bestD = 64f;
			foreach (var drop in UnityEngine.Object.FindObjectsByType<MoneyDrop>(FindObjectsSortMode.None))
			{
				if (drop == null) continue;
				float d = (drop.transform.position - pos).sqrMagnitude;
				if (d < bestD) { bestD = d; best = drop; }
			}
			if (best != null)
				UnityEngine.Object.Destroy(best.gameObject);
		}
		catch (Exception ex)
		{
			Plugin.Log.LogWarning($"MoneyFx: {ex.Message}");
		}
		finally
		{
			_applyingRemotePickup = false;
		}
	}

	public static void ApplyDestructibleFx(Vector3 pos)
	{
		if (_applyingRemoteDestructible) return;
		_applyingRemoteDestructible = true;
		try
		{
			DestructibleObject? best = null;
			float bestD = 100f;
			foreach (var d in UnityEngine.Object.FindObjectsByType<DestructibleObject>(FindObjectsSortMode.None))
			{
				if (d == null) continue;
				float dist = (d.transform.position - pos).sqrMagnitude;
				if (dist < bestD) { bestD = dist; best = d; }
			}
			best?.DestroyObject();
		}
		catch (Exception ex)
		{
			Plugin.Log.LogWarning($"DestructibleFx: {ex.Message}");
		}
		finally
		{
			_applyingRemoteDestructible = false;
		}
	}

	public static void ApplyJumpFx(ulong peer, Vector3 pos)
	{
		var ghost = MpRuntime.Instance?.GetGhost(peer);
		if (ghost?.Root != null)
		{
			var p = ghost.Root.transform.position;
			float boostY = Mathf.Max(p.y + 2.5f, pos.y + 1.5f);
			ghost.PushTarget(new Vector3(p.x, boostY, p.z), ghost.Root.transform.rotation,
				Mathf.Max(ghost.Hp > 0 ? 25f : 0f, 20f), ghost.Hp, Time.unscaledTime);
			ghost.PulseSpinOut(40f);
			ghost.PulseBlink(2);
		}
		try
		{
			AudioClip[]? clips = null;
			foreach (var btn in Resources.FindObjectsOfTypeAll<NJumpButton>())
			{
				if (btn?.jumpSounds != null && btn.jumpSounds.Length > 0)
				{
					clips = btn.jumpSounds;
					break;
				}
			}
			if (clips != null)
				SoundController.PlayRandomSoundAtPosition(clips, pos, 0.55f, 4f, 90f);
		}
		catch { /* audio */ }
	}

	private static float _lastTrainBroadcast = -999f;
	private static int _lastAmmoSent = -1;

	public static void PulseLocalAmmoState(MpSession session, MagazineManager? mm = null)
	{
		if (session == null || !session.Linked) return;
		try
		{
			mm ??= UnityEngine.Object.FindFirstObjectByType<MagazineManager>();
			if (mm == null) return;
			var field = AccessTools.Field(typeof(MagazineManager), "items");
			int count = 0;
			if (field?.GetValue(mm) is System.Collections.IList list)
				count = list.Count;
			count = Mathf.Clamp(count, 0, 255);
			if (count == _lastAmmoSent) return;
			_lastAmmoSent = count;
			session.Broadcast(NetProtocol.AmmoState((byte)count), false);
		}
		catch { /* ammo optional */ }
	}

	public static void PulseTrainSync(MpSession session)
	{
		if (session == null || !session.Linked || !session.IsHost) return;
		if (Time.unscaledTime - _lastTrainBroadcast < 0.35f) return;
		_lastTrainBroadcast = Time.unscaledTime;
		var trains = UnityEngine.Object.FindObjectsByType<TrainController>(FindObjectsSortMode.None);
		for (int i = 0; i < trains.Length && i < 255; i++)
		{
			var t = trains[i];
			if (t == null) continue;
			session.Broadcast(NetProtocol.TrainSnap((byte)i, t.HeadDistance, t.IsMoving), false);
		}
	}

	public static void ApplyTrainSnap(byte index, float headDistance, bool moving)
	{
		try
		{
			var trains = UnityEngine.Object.FindObjectsByType<TrainController>(FindObjectsSortMode.None);
			if (index >= trains.Length) return;
			var t = trains[index];
			if (t == null) return;
			t.TeleportTo(headDistance);
			if (moving) t.StartMoving();
			else t.StopMoving();
		}
		catch (Exception ex)
		{
			Plugin.Log.LogWarning($"TrainSnap: {ex.Message}");
		}
	}

	public static void ApplyTotemFx(ulong peer, float healed, int left)
	{
		_ = left;
		var ghost = MpRuntime.Instance?.GetGhost(peer);
		if (ghost?.Root == null) return;
		SpawnFlashLine(ghost.Root.transform.position + Vector3.up * 1.5f,
			new Color(0.3f, 1f, 0.5f, 0.9f), 1f + healed * 2f, 0.6f);
		if (ghost.Hp < 100f)
			ghost.PushTarget(ghost.Root.transform.position, ghost.Root.transform.rotation, 0f,
				Mathf.Min(100f, ghost.Hp + healed * 100f), Time.unscaledTime);
	}

	public static void ApplySlowMoFx(ulong peer, float duration)
	{
		var ghost = MpRuntime.Instance?.GetGhost(peer);
		if (ghost?.Root == null) return;
		SpawnFlashLine(ghost.Root.transform.position + Vector3.up * 1.2f,
			new Color(0.55f, 0.35f, 1f, 0.95f), 2.2f, Mathf.Clamp(duration * 0.15f, 0.4f, 1.2f));
		MpRuntime.Instance?.NotifyChatToast($"* slow-mo ({duration:0.0}s)");
	}

	public static void ApplyDriftFx(ulong peer, Vector3 pos, float meters)
	{
		_ = meters;
		var origin = pos;
		var ghost = MpRuntime.Instance?.GetGhost(peer);
		if (ghost?.Root != null)
			origin = ghost.Root.transform.position;
		SpawnFlashLine(origin + Vector3.up * 0.3f, new Color(1f, 0.85f, 0.4f, 0.7f), 0.8f, 0.25f);
	}

	public static void ApplySpunOutFx(ulong peer, float speed)
	{
		var ghost = MpRuntime.Instance?.GetGhost(peer);
		var origin = ghost?.Root != null ? ghost.Root.transform.position : Vector3.zero;
		SpawnFlashLine(origin + Vector3.up * 1f, new Color(1f, 0.45f, 0.1f, 0.9f), 1.4f, 0.5f);
		_ = speed;
	}

	public static void ApplyCarDamagedFx(ulong peer, float fraction, byte kind)
	{
		_ = kind;
		var ghost = MpRuntime.Instance?.GetGhost(peer);
		if (ghost?.Root == null) return;
		SpawnFlashLine(ghost.Root.transform.position + Vector3.up * 1.1f,
			new Color(1f, 0.2f, 0.15f, 0.85f), 0.6f + fraction, 0.35f);
	}

	private static void SpawnFlashLine(Vector3 origin, Color color, float height, float life)
	{
		var go = new GameObject("HGMP_FxFlash");
		go.transform.position = origin;
		var lr = go.AddComponent<LineRenderer>();
		lr.positionCount = 2;
		lr.SetPosition(0, origin);
		lr.SetPosition(1, origin + Vector3.up * height);
		lr.startWidth = 0.22f;
		lr.endWidth = 0.04f;
		lr.material = new Material(Shader.Find("Sprites/Default") ?? Shader.Find("Legacy Shaders/Particles/Alpha Blended"));
		lr.startColor = color;
		var end = color; end.a *= 0.15f;
		lr.endColor = end;
		UnityEngine.Object.Destroy(go, life);
	}

	public static void ApplyBossKill()
	{
		BossRace.ReportDestroyed();
		MpRuntime.Instance?.Session.AddChat("* Boss destroyed (synced)");
	}

	public static void SendLateJoinBundle(MpSession session, ulong peerId)
	{
		if (session == null || !session.IsHost) return;
		session.BroadcastSlotMap();
		if (RaceSync.HasSetup)
		{
			session.SendTo(peerId, NetProtocol.RaceSetup(
				RaceSync.SpawnSeed, RaceSync.ShortRace, RaceSync.TrackVariantByte, RaceSync.MaxLaps,
				RaceSync.WeatherName, RaceSync.MapName, RaceSync.SceneName,
				RaceSync.ObjectiveKindByte, RaceSync.ObjectiveAmount, RaceSync.ObjectiveTarget,
				RaceSync.MusicName, RaceSync.CeasefireSeconds, RaceSync.SelectedNodeIndex,
				RaceSync.FreePlay, RaceSync.NoBots), true);
		}
		BroadcastMapSelect(session);
		BroadcastRunSnap(session);
		// Shop/money/offers intentionally omitted — each peer keeps private economy.
		ForceBroadcastLoadout(session); // peer visuals only — never applies to local inventory
		MpRuntime.Instance?.BroadcastLocalCarLook(force: true);
		// World hazards / weather so late joiners match host immediately.
		try
		{
			if (!string.IsNullOrEmpty(RaceSync.WeatherName))
				session.SendTo(peerId, NetProtocol.WeatherFx(RaceSync.WeatherName), true);
			_lastTornadoBroadcast = 0f;
			PulseTornadoSync(session);
			_lastTrainBroadcast = 0f;
			PulseTrainSync(session);
			PulseCutoffSync(session);
		}
		catch (Exception ex)
		{
			Plugin.Log.LogWarning($"LateJoin world snap: {ex.Message}");
		}
		if (session.RaceStartReceived)
			session.SendTo(peerId, NetProtocol.RaceStart(Time.unscaledTime), true);
		if (GameManager.raceOver)
			session.SendTo(peerId, NetProtocol.RaceEnd(99, false, false, false, false), true);
	}
}

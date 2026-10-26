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
	private static string _lastLoadoutKey = "";
	private static bool _localNitroOn;
	private static float _lastCutoffBroadcast = -999f;
	private static AudioClip[]? _cachedShootSounds;

	private static readonly FieldInfo? RaceCutoffInstance =
		AccessTools.Field(typeof(RaceCutoff), "instance");
	private static readonly FieldInfo? RaceCutoffEndsAt =
		AccessTools.Field(typeof(RaceCutoff), "endsAt");
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
			session.Broadcast(NetProtocol.RunSnap(json), true);
			Plugin.Log.LogInfo($"RunSnap broadcast ({json.Length} chars) — graph only, no wallets");
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

	public static void TryRegisterGhostOnLeaderboard(GhostCar ghost)
	{
		if (ghost?.BoundEnemy == null) return;
		var tm = UnityEngine.Object.FindFirstObjectByType<TrackManager>();
		var lm = tm?.lapManager;
		if (lm == null) return;
		try { lm.RegisterCar(ghost.BoundEnemy.gameObject); }
		catch { /* already registered */ }
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

	public static void StorePeerLap(ulong peer, int lap, float progress, int place, bool finished)
	{
		PeerLaps[peer] = (lap, progress, place, finished);
		var ghost = MpRuntime.Instance?.GetGhost(peer);
		if (ghost?.BoundEnemy == null) return;
		var tm = UnityEngine.Object.FindFirstObjectByType<TrackManager>();
		var lm = tm != null ? tm.lapManager : null;
		if (lm?.leaderboard == null) return;
		var go = ghost.BoundEnemy.gameObject;
		if (!lm.leaderboard.TryGetValue(go, out var e))
		{
			TryRegisterGhostOnLeaderboard(ghost);
			if (!lm.leaderboard.TryGetValue(go, out e)) return;
		}
		e.currentLap = lap;
		e.lapProgress = progress;
		e.position = place;
		e.isFinished = finished;
		try
		{
			if (lm.trackLine != null && lm.trackLine.trackLength > 0.01f)
			{
				float frac = Mathf.Clamp01(progress / 100f);
				e.totalDistance = (lap + frac) * lm.trackLine.trackLength;
				e.trackProgress = e.totalDistance;
			}
		}
		catch { /* track line */ }
		lm.leaderboard[go] = e;
	}

	public static void SpawnGunTracer(Vector3 origin, Vector3 target)
	{
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
		PlayRemoteGunSound(origin);
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
		if (ghost?.BoundEnemy == null) return;
		var flames = ghost.BoundEnemy.GetComponentInChildren<NitroFlames>(true);
		if (flames?.flames == null) return;
		foreach (var sr in flames.flames)
			if (sr != null) sr.enabled = on;
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
				var field = AccessTools.Field(typeof(NNitroLever), "activeBoost");
				float boost = field != null ? (float)field.GetValue(lever)! : 0f;
				on = boost > 0.01f;
				break;
			}
		}
		catch { return; }

		if (_localNitroOn && !on)
			session.Broadcast(NetProtocol.NitroFx(false, 0f), false);
		_localNitroOn = on;
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
			session.SendTo(peer, NetProtocol.CollideImpulse(-impulse, at), false);
		}
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
				RaceSync.MusicName, RaceSync.CeasefireSeconds, RaceSync.SelectedNodeIndex), true);
		}
		BroadcastMapSelect(session);
		BroadcastRunSnap(session);
		// Shop/money/offers intentionally omitted — each peer keeps private economy.
		ForceBroadcastLoadout(session); // peer visuals only — never applies to local inventory
		MpRuntime.Instance?.BroadcastLocalCarLook(force: true);
		if (session.RaceStartReceived)
			session.SendTo(peerId, NetProtocol.RaceStart(Time.unscaledTime), true);
		if (GameManager.raceOver)
			session.SendTo(peerId, NetProtocol.RaceEnd(99, false, false, false, false), true);
	}
}

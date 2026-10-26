using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace HalfGripMultiplayer;

/// <summary>Gun hits on remote ghosts → networked PvP (body AND tire rays).</summary>
[HarmonyPatch(typeof(Gun))]
internal static class GunPvPPatches
{
	[HarmonyPostfix]
	[HarmonyPatch("HandleEnemyCollision")]
	private static void HandleEnemyCollision_Post(Gun __instance, RaycastHit hit, Ray shotRay)
	{
		_ = shotRay;
		TryHitRemote(__instance, hit.collider, hit.point);
	}

	/// <summary>Tire hits skip HandleEnemyCollision — catch them here.</summary>
	[HarmonyPrefix]
	[HarmonyPatch("ShootSeen")]
	private static bool ShootSeen_Prefix(Gun __instance, CarTireDamage seen, Ray ray, float distance, Vector3 normal, bool tire)
	{
		_ = distance;
		_ = normal;
		_ = tire;
		if (seen?.Enemy == null) return true;
		var box = seen.Enemy.GetComponent<RemotePlayerHitbox>();
		if (box == null) return true;
		TryHitRemote(__instance, seen.Enemy.transform, ray.GetPoint(distance));
		return false; // do not TakeDamage the invincible ghost body
	}

	private static void TryHitRemote(Gun gun, Component? hitFrom, Vector3 point)
	{
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		if (hitFrom == null) return;
		var box = hitFrom.GetComponentInParent<RemotePlayerHitbox>()
		          ?? hitFrom.GetComponent<RemotePlayerHitbox>();
		if (box == null) return;

		float dmg = 50f;
		try
		{
			var prop = AccessTools.Property(typeof(Gun), "Damage");
			if (prop != null) dmg = (float)prop.GetValue(gun)!;
			else
			{
				var field = AccessTools.Field(typeof(Gun), "damage");
				if (field != null) dmg = (float)field.GetValue(gun)!;
			}
		}
		catch { /* keep default */ }
		dmg *= box.DamageMultiplier;
		rt.DealDamageToPeer(box.PeerId, dmg, point);
	}
}

/// <summary>
/// Guard CameraSwitch when Main/Interior cameras were destroyed (scene sync).
/// Rebind to live cameras — never permanently disable (that left guests with a broken view).
/// </summary>
[HarmonyPatch(typeof(CameraSwitch), "LateUpdate")]
internal static class CameraSwitchPatches
{
	private static readonly System.Reflection.FieldInfo? MainCam =
		AccessTools.Field(typeof(CameraSwitch), "mainCamera");
	private static readonly System.Reflection.FieldInfo? InteriorCam =
		AccessTools.Field(typeof(CameraSwitch), "interiorCamera");
	private static readonly System.Reflection.FieldInfo? RearCam =
		AccessTools.Field(typeof(CameraSwitch), "rearViewCamera");
	private static readonly System.Reflection.FieldInfo? OutsideCam =
		AccessTools.Field(typeof(CameraSwitch), "outsideCamera");

	[HarmonyPrefix]
	private static bool Prefix(CameraSwitch __instance)
	{
		if (__instance == null) return false;
		var main = MainCam?.GetValue(__instance) as Camera;
		var interior = InteriorCam?.GetValue(__instance) as Camera;
		if (main != null && interior != null) return true;

		TryRebind(__instance);
		main = MainCam?.GetValue(__instance) as Camera;
		interior = InteriorCam?.GetValue(__instance) as Camera;
		return main != null && interior != null;
	}

	private static void TryRebind(CameraSwitch cs)
	{
		SetCam(MainCam, cs, "Main Camera");
		SetCam(InteriorCam, cs, "InteriorCamera");
		SetCam(RearCam, cs, "RearViewCamera");
		SetCam(OutsideCam, cs, "OutsideCamera");
	}

	private static void SetCam(System.Reflection.FieldInfo? field, CameraSwitch cs, string goName)
	{
		if (field == null) return;
		var existing = field.GetValue(cs) as Camera;
		if (existing != null) return;
		var go = GameObject.Find(goName);
		if (go == null) return;
		var cam = go.GetComponent<Camera>();
		if (cam != null) field.SetValue(cs, cam);
	}
}

/// <summary>Host: push track AND lobby/UI loads to guests.</summary>
[HarmonyPatch(typeof(SceneLoader), nameof(SceneLoader.LoadScene), typeof(string))]
internal static class SceneLoaderPatches
{
	[HarmonyPostfix]
	private static void Postfix(string sceneName)
	{
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked || !rt.Session.IsHost) return;
		if (string.IsNullOrEmpty(sceneName)) return;
		bool sync = sceneName.StartsWith("SCENE_TRACK_", StringComparison.Ordinal)
		            || sceneName.StartsWith("SCENE_UI_", StringComparison.Ordinal);
		if (!sync) return;

		if (sceneName.StartsWith("SCENE_UI_", StringComparison.Ordinal))
		{
			rt.Session.InLobbyPhase = true;
			rt.Session.RaceStartReceived = false;
			rt.Session.ClearAllReady();
			rt.Session.AddChat("* Lobby — everyone configure, then Ready");
		}
		rt.Session.BroadcastHostScene(sceneName);
	}
}

/// <summary>Lobby gate: host cannot Start Race until every connected peer pressed Ready. Guests cannot start.</summary>
[HarmonyPatch(typeof(MenuManager), nameof(MenuManager.StartRaceButtonEvent))]
internal static class MenuStartRacePatches
{
	[HarmonyPrefix]
	private static bool Prefix()
	{
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return true;
		if (!rt.Session.IsHost)
		{
			rt.Session.AddChat("* Only the host can start the race");
			Plugin.Log.LogInfo("Guest blocked from StartRace");
			return false;
		}
		if (rt.Session.Peers.Count > 0)
		{
			// Host is authority — never soft-lock lobby on a missing Ready packet.
			if (!rt.Session.AllPeersReady())
				rt.Session.AddChat("* Host starting — all marked Ready");
			rt.Session.MarkAllPeersReady();
			Plugin.Log.LogInfo("Host StartRace: auto MarkAllPeersReady");
		}
		rt.Session.ClearForceStart();
		return true;
	}
}

/// <summary>Push weather / laps / variant / spawn seed immediately after BeginRace, before LoadTrackScene.</summary>
[HarmonyPatch(typeof(RunState), nameof(RunState.BeginRace))]
internal static class BeginRaceSetupPatches
{
	[HarmonyPostfix]
	private static void Postfix()
	{
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked || !rt.Session.IsHost) return;
		rt.Session.InLobbyPhase = false;
		rt.Session.BroadcastRaceSetup();
		rt.Session.AddChat("* Race setup synced — loading track");
	}
}

/// <summary>Shared RNG so host/guest enemy grids match for bot index + slot claims.</summary>
[HarmonyPatch(typeof(TrackManager), "SpawnEnemies")]
internal static class SpawnEnemiesSeedPatches
{
	[HarmonyPrefix]
	private static void Prefix()
	{
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		RaceSync.InitSpawnSeedIfNeeded();
	}
}

/// <summary>
/// LapManager only registers cars when leaderboard is empty. If a ghost registered first,
/// the player never gets in → GetCarProjectedPoint throws → no laps for anyone.
/// Always ensure the local player (and live bots) are on the board.
/// Also swallow GetCarProjectedPoint failures so one bad car cannot stop all lap ticks.
/// </summary>
[HarmonyPatch(typeof(LapManager), "Update")]
internal static class LapManagerEnsureRegisteredPatches
{
	private static readonly FieldInfo? PlayerCarField =
		AccessTools.Field(typeof(LapManager), "playerCar");
	private static readonly FieldInfo? EnemiesField =
		AccessTools.Field(typeof(LapManager), "enemies");

	[HarmonyPrefix]
	private static void Prefix(LapManager __instance)
	{
		if (__instance?.leaderboard == null) return;
		var player = PlayerCarField?.GetValue(__instance) as GameObject;
		if (player != null && !__instance.leaderboard.ContainsKey(player))
		{
			try { __instance.RegisterCar(player); }
			catch (Exception ex) { Plugin.Log.LogWarning($"Lap register player: {ex.Message}"); }
		}
		if (EnemiesField?.GetValue(__instance) is System.Collections.IList list)
		{
			foreach (var item in list)
			{
				if (item is not EnemyController enemy || enemy == null || !enemy.alive) continue;
				var go = enemy.gameObject;
				if (__instance.leaderboard.ContainsKey(go)) continue;
				try { __instance.RegisterCar(go); }
				catch { /* */ }
			}
		}
	}
}

[HarmonyPatch(typeof(LapManager), "GetCarProjectedPoint")]
internal static class LapManagerProjectedPointGuardPatches
{
	[HarmonyPrefix]
	private static bool Prefix(LapManager __instance, GameObject car)
	{
		if (__instance?.leaderboard == null || car == null) return false;
		if (!__instance.leaderboard.ContainsKey(car))
		{
			try { __instance.RegisterCar(car); }
			catch { return false; }
			if (!__instance.leaderboard.ContainsKey(car)) return false;
		}
		return true;
	}
}

/// <summary>In MP, never use the track's editorLoadout — always this client's saved gear.</summary>
[HarmonyPatch(typeof(LoadoutSpawner), "ResolveLoadout")]
internal static class LoadoutSpawnerResolveMpPatches
{
	[HarmonyPrefix]
	private static bool Prefix(ref System.Collections.Generic.List<UpgradeComponent> __result)
	{
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return true;
		WorldSync.RestoreLocalLoadoutFromSave();
		if (!PlayerLoadout.Known || PlayerLoadout.Equipped.Count == 0)
		{
			__result = null!;
			Plugin.Log.LogWarning("MP ResolveLoadout: no saved gear for this client");
			return false;
		}
		__result = new System.Collections.Generic.List<UpgradeComponent>(PlayerLoadout.Equipped);
		return false;
	}
}

/// <summary>After grid + enemies exist: place local car on assigned slot, freeze guest AI.</summary>
[HarmonyPatch(typeof(TrackManager), "Start")]
internal static class TrackManagerStartPatches
{
	[HarmonyPostfix]
	private static void Postfix(TrackManager __instance)
	{
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		if (__instance == null || __instance.playerCarController == null) return;
		rt.OnTrackReady(__instance);
	}
}

/// <summary>Shared race start: delay TrackManager countdown until host fires RaceStart.</summary>
[HarmonyPatch(typeof(TrackManager), "RaceCountdown")]
internal static class RaceStartPatches
{
	internal static bool BypassGate;

	[HarmonyPrefix]
	private static bool Prefix(TrackManager __instance, ref IEnumerator __result)
	{
		var rt = MpRuntime.Instance;
		if (BypassGate || rt == null || !rt.Session.Linked) return true;
		if (__instance == null || __instance.playerCarController == null) return true;
		__result = GatedCountdown(__instance);
		return false;
	}

	private static IEnumerator GatedCountdown(TrackManager tm)
	{
		var session = MpRuntime.Instance!.Session;
		session.InLobbyPhase = false;
		if (session.IsHost)
			session.RaceStartReceived = false;
		session.SetReady(true);
		session.AddChat("* Waiting for shared race start...");
		Plugin.Log.LogInfo("Race gate: local ready (real race)");

		float timeoutAt = Time.unscaledTime + 120f;
		float nextReadyPulse = 0f;
		float soloGraceAt = Time.unscaledTime + 8f;
		if (session.IsHost)
		{
			while (Time.unscaledTime < timeoutAt)
			{
				if (session.RaceStartReceived) break;
				if (Time.unscaledTime >= nextReadyPulse)
				{
					nextReadyPulse = Time.unscaledTime + 0.5f;
					session.SetReady(true, quiet: true);
					if (session.Peers.Count > 0 &&
					    (session.AllPeersReady() || session.ForceStartUnlocked))
					{
						session.ClearForceStart();
						session.BroadcastRaceStart();
						break;
					}
					if (session.Peers.Count == 0 && Time.unscaledTime >= soloGraceAt)
					{
						session.BroadcastRaceStart();
						break;
					}
				}
				yield return null;
			}
			if (!session.RaceStartReceived)
			{
				Plugin.Log.LogWarning("Race gate: host timeout — starting anyway");
				session.BroadcastRaceStart();
			}
		}
		else
		{
			while (!session.RaceStartReceived && Time.unscaledTime < timeoutAt)
			{
				if (Time.unscaledTime >= nextReadyPulse)
				{
					nextReadyPulse = Time.unscaledTime + 0.5f;
					session.SetReady(true, quiet: true);
				}
				yield return null;
			}
			if (!session.RaceStartReceived)
				Plugin.Log.LogWarning("Race gate: guest timeout — running local countdown");
		}

		session.AddChat("* Countdown go");
		Plugin.Log.LogInfo("Race gate: countdown go");
		BypassGate = true;
		IEnumerator? original = null;
		try
		{
			var method = AccessTools.Method(typeof(TrackManager), "RaceCountdown");
			original = (IEnumerator)method.Invoke(tm, null)!;
		}
		finally
		{
			BypassGate = false;
		}

		if (original != null)
		{
			while (original.MoveNext())
				yield return original.Current;
		}
	}
}

/// <summary>Paint / chassis change → push CarLook to peers.</summary>
[HarmonyPatch(typeof(CarSkinController), nameof(CarSkinController.ApplySkin))]
internal static class CarSkinApplyPatches
{
	[HarmonyPostfix]
	private static void Postfix(CarSkinController __instance)
	{
		if (__instance == null || !__instance.isPlayer) return;
		CarLookSync.MarkDirty();
		MpRuntime.Instance?.BroadcastLocalCarLook(force: true);
	}
}

[HarmonyPatch(typeof(CarSkinController), nameof(CarSkinController.Refit))]
internal static class CarSkinRefitPatches
{
	[HarmonyPostfix]
	private static void Postfix(CarSkinController __instance)
	{
		if (__instance == null || !__instance.isPlayer) return;
		CarLookSync.MarkDirty();
		MpRuntime.Instance?.BroadcastLocalCarLook(force: true);
	}
}

[HarmonyPatch(typeof(PlayerCarSkin), nameof(PlayerCarSkin.Wear))]
internal static class PlayerPaintWearPatches
{
	[HarmonyPostfix]
	private static void Postfix()
	{
		CarLookSync.MarkDirty();
		MpRuntime.Instance?.BroadcastLocalCarLook(force: true);
	}
}

/// <summary>Broadcast wreck when local player HP hits 0 (once per death).</summary>
internal static class WreckSyncGate
{
	private static float _last = -999f;

	public static bool TrySend()
	{
		if (Time.unscaledTime - _last < 1.5f) return false;
		_last = Time.unscaledTime;
		return true;
	}

	public static void Reset() => _last = -999f;
}

[HarmonyPatch(typeof(CarLink), nameof(CarLink.TakeCrashDamage))]
internal static class DamageBroadcastPatches
{
	[HarmonyPostfix]
	private static void Postfix(CarLink __instance, float damage)
	{
		_ = damage;
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		if (__instance == null || __instance.cc == null || !__instance.cc.isPlayer) return;
		if (__instance.hp > 0f)
		{
			WreckSyncGate.Reset();
			return;
		}
		if (!WreckSyncGate.TrySend()) return;
		rt.Session.Broadcast(NetProtocol.Wreck(), true);
		var pos = __instance.cc.transform.position;
		rt.Session.Broadcast(NetProtocol.ExplodeFx(1, pos), false);
		rt.Session.AddChat("* You wrecked");
		rt.EnterSpectate();
	}
}

/// <summary>Also catch LoadSceneWithEffects so fade transitions sync lobby/track.</summary>
[HarmonyPatch(typeof(SceneLoader), nameof(SceneLoader.LoadSceneWithEffects))]
internal static class SceneLoaderEffectsPatches
{
	[HarmonyPostfix]
	private static void Postfix(string sceneName, float fadeTimer)
	{
		_ = fadeTimer;
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked || !rt.Session.IsHost) return;
		if (string.IsNullOrEmpty(sceneName)) return;
		bool sync = sceneName.StartsWith("SCENE_TRACK_", StringComparison.Ordinal)
		            || sceneName.StartsWith("SCENE_UI_", StringComparison.Ordinal);
		if (!sync) return;
		if (sceneName.StartsWith("SCENE_UI_", StringComparison.Ordinal))
		{
			rt.Session.InLobbyPhase = true;
			rt.Session.RaceStartReceived = false;
		}
		rt.Session.BroadcastHostScene(sceneName);
	}
}

/// <summary>Shared RNG for chickens/traps so both clients get the same hazard layout.</summary>
[HarmonyPatch(typeof(hazardsSpawner), "Start")]
internal static class HazardsSeedPatches
{
	[HarmonyPrefix]
	private static void Prefix()
	{
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		RaceSync.InitHazardSeedIfNeeded();
	}
}

[HarmonyPatch(typeof(HazardsSpawner_FINAL), "Start")]
internal static class HazardsFinalSeedPatches
{
	[HarmonyPrefix]
	private static void Prefix()
	{
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		RaceSync.InitHazardSeedIfNeeded();
	}
}

/// <summary>Clients skip tornado pathing — host snaps positions so force spheres stay aligned.</summary>
[HarmonyPatch(typeof(Tornado), "Update")]
internal static class TornadoClientPatches
{
	private static readonly System.Reflection.MethodInfo? AffectCars =
		AccessTools.Method(typeof(Tornado), "AffectCars");
	private static readonly System.Reflection.FieldInfo? ForceTimer =
		AccessTools.Field(typeof(Tornado), "forceTimer");
	private static readonly System.Reflection.FieldInfo? ForceInterval =
		AccessTools.Field(typeof(Tornado), "forceInterval");

	[HarmonyPrefix]
	private static bool Prefix(Tornado __instance)
	{
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked || rt.Session.IsHost) return true;
		// Respect forceInterval — calling AffectCars every frame multiplies impulse vs host.
		try
		{
			if (ForceTimer == null || ForceInterval == null || AffectCars == null) return false;
			float timer = (float)ForceTimer.GetValue(__instance)!;
			float interval = (float)ForceInterval.GetValue(__instance)!;
			timer += Time.deltaTime;
			if (timer >= interval)
			{
				timer = 0f;
				AffectCars.Invoke(__instance, null);
			}
			ForceTimer.SetValue(__instance, timer);
		}
		catch { /* */ }
		return false;
	}
}

/// <summary>Host-only barrel belt spawns; remotes instantiate from BarrelFx.</summary>
[HarmonyPatch(typeof(BarrelSpawner), "SpawnObject")]
internal static class BarrelSpawnSyncPatches
{
	[HarmonyPrefix]
	private static bool Prefix(BarrelSpawner __instance)
	{
		if (WorldSync.SuppressBarrelBroadcast) return false;
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return true;
		if (!rt.Session.IsHost) return false;
		return true;
	}

	[HarmonyPostfix]
	private static void Postfix(BarrelSpawner __instance)
	{
		if (WorldSync.SuppressBarrelBroadcast) return;
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked || !rt.Session.IsHost) return;
		if (__instance == null) return;
		Vector3 pos = __instance.spawnPoint != null
			? __instance.spawnPoint.position
			: __instance.transform.position;
		rt.Session.Broadcast(NetProtocol.BarrelFx(pos), true);
	}
}

// RaceEnd broadcast lives on TrackManager.EndRace (EndRaceSyncPatches) — more reliable.

/// <summary>Gun muzzle/tracer FX for remotes (both host→guest and guest→host).</summary>
[HarmonyPatch(typeof(Gun), "FireEffects")]
internal static class GunFireFxPatches
{
	[HarmonyPostfix]
	private static void Postfix(Gun __instance, Vector3 targetPoint)
	{
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		if (__instance == null) return;
		// Skip guns sitting on a remote ghost (those are visual-only).
		if (__instance.GetComponentInParent<RemotePlayerHitbox>() != null) return;
		var cc = __instance.carLink != null ? __instance.carLink.cc : null;
		if (cc == null || !cc.isPlayer) return;
		var origin = __instance.firePoint != null ? __instance.firePoint.position : cc.transform.position;
		rt.Session.Broadcast(NetProtocol.GunFx(origin, targetPoint, rt.Session.LocalId), true);
	}
}

/// <summary>Map node selection on the run graph.</summary>
[HarmonyPatch(typeof(RunState), nameof(RunState.Select))]
internal static class RunSelectSyncPatches
{
	[HarmonyPostfix]
	private static void Postfix(int index)
	{
		_ = index;
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked || !rt.Session.IsHost) return;
		WorldSync.BroadcastMapSelect(rt.Session);
	}
}

[HarmonyPatch(typeof(RunState), nameof(RunState.RerollSelectedMap))]
internal static class RunRerollSyncPatches
{
	[HarmonyPostfix]
	private static void Postfix(bool __result)
	{
		if (!__result) return;
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked || !rt.Session.IsHost) return;
		WorldSync.BroadcastMapSelect(rt.Session);
		// Map/weather may change — push a lightweight RaceSetup when already mid-lobby.
		if (rt.Session.InLobbyPhase)
			rt.Session.BroadcastRaceSetup();
	}
}

/// <summary>Ammo pickup removes the box on peers.</summary>
[HarmonyPatch(typeof(AmmoDrop), "Collect")]
internal static class AmmoPickupSyncPatches
{
	[HarmonyPostfix]
	private static void Postfix(AmmoDrop __instance, CarControllerNew car, bool __result)
	{
		if (!__result || __instance == null || car == null || !car.isPlayer) return;
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		rt.Session.Broadcast(NetProtocol.AmmoFx(__instance.transform.position, __instance.magazines), true);
		WorldSync.PulseLocalAmmoState(rt.Session);
	}
}

/// <summary>Rack magazine count on peer nameplates.</summary>
[HarmonyPatch(typeof(MagazineManager), nameof(MagazineManager.Grant))]
internal static class AmmoGrantStatePatches
{
	[HarmonyPostfix]
	private static void Postfix(MagazineManager __instance, int magazines, int __result)
	{
		_ = magazines;
		_ = __result;
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		WorldSync.PulseLocalAmmoState(rt.Session, __instance);
	}
}

[HarmonyPatch(typeof(MagazineManager), nameof(MagazineManager.Eat))]
internal static class AmmoEatStatePatches
{
	[HarmonyPostfix]
	private static void Postfix(MagazineManager __instance, int magazines, int __result)
	{
		_ = magazines;
		_ = __result;
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		WorldSync.PulseLocalAmmoState(rt.Session, __instance);
	}
}

/// <summary>Nitro flames on remote ghosts.</summary>
[HarmonyPatch]
internal static class NitroFxPatches
{
	private static System.Reflection.MethodBase TargetMethod() =>
		AccessTools.Method(typeof(Signals), nameof(Signals.Emit)).MakeGenericMethod(typeof(NitroFired));

	[HarmonyPostfix]
	private static void Postfix(NitroFired signal)
	{
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		rt.Session.Broadcast(NetProtocol.NitroFx(true, signal.power), false);
	}
}

/// <summary>Hold-R reset pose snap for remotes.</summary>
[HarmonyPatch(typeof(PlayerResetLogic), nameof(PlayerResetLogic.ResetOnTrack))]
internal static class ResetFxPatches
{
	[HarmonyPostfix]
	private static void Postfix(PlayerResetLogic __instance)
	{
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		var car = MpRuntimeFindCar();
		if (car == null) return;
		rt.Session.Broadcast(NetProtocol.ResetFx(car.transform.position, car.transform.rotation), true);
	}

	private static CarControllerNew? MpRuntimeFindCar()
	{
		foreach (var c in UnityEngine.Object.FindObjectsByType<CarControllerNew>(FindObjectsSortMode.None))
			if (c != null && c.isPlayer && c.isActiveAndEnabled) return c;
		return UnityEngine.Object.FindFirstObjectByType<TrackManager>()?.playerCarController;
	}
}

/// <summary>Boss destroyed flag.</summary>
[HarmonyPatch(typeof(BossRace), nameof(BossRace.ReportDestroyed))]
internal static class BossKillSyncPatches
{
	[HarmonyPostfix]
	private static void Postfix()
	{
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked || !rt.Session.IsHost) return;
		rt.Session.Broadcast(NetProtocol.BossKill(), true);
	}
}

/// <summary>Loadout equipped in menu → push ids for REMOTE VISUALS only (local inventory stays yours).</summary>
[HarmonyPatch(typeof(PlayerLoadout), nameof(PlayerLoadout.Set))]
internal static class LoadoutSetSyncPatches
{
	[HarmonyPostfix]
	private static void Postfix()
	{
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		WorldSync.ForceBroadcastLoadout(rt.Session);
	}
}

/// <summary>AI hits a remote ghost hitbox → networked damage to that peer.</summary>
[HarmonyPatch(typeof(EnemyGun), "HandleHit")]
internal static class EnemyGunHitPeerPatches
{
	[HarmonyPrefix]
	private static bool Prefix(EnemyGun __instance, RaycastHit hit, Ray shotRay)
	{
		_ = shotRay;
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return true;
		var box = hit.collider.GetComponentInParent<RemotePlayerHitbox>();
		if (box == null) return true;
		float dmg = __instance != null ? __instance.damage : 25f;
		rt.DealDamageToPeer(box.PeerId, dmg, hit.point);
		return false; // don't also TakeDamage the invincible ghost body
	}
}

/// <summary>Host AI muzzle tracers for guests.</summary>
[HarmonyPatch(typeof(EnemyGun), "FireEffects")]
internal static class EnemyGunFxPatches
{
	[HarmonyPostfix]
	private static void Postfix(EnemyGun __instance, Vector3 origin, Vector3 landedPoint)
	{
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked || !rt.Session.IsHost) return;
		if (__instance == null) return;
		// Skip guns on peer-claimed slots (those are remotes, not AI).
		if (__instance.GetComponentInParent<RemotePlayerHitbox>() != null) return;
		rt.Session.Broadcast(NetProtocol.GunFx(origin, landedPoint), false);
	}
}

/// <summary>Dent / impact marks on peer cars and bots.</summary>
[HarmonyPatch(typeof(CarDamageVisuals), nameof(CarDamageVisuals.ReportHit))]
internal static class DentFxPatches
{
	private static bool _remote;

	internal static void ApplyRemote()
	{
		_remote = true;
	}

	[HarmonyPostfix]
	private static void Postfix(CarDamageVisuals __instance, Vector3 worldPoint, Vector3 worldNormal, float strength, DamageMarkKind kind)
	{
		if (_remote)
		{
			_remote = false;
			return;
		}
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		if (__instance == null) return;

		var box = __instance.GetComponentInParent<RemotePlayerHitbox>();
		if (box != null)
		{
			rt.Session.Broadcast(NetProtocol.DentFx(0, box.PeerId, worldPoint, worldNormal, strength, (byte)kind), false);
			return;
		}
		if (!rt.Session.IsHost) return;
		var enemy = __instance.GetComponentInParent<EnemyController>();
		if (enemy == null || enemy.GetComponent<RemotePlayerHitbox>() != null) return;
		var tm = UnityEngine.Object.FindFirstObjectByType<TrackManager>();
		if (tm == null) return;
		int idx = -1;
		for (int i = 0; i < tm.Enemies.Count; i++)
			if (tm.Enemies[i] == enemy) { idx = i; break; }
		if (idx < 0) return;
		rt.Session.Broadcast(NetProtocol.DentFx(1, (ulong)idx, worldPoint, worldNormal, strength, (byte)kind), false);
	}
}

/// <summary>After host advances the run, push map graph (not wallets).</summary>
[HarmonyPatch(typeof(RunState), nameof(RunState.FinishRace))]
internal static class RunFinishSnapPatches
{
	[HarmonyPostfix]
	private static void Postfix()
	{
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked || !rt.Session.IsHost) return;
		WorldSync.BroadcastRunSnap(rt.Session);
		WorldSync.BroadcastMapSelect(rt.Session);
	}
}

[HarmonyPatch(typeof(RunState), nameof(RunState.StartRun))]
internal static class RunStartSnapPatches
{
	[HarmonyPostfix]
	private static void Postfix()
	{
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked || !rt.Session.IsHost) return;
		WorldSync.BroadcastRunSnap(rt.Session);
	}
}

/// <summary>Local tire burst → peers show the same wheel dead.</summary>
[HarmonyPatch(typeof(CarTireDamage), "Burst")]
internal static class TireBurstSyncPatches
{
	[HarmonyPostfix]
	private static void Postfix(CarTireDamage __instance, int index)
	{
		if (WorldSync.SuppressTireBroadcast) return;
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		if (__instance == null) return;
		var cc = __instance.GetComponentInParent<CarControllerNew>();
		if (cc == null || !cc.isPlayer) return;
		var point = cc.transform.position;
		try
		{
			var wheels = AccessTools.Field(typeof(CarTireDamage), "wheels")?.GetValue(__instance) as Transform[];
			if (wheels != null && index >= 0 && index < wheels.Length && wheels[index] != null)
				point = wheels[index].position;
		}
		catch { /* fallback pos */ }
		rt.Session.Broadcast(NetProtocol.TireFx((byte)Mathf.Clamp(index, 0, 255), point), true);
	}
}

/// <summary>Bullet shield block FX on remotes.</summary>
[HarmonyPatch(typeof(BulletShield), nameof(BulletShield.Block))]
internal static class ShieldBlockSyncPatches
{
	[HarmonyPostfix]
	private static void Postfix(BulletShield __instance, Vector3 point)
	{
		if (WorldSync.SuppressShieldBroadcast) return;
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		if (__instance == null) return;
		var cc = __instance.GetComponentInParent<CarControllerNew>();
		if (cc == null || !cc.isPlayer) return;
		rt.Session.Broadcast(NetProtocol.ShieldFx(point), false);
	}
}

/// <summary>Blasts: sync FX + deal networked damage to invincible ghost peers.</summary>
[HarmonyPatch(typeof(AreaBlast), nameof(AreaBlast.Apply))]
internal static class AreaBlastSyncPatches
{
	[HarmonyPostfix]
	private static void Postfix(Vector3 centre, float radius, float damageMax, float damageMin, BlastSource source, EnemyController spare)
	{
		_ = spare;
		if (WorldSync.SuppressBlastBroadcast) return;
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		WorldSync.BroadcastBlast(rt.Session, centre, radius, damageMax, damageMin, (byte)source);
	}
}

/// <summary>Ram into a remote ghost hitbox → PvP damage.</summary>
[HarmonyPatch(typeof(Ram), "OnCollisionEnter")]
internal static class RamPeerDamagePatches
{
	[HarmonyPostfix]
	private static void Postfix(Ram __instance, Collision collision)
	{
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		if (__instance == null || collision == null) return;
		var box = collision.collider.GetComponentInParent<RemotePlayerHitbox>();
		if (box == null) return;
		float dmg = __instance.damage;
		try { dmg = Stats.Get(Stat.RamDamage, dmg); } catch { /* keep */ }
		rt.DealDamageToPeer(box.PeerId, dmg, collision.GetContact(0).point);
	}
}

/// <summary>Host/AI rocket launch → guests spawn a VFX-only rocket.</summary>
[HarmonyPatch(typeof(EnemyRocketLauncher), "LaunchShot")]
internal static class RocketLaunchSyncPatches
{
	[HarmonyPostfix]
	private static void Postfix(EnemyRocketLauncher __instance, Vector3 origin, Vector3 direction, Transform aimTarget)
	{
		if (WorldSync.SuppressRocketBroadcast) return;
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		if (__instance?.rocketPrefab == null) return;
		Vector3 landing = origin + direction.normalized * 50f;
		try
		{
			var m = AccessTools.Method(typeof(EnemyRocketLauncher), "LandingPoint");
			if (m != null)
				landing = (Vector3)m.Invoke(__instance, new object[] { origin, direction, aimTarget })!;
		}
		catch { /* fallback */ }
		var prefab = __instance.rocketPrefab;
		rt.Session.Broadcast(NetProtocol.RocketFx(origin, landing, prefab.speed, prefab.arcHeight), false);
	}
}

/// <summary>Remote rockets: explode VFX only — AreaBlast already comes via BlastFx from the authority.</summary>
[HarmonyPatch(typeof(Rocket), "Explode")]
internal static class RemoteRocketExplodePatches
{
	[HarmonyPrefix]
	private static bool Prefix(Rocket __instance, Vector3 at)
	{
		if (__instance == null || __instance.GetComponent<RemoteRocketTag>() == null) return true;
		try
		{
			if (__instance.explosionEffect != null)
				UnityEngine.Object.Instantiate(__instance.explosionEffect, at, Quaternion.identity);
			SoundController.PlayRandomSoundAtPosition(
				__instance.explosionSounds, at, __instance.explosionVolume,
				__instance.explosionSoundMinDistance, __instance.explosionSoundMaxDistance,
				__instance.explosionSoundPitchVariance);
		}
		catch { /* fx */ }
		UnityEngine.Object.Destroy(__instance.gameObject);
		return false;
	}
}

/// <summary>Multiplayer: never freeze or slow the shared clock.</summary>
[HarmonyPatch(typeof(TimeScale), nameof(TimeScale.Set))]
internal static class TimeScaleSetMpPatches
{
	[HarmonyPrefix]
	private static bool Prefix(float factor)
	{
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return true;
		return factor >= 0.999f;
	}
}

[HarmonyPatch(typeof(TimeScale), nameof(TimeScale.Hold))]
internal static class TimeScaleHoldMpPatches
{
	[HarmonyPrefix]
	private static bool Prefix(bool held)
	{
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return true;
		return !held; // allow unhold; block pause freeze
	}
}

/// <summary>
/// Everyone may EndRace locally (fixes infinite races when guest finished first).
/// Host still broadcasts RaceEnd so peers who are still racing get pulled out.
/// </summary>
[HarmonyPatch(typeof(TrackManager), "EndRace")]
internal static class EndRaceSyncPatches
{
	[HarmonyPostfix]
	private static void Postfix()
	{
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		if (WorldSync.SuppressLocalRaceEndBroadcast) return;
		if (!rt.Session.IsHost) return;
		var last = RaceTracker.Last;
		WorldSync.BroadcastRaceEnd(rt.Session,
			last.survived ? last.place : 99,
			last.survived, last.objectiveMet, !last.survived, RaceTracker.Quit);
	}
}

/// <summary>Quit → tell peers the race aborted / peer left.</summary>
[HarmonyPatch(typeof(PauseController), nameof(PauseController.QuitRace))]
internal static class QuitRaceAbortPatches
{
	[HarmonyPrefix]
	private static void Prefix()
	{
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		if (rt.Session.IsHost)
			WorldSync.BroadcastRaceEnd(rt.Session, 99, false, false, false, true);
		else
			rt.Session.Broadcast(NetProtocol.RaceAbort(), true);
	}
}

[HarmonyPatch(typeof(PauseController), nameof(PauseController.QuitRaceFree))]
internal static class QuitRaceFreeAbortPatches
{
	[HarmonyPrefix]
	private static void Prefix()
	{
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		rt.Session.Broadcast(NetProtocol.RaceAbort(), true);
	}
}

/// <summary>Health / effect pickup tombstones (heal/refill stay local).</summary>
[HarmonyPatch(typeof(HealthDrop), "Collect")]
internal static class HealthPickupSyncPatches
{
	[HarmonyPostfix]
	private static void Postfix(HealthDrop __instance, CarControllerNew car, bool __result)
	{
		if (!__result || __instance == null || car == null || !car.isPlayer) return;
		if (WorldSync.SuppressPickupBroadcast) return;
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		rt.Session.Broadcast(NetProtocol.PickupFx(1, __instance.transform.position), true);
	}
}

[HarmonyPatch(typeof(EffectDrop), "Collect")]
internal static class EffectPickupSyncPatches
{
	[HarmonyPostfix]
	private static void Postfix(EffectDrop __instance, CarControllerNew car, bool __result)
	{
		if (!__result || __instance == null || car == null || !car.isPlayer) return;
		if (WorldSync.SuppressPickupBroadcast) return;
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		rt.Session.Broadcast(NetProtocol.PickupFx(2, __instance.transform.position), true);
	}
}

/// <summary>Cash pickup tombstone (money amount stays local / per-player).</summary>
[HarmonyPatch(typeof(MoneyDrop), "Collect")]
internal static class MoneyPickupSyncPatches
{
	[HarmonyPostfix]
	private static void Postfix(MoneyDrop __instance, CarControllerNew car, bool __result)
	{
		if (!__result || __instance == null || car == null || !car.isPlayer) return;
		if (WorldSync.SuppressPickupBroadcast) return;
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		rt.Session.Broadcast(NetProtocol.MoneyFx(__instance.transform.position), true);
	}
}

/// <summary>Horn beep heard by peers.</summary>
[HarmonyPatch(typeof(NClaxon), "OnGrab")]
internal static class HornGrabPatches
{
	[HarmonyPostfix]
	private static void Postfix(NClaxon __instance)
	{
		_ = __instance;
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		rt.Session.Broadcast(NetProtocol.HornFx(true), false);
	}
}

[HarmonyPatch(typeof(NClaxon), "OnRelease")]
internal static class HornReleasePatches
{
	[HarmonyPostfix]
	private static void Postfix(NClaxon __instance)
	{
		_ = __instance;
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		rt.Session.Broadcast(NetProtocol.HornFx(false), false);
	}
}

/// <summary>World props smashed by a peer disappear for everyone (money reward stays local).</summary>
[HarmonyPatch(typeof(DestructibleObject), nameof(DestructibleObject.DestroyObject))]
internal static class DestructibleSyncPatches
{
	[HarmonyPostfix]
	private static void Postfix(DestructibleObject __instance)
	{
		if (__instance == null) return;
		if (WorldSync.SuppressDestructibleBroadcast) return;
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		rt.Session.Broadcast(NetProtocol.DestructibleFx(__instance.transform.position), true);
	}
}

/// <summary>Jump button — peers hear/see the hop.</summary>
[HarmonyPatch(typeof(NJumpButton), "Jump")]
internal static class JumpFxPatches
{
	[HarmonyPostfix]
	private static void Postfix(NJumpButton __instance)
	{
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		var car = UnityEngine.Object.FindFirstObjectByType<TrackManager>()?.playerCarController;
		var pos = car != null ? car.transform.position : __instance.transform.position;
		rt.Session.Broadcast(NetProtocol.JumpFx(pos), false);
	}
}

/// <summary>Guest auto-ready when race setup arrives so host StartRace is not stuck.</summary>
[HarmonyPatch(typeof(RunState), nameof(RunState.BeginRace))]
internal static class BeginRaceAutoReadyPatches
{
	[HarmonyPrefix]
	private static void Prefix()
	{
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		// Host will broadcast setup; everyone should be Ready for the gate.
		rt.Session.SetReady(true, quiet: true);
	}
}

/// <summary>Gear changes appear on remote nameplates.</summary>
[HarmonyPatch]
internal static class GearFxPatches
{
	private static System.Reflection.MethodBase TargetMethod() =>
		AccessTools.Method(typeof(Signals), nameof(Signals.Emit)).MakeGenericMethod(typeof(GearChanged));

	[HarmonyPostfix]
	private static void Postfix(GearChanged signal)
	{
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		rt.Session.Broadcast(NetProtocol.GearFx((sbyte)Mathf.Clamp(signal.gear, -128, 127), signal.inGear), false);
	}
}

/// <summary>Phoenix/totem save — peers see a heal flash.</summary>
[HarmonyPatch]
internal static class TotemFxPatches
{
	private static System.Reflection.MethodBase TargetMethod() =>
		AccessTools.Method(typeof(Signals), nameof(Signals.Emit)).MakeGenericMethod(typeof(TotemUsed));

	[HarmonyPostfix]
	private static void Postfix(TotemUsed signal)
	{
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		rt.Session.Broadcast(NetProtocol.TotemFx(signal.healed, signal.left), true);
	}
}

/// <summary>Slow-mo visual only (timescale stays blocked in MP).</summary>
[HarmonyPatch]
internal static class SlowMoFxPatches
{
	private static System.Reflection.MethodBase TargetMethod() =>
		AccessTools.Method(typeof(Signals), nameof(Signals.Emit)).MakeGenericMethod(typeof(SlowMoStarted));

	[HarmonyPostfix]
	private static void Postfix(SlowMoStarted signal)
	{
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		rt.Session.Broadcast(NetProtocol.SlowMoFx(signal.duration), false);
	}
}

/// <summary>Drift spark/smoke cue on remotes (earnings stay local).</summary>
[HarmonyPatch]
internal static class DriftFxPatches
{
	private static float _next;

	private static System.Reflection.MethodBase TargetMethod() =>
		AccessTools.Method(typeof(Signals), nameof(Signals.Emit)).MakeGenericMethod(typeof(Drifted));

	[HarmonyPostfix]
	private static void Postfix(Drifted signal)
	{
		if (Time.unscaledTime < _next) return;
		_next = Time.unscaledTime + 0.35f;
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		var car = UnityEngine.Object.FindFirstObjectByType<TrackManager>()?.playerCarController;
		var pos = car != null ? car.transform.position : Vector3.zero;
		rt.Session.Broadcast(NetProtocol.DriftFx(pos, signal.meters), false);
	}
}

[HarmonyPatch]
internal static class SpunOutFxPatches
{
	private static System.Reflection.MethodBase TargetMethod() =>
		AccessTools.Method(typeof(Signals), nameof(Signals.Emit)).MakeGenericMethod(typeof(SpunOut));

	[HarmonyPostfix]
	private static void Postfix(SpunOut signal)
	{
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		rt.Session.Broadcast(NetProtocol.SpunOutFx(signal.speed), false);
	}
}

/// <summary>Explicit exhaust plume toggles (also covered by Pose flags).</summary>
[HarmonyPatch(typeof(ExaustPipeEffect), nameof(ExaustPipeEffect.StartEmission))]
internal static class ExhaustStartPatches
{
	[HarmonyPostfix]
	private static void Postfix(ExaustPipeEffect __instance)
	{
		_ = __instance;
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		if (__instance.GetComponentInParent<RemotePlayerHitbox>() != null) return;
		rt.Session.Broadcast(NetProtocol.ExhaustFx(true), false);
	}
}

[HarmonyPatch(typeof(ExaustPipeEffect), nameof(ExaustPipeEffect.StopEmission))]
internal static class ExhaustStopPatches
{
	[HarmonyPostfix]
	private static void Postfix(ExaustPipeEffect __instance)
	{
		_ = __instance;
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		if (__instance.GetComponentInParent<RemotePlayerHitbox>() != null) return;
		rt.Session.Broadcast(NetProtocol.ExhaustFx(false), false);
	}
}

[HarmonyPatch]
internal static class CarDamagedFxPatches
{
	private static float _next;

	private static System.Reflection.MethodBase TargetMethod() =>
		AccessTools.Method(typeof(Signals), nameof(Signals.Emit)).MakeGenericMethod(typeof(CarDamaged));

	[HarmonyPostfix]
	private static void Postfix(CarDamaged signal)
	{
		if (signal.fraction < 0.02f) return;
		if (Time.unscaledTime < _next) return;
		_next = Time.unscaledTime + 0.12f;
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		rt.Session.Broadcast(NetProtocol.CarDamagedFx(signal.fraction, (byte)signal.kind), false);
	}
}

/// <summary>Spike pierce → FX + PvP damage when the target is a remote ghost bot.</summary>
[HarmonyPatch(typeof(Spike))]
internal static class SpikePierceSyncPatches
{
	[HarmonyPatch("Pierce")]
	[HarmonyPostfix]
	private static void Postfix(Spike __instance, EnemyController enemy, RaycastHit hit, Vector3 dir)
	{
		if (WorldSync.SuppressSpikeBroadcast) return;
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		if (__instance == null) return;
		if (__instance.GetComponentInParent<RemotePlayerHitbox>() != null) return;
		var point = hit.point;
		rt.Session.Broadcast(NetProtocol.SpikeFx(point, dir), false);
		var box = enemy != null ? enemy.GetComponent<RemotePlayerHitbox>() : null;
		if (box != null)
		{
			float dmg = __instance.pierceDamage;
			if (dmg < 1f) dmg = 12f;
			rt.DealDamageToPeer(box.PeerId, dmg, point);
		}
	}
}

[HarmonyPatch(typeof(GlassDamage), nameof(GlassDamage.Shatter))]
internal static class GlassShatterSyncPatches
{
	[HarmonyPostfix]
	private static void Postfix(GlassDamage __instance)
	{
		if (WorldSync.SuppressGlassBroadcast) return;
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		if (__instance == null) return;
		if (__instance.GetComponentInParent<RemotePlayerHitbox>() != null) return;
		rt.Session.Broadcast(NetProtocol.GlassFx(__instance.transform.position), true);
	}
}

[HarmonyPatch(typeof(FellableTerrain))]
internal static class TreeFellSyncPatches
{
	[HarmonyPatch("Fell")]
	[HarmonyPostfix]
	private static void Postfix(FellableTerrain __instance, int index, Vector3 push, Vector3 away)
	{
		_ = away;
		if (WorldSync.SuppressTreeBroadcast) return;
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked || !rt.Session.IsHost) return;
		if (__instance == null) return;
		int terrainIndex = 0;
		var all = UnityEngine.Object.FindObjectsByType<FellableTerrain>(FindObjectsSortMode.None);
		for (int i = 0; i < all.Length; i++)
		{
			if (all[i] == __instance) { terrainIndex = i; break; }
		}
		rt.Session.Broadcast(NetProtocol.TreeFx(terrainIndex, index, push), true);
	}
}

[HarmonyPatch(typeof(JumpPad))]
internal static class JumpPadLaunchSyncPatches
{
	[HarmonyPatch("Launch")]
	[HarmonyPostfix]
	private static void Postfix(JumpPad __instance, Rigidbody rb)
	{
		_ = __instance;
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		if (rb == null) return;
		var car = rb.GetComponentInParent<CarControllerNew>();
		if (car == null || !car.isPlayer) return;
		rt.Session.Broadcast(NetProtocol.JumpFx(rb.position), false);
	}
}

[HarmonyPatch(typeof(RoadPickup), "Collect", typeof(CarControllerNew))]
internal static class RoadPickupSyncPatches
{
	[HarmonyPostfix]
	private static void Postfix(RoadPickup __instance, CarControllerNew car, bool __result)
	{
		if (!__result) return;
		if (WorldSync.SuppressPickupBroadcast) return;
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		if (__instance == null || car == null || !car.isPlayer) return;
		rt.Session.Broadcast(NetProtocol.PickupFx(3, __instance.transform.position), true);
	}
}

[HarmonyPatch(typeof(MudPuddle), "OnTriggerEnter")]
internal static class MudPuddleSyncPatches
{
	[HarmonyPostfix]
	private static void Postfix(MudPuddle __instance, Collider other)
	{
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		if (__instance == null || other == null) return;
		var car = other.GetComponentInParent<CarControllerNew>();
		if (car == null || !car.isPlayer) return;
		rt.Session.Broadcast(NetProtocol.MudFx(__instance.transform.position), false);
	}
}

[HarmonyPatch(typeof(Spikes), "OnTriggerEnter")]
internal static class RoadSpikesSyncPatches
{
	[HarmonyPostfix]
	private static void Postfix(Spikes __instance, Collider other)
	{
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		if (__instance == null || other == null) return;
		var car = other.GetComponentInParent<CarControllerNew>();
		if (car == null || !car.isPlayer) return;
		rt.Session.Broadcast(NetProtocol.TrapFx(__instance.transform.position, __instance.damage), false);
	}
}

[HarmonyPatch(typeof(HeadHammer), "OnCollisionEnter")]
internal static class HeadHammerSyncPatches
{
	[HarmonyPostfix]
	private static void Postfix(HeadHammer __instance, Collision collision)
	{
		HammerHitSync.PeerHit(HammerHitSync.ReadDamage(__instance), collision);
	}
}

[HarmonyPatch(typeof(SpinnerHammer), "OnCollisionEnter")]
internal static class SpinnerHammerSyncPatches
{
	[HarmonyPostfix]
	private static void Postfix(SpinnerHammer __instance, Collision collision)
	{
		HammerHitSync.PeerHit(HammerHitSync.ReadDamage(__instance), collision);
	}
}

internal static class HammerHitSync
{
	public static float ReadDamage(object? hammer)
	{
		if (hammer == null) return 10f;
		try
		{
			var f = AccessTools.Field(hammer.GetType(), "damage");
			if (f != null) return (float)f.GetValue(hammer)!;
		}
		catch { /* */ }
		return 10f;
	}

	public static void PeerHit(float damage, Collision collision)
	{
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		if (collision == null) return;
		var point = collision.contactCount > 0 ? collision.GetContact(0).point : collision.transform.position;
		var box = collision.collider.GetComponentInParent<RemotePlayerHitbox>();
		if (box != null)
		{
			rt.DealDamageToPeer(box.PeerId, Mathf.Max(1f, damage), point);
			rt.Session.Broadcast(NetProtocol.HammerFx(point, damage), false);
			return;
		}
		var car = collision.collider.GetComponentInParent<CarControllerNew>();
		if (car != null && car.isPlayer)
			rt.Session.Broadcast(NetProtocol.HammerFx(point, damage), false);
	}
}

[HarmonyPatch]
internal static class EnemyKillFeedPatches
{
	private static System.Reflection.MethodBase TargetMethod() =>
		AccessTools.Method(typeof(Signals), nameof(Signals.Emit)).MakeGenericMethod(typeof(EnemyKilled));

	[HarmonyPostfix]
	private static void Postfix(EnemyKilled signal)
	{
		if (!signal.byPlayer) return;
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		string name = "enemy";
		try
		{
			if (signal.enemy?.skinController != null &&
			    !string.IsNullOrEmpty(signal.enemy.skinController.carName))
				name = signal.enemy.skinController.carName;
			else if (signal.enemy != null)
				name = signal.enemy.name;
		}
		catch { /* */ }
		string text = $"* AI down: {name}";
		rt.Session.Broadcast(NetProtocol.KillFeed(text), true);
		rt.NotifyChatToast(text);
	}
}

[HarmonyPatch]
internal static class WreckImminentToastPatches
{
	private static System.Reflection.MethodBase TargetMethod() =>
		AccessTools.Method(typeof(Signals), nameof(Signals.Emit)).MakeGenericMethod(typeof(WreckImminent));

	[HarmonyPostfix]
	private static void Postfix(WreckImminent signal)
	{
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		string text = signal.saved ? "* Totem save!" : "* Near wreck!";
		rt.Session.Broadcast(NetProtocol.KillFeed(text), true);
		rt.NotifyChatToast(text);
	}
}

[HarmonyPatch(typeof(NLeechNitroLever))]
internal static class LeechNitroSyncPatches
{
	[HarmonyPatch("Drain")]
	[HarmonyPostfix]
	private static void Postfix(NLeechNitroLever __instance, bool __result)
	{
		if (!__result) return;
		_ = __instance;
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		rt.Session.Broadcast(NetProtocol.LeechFx(true), false);
	}
}

/// <summary>Train turret shots: tracer for peers + PvP when hitting a ghost hitbox.</summary>
[HarmonyPatch(typeof(TrainTurret))]
internal static class TrainTurretSyncPatches
{
	[HarmonyPatch("Fire")]
	[HarmonyPostfix]
	private static void FirePostfix(TrainTurret __instance)
	{
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		if (__instance == null) return;
		Transform? muzzle = null;
		try
		{
			muzzle = AccessTools.Field(typeof(TrainTurret), "muzzle")?.GetValue(__instance) as Transform;
		}
		catch { /* */ }
		if (muzzle == null) muzzle = __instance.transform;
		var origin = muzzle.position;
		var target = origin + muzzle.forward * 80f;
		rt.Session.Broadcast(NetProtocol.GunFx(origin, target, 0), false);
	}

	[HarmonyPatch("HandleHit")]
	[HarmonyPostfix]
	private static void HitPostfix(TrainTurret __instance, RaycastHit hit)
	{
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		if (__instance == null) return;
		var box = hit.collider != null ? hit.collider.GetComponentInParent<RemotePlayerHitbox>() : null;
		if (box == null) return;
		float dmg = 10f;
		try
		{
			var f = AccessTools.Field(typeof(TrainTurret), "damage");
			if (f != null) dmg = (float)f.GetValue(__instance)!;
		}
		catch { /* */ }
		rt.DealDamageToPeer(box.PeerId, Mathf.Max(1f, dmg), hit.point);
	}
}

[HarmonyPatch(typeof(IceSpike), "OnCollisionEnter")]
internal static class IceSpikeSyncPatches
{
	private static readonly HashSet<int> Fired = new();

	internal static void ClearFired() => Fired.Clear();

	[HarmonyPostfix]
	private static void Postfix(IceSpike __instance, Collision collision)
	{
		_ = collision;
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		if (__instance == null) return;
		int id = __instance.GetInstanceID();
		if (!Fired.Add(id)) return;
		var pos = __instance.transform.position;
		WorldSync.BroadcastBlast(rt.Session, pos, 3.5f, 25f, 8f, (byte)BlastSource.World);
	}
}

[HarmonyPatch(typeof(NInverter))]
internal static class InvertSyncPatches
{
	[HarmonyPatch("Flip")]
	[HarmonyPostfix]
	private static void Postfix(NInverter __instance)
	{
		_ = __instance;
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		rt.Session.Broadcast(NetProtocol.InvertFx(), true);
	}
}

[HarmonyPatch(typeof(ComboManager), nameof(ComboManager.UpdateComboInstant))]
internal static class ComboInstantSyncPatches
{
	private static float _next;

	[HarmonyPostfix]
	private static void Postfix(string name, ComboType type, float value)
	{
		_ = type;
		if (value < 3f) return;
		if (Time.unscaledTime < _next) return;
		_next = Time.unscaledTime + 0.6f;
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		rt.Session.Broadcast(NetProtocol.ComboFx(name, value), false);
	}
}

[HarmonyPatch(typeof(CheckpointController), "OnTriggerEnter")]
internal static class CheckpointSyncPatches
{
	private static float _next;

	[HarmonyPostfix]
	private static void Postfix(CheckpointController __instance, Collider other)
	{
		if (Time.unscaledTime < _next) return;
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		if (__instance == null || other == null) return;
		var car = other.GetComponentInParent<CarControllerNew>();
		if (car == null || !car.isPlayer) return;
		_next = Time.unscaledTime + 1.2f;
		byte index = 0;
		var all = UnityEngine.Object.FindObjectsByType<CheckpointController>(FindObjectsSortMode.None);
		for (int i = 0; i < all.Length; i++)
		{
			if (all[i] == __instance) { index = (byte)Mathf.Clamp(i, 0, 255); break; }
		}
		rt.Session.Broadcast(NetProtocol.CheckpointFx(index, __instance.isStartingCheckpoint), false);
	}
}

[HarmonyPatch(typeof(NGuillotineBox))]
internal static class GuillotineSyncPatches
{
	[HarmonyPatch("Taken")]
	[HarmonyPostfix]
	private static void Postfix(NGuillotineBox __instance)
	{
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		if (__instance == null) return;
		rt.Session.Broadcast(NetProtocol.GuillotineFx(__instance.transform.position), true);
	}
}

[HarmonyPatch(typeof(NDrainNitroLever), "Bite")]
internal static class DrainNitroSyncPatches
{
	[HarmonyPostfix]
	private static void Postfix(NDrainNitroLever __instance, float share)
	{
		_ = share;
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		if (__instance == null) return;
		rt.Session.Broadcast(NetProtocol.DrainFx(__instance.transform.position), false);
	}
}

[HarmonyPatch(typeof(WheatTrampler))]
internal static class WheatTrampleSyncPatches
{
	private static float _next;

	[HarmonyPatch("Emit")]
	[HarmonyPostfix]
	private static void Postfix(WheatTrampler __instance, TrampleField field)
	{
		_ = field;
		if (Time.unscaledTime < _next) return;
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		if (__instance == null) return;
		var car = __instance.GetComponentInParent<CarControllerNew>();
		if (car == null || !car.isPlayer) return;
		_next = Time.unscaledTime + 0.28f;
		var origin = __instance.transform.position;
		try
		{
			var prop = AccessTools.Property(typeof(WheatTrampler), "Origin");
			if (prop?.GetValue(__instance) is Transform ot && ot != null)
				origin = ot.position;
		}
		catch { /* */ }
		float strength = 0.6f;
		float radius = 1.8f;
		try
		{
			var sf = AccessTools.Field(typeof(WheatTrampler), "crushStrength");
			var rf = AccessTools.Field(typeof(WheatTrampler), "crushRadius");
			if (sf != null) strength = (float)sf.GetValue(__instance)!;
			if (rf != null) radius = (float)rf.GetValue(__instance)!;
		}
		catch { /* */ }
		rt.Session.Broadcast(NetProtocol.TrampleFx(origin, strength, radius), false);
	}
}

[HarmonyPatch(typeof(PlayerCarEffectController), nameof(PlayerCarEffectController.StartFlamesEffect))]
internal static class PlayerFlamesSyncPatches
{
	[HarmonyPostfix]
	private static void Postfix(PlayerCarEffectController __instance)
	{
		_ = __instance;
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		rt.Session.Broadcast(NetProtocol.BurnFx(true), false);
	}
}

[HarmonyPatch(typeof(PlayerCarEffectController), nameof(PlayerCarEffectController.AddSmallExplosionEffect))]
internal static class PlayerSmallExplodeSyncPatches
{
	[HarmonyPostfix]
	private static void Postfix(PlayerCarEffectController __instance)
	{
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		if (__instance == null) return;
		var pos = __instance.transform.position;
		rt.Session.Broadcast(NetProtocol.ExplodeFx(0, pos), false);
	}
}

/// <summary>Host AI nitro flames → guests light the matching bot.</summary>
[HarmonyPatch(typeof(EnemyNitro))]
internal static class EnemyNitroFlameSyncPatches
{
	[HarmonyPatch("SetFlames")]
	[HarmonyPostfix]
	private static void Postfix(EnemyNitro __instance, bool lit)
	{
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked || !rt.Session.IsHost) return;
		if (__instance == null) return;
		if (__instance.GetComponentInParent<RemotePlayerHitbox>() != null) return;
		var enemy = __instance.GetComponentInParent<EnemyController>();
		if (enemy == null) return;
		var tm = UnityEngine.Object.FindFirstObjectByType<TrackManager>();
		if (tm?.Enemies == null) return;
		int idx = -1;
		for (int i = 0; i < tm.Enemies.Count; i++)
		{
			if (tm.Enemies[i] == enemy) { idx = i; break; }
		}
		if (idx < 0 || idx > 255) return;
		rt.Session.Broadcast(NetProtocol.BotFlameFx((byte)idx, lit), false);
	}
}

[HarmonyPatch(typeof(NAmmoEaterLever))]
internal static class AmmoEaterSyncPatches
{
	[HarmonyPatch("Feed")]
	[HarmonyPostfix]
	private static void Postfix(NAmmoEaterLever __instance, bool __result)
	{
		if (!__result) return;
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		if (__instance == null) return;
		rt.Session.Broadcast(NetProtocol.AmmoEatFx(__instance.transform.position), false);
	}
}

[HarmonyPatch(typeof(RaceObjectiveAnnouncer))]
internal static class ObjectiveCompleteSyncPatches
{
	[HarmonyPatch("Complete")]
	[HarmonyPostfix]
	private static void Postfix(RaceObjectiveAnnouncer __instance)
	{
		_ = __instance;
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		const string line = "* Objective complete!";
		rt.Session.Broadcast(NetProtocol.ObjectiveFx(line), true);
		rt.NotifyChatToast(line);
	}
}

[HarmonyPatch(typeof(LiveTree), nameof(LiveTree.Kick))]
internal static class LiveTreeKickSyncPatches
{
	private static float _next;

	[HarmonyPostfix]
	private static void Postfix(LiveTree __instance, Vector3 lean, float degreesPerSecond)
	{
		if (Time.unscaledTime < _next) return;
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked || !rt.Session.IsHost) return;
		if (__instance == null) return;
		_next = Time.unscaledTime + 0.15f;
		rt.Session.Broadcast(NetProtocol.TreeKickFx(__instance.transform.position, lean, degreesPerSecond), false);
	}
}

[HarmonyPatch(typeof(PlayerDamageEffects))]
internal static class PlayerBurnSyncPatches
{
	[HarmonyPatch("StartBurning")]
	[HarmonyPostfix]
	private static void StartPost(PlayerDamageEffects __instance)
	{
		_ = __instance;
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		rt.Session.Broadcast(NetProtocol.BurnFx(true), false);
	}

	[HarmonyPatch("StopBurning")]
	[HarmonyPostfix]
	private static void StopPost(PlayerDamageEffects __instance)
	{
		_ = __instance;
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		rt.Session.Broadcast(NetProtocol.BurnFx(false), false);
	}
}

[HarmonyPatch(typeof(EffectRefiller), nameof(EffectRefiller.Refill))]
internal static class EffectRefillSyncPatches
{
	[HarmonyPostfix]
	private static void Postfix(EffectRefiller __instance)
	{
		_ = __instance;
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		rt.Session.Broadcast(NetProtocol.RefillFx(0, 1f), false);
		rt.NotifyChatToast("* effects refilled");
	}
}

[HarmonyPatch(typeof(EnemyTargetingIndicator), "Update")]
internal static class ThreatIndicatorSyncPatches
{
	private static bool _last;
	private static float _next;

	[HarmonyPostfix]
	private static void Postfix(EnemyTargetingIndicator __instance)
	{
		if (Time.unscaledTime < _next) return;
		_next = Time.unscaledTime + 0.35f;
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		if (__instance == null) return;
		bool any = false;
		try
		{
			var m = AccessTools.Method(typeof(EnemyTargetingIndicator), "AnyEnemyTargetingPlayer");
			if (m != null) any = (bool)m.Invoke(__instance, null)!;
		}
		catch { return; }
		if (any == _last) return;
		_last = any;
		rt.Session.Broadcast(NetProtocol.ThreatFx(any), false);
		if (any) rt.NotifyChatToast("* AI locking on YOU");
	}
}

[HarmonyPatch(typeof(NCanisterLever))]
internal static class CanisterSyncPatches
{
	private static float _next;

	[HarmonyPatch("TakeRefill")]
	[HarmonyPostfix]
	private static void Postfix(NCanisterLever __instance, float fraction, float __result)
	{
		_ = fraction;
		if (Time.unscaledTime < _next) return;
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		if (__instance == null) return;
		_next = Time.unscaledTime + 0.4f;
		rt.Session.Broadcast(NetProtocol.CanisterFx(__result), false);
	}
}

[HarmonyPatch(typeof(TimeRewinder), nameof(TimeRewinder.Begin))]
internal static class TimeRewindSyncPatches
{
	[HarmonyPostfix]
	private static void Postfix(TimeRewinder __instance, bool __result)
	{
		if (!__result) return;
		_ = __instance;
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		rt.Session.Broadcast(NetProtocol.InvertFx(), true);
	}
}

[HarmonyPatch(typeof(CameraSwitch), nameof(CameraSwitch.Toggle))]
internal static class CameraToggleSyncPatches
{
	[HarmonyPostfix]
	private static void Postfix(CameraSwitch __instance)
	{
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		if (__instance == null) return;
		if (__instance.GetComponentInParent<RemotePlayerHitbox>() != null) return;
		byte outside = 0;
		try { outside = (byte)(__instance.Outside ? 1 : 0); } catch { /* */ }
		rt.Session.Broadcast(NetProtocol.CamFx(outside), false);
	}
}

[HarmonyPatch(typeof(NHandBrake), nameof(NHandBrake.ToggleLock))]
internal static class HandBrakeLockSyncPatches
{
	[HarmonyPostfix]
	private static void Postfix(NHandBrake __instance)
	{
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		if (__instance == null) return;
		// ToggleLock flips cabin lock; treat engaged handbrake as locked cue.
		bool locked = __instance.value > 0.45f;
		rt.Session.Broadcast(NetProtocol.HbLockFx(locked), false);
	}
}

[HarmonyPatch(typeof(LapManager), nameof(LapManager.ReportShortcut))]
internal static class ShortcutSyncPatches
{
	[HarmonyPostfix]
	private static void Postfix(LapManager __instance, float metres)
	{
		_ = __instance;
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		rt.Session.Broadcast(NetProtocol.ShortcutFx(metres), true);
	}
}

[HarmonyPatch]
internal static class MoneyEarnedSyncPatches
{
	private static float _next;

	private static System.Reflection.MethodBase TargetMethod() =>
		AccessTools.Method(typeof(Signals), nameof(Signals.Emit)).MakeGenericMethod(typeof(MoneyEarned));

	[HarmonyPostfix]
	private static void Postfix(MoneyEarned signal)
	{
		if (signal.amount <= 0.01f) return;
		if (Time.unscaledTime < _next) return;
		_next = Time.unscaledTime + 0.5f;
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		// Toast only — wallets stay per-player.
		rt.Session.Broadcast(NetProtocol.EarnFx(signal.label ?? "", signal.amount), false);
	}
}

[HarmonyPatch(typeof(BlinkerIndicator), nameof(BlinkerIndicator.AddBlinks))]
internal static class BlinkerSyncPatches
{
	[HarmonyPostfix]
	private static void Postfix(BlinkerIndicator __instance, int blink)
	{
		_ = __instance;
		if (blink <= 0) return;
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		rt.Session.Broadcast(NetProtocol.BlinkFx(blink), false);
	}
}

[HarmonyPatch]
internal static class EffectRechargedSyncPatches
{
	private static System.Reflection.MethodBase TargetMethod() =>
		AccessTools.Method(typeof(Signals), nameof(Signals.Emit)).MakeGenericMethod(typeof(EffectRecharged));

	[HarmonyPostfix]
	private static void Postfix(EffectRecharged signal)
	{
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		rt.Session.Broadcast(NetProtocol.RefillFx((byte)(int)signal.kind, signal.amount), false);
	}
}

[HarmonyPatch]
internal static class AmmoPickedUpSyncPatches
{
	private static System.Reflection.MethodBase TargetMethod() =>
		AccessTools.Method(typeof(Signals), nameof(Signals.Emit)).MakeGenericMethod(typeof(AmmoPickedUp));

	[HarmonyPostfix]
	private static void Postfix(AmmoPickedUp signal)
	{
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		rt.Session.Broadcast(NetProtocol.CabinAmmoFx(1, signal.magazines), false);
	}
}

[HarmonyPatch]
internal static class AmmoReleasedSyncPatches
{
	private static System.Reflection.MethodBase TargetMethod() =>
		AccessTools.Method(typeof(Signals), nameof(Signals.Emit)).MakeGenericMethod(typeof(AmmoReleased));

	[HarmonyPostfix]
	private static void Postfix(AmmoReleased signal)
	{
		_ = signal;
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		rt.Session.Broadcast(NetProtocol.CabinAmmoFx(0, 0), false);
	}
}

[HarmonyPatch(typeof(Gun), "EnterAimMode")]
internal static class GunEnterAimSyncPatches
{
	private static float _next;

	[HarmonyPostfix]
	private static void Postfix()
	{
		if (Time.unscaledTime < _next) return;
		_next = Time.unscaledTime + 0.15f;
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		rt.Session.Broadcast(NetProtocol.AimFx(true), false);
	}
}

[HarmonyPatch(typeof(Gun), "ExitAimMode")]
internal static class GunExitAimSyncPatches
{
	private static float _next;

	[HarmonyPostfix]
	private static void Postfix()
	{
		if (Time.unscaledTime < _next) return;
		_next = Time.unscaledTime + 0.15f;
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		rt.Session.Broadcast(NetProtocol.AimFx(false), false);
	}
}

[HarmonyPatch(typeof(NPumpActionGun), "EnterAimMode")]
internal static class PumpEnterAimSyncPatches
{
	private static float _next;

	[HarmonyPostfix]
	private static void Postfix()
	{
		if (Time.unscaledTime < _next) return;
		_next = Time.unscaledTime + 0.15f;
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		rt.Session.Broadcast(NetProtocol.AimFx(true), false);
	}
}

[HarmonyPatch(typeof(NPumpActionGun), "ExitAimMode")]
internal static class PumpExitAimSyncPatches
{
	private static float _next;

	[HarmonyPostfix]
	private static void Postfix()
	{
		if (Time.unscaledTime < _next) return;
		_next = Time.unscaledTime + 0.15f;
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		rt.Session.Broadcast(NetProtocol.AimFx(false), false);
	}
}

[HarmonyPatch(typeof(NAutomaticGun), nameof(NAutomaticGun.OnMagazineSeated))]
internal static class MagSeatedSyncPatches
{
	[HarmonyPostfix]
	private static void Postfix()
	{
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		rt.Session.Broadcast(NetProtocol.MagFx(true), false);
	}
}

[HarmonyPatch(typeof(NAutomaticGun), nameof(NAutomaticGun.OnMagazineRemoved))]
internal static class MagRemovedSyncPatches
{
	[HarmonyPostfix]
	private static void Postfix()
	{
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		rt.Session.Broadcast(NetProtocol.MagFx(false), false);
	}
}

[HarmonyPatch(typeof(PauseController), nameof(PauseController.PauseRace))]
internal static class PauseRaceSyncPatches
{
	[HarmonyPostfix]
	private static void Postfix()
	{
		if (WorldSync.SuppressPauseBroadcast) return;
		var rt = MpRuntime.Instance;
		// Only host owns shared pause — guest pause stays local.
		if (rt == null || !rt.Session.Linked || !rt.Session.IsHost) return;
		rt.Session.Broadcast(NetProtocol.PauseFx(true), true);
	}
}

[HarmonyPatch(typeof(PauseController), nameof(PauseController.Resume))]
internal static class ResumeRaceSyncPatches
{
	[HarmonyPostfix]
	private static void Postfix()
	{
		if (WorldSync.SuppressPauseBroadcast) return;
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked || !rt.Session.IsHost) return;
		rt.Session.Broadcast(NetProtocol.PauseFx(false), true);
	}
}

[HarmonyPatch(typeof(RaceCutoff), "Say")]
internal static class RaceCutoffSaySyncPatches
{
	[HarmonyPostfix]
	private static void Postfix(string line)
	{
		if (WorldSync.SuppressCutoffSayBroadcast) return;
		if (string.IsNullOrWhiteSpace(line)) return;
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked || !rt.Session.IsHost) return;
		rt.Session.Broadcast(NetProtocol.CutoffSay(line), true);
	}
}

[HarmonyPatch(typeof(NPumpActionGun), nameof(NPumpActionGun.OnShellLoaded))]
internal static class PumpShellSyncPatches
{
	[HarmonyPostfix]
	private static void Postfix()
	{
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		rt.Session.Broadcast(NetProtocol.GunActFx(0), false);
	}
}

[HarmonyPatch(typeof(NBreakActionGun), "OnBreakOpened")]
internal static class BreakOpenSyncPatches
{
	[HarmonyPostfix]
	private static void Postfix()
	{
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		rt.Session.Broadcast(NetProtocol.GunActFx(1), false);
	}
}

[HarmonyPatch(typeof(NBreakActionGun), "OnBreakClosed")]
internal static class BreakCloseSyncPatches
{
	[HarmonyPostfix]
	private static void Postfix()
	{
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		rt.Session.Broadcast(NetProtocol.GunActFx(2), false);
	}
}

[HarmonyPatch(typeof(NBreakActionGun), "OnEjected")]
internal static class BreakEjectSyncPatches
{
	[HarmonyPostfix]
	private static void Postfix()
	{
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		rt.Session.Broadcast(NetProtocol.GunActFx(3), false);
	}
}

[HarmonyPatch(typeof(NRevolver), "Spin")]
internal static class RevolverSpinSyncPatches
{
	private static float _next;

	[HarmonyPostfix]
	private static void Postfix(float revolutionsPerSecond)
	{
		if (Time.unscaledTime < _next) return;
		_next = Time.unscaledTime + 0.2f;
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		rt.Session.Broadcast(NetProtocol.GunActFx(4, revolutionsPerSecond), false);
	}
}

[HarmonyPatch(typeof(PlayerCarEffectController), nameof(PlayerCarEffectController.StartSmokeEmission))]
internal static class PlayerSmokeOnSyncPatches
{
	private static float _next;
	private static bool _last;

	[HarmonyPostfix]
	private static void Postfix()
	{
		if (_last && Time.unscaledTime < _next) return;
		_last = true;
		_next = Time.unscaledTime + 0.4f;
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		rt.Session.Broadcast(NetProtocol.SmokeFx(true), false);
	}
}

[HarmonyPatch(typeof(PlayerCarEffectController), nameof(PlayerCarEffectController.StopSmokeEmission))]
internal static class PlayerSmokeOffSyncPatches
{
	private static float _next;
	private static bool _last = true;

	[HarmonyPostfix]
	private static void Postfix()
	{
		if (!_last && Time.unscaledTime < _next) return;
		_last = false;
		_next = Time.unscaledTime + 0.4f;
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		rt.Session.Broadcast(NetProtocol.SmokeFx(false), false);
	}
}

[HarmonyPatch(typeof(GlassDamage), nameof(GlassDamage.ReportHit))]
internal static class GlassCrackSyncPatches
{
	private static float _next;

	[HarmonyPostfix]
	private static void Postfix(GlassDamage __instance, float strength)
	{
		if (WorldSync.SuppressGlassBroadcast) return;
		if (__instance == null || __instance.Shattered) return;
		if (strength < 0.02f) return;
		if (Time.unscaledTime < _next) return;
		_next = Time.unscaledTime + 0.25f;
		if (__instance.GetComponentInParent<RemotePlayerHitbox>() != null) return;
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		rt.Session.Broadcast(NetProtocol.GlassCrackFx(strength), false);
	}
}

[HarmonyPatch(typeof(WeatherController), nameof(WeatherController.ApplyProfile))]
internal static class WeatherApplySyncPatches
{
	private static float _next;
	private static string _last = "";

	[HarmonyPostfix]
	private static void Postfix(WeatherController __instance)
	{
		if (WorldSync.SuppressWeatherBroadcast) return;
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked || !rt.Session.IsHost) return;
		var name = __instance?.activeProfile != null ? __instance.activeProfile.name : "";
		if (string.IsNullOrEmpty(name)) return;
		if (name == _last && Time.unscaledTime < _next) return;
		_last = name;
		_next = Time.unscaledTime + 1f;
		rt.Session.Broadcast(NetProtocol.WeatherFx(name), true);
	}
}

[HarmonyPatch(typeof(HandBrakeAssembly), nameof(HandBrakeAssembly.Grab))]
internal static class HandBrakeGrabSyncPatches
{
	private static float _next;

	[HarmonyPostfix]
	private static void Postfix()
	{
		if (Time.unscaledTime < _next) return;
		_next = Time.unscaledTime + 0.3f;
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		rt.Session.Broadcast(NetProtocol.HbGrabFx(), false);
	}
}

[HarmonyPatch(typeof(CarLink), nameof(CarLink.Heal))]
internal static class CarHealSyncPatches
{
	private static float _next;

	[HarmonyPostfix]
	private static void Postfix(float amount, float __result)
	{
		_ = __result;
		if (amount < 0.5f) return;
		if (Time.unscaledTime < _next) return;
		_next = Time.unscaledTime + 0.35f;
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		rt.Session.Broadcast(NetProtocol.HealFx(amount), false);
	}
}

[HarmonyPatch(typeof(CarLink), "MoneyShift")]
internal static class MoneyShiftSyncPatches
{
	private static readonly System.Reflection.FieldInfo? StallFlash =
		AccessTools.Field(typeof(CarLink), "_stallFlash");
	private static readonly System.Reflection.FieldInfo? MoneyFlash =
		AccessTools.Field(typeof(CarLink), "_moneyShiftFlash");

	[HarmonyPrefix]
	private static void Prefix(CarLink __instance, out byte __state)
	{
		__state = 0;
		try
		{
			float stall = StallFlash != null ? (float)StallFlash.GetValue(__instance)! : 0f;
			float money = MoneyFlash != null ? (float)MoneyFlash.GetValue(__instance)! : 0f;
			if (stall > 0.05f) __state = 1;
			else if (money > 0.05f) __state = 2;
		}
		catch { __state = 0; }
	}

	[HarmonyPostfix]
	private static void Postfix(CarLink __instance, byte __state)
	{
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		try
		{
			float stall = StallFlash != null ? (float)StallFlash.GetValue(__instance)! : 0f;
			float money = MoneyFlash != null ? (float)MoneyFlash.GetValue(__instance)! : 0f;
			if (stall > 0.2f && __state != 1)
				rt.Session.Broadcast(NetProtocol.ShiftHitFx(1), false);
			else if (money > 0.2f && __state != 2)
				rt.Session.Broadcast(NetProtocol.ShiftHitFx(0), false);
		}
		catch { /* */ }
	}
}

[HarmonyPatch(typeof(TrainController), nameof(TrainController.StartMoving))]
internal static class TrainStartSyncPatches
{
	[HarmonyPostfix]
	private static void Postfix(TrainController __instance)
	{
		if (WorldSync.SuppressTrainMoveBroadcast) return;
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked || !rt.Session.IsHost) return;
		byte idx = IndexOfTrain(__instance);
		rt.Session.Broadcast(NetProtocol.TrainMoveFx(idx, true), true);
	}

	internal static byte IndexOfTrain(TrainController train)
	{
		var all = UnityEngine.Object.FindObjectsByType<TrainController>(FindObjectsSortMode.None);
		for (byte i = 0; i < all.Length && i < 255; i++)
			if (all[i] == train) return i;
		return 0;
	}
}

[HarmonyPatch(typeof(TrainController), nameof(TrainController.StopMoving))]
internal static class TrainStopSyncPatches
{
	[HarmonyPostfix]
	private static void Postfix(TrainController __instance)
	{
		if (WorldSync.SuppressTrainMoveBroadcast) return;
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked || !rt.Session.IsHost) return;
		byte idx = TrainStartSyncPatches.IndexOfTrain(__instance);
		rt.Session.Broadcast(NetProtocol.TrainMoveFx(idx, false), true);
	}
}

[HarmonyPatch(typeof(MagazineManager), "GrantForKill")]
internal static class KillAmmoGrantSyncPatches
{
	[HarmonyPrefix]
	private static void Prefix(MagazineManager __instance, out int __state)
	{
		__state = 0;
		try { __state = __instance.Count; } catch { __state = 0; }
	}

	[HarmonyPostfix]
	private static void Postfix(MagazineManager __instance, int __state)
	{
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		int after = __state;
		try { after = __instance.Count; } catch { /* */ }
		int gained = Mathf.Max(0, after - __state);
		rt.Session.Broadcast(NetProtocol.KillAmmoFx(gained > 0 ? gained : 1), false);
		WorldSync.PulseLocalAmmoState(rt.Session, __instance);
	}
}

[HarmonyPatch(typeof(Feats), nameof(Feats.Award))]
internal static class FeatAwardSyncPatches
{
	[HarmonyPrefix]
	private static void Prefix(Feat feat, out bool __state)
	{
		__state = !Feats.Has(feat);
	}

	[HarmonyPostfix]
	private static void Postfix(Feat feat, bool __state)
	{
		if (!__state || !Feats.Has(feat)) return;
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		string label = "";
		try { label = Feats.Label(feat); } catch { label = feat.ToString(); }
		rt.Session.Broadcast(NetProtocol.FeatFx((int)feat, label), true);
	}
}

[HarmonyPatch(typeof(GunLoadoutActivator), nameof(GunLoadoutActivator.Apply))]
internal static class GunTypeSyncPatches
{
	private static float _next;
	private static byte _last = 255;

	[HarmonyPostfix]
	private static void Postfix(GunType equipped)
	{
		byte g = (byte)(int)equipped;
		if (g == _last && Time.unscaledTime < _next) return;
		_last = g;
		_next = Time.unscaledTime + 0.5f;
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		rt.Session.Broadcast(NetProtocol.GunTypeFx(g), true);
	}
}

[HarmonyPatch(typeof(ShifterLoadoutActivator), nameof(ShifterLoadoutActivator.Apply))]
internal static class ShiftTypeSyncPatches
{
	private static float _next;
	private static byte _last = 255;

	[HarmonyPostfix]
	private static void Postfix(ShifterType fittedType)
	{
		byte s = (byte)(int)fittedType;
		if (s == _last && Time.unscaledTime < _next) return;
		_last = s;
		_next = Time.unscaledTime + 0.5f;
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		rt.Session.Broadcast(NetProtocol.ShiftTypeFx(s), true);
	}
}

[HarmonyPatch(typeof(NShell), "OnGrab")]
internal static class ShellGrabSyncPatches
{
	private static float _next;

	[HarmonyPostfix]
	private static void Postfix()
	{
		if (Time.unscaledTime < _next) return;
		_next = Time.unscaledTime + 0.2f;
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		rt.Session.Broadcast(NetProtocol.ShellFx(1), false);
	}
}

[HarmonyPatch(typeof(NShell), "OnRelease")]
internal static class ShellReleaseSyncPatches
{
	private static float _next;

	[HarmonyPostfix]
	private static void Postfix()
	{
		if (Time.unscaledTime < _next) return;
		_next = Time.unscaledTime + 0.2f;
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		rt.Session.Broadcast(NetProtocol.ShellFx(0), false);
	}
}

[HarmonyPatch(typeof(NAutomaticGun), "OnSlidePulled")]
internal static class SlidePullSyncPatches
{
	[HarmonyPostfix]
	private static void Postfix()
	{
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		rt.Session.Broadcast(NetProtocol.SlideFx(0), false);
	}
}

[HarmonyPatch(typeof(NAutomaticGun), "OnSlideReturned")]
internal static class SlideReturnSyncPatches
{
	[HarmonyPostfix]
	private static void Postfix()
	{
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		rt.Session.Broadcast(NetProtocol.SlideFx(1), false);
	}
}

[HarmonyPatch(typeof(NAutomaticGun), "LockSlideOpen")]
internal static class SlideLockSyncPatches
{
	[HarmonyPostfix]
	private static void Postfix()
	{
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		rt.Session.Broadcast(NetProtocol.SlideFx(2), false);
	}
}

[HarmonyPatch(typeof(NAutomaticGun), "ReleaseSlideLock")]
internal static class SlideUnlockSyncPatches
{
	[HarmonyPostfix]
	private static void Postfix()
	{
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		rt.Session.Broadcast(NetProtocol.SlideFx(3), false);
	}
}

[HarmonyPatch(typeof(NBulletClip), "OnRoundsTaken")]
internal static class ClipRoundsSyncPatches
{
	private static float _next;

	[HarmonyPostfix]
	private static void Postfix(int consumed)
	{
		if (consumed <= 0) return;
		if (Time.unscaledTime < _next) return;
		_next = Time.unscaledTime + 0.15f;
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		rt.Session.Broadcast(NetProtocol.ClipFx(consumed), false);
	}
}

[HarmonyPatch(typeof(ItemUnlocks), nameof(ItemUnlocks.Unlock), typeof(UpgradeComponent), typeof(bool))]
internal static class UnlockSyncPatches
{
	[HarmonyPostfix]
	private static void Postfix(UpgradeComponent upgrade, bool withItem, UpgradeComponent __result)
	{
		_ = withItem;
		if (__result == null || upgrade == null) return;
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		string id = !string.IsNullOrEmpty(upgrade.id) ? upgrade.id : (upgrade.name ?? "");
		string label = !string.IsNullOrEmpty(upgrade.DisplayName) ? upgrade.DisplayName : (upgrade.name ?? id);
		rt.Session.Broadcast(NetProtocol.UnlockFx(id, label), true);
	}
}

[HarmonyPatch(typeof(CameraSwitch), nameof(CameraSwitch.Release))]
internal static class CamLookSyncPatches
{
	[HarmonyPostfix]
	private static void Postfix(bool released)
	{
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		rt.Session.Broadcast(NetProtocol.CamLookFx((byte)(released ? 1 : 0)), false);
	}
}

[HarmonyPatch(typeof(RaceTracker), nameof(RaceTracker.ReportWrecked))]
internal static class RaceWreckSyncPatches
{
	[HarmonyPostfix]
	private static void Postfix()
	{
		if (!WreckSyncGate.TrySend()) return;
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		rt.Session.Broadcast(NetProtocol.Wreck(), true);
		var car = UnityEngine.Object.FindFirstObjectByType<TrackManager>()?.playerCarController;
		if (car != null)
			rt.Session.Broadcast(NetProtocol.ExplodeFx(1, car.transform.position), false);
		rt.EnterSpectate();
	}
}

[HarmonyPatch(typeof(CarDamageVisuals), nameof(CarDamageVisuals.ClearDamage))]
internal static class RepairClearSyncPatches
{
	[HarmonyPostfix]
	private static void Postfix(CarDamageVisuals __instance)
	{
		if (WorldSync.SuppressRepairBroadcast) return;
		if (__instance == null) return;
		if (__instance.GetComponentInParent<RemotePlayerHitbox>() != null) return;
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		rt.Session.Broadcast(NetProtocol.RepairFx(), false);
	}
}

[HarmonyPatch(typeof(CarDamageVisuals), nameof(CarDamageVisuals.RepairRun))]
internal static class RepairRunSyncPatches
{
	[HarmonyPostfix]
	private static void Postfix(CarDamageVisuals __instance)
	{
		if (WorldSync.SuppressRepairBroadcast) return;
		if (__instance == null) return;
		if (__instance.GetComponentInParent<RemotePlayerHitbox>() != null) return;
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		rt.Session.Broadcast(NetProtocol.RepairFx(), false);
	}
}

[HarmonyPatch(typeof(CarDamageVisuals), nameof(CarDamageVisuals.AddGrime))]
internal static class GrimeSyncPatches
{
	private static float _next;

	[HarmonyPostfix]
	private static void Postfix(CarDamageVisuals __instance, float amount)
	{
		if (WorldSync.SuppressGrimeBroadcast) return;
		if (amount < 0.01f) return;
		if (Time.unscaledTime < _next) return;
		_next = Time.unscaledTime + 0.6f;
		if (__instance == null) return;
		if (__instance.GetComponentInParent<RemotePlayerHitbox>() != null) return;
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		rt.Session.Broadcast(NetProtocol.GrimeFx(amount), false);
	}
}

[HarmonyPatch(typeof(NMagazine), nameof(NMagazine.TakeBullet))]
internal static class MagShotSyncPatches
{
	private static float _next;

	[HarmonyPostfix]
	private static void Postfix()
	{
		if (Time.unscaledTime < _next) return;
		_next = Time.unscaledTime + 0.08f;
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		rt.Session.Broadcast(NetProtocol.MagShotFx(), false);
	}
}

[HarmonyPatch(typeof(CarSkinController), nameof(CarSkinController.SetWindshield))]
internal static class WindshieldSyncPatches
{
	private static float _next;

	[HarmonyPostfix]
	private static void Postfix(CarSkinController __instance, bool active)
	{
		if (WorldSync.SuppressWindshieldBroadcast) return;
		if (Time.unscaledTime < _next) return;
		_next = Time.unscaledTime + 0.3f;
		if (__instance == null) return;
		if (__instance.GetComponentInParent<RemotePlayerHitbox>() != null) return;
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		rt.Session.Broadcast(NetProtocol.WindshieldFx(active), false);
	}
}

[HarmonyPatch(typeof(RaceObjectiveAnnouncer), "Say")]
internal static class ObjectiveSaySyncPatches
{
	private static float _next;
	private static string _last = "";

	[HarmonyPostfix]
	private static void Postfix(string line, float seconds)
	{
		if (WorldSync.SuppressObjSayBroadcast) return;
		if (string.IsNullOrWhiteSpace(line)) return;
		if (line == _last && Time.unscaledTime < _next) return;
		_last = line;
		_next = Time.unscaledTime + 0.8f;
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked || !rt.Session.IsHost) return;
		rt.Session.Broadcast(NetProtocol.ObjSayFx(line, seconds), true);
	}
}

[HarmonyPatch(typeof(NSteeringWheel), nameof(NSteeringWheel.ToggleLock))]
internal static class SteerLockSyncPatches
{
	[HarmonyPostfix]
	private static void Postfix(NSteeringWheel __instance)
	{
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		if (__instance?.wheel == null) return;
		rt.Session.Broadcast(NetProtocol.SteerLockFx(__instance.wheel.frozen), false);
	}
}

[HarmonyPatch(typeof(NSteeringWheel), nameof(NSteeringWheel.StartRecenter))]
internal static class SteerRecenterSyncPatches
{
	private static float _next;

	[HarmonyPostfix]
	private static void Postfix()
	{
		if (Time.unscaledTime < _next) return;
		_next = Time.unscaledTime + 0.4f;
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		rt.Session.Broadcast(NetProtocol.RecenterFx(), false);
	}
}

[HarmonyPatch(typeof(CarLink), nameof(CarLink.MatchRevs))]
internal static class MatchRevSyncPatches
{
	private static float _next;
	private static int _lastGear = int.MinValue;

	[HarmonyPostfix]
	private static void Postfix(CarLink __instance)
	{
		if (__instance?.sa == null) return;
		int gear = __instance.sa.inGear ? __instance.sa.gear : 0;
		if (gear == _lastGear) return;
		_lastGear = gear;
		if (Time.unscaledTime < _next) return;
		_next = Time.unscaledTime + 0.9f;
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		rt.Session.Broadcast(NetProtocol.MatchRevFx(), false);
	}
}

[HarmonyPatch(typeof(ComboManager), nameof(ComboManager.UpdateComboContinuous))]
internal static class ComboContinuousSyncPatches
{
	private static float _next;

	[HarmonyPostfix]
	private static void Postfix(string name, ComboType type, float value)
	{
		_ = type;
		if (value < 5f) return;
		if (Time.unscaledTime < _next) return;
		_next = Time.unscaledTime + 1.2f;
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		rt.Session.Broadcast(NetProtocol.ComboFx(name, value), false);
	}
}

[HarmonyPatch(typeof(ComboManager), nameof(ComboManager.IncrementComboInstant))]
internal static class ComboIncrementSyncPatches
{
	private static float _next;

	[HarmonyPostfix]
	private static void Postfix(string name, ComboType type, float value)
	{
		_ = type;
		if (value < 2f) return;
		if (Time.unscaledTime < _next) return;
		_next = Time.unscaledTime + 0.7f;
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		rt.Session.Broadcast(NetProtocol.ComboFx(name, value), false);
	}
}

[HarmonyPatch(typeof(LifetimeStats), nameof(LifetimeStats.RecordTime))]
internal static class RecordTimeSyncPatches
{
	[HarmonyPrefix]
	private static void Prefix(MapDefinition map, bool reversed, float seconds, out bool __state)
	{
		__state = false;
		if (map == null || seconds < 0.5f) return;
		float best = LifetimeStats.BestTime(map, reversed);
		__state = best <= 0f || seconds < best;
	}

	[HarmonyPostfix]
	private static void Postfix(MapDefinition map, bool reversed, float seconds, bool __state)
	{
		if (!__state) return;
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		string label = map != null
			? (!string.IsNullOrEmpty(map.displayName) ? map.displayName : map.name)
			: "track";
		if (reversed) label += " (rev)";
		rt.Session.Broadcast(NetProtocol.RecordFx(label ?? "track", seconds), true);
	}
}

[HarmonyPatch(typeof(LapShield), "CallFarAhead")]
internal static class LapAheadSyncPatches
{
	[HarmonyPostfix]
	private static void Postfix(EnemyController enemy)
	{
		_ = enemy;
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked || !rt.Session.IsHost) return;
		int laps = 1;
		try
		{
			var inst = UnityEngine.Object.FindFirstObjectByType<LapShield>();
			if (inst != null && enemy != null)
			{
				var m = AccessTools.Method(typeof(LapShield), "LapsAhead");
				if (m != null)
					laps = Mathf.Max(1, Mathf.RoundToInt((float)m.Invoke(inst, new object[] { enemy })!));
			}
		}
		catch { laps = 1; }
		rt.Session.Broadcast(NetProtocol.LapAheadFx(laps), true);
	}
}

[HarmonyPatch(typeof(NO2), nameof(NO2.ApplyUpgrade))]
internal static class NosUpgradeSyncPatches
{
	[HarmonyPostfix]
	private static void Postfix()
	{
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		rt.Session.Broadcast(NetProtocol.NosUpFx(), false);
	}
}

[HarmonyPatch(typeof(CarLink), nameof(CarLink.RefreshParts))]
internal static class PartsRefreshSyncPatches
{
	private static float _next;

	[HarmonyPostfix]
	private static void Postfix(CarLink __instance)
	{
		if (__instance?.cc == null || !__instance.cc.isPlayer) return;
		if (Time.unscaledTime < _next) return;
		_next = Time.unscaledTime + 1f;
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		rt.Session.Broadcast(NetProtocol.PartsFx(), false);
	}
}

[HarmonyPatch(typeof(Shaker), nameof(Shaker.KickSpring))]
internal static class ShakeSyncPatches
{
	private static float _next;

	[HarmonyPostfix]
	private static void Postfix(float severity)
	{
		if (severity < 0.25f) return;
		if (Time.unscaledTime < _next) return;
		_next = Time.unscaledTime + 0.4f;
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		rt.Session.Broadcast(NetProtocol.ShakeFx(severity), false);
	}
}

[HarmonyPatch(typeof(RaceStats), nameof(RaceStats.ReportChickensDestroyed))]
internal static class ChickenCountSyncPatches
{
	private static float _next;

	[HarmonyPostfix]
	private static void Postfix(int count)
	{
		if (count <= 0) return;
		if (Time.unscaledTime < _next) return;
		_next = Time.unscaledTime + 0.35f;
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		rt.Session.Broadcast(NetProtocol.ChickenFx(count), false);
	}
}

[HarmonyPatch(typeof(chicken), "DestroyObject")]
internal static class ChickenGoneSyncPatches
{
	[HarmonyPrefix]
	private static void Prefix(chicken __instance)
	{
		if (WorldSync.SuppressChickenBroadcast) return;
		if (__instance == null) return;
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		rt.Session.Broadcast(NetProtocol.ChickenGoneFx(__instance.transform.position), true);
	}
}

[HarmonyPatch]
internal static class ItemActivatedSyncPatches
{
	private static float _next;

	private static System.Reflection.MethodBase TargetMethod() =>
		AccessTools.Method(typeof(Signals), nameof(Signals.Emit)).MakeGenericMethod(typeof(ItemActivated));

	[HarmonyPostfix]
	private static void Postfix(ItemActivated signal)
	{
		_ = signal;
		if (Time.unscaledTime < _next) return;
		_next = Time.unscaledTime + 0.5f;
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		rt.Session.Broadcast(NetProtocol.ItemUseFx(), false);
	}
}

[HarmonyPatch]
internal static class CarModelChangedSyncPatches
{
	private static float _next;

	private static System.Reflection.MethodBase TargetMethod() =>
		AccessTools.Method(typeof(Signals), nameof(Signals.Emit)).MakeGenericMethod(typeof(CarModelChanged));

	[HarmonyPostfix]
	private static void Postfix(CarModelChanged signal)
	{
		_ = signal;
		if (Time.unscaledTime < _next) return;
		_next = Time.unscaledTime + 1f;
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		rt.Session.Broadcast(NetProtocol.ModelFx(), true);
		rt.BroadcastLocalCarLook(true);
	}
}

[HarmonyPatch(typeof(DiceCharm), "Roll")]
internal static class DiceRollSyncPatches
{
	private static readonly System.Reflection.FieldInfo? RolledField =
		AccessTools.Field(typeof(DiceCharm), "rolled");

	[HarmonyPostfix]
	private static void Postfix(DiceCharm __instance)
	{
		if (__instance == null || RolledField == null) return;
		float rolled;
		try { rolled = (float)RolledField.GetValue(__instance)!; }
		catch { return; }
		if (rolled < 0f) return;
		byte kind;
		if (rolled < __instance.hurtChance) kind = 0;
		else if (rolled < __instance.hurtChance + __instance.healChance) kind = 1;
		else kind = 2;
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		rt.Session.Broadcast(NetProtocol.DiceFx(kind), false);
	}
}

[HarmonyPatch(typeof(BeerBottle), "OnGrab")]
internal static class BeerDrinkSyncPatches
{
	private static readonly System.Reflection.FieldInfo? DrinkingField =
		AccessTools.Field(typeof(BeerBottle), "drinking");

	[HarmonyPrefix]
	private static void Prefix(BeerBottle __instance, out bool __state)
	{
		__state = false;
		if (__instance == null) return;
		try
		{
			bool drinking = DrinkingField != null && (bool)DrinkingField.GetValue(__instance)!;
			__state = !drinking && __instance.canBeGrabbed;
		}
		catch { __state = false; }
	}

	[HarmonyPostfix]
	private static void Postfix(BeerBottle __instance, bool __state)
	{
		_ = __instance;
		if (!__state) return;
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		rt.Session.Broadcast(NetProtocol.BeerFx(), false);
	}
}

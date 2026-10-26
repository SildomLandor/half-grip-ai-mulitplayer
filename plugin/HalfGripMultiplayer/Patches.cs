using System;
using System.Collections;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace HalfGripMultiplayer;

/// <summary>When a gun ray hits a remote ghost hitbox, deal networked PvP damage.</summary>
[HarmonyPatch(typeof(Gun))]
internal static class GunPvPPatches
{
	[HarmonyPostfix]
	[HarmonyPatch("HandleEnemyCollision")]
	private static void HandleEnemyCollision_Post(Gun __instance, RaycastHit hit, Ray shotRay)
	{
		_ = shotRay;
		TryHitRemote(__instance, hit);
	}

	private static void TryHitRemote(Gun gun, RaycastHit hit)
	{
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		var box = hit.collider.GetComponentInParent<RemotePlayerHitbox>();
		if (box == null) return;

		float dmg = 50f;
		try
		{
			var field = AccessTools.Field(typeof(Gun), "damage");
			if (field != null) dmg = (float)field.GetValue(gun);
		}
		catch { /* keep default */ }
		dmg *= box.DamageMultiplier;
		rt.DealDamageToPeer(box.PeerId, dmg, hit.point);
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
		if (rt.Session.Peers.Count > 0 && !rt.Session.AllPeersReady())
		{
			rt.Session.AddChat("* Wait for all players Ready");
			Plugin.Log.LogInfo("Host StartRace blocked — not all ready");
			return false;
		}
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
					if (session.Peers.Count > 0 && session.AllPeersReady())
					{
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

/// <summary>Broadcast wreck when local player HP hits 0.</summary>
[HarmonyPatch(typeof(CarLink), nameof(CarLink.TakeCrashDamage))]
internal static class DamageBroadcastPatches
{
	[HarmonyPostfix]
	private static void Postfix(CarLink __instance, float damage)
	{
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		if (__instance == null || __instance.cc == null || !__instance.cc.isPlayer) return;
		if (__instance.hp > 0f) return;
		rt.Session.Broadcast(NetProtocol.Wreck(), true);
		rt.Session.AddChat("* You wrecked");
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

/// <summary>Host finishes → everyone ends together.</summary>
[HarmonyPatch(typeof(GameManager), nameof(GameManager.RaceFinished))]
internal static class RaceFinishedSyncPatches
{
	[HarmonyPrefix]
	private static void Prefix(int position)
	{
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		if (!rt.Session.IsHost || WorldSync.SuppressLocalRaceEndBroadcast) return;
		var last = RaceTracker.Last;
		WorldSync.BroadcastRaceEnd(rt.Session, position, last.survived, last.objectiveMet, !last.survived, RaceTracker.Quit);
	}
}

/// <summary>Gun muzzle/tracer FX for remotes.</summary>
[HarmonyPatch(typeof(Gun), "FireEffects")]
internal static class GunFireFxPatches
{
	[HarmonyPostfix]
	private static void Postfix(Gun __instance, Vector3 targetPoint)
	{
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return;
		if (__instance == null) return;
		var cc = __instance.carLink != null ? __instance.carLink.cc : null;
		if (cc == null || !cc.isPlayer) return;
		var origin = __instance.firePoint != null ? __instance.firePoint.position : cc.transform.position;
		rt.Session.Broadcast(NetProtocol.GunFx(origin, targetPoint), false);
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

/// <summary>Guests wait for host RaceEnd — local EndRace would desync payouts/UI.</summary>
[HarmonyPatch(typeof(TrackManager), "EndRace")]
internal static class GuestEndRaceGatePatches
{
	[HarmonyPrefix]
	private static bool Prefix()
	{
		var rt = MpRuntime.Instance;
		if (rt == null || !rt.Session.Linked) return true;
		if (rt.Session.IsHost) return true;
		if (WorldSync.SuppressLocalRaceEndBroadcast) return true; // applying host RaceEnd path
		Plugin.Log.LogInfo("Guest EndRace blocked — waiting for host RaceEnd");
		rt.Session.AddChat("* Finished — waiting for host");
		return false;
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

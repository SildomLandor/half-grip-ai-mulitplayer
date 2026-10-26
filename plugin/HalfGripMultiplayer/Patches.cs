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

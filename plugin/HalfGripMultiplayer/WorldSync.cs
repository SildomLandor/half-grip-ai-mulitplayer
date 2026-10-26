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
	private static bool _applyingRemoteRaceEnd;
	private static bool _applyingRemoteMapSelect;
	private static float _lastShopPulse;
	private static string _lastLoadoutKey = "";

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
			GameManager.raceOver = true;
			BossRace.Reset();
			MpRuntime.Instance?.Session.AddChat($"* Host ended race (place={place})");
			Plugin.Log.LogInfo($"RaceEnd apply place={place} wrecked={wrecked}");
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

	public static void PulseShop(MpSession session)
	{
		if (session == null || !session.Linked || !session.IsHost) return;
		if (Time.unscaledTime < _lastShopPulse) return;
		_lastShopPulse = Time.unscaledTime + 2f;
		string offer = "";
		try { offer = SaveData.GetString("shop.offer") ?? ""; } catch { /* */ }
		session.Broadcast(NetProtocol.ShopState(ShopManager.totalMoney, offer), true);
	}

	public static void ApplyShopState(float money, string offerKey)
	{
		try
		{
			ShopManager.totalMoney = money;
			if (!string.IsNullOrEmpty(offerKey))
				SaveData.SetString("shop.offer", offerKey);
		}
		catch (Exception ex)
		{
			Plugin.Log.LogWarning($"ShopState apply: {ex.Message}");
		}
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
		try
		{
			string offer = SaveData.GetString("shop.offer") ?? "";
			session.SendTo(peerId, NetProtocol.ShopState(ShopManager.totalMoney, offer), true);
		}
		catch { /* */ }
		ForceBroadcastLoadout(session);
		MpRuntime.Instance?.BroadcastLocalCarLook(force: true);
		if (session.RaceStartReceived)
			session.SendTo(peerId, NetProtocol.RaceStart(Time.unscaledTime), true);
		if (GameManager.raceOver)
			session.SendTo(peerId, NetProtocol.RaceEnd(99, false, false, false, false), true);
	}
}

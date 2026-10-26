using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace HalfGripMultiplayer;

/// <summary>
/// Shared race context: weather / short race / laps / track variant / grid slots / AI bot poses.
/// Host is authority; guests apply before or as the track loads.
/// </summary>
internal static class RaceSync
{
	private static readonly PropertyInfo? ActiveWeatherProp =
		AccessTools.Property(typeof(RunState), nameof(RunState.ActiveWeather));
	private static readonly PropertyInfo? ActiveMapProp =
		AccessTools.Property(typeof(RunState), nameof(RunState.ActiveMap));
	private static readonly PropertyInfo? ActiveMapMusicProp =
		AccessTools.Property(typeof(RunState), nameof(RunState.ActiveMapMusic));
	private static readonly FieldInfo? StartPositionsField =
		AccessTools.Field(typeof(TrackManager), "startPositions");
	private static readonly FieldInfo? PlayerStartField =
		AccessTools.Field(typeof(TrackManager), "playerStart");

	public static int SpawnSeed { get; private set; } = 1337;
	public static bool ShortRace { get; private set; }
	public static byte TrackVariantByte { get; private set; }
	public static int MaxLaps { get; private set; } = 3;
	public static string WeatherName { get; private set; } = "";
	public static string MapName { get; private set; } = "";
	public static string SceneName { get; private set; } = "";
	public static byte ObjectiveKindByte { get; private set; }
	public static int ObjectiveAmount { get; private set; }
	public static string ObjectiveTarget { get; private set; } = "";
	public static string MusicName { get; private set; } = "";
	public static float CeasefireSeconds { get; private set; } = 10f;
	public static int SelectedNodeIndex { get; private set; } = -1;
	/// <summary>Sandbox-like: ignore finish line / cutoff pressure; pick any map.</summary>
	public static bool FreePlay { get; set; }
	/// <summary>Do not keep AI cars — peers use ghost primitives or empty grid.</summary>
	public static bool NoBots { get; set; }
	public static bool HasSetup { get; private set; }

	/// <summary>True for the next TrackManager.SpawnEnemies — forces shared UnityEngine.Random seed.</summary>
	public static bool SeedNextSpawn { get; set; }
	public static bool SeedNextHazards { get; set; }

	private static readonly PropertyInfo? PendingObjectiveProp =
		AccessTools.Property(typeof(RunState), nameof(RunState.PendingObjective));
	private static readonly PropertyInfo? SelectedIndexProp =
		AccessTools.Property(typeof(RunState), nameof(RunState.SelectedIndex));

	public static void CaptureFromHost()
	{
		var cfg = RunConfig.Instance;
		ShortRace = RaceLength.Short;
		TrackVariantByte = (byte)TrackVariants.Current;
		int laps = GameManager.lapsPerRace > 0 ? GameManager.lapsPerRace : 3;
		if (cfg != null)
		{
			bool shortNow = ShortRace;
			var tmLive = UnityEngine.Object.FindFirstObjectByType<TrackManager>();
			if (tmLive != null && tmLive.alwaysShort) shortNow = true;
			laps = shortNow ? cfg.shortRaceLaps : cfg.lapsPerRace;
		}
		MaxLaps = laps;

		WeatherName = RunState.ActiveWeather != null ? RunState.ActiveWeather.name : "";
		MapName = RunState.ActiveMap != null ? RunState.ActiveMap.name : "";
		if (string.IsNullOrEmpty(MapName) && RunState.HasSelection && RunState.Selected?.map != null)
			MapName = RunState.Selected.map.name;
		SceneName = SceneManager.GetActiveScene().name;

		var obj = RunState.PendingObjective;
		ObjectiveKindByte = (byte)obj.kind;
		ObjectiveAmount = obj.amount;
		ObjectiveTarget = obj.target ?? "";
		MusicName = RunState.ActiveMapMusic != null ? RunState.ActiveMapMusic.name : "";
		CeasefireSeconds = AmmoLockSwitch.CeasefireSeconds;
		SelectedNodeIndex = RunState.HasSelection ? RunState.SelectedIndex : -1;
		// FreePlay / NoBots kept from host UI toggles (not overwritten here).

		SpawnSeed = unchecked(Environment.TickCount ^ (int)(DateTime.UtcNow.Ticks & 0x7fffffff));
		if (SpawnSeed == 0) SpawnSeed = 1337;
		HasSetup = true;
		SeedNextSpawn = true;
		SeedNextHazards = true;
		Plugin.Log.LogInfo(
			$"RaceSetup capture: weather={WeatherName} short={ShortRace} variant={TrackVariantByte} laps={MaxLaps} seed={SpawnSeed} free={FreePlay} noBots={NoBots}");
	}

	public static void ApplyPacket(
		int spawnSeed, bool shortRace, byte variant, int maxLaps,
		string weatherName, string mapName, string sceneName,
		byte objectiveKind, int objectiveAmount, string objectiveTarget,
		string musicName, float ceasefireSeconds, int selectedNodeIndex,
		bool freePlay = false, bool noBots = false)
	{
		SpawnSeed = spawnSeed == 0 ? 1337 : spawnSeed;
		ShortRace = shortRace;
		TrackVariantByte = variant;
		MaxLaps = maxLaps > 0 ? maxLaps : 3;
		WeatherName = weatherName ?? "";
		MapName = mapName ?? "";
		SceneName = sceneName ?? "";
		ObjectiveKindByte = objectiveKind;
		ObjectiveAmount = objectiveAmount;
		ObjectiveTarget = objectiveTarget ?? "";
		MusicName = musicName ?? "";
		CeasefireSeconds = ceasefireSeconds > 0f ? ceasefireSeconds : AmmoLockSwitch.CeasefireSeconds;
		SelectedNodeIndex = selectedNodeIndex;
		FreePlay = freePlay;
		NoBots = noBots;
		HasSetup = true;
		SeedNextSpawn = true;
		SeedNextHazards = true;
		ApplyRunContext();
		Plugin.Log.LogInfo(
			$"RaceSetup apply: weather={WeatherName} short={ShortRace} laps={MaxLaps} free={FreePlay} noBots={NoBots}");
	}

	public static void ApplyRunContext()
	{
		RaceLength.Set(ShortRace);
		TrackVariants.Set((TrackVariant)TrackVariantByte);
		AmmoLockSwitch.CeasefireSeconds = CeasefireSeconds;
		try
		{
			// Sandbox-like: never auto-end on finish line.
			TrackManager.ignoreFinish = FreePlay;
			if (FreePlay)
				TrackManager.endRaceInstead = null;
		}
		catch { /* TrackManager statics */ }

		var objective = new RaceObjective
		{
			kind = (ObjectiveKind)ObjectiveKindByte,
			amount = ObjectiveAmount,
			target = ObjectiveTarget,
		};
		PendingObjectiveProp?.SetValue(null, objective);

		if (SelectedNodeIndex >= 0 && RunState.Active)
		{
			try
			{
				if (RunState.Choices.Contains(SelectedNodeIndex))
					RunState.Select(SelectedNodeIndex);
				else
					SelectedIndexProp?.SetValue(null, SelectedNodeIndex);
			}
			catch { /* guest run graph may differ */ }
		}

		WeatherProfile? weather = FindWeather(WeatherName);
		if (weather != null)
			ActiveWeatherProp?.SetValue(null, weather);

		MapDefinition? map = FindMap(MapName);
		if (map != null)
		{
			ActiveMapProp?.SetValue(null, map);
			if (weather == null && map.weather != null)
			{
				ActiveWeatherProp?.SetValue(null, map.weather);
				WeatherName = map.weather.name;
			}
			try
			{
				AudioClip? music = null;
				if (!string.IsNullOrEmpty(MusicName))
				{
					music = Resources.Load<AudioClip>(MusicName);
					if (music == null)
					{
						foreach (var clip in Resources.FindObjectsOfTypeAll<AudioClip>())
						{
							if (clip != null && string.Equals(clip.name, MusicName, StringComparison.OrdinalIgnoreCase))
							{
								music = clip;
								break;
							}
						}
					}
				}
				if (music == null)
					music = map.PickMusic();
				ActiveMapMusicProp?.SetValue(null, music);
			}
			catch { /* offline */ }
		}

		var wc = UnityEngine.Object.FindFirstObjectByType<WeatherController>();
		if (wc != null && RunState.ActiveWeather != null)
		{
			wc.activeProfile = RunState.ActiveWeather;
			wc.ApplyProfile();
			WeatherGrip.Current = RunState.ActiveWeather;
		}
	}

	public static void InitHazardSeedIfNeeded()
	{
		if (!SeedNextHazards && !HasSetup) return;
		UnityEngine.Random.InitState(SpawnSeed ^ 0x42A7D5);
		SeedNextHazards = false;
		Plugin.Log.LogInfo($"Hazard seed applied: {SpawnSeed ^ 0x42A7D5}");
	}

	public static void ApplyLapsToTrack(TrackManager tm)
	{
		if (tm?.lapManager == null) return;
		if (MaxLaps > 0)
			tm.lapManager.maxLaps = MaxLaps;
	}

	public static void InitSpawnSeedIfNeeded()
	{
		if (!SeedNextSpawn && !HasSetup) return;
		UnityEngine.Random.InitState(SpawnSeed);
		SeedNextSpawn = false;
		Plugin.Log.LogInfo($"Spawn seed applied: {SpawnSeed}");
	}

	public static List<Transform> CollectGridSlots(TrackManager tm)
	{
		var grid = new List<Transform>();
		if (tm == null) return grid;
		var playerStart = PlayerStartField?.GetValue(tm) as Transform;
		if (playerStart != null) grid.Add(playerStart);
		if (StartPositionsField?.GetValue(tm) is List<Transform> starts)
		{
			foreach (var t in starts)
				if (t != null) grid.Add(t);
		}
		return grid;
	}

	public static void PlaceLocalPlayer(TrackManager tm, int slot)
	{
		var car = FindLocalPlayerCar(tm);
		if (car == null) return;
		var grid = CollectGridSlots(tm);
		if (grid.Count == 0) return;
		slot = Mathf.Clamp(slot, 0, grid.Count - 1);
		var marker = grid[slot];
		if (marker == null) return;

		var pos = marker.position;
		// Match TrackManager.PlacePlayerOnGrid height adjust when variants active.
		if (TrackVariants.Active && grid[0] != null && slot > 0)
		{
			float dy = car.transform.position.y - grid[0].position.y;
			pos = marker.position + Vector3.up * dy;
		}
		else if (TrackVariants.Active && slot == 0)
		{
			// playerStart already placed by vanilla for slot 0
		}

		// Match SpawnEnemies heading: layout uses +90° yaw then LayoutHeading (mirror/reverse).
		var rot = LayoutHeading(Quaternion.Euler(0f, 90f, 0f));
		TeleportCar(car, pos, rot);
		Plugin.Log.LogInfo($"Local player placed on grid slot {slot} @ {pos} rotY={rot.eulerAngles.y:0}");
	}

	private static Quaternion LayoutHeading(Quaternion drawn)
	{
		try
		{
			if (TrackVariants.Mirrored)
				drawn = TrackVariantApplier.MirrorRotation(drawn);
			if (TrackVariants.Reversed)
				drawn = Quaternion.Euler(0f, 180f, 0f) * drawn;
		}
		catch { /* variants */ }
		return drawn;
	}

	/// <summary>Remove AI cars so only human peers remain (sandbox).</summary>
	public static void StripBotsIfNeeded(TrackManager tm)
	{
		if (!NoBots || tm == null) return;
		var doomed = new List<EnemyController>();
		foreach (var e in tm.Enemies)
		{
			if (e == null) continue;
			if (e.GetComponent<RemotePlayerHitbox>() != null) continue;
			doomed.Add(e);
		}
		foreach (var e in doomed)
		{
			try
			{
				UnityEngine.Object.Destroy(e.gameObject);
			}
			catch { /* */ }
		}
		try
		{
			var field = AccessTools.Field(typeof(TrackManager), "enemies");
			if (field?.GetValue(tm) is System.Collections.IList list)
			{
				foreach (var e in doomed)
					list.Remove(e);
			}
		}
		catch { /* */ }
		Plugin.Log.LogInfo($"NoBots: stripped {doomed.Count} AI cars");
	}

	public static Transform? SlotMarker(TrackManager tm, int slot)
	{
		var grid = CollectGridSlots(tm);
		if (grid.Count == 0) return null;
		slot = Mathf.Clamp(slot, 0, grid.Count - 1);
		return grid[slot];
	}

	public static EnemyController? FindEnemyNear(TrackManager tm, Vector3 near, HashSet<int> claimed)
	{
		EnemyController? best = null;
		float bestDist = float.MaxValue;
		foreach (var e in tm.Enemies)
		{
			if (e == null || !e.alive) continue;
			if (claimed.Contains(e.GetInstanceID())) continue;
			if (e.GetComponent<RemotePlayerHitbox>() != null) continue;
			float d = (e.transform.position - near).sqrMagnitude;
			if (d < bestDist)
			{
				bestDist = d;
				best = e;
			}
		}
		return best;
	}

	public static void FreezeGuestBots(TrackManager tm)
	{
		if (tm == null) return;
		foreach (var e in tm.Enemies)
		{
			if (e == null) continue;
			if (e.GetComponent<RemotePlayerHitbox>() != null) continue;
			e.canMove = false;
			var rb = e.GetComponent<Rigidbody>();
			if (rb != null) rb.isKinematic = true;
			if (e.carController != null) e.carController.enabled = false;
		}
	}

	private static readonly FieldInfo? EnemyHealthField =
		AccessTools.Field(typeof(EnemyController), "health");

	public static void ApplyBotPose(TrackManager tm, byte index, Vector3 pos, Quaternion rot, float speed, float hp)
	{
		_ = speed;
		if (tm == null) return;
		var enemies = tm.Enemies;
		if (index >= enemies.Count) return;
		var e = enemies[index];
		if (e == null) return;
		if (e.GetComponent<RemotePlayerHitbox>() != null) return; // player-owned slot

		e.canMove = false;
		if (e.carController != null) e.carController.enabled = false;
		var rb = e.GetComponent<Rigidbody>();
		if (rb != null)
		{
			rb.isKinematic = true;
			rb.position = pos;
			rb.rotation = rot;
		}
		else
			e.transform.SetPositionAndRotation(pos, rot);

		// Host HP → guest: kill or mirror health without granting local kill credit.
		if (!e.alive) return;
		if (hp <= 0f)
		{
			e.TakeDamage(9999f, byPlayer: false);
			return;
		}
		EnemyHealthField?.SetValue(e, hp);
	}

	public static List<(byte index, Vector3 pos, Quaternion rot, float speed, float hp)> CollectHostBotPoses(TrackManager tm)
	{
		var list = new List<(byte, Vector3, Quaternion, float, float)>();
		if (tm == null) return list;
		var enemies = tm.Enemies;
		for (int i = 0; i < enemies.Count && i < 255; i++)
		{
			var e = enemies[i];
			if (e == null || !e.alive) continue;
			if (e.GetComponent<RemotePlayerHitbox>() != null) continue;
			float hp = 100f;
			var link = e.GetComponent<CarLink>();
			if (link != null) hp = link.hp;
			float speed = e.carController != null ? e.carController.forwardSpeed : 0f;
			list.Add(((byte)i, e.transform.position, e.transform.rotation, speed, hp));
		}
		return list;
	}

	private static void TeleportCar(CarControllerNew car, Vector3 pos, Quaternion rot)
	{
		var listener = car.GetComponentInChildren<AudioListener>(true);
		bool reinstate = listener != null && listener.enabled;
		if (reinstate) listener!.enabled = false;
		car.transform.SetPositionAndRotation(pos, rot);
		var rb = car.rb != null ? car.rb : car.GetComponent<Rigidbody>();
		if (rb != null)
		{
			rb.position = pos;
			rb.rotation = rot;
			rb.linearVelocity = Vector3.zero;
			rb.angularVelocity = Vector3.zero;
		}
		if (reinstate) listener!.enabled = true;
	}

	private static CarControllerNew? FindLocalPlayerCar(TrackManager tm)
	{
		if (tm?.playerCarController != null) return tm.playerCarController;
		foreach (var c in UnityEngine.Object.FindObjectsByType<CarControllerNew>(FindObjectsSortMode.None))
		{
			if (c != null && c.isPlayer && c.isActiveAndEnabled)
				return c;
		}
		return null;
	}

	private static WeatherProfile? FindWeather(string name)
	{
		if (string.IsNullOrEmpty(name)) return null;
		var cfg = RunConfig.Instance;
		if (cfg?.maps != null)
		{
			foreach (var m in cfg.maps)
			{
				if (m?.weather != null &&
				    (string.Equals(m.weather.name, name, StringComparison.OrdinalIgnoreCase) ||
				     string.Equals(m.name, name, StringComparison.OrdinalIgnoreCase)))
					return m.weather;
			}
		}
		foreach (var w in Resources.FindObjectsOfTypeAll<WeatherProfile>())
		{
			if (w != null && string.Equals(w.name, name, StringComparison.OrdinalIgnoreCase))
				return w;
		}
		return null;
	}

	private static MapDefinition? FindMap(string name)
	{
		if (string.IsNullOrEmpty(name)) return null;
		var cfg = RunConfig.Instance;
		if (cfg == null) return null;
		var byName = cfg.FindMap(name);
		if (byName != null) return byName;
		foreach (var m in cfg.maps)
		{
			if (m == null) continue;
			if (string.Equals(m.name, name, StringComparison.OrdinalIgnoreCase)) return m;
			if (!string.IsNullOrEmpty(m.displayName) &&
			    string.Equals(m.displayName, name, StringComparison.OrdinalIgnoreCase))
				return m;
		}
		return null;
	}
}

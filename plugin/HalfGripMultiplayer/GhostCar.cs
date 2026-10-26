using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HalfGripMultiplayer;

/// <summary>
/// Lightweight remote stand-in. Never clones the local player car — that copied
/// CameraSwitch / camera overlays and flooded the log with NREs.
/// Prefers a claimed EnemyController at the peer's grid slot; falls back to a tinted primitive.
/// </summary>
internal sealed class GhostCar
{
	public GameObject Root { get; private set; } = null!;
	public RemotePlayerHitbox Hitbox { get; private set; } = null!;
	public string Label { get; }
	public float Hp { get; private set; } = 100f;
	public EnemyController? BoundEnemy { get; private set; }
	public int PreferredSlot { get; set; } = -1;
	public CarLookData? PendingLook { get; set; }
	public ulong PeerId { get; private set; }

	private Vector3 _targetPos;
	private Quaternion _targetRot = Quaternion.identity;
	private TextMesh? _labelMesh;
	private GameObject? _fallbackBody;
	private Rigidbody? _enemyRb;
	private bool _enemyWasCanMove;
	private bool _enemyWasInvincible;
	private float _steerDeg;
	private bool _braking;
	private bool _handbrake;
	private bool _brakeLit;
	private bool _transparent;
	private float _fallbackAt = -1f;
	private float _speed;
	private sbyte _gear;
	private bool _inGear;
	private Vector3 _lastTargetPos;
	private float _lastTargetTime = -1f;
	private Vector3 _extrapolatedVel;
	private LineRenderer? _hpBar;
	private float _spinUntil;
	private int _ammo = -1;
	private bool _reverse;
	private bool _exhaust;
	private bool _exhaustOn;
	private bool _trails;
	private bool _trailsOn;
	private bool _magnet;
	private bool _glue;
	private bool _lightsOn;
	private LineRenderer? _magnetBeam;
	private LineRenderer? _glueRing;
	private LineRenderer? _smokePlume;
	private bool _burning;
	private bool _airborne;
	private bool _fxSmokeLit;
	private EnemyCarEffectController? _enemyFx;
	private AudioSource? _engineHum;
	private static AudioClip? _engineClip;
	private int _blinkLeft;
	private float _blinkUntil;

	private GhostCar(string label) => Label = label;

	public static GhostCar Spawn(ulong peerId, string label, bool transparent, int preferredSlot = -1)
	{
		var g = new GhostCar(label)
		{
			PreferredSlot = preferredSlot,
			PeerId = peerId,
			_transparent = transparent,
		};
		g.Root = new GameObject($"HGMP_Ghost_{label}");
		Object.DontDestroyOnLoad(g.Root);

		if (!g.TryBindEnemy(peerId))
		{
			// Defer cube/mesh until enemies exist — avoids [primitive] flash mid-load.
			g.EnsureTempHitbox(peerId);
			// Enemies can spawn several seconds after scene load — wait longer before cube fallback.
			g._fallbackAt = Time.unscaledTime + 6.5f;
			Plugin.Log.LogInfo($"PvP ghost spawned: {label} ({peerId}) slot={preferredSlot} [waiting for bot]");
		}
		else
		{
			Plugin.Log.LogInfo($"PvP ghost spawned: {label} ({peerId}) slot={preferredSlot} [bot slot]");
		}

		var labelGo = new GameObject("Name");
		labelGo.transform.SetParent(g.Root.transform, false);
		labelGo.transform.localPosition = new Vector3(0f, 2.0f, 0f);
		g._labelMesh = labelGo.AddComponent<TextMesh>();
		g._labelMesh.text = label;
		g._labelMesh.characterSize = 0.12f;
		g._labelMesh.anchor = TextAnchor.MiddleCenter;
		g._labelMesh.color = Color.white;
		g.EnsureHpBar();
		return g;
	}

	public void PushTarget(Vector3 pos, Quaternion rot, float speed, float hp, float remoteTime)
	{
		float now = Time.unscaledTime;
		if (_lastTargetTime > 0f)
		{
			float dtr = Mathf.Max(0.001f, now - _lastTargetTime);
			_extrapolatedVel = (pos - _lastTargetPos) / dtr;
			// Cap runaway extrapolation (teleports / first frame after bind).
			if (_extrapolatedVel.sqrMagnitude > 120f * 120f)
				_extrapolatedVel = Vector3.ClampMagnitude(_extrapolatedVel, 120f);
		}
		_lastTargetPos = pos;
		_lastTargetTime = now;
		_targetPos = pos;
		_targetRot = rot;
		Hp = hp;
		_speed = speed;
		_ = remoteTime;
		RefreshLabel();
		UpdateHpBar();

		TryLateBind();
	}

	public int AmmoCount => _ammo;

	public void SetAmmo(int magazines)
	{
		_ammo = magazines;
		RefreshLabel();
	}

	private byte _gunType = 255;
	private byte _shifterType = 255;

	public void SetGunType(byte gunType)
	{
		_gunType = gunType;
		RefreshLabel();
		PulseBlink(2);
	}

	public void SetShifterType(byte shifterType)
	{
		_shifterType = shifterType;
		RefreshLabel();
		PulseBlink(1);
	}

	private float _labelCamDist = 40f;

	private void RefreshLabel()
	{
		if (_labelMesh == null) return;
		// Far LOD: name + HP only (keeps nameplates readable at range).
		if (_labelCamDist > 90f)
		{
			_labelMesh.text = $"{Label}  {Mathf.CeilToInt(Hp)}hp";
			return;
		}
		string g = _inGear ? $"G{_gear}" : "N";
		string ammo = _ammo >= 0 ? $"  x{_ammo}" : "";
		string air = _airborne ? "  AIR" : "";
		string aim = _aiming ? "  AIM" : "";
		string act = Time.unscaledTime < _gunActUntil
			? _gunActKind switch { 0 => "  LOAD", 1 => "  BRK+", 2 => "  BRK-", 3 => "  EJCT", 4 => "  SPIN", _ => "" }
			: "";
		string nos = _nitroLit ? "  NOS" : "";
		string hit = Time.unscaledTime < _shiftHitUntil
			? (_shiftHitKind == 1 ? "  STALL" : "  $$")
			: "";
		string gun = _gunType switch
		{
			1 => "  SG",
			2 => "  REV",
			3 => "  SMG",
			4 => "  SAW",
			_ => ""
		};
		string sh = _shifterType switch
		{
			1 => "  AT",
			2 => "  CVT",
			3 => "  SEQ",
			4 => "  LEN",
			_ => ""
		};
		string slide = Time.unscaledTime < _slideUntil
			? _slideKind switch { 0 => "  SLD+", 1 => "  SLD-", 2 => "  LOCK", 3 => "  UNLK", _ => "" }
			: "";
		string look = _camLook ? "  LOOK" : "";
		string lockS = _steerLocked ? "  WLOCK" : "";
		string match = Time.unscaledTime < _matchRevUntil ? "  PERF" : "";
		string recenter = Time.unscaledTime < _recenterUntil ? "  CTR" : "";
		string rtt = "";
		string place = "";
		try
		{
			var sess = MpRuntime.Instance?.Session;
			if (sess != null && sess.Peers.TryGetValue(PeerId, out var peer) && peer.RttMs > 1f)
				rtt = $"  {peer.RttMs:0}ms";
			if (WorldSync.TryGetPeerLap(PeerId, out var lap) && lap.place > 0)
				place = lap.finished ? "  FIN" : $"  P{lap.place}";
		}
		catch { /* */ }
		string detail = _labelCamDist > 55f
			? $"{place}{g}{ammo}{air}{nos}{rtt}"
			: $"{place}{g}{ammo}{air}{aim}{act}{nos}{hit}{gun}{sh}{slide}{look}{lockS}{match}{recenter}{rtt}";
		_labelMesh.text = $"{Label}  {Mathf.CeilToInt(Hp)}hp  {detail}";
	}

	public void Tick(float dt)
	{
		if (Root == null) return;
		TryLateBind();
		float age = _lastTargetTime > 0f ? Mathf.Clamp(Time.unscaledTime - _lastTargetTime, 0f, 0.25f) : 0f;
		var predicted = _targetPos + _extrapolatedVel * age;
		if (Time.unscaledTime < _spinUntil)
			predicted += Root.transform.right * (Mathf.Sin(Time.unscaledTime * 28f) * 0.35f);
		if (_airborne)
			predicted += Vector3.up * (0.12f + Mathf.Abs(Mathf.Sin(Time.unscaledTime * 6f)) * 0.08f);
		float t = 1f - Mathf.Exp(-14f * dt);
		var pos = Vector3.Lerp(Root.transform.position, predicted, t);
		var rot = Quaternion.Slerp(Root.transform.rotation, _targetRot, t);
		if (_airborne)
			rot = rot * Quaternion.Euler(Mathf.Sin(Time.unscaledTime * 4f) * 4f, 0f, 0f);
		else if (Mathf.Abs(_steerDeg) > 2f)
			rot = rot * Quaternion.Euler(0f, 0f, Mathf.Clamp(-_steerDeg * 0.12f, -8f, 8f));

		if (BoundEnemy != null)
		{
			var et = BoundEnemy.transform;
			if (_enemyRb != null)
			{
				_enemyRb.isKinematic = true;
				_enemyRb.position = pos;
				_enemyRb.rotation = rot;
			}
			else
			{
				et.SetPositionAndRotation(pos, rot);
			}
			BoundEnemy.canMove = false;
			Root.transform.SetPositionAndRotation(pos, rot);
		}
		else
		{
			Root.transform.SetPositionAndRotation(pos, rot);
		}

		if (_labelMesh != null && Camera.main != null)
		{
			var cam = Camera.main.transform;
			_labelMesh.transform.rotation = Quaternion.LookRotation(
				_labelMesh.transform.position - cam.position);
			float dist = Vector3.Distance(cam.position, Root.transform.position);
			bool lodChanged = (dist > 90f) != (_labelCamDist > 90f) || (dist > 55f) != (_labelCamDist > 55f);
			_labelCamDist = dist;
			if (lodChanged) RefreshLabel();
			float scale = Mathf.Clamp(dist / 40f, 0.55f, 2.2f);
			_labelMesh.characterSize = 0.12f * scale;
			_labelMesh.gameObject.SetActive(dist < 220f);
		}

		ApplySteerVisual();
		ApplyBrakeLights();
		ApplyExhaust();
		ApplyTrails();
		ApplyMagnetVisual();
		ApplyGlueVisual();
		ApplyHeadlights();
		ApplySmoke();
		ApplyEngineHum();
		SpinWheels(dt);
		UpdateHpBar();
	}

	/// <summary>Track just became ready — claim a bot body if we still have none.</summary>
	public void ForceRebind()
	{
		if (BoundEnemy != null) return;
		_fallbackAt = Time.unscaledTime + 4f;
		TryLateBind();
	}

	public void SetBurning(bool on)
	{
		_burning = on || Hp < 35f;
		ApplySmoke();
	}

	public void SetExhaust(bool on)
	{
		_exhaust = on;
		ApplyExhaust();
	}

	public void SetTrails(bool on)
	{
		_trails = on;
		ApplyTrails();
	}

	public void SetMagnet(bool on)
	{
		_magnet = on;
		ApplyMagnetVisual();
	}

	public void SetGlue(bool on)
	{
		_glue = on;
		ApplyGlueVisual();
	}

	public void SetLeech(bool on)
	{
		if (on)
			PulseDamaged(0.4f);
	}

	public void SetThreatened(bool on)
	{
		if (_labelMesh == null) return;
		_labelMesh.color = on ? new Color(1f, 0.35f, 0.15f, 1f) : Color.white;
	}

	public void SetCamOutside(bool outside)
	{
		if (_labelMesh == null) return;
		// Brief tint: cabin=cyan, exterior=white flash.
		_labelMesh.color = outside ? new Color(1f, 1f, 0.7f, 1f) : new Color(0.6f, 0.9f, 1f, 1f);
	}

	public void SetHandbrakeLocked(bool locked)
	{
		_handbrake = locked;
		_braking = locked || _braking;
		ApplyBrakeLights();
		if (locked) PulseDamaged(0.25f);
	}

	public void PulseBlink(int blinks)
	{
		_blinkLeft = Mathf.Max(0, blinks);
		_blinkUntil = Time.unscaledTime + Mathf.Clamp(_blinkLeft * 0.18f, 0.3f, 2.2f);
		if (_labelMesh != null)
			_labelMesh.color = new Color(1f, 0.85f, 0.2f, 1f);
	}

	private bool _aiming;

	public void SetAiming(bool aiming)
	{
		_aiming = aiming;
		RefreshLabel();
		if (_labelMesh != null)
			_labelMesh.color = aiming ? new Color(1f, 0.45f, 0.2f, 1f) : Color.white;
	}

	public void PulseMag(bool seated)
	{
		PulseBlink(seated ? 3 : 1);
		if (seated) PulseDamaged(0.2f);
	}

	private bool _forcedSmoke;
	private bool _nitroLit;
	private float _engineIndicator = -1f;
	private float _gunActUntil;
	private byte _gunActKind;

	public void SetEngineIndicator(float indicator)
	{
		_engineIndicator = indicator;
	}

	public void SetForcedSmoke(bool on)
	{
		_forcedSmoke = on;
		ApplySmoke();
	}

	public void SetNitroLit(bool on)
	{
		if (_nitroLit == on) return;
		_nitroLit = on;
		try
		{
			var root = BoundEnemy != null ? BoundEnemy.gameObject : Root;
			var nitro = root != null ? root.GetComponentInChildren<EnemyNitro>(true) : null;
			if (nitro != null)
			{
				AccessTools.Method(typeof(EnemyNitro), "SetFlames")?.Invoke(nitro, new object[] { on });
			}
			else if (!on)
			{
				GetEnemyFx()?.StopFireEmission();
			}
		}
		catch { /* visual */ }
		if (on) PulseBlink(2);
	}

	public void PulseGunAct(byte kind, float param)
	{
		_ = param;
		_gunActKind = kind;
		_gunActUntil = Time.unscaledTime + (kind == 4 ? 0.55f : 0.35f);
		PulseBlink(kind == 0 || kind == 3 ? 2 : 1);
		RefreshLabel();
	}

	public void PlayExplode(byte kind)
	{
		try
		{
			var fx = GetEnemyFx();
			if (fx == null) return;
			if (kind == 0) fx.AddSmallExplosionEffect();
			else fx.StartBigExplosionDestroyedEffect();
		}
		catch { /* fx */ }
	}

	public void PulseSpinOut(float speed)
	{
		_spinUntil = Time.unscaledTime + Mathf.Clamp(0.4f + speed * 0.02f, 0.5f, 1.6f);
	}

	public void PulseDamaged(float fraction)
	{
		if (_labelMesh == null) return;
		_labelMesh.color = Color.Lerp(Color.white, Color.red, Mathf.Clamp01(fraction * 2f));
	}

	public void ApplyAnim(float steerDeg, bool braking, bool handbrake = false, bool reverse = false, bool exhaust = false, bool trails = false, bool airborne = false)
	{
		_steerDeg = steerDeg;
		_braking = braking || handbrake || reverse;
		_handbrake = handbrake;
		_reverse = reverse;
		_exhaust = exhaust;
		_trails = trails || handbrake;
		_airborne = airborne;
		RefreshLabel();
		if (BoundEnemy?.carController != null)
			BoundEnemy.carController.steeringAngleDeg = steerDeg;
	}

	public void PulseInvert()
	{
		PulseDamaged(0.7f);
		_spinUntil = Time.unscaledTime + 0.55f;
	}

	public void SetGear(sbyte gear, bool inGear)
	{
		bool changed = gear != _gear || inGear != _inGear;
		_gear = gear;
		_inGear = inGear;
		RefreshLabel();
		if (changed)
			PulseBlink(1);
	}

	public void ApplyHeal(float amount)
	{
		if (amount <= 0.01f) return;
		Hp = Mathf.Min(100f, Hp + amount);
		RefreshLabel();
		UpdateHpBar();
		PulseBlink(3);
		PulseDamaged(0.15f);
	}

	private float _shiftHitUntil;
	private byte _shiftHitKind;

	public void PulseShiftHit(byte kind)
	{
		_shiftHitKind = kind;
		_shiftHitUntil = Time.unscaledTime + 0.9f;
		PulseDamaged(kind == 1 ? 0.35f : 0.55f);
		PulseBlink(kind == 1 ? 2 : 4);
		RefreshLabel();
		if (_labelMesh != null)
			_labelMesh.color = kind == 1
				? new Color(1f, 0.55f, 0.15f, 1f)
				: new Color(1f, 0.2f, 0.2f, 1f);
	}

	private float _slideUntil;
	private byte _slideKind;
	private bool _camLook;

	public void PulseSlide(byte kind)
	{
		_slideKind = kind;
		_slideUntil = Time.unscaledTime + 0.4f;
		PulseBlink(kind == 0 || kind == 2 ? 2 : 1);
		RefreshLabel();
	}

	public void SetCamLook(bool released)
	{
		_camLook = released;
		RefreshLabel();
		if (_labelMesh != null)
			_labelMesh.color = released ? new Color(0.5f, 0.85f, 1f, 1f) : Color.white;
	}

	public void PulseMagShot()
	{
		PulseBlink(1);
		if (_labelMesh != null)
			_labelMesh.color = new Color(1f, 0.9f, 0.3f, 1f);
	}

	private bool _steerLocked;
	private float _matchRevUntil;
	private float _recenterUntil;

	public void SetSteerLocked(bool locked)
	{
		_steerLocked = locked;
		RefreshLabel();
		PulseBlink(locked ? 3 : 1);
	}

	public void PulseRecenter()
	{
		_recenterUntil = Time.unscaledTime + 0.6f;
		PulseBlink(2);
		RefreshLabel();
	}

	public void PulseMatchRev()
	{
		_matchRevUntil = Time.unscaledTime + 0.7f;
		PulseBlink(3);
		RefreshLabel();
		if (_labelMesh != null)
			_labelMesh.color = new Color(0.3f, 1f, 0.55f, 1f);
	}

	public void PulseShake(float severity)
	{
		_spinUntil = Mathf.Max(_spinUntil, Time.unscaledTime + Mathf.Clamp(severity * 0.4f, 0.15f, 0.7f));
		PulseDamaged(Mathf.Clamp(severity * 0.5f, 0.1f, 0.8f));
		PulseBlink(2);
	}

	public void Destroy()
	{
		if (Hitbox != null)
			WorldSync.ClearGhostExtras(Hitbox.PeerId);
		ReleaseEnemy();
		if (Root != null) Object.Destroy(Root);
	}

	private void TryLateBind()
	{
		if (BoundEnemy != null) return;
		if (TryBindEnemy(PeerId))
		{
			_fallbackAt = -1f;
			return;
		}
		if (_fallbackBody == null && _fallbackAt > 0f && Time.unscaledTime >= _fallbackAt)
		{
			BuildFallbackVisual(PeerId, _transparent);
			_fallbackAt = -1f;
			Plugin.Log.LogInfo($"PvP ghost {Label}: fallback visual (no bot slot)");
		}
	}

	private void EnsureTempHitbox(ulong peerId)
	{
		if (Hitbox != null) return;
		var hitGo = new GameObject("PvPHitboxTemp");
		hitGo.transform.SetParent(Root.transform, false);
		hitGo.transform.localPosition = new Vector3(0f, 0.5f, 0f);
		hitGo.transform.localScale = new Vector3(2.2f, 1.4f, 4.0f);
		hitGo.AddComponent<BoxCollider>();
		Hitbox = hitGo.AddComponent<RemotePlayerHitbox>();
		Hitbox.PeerId = peerId;
	}

	private bool TryBindEnemy(ulong peerId)
	{
		// Already bound to a live bot — never reclaim (prevents Enemy_0 double-claim).
		if (BoundEnemy != null) return true;

		var enemy = ClaimEnemyForSlot(PreferredSlot);
		if (enemy == null) return false;

		BoundEnemy = enemy;
		_enemyWasCanMove = enemy.canMove;
		_enemyWasInvincible = enemy.invincible;
		enemy.canMove = false;
		enemy.invincible = true; // local AI HP must not steal PvP hits
		_enemyRb = enemy.GetComponent<Rigidbody>();
		if (_enemyRb != null)
		{
			_enemyRb.isKinematic = true;
			_enemyRb.detectCollisions = true;
		}

		if (_fallbackBody != null)
		{
			Object.Destroy(_fallbackBody);
			_fallbackBody = null;
		}

		Root.transform.SetPositionAndRotation(enemy.transform.position, enemy.transform.rotation);
		_targetPos = Root.transform.position;
		_targetRot = Root.transform.rotation;

		// Drop temp hitbox on Root if we had one while waiting.
		if (Hitbox != null && Hitbox.gameObject != enemy.gameObject &&
		    Hitbox.transform.IsChildOf(Root.transform))
			Object.Destroy(Hitbox.gameObject);

		Hitbox = enemy.gameObject.GetComponent<RemotePlayerHitbox>()
		         ?? enemy.gameObject.AddComponent<RemotePlayerHitbox>();
		Hitbox.PeerId = peerId;
		Hitbox.DamageMultiplier = 1f;

		// Loadout may have arrived before claim — attach now.
		if (WorldSync.TryGetLoadout(peerId, out var ids))
			WorldSync.AttachLoadoutVisuals(peerId, enemy.transform, ids);

		// Bot CameraSwitch fights the local view and NREs on destroyed cameras — kill immediately.
		foreach (var cs in enemy.GetComponentsInChildren<CameraSwitch>(true))
		{
			cs.enabled = false;
			Object.Destroy(cs);
		}
		if (enemy.carController != null)
			enemy.carController.enabled = false;

		Plugin.Log.LogInfo($"Remote peer {peerId} claimed bot '{enemy.name}' for grid slot {PreferredSlot}");
		if (PendingLook.HasValue)
			ApplyLook(PendingLook.Value);
		else if (CarLookSync.TryGetPending(peerId, out var pending))
			ApplyLook(pending);
		if (WorldSync.TryGetLoadout(peerId, out var loadout))
			WorldSync.AttachLoadoutVisuals(peerId, enemy.transform, loadout);
		WorldSync.TryRegisterGhostOnLeaderboard(this);
		return true;
	}

	public void ApplyLook(CarLookData look)
	{
		PendingLook = look;
		if (Hitbox != null)
			CarLookSync.StorePending(Hitbox.PeerId, look);
		if (BoundEnemy == null) return;
		var csc = BoundEnemy.GetComponent<CarSkinController>();
		CarLookSync.ApplyToController(csc, look);
	}

	private void ReleaseEnemy()
	{
		if (BoundEnemy == null) return;
		BoundEnemy.canMove = _enemyWasCanMove;
		BoundEnemy.invincible = _enemyWasInvincible;
		if (_enemyRb != null) _enemyRb.isKinematic = false;
		var hb = BoundEnemy.GetComponent<RemotePlayerHitbox>();
		if (hb != null) Object.Destroy(hb);
		BoundEnemy = null;
		_enemyRb = null;
	}

	private static EnemyController? ClaimEnemyForSlot(int preferredSlot)
	{
		var claimed = new HashSet<int>();
		var rt = MpRuntime.Instance;
		if (rt != null)
		{
			foreach (var g in rt.AllGhosts())
			{
				if (g?.BoundEnemy != null)
					claimed.Add(g.BoundEnemy.GetInstanceID());
			}
		}

		var tm = Object.FindFirstObjectByType<TrackManager>();
		if (tm != null && preferredSlot >= 0)
		{
			var marker = RaceSync.SlotMarker(tm, preferredSlot);
			if (marker != null)
			{
				var near = RaceSync.FindEnemyNear(tm, marker.position, claimed);
				if (near != null) return near;
			}
		}

		if (tm != null)
		{
			foreach (var e in tm.Enemies)
			{
				if (e == null || !e.alive) continue;
				if (claimed.Contains(e.GetInstanceID())) continue;
				if (e.GetComponent<RemotePlayerHitbox>() != null) continue;
				return e;
			}
		}

		foreach (var e in Object.FindObjectsByType<EnemyController>(FindObjectsSortMode.None))
		{
			if (e == null || !e.alive) continue;
			if (claimed.Contains(e.GetInstanceID())) continue;
			if (e.GetComponent<RemotePlayerHitbox>() != null) continue;
			return e;
		}
		return null;
	}

	private void BuildFallbackVisual(ulong peerId, bool transparent)
	{
		// Prefer a lightweight clone of a live car mesh over a bare cube.
		if (!TryCloneCarMesh(transparent))
		{
			_fallbackBody = GameObject.CreatePrimitive(PrimitiveType.Cube);
			_fallbackBody.name = "Fallback";
			_fallbackBody.transform.SetParent(Root.transform, false);
			_fallbackBody.transform.localScale = new Vector3(1.8f, 0.9f, 3.4f);
			Object.Destroy(_fallbackBody.GetComponent<Collider>());
			var r = _fallbackBody.GetComponent<MeshRenderer>();
			if (r != null)
				r.material.color = transparent
					? new Color(1f, 0.35f, 0.25f, 0.55f)
					: new Color(0.9f, 0.25f, 0.2f, 1f);
		}

		var hitGo = new GameObject("PvPHitbox");
		hitGo.transform.SetParent(Root.transform, false);
		hitGo.transform.localPosition = new Vector3(0f, 0.5f, 0f);
		hitGo.transform.localScale = new Vector3(2.2f, 1.4f, 4.0f);
		hitGo.AddComponent<BoxCollider>();
		Hitbox = hitGo.AddComponent<RemotePlayerHitbox>();
		Hitbox.PeerId = peerId;
	}

	private void EnsureHpBar()
	{
		if (_hpBar != null) return;
		var go = new GameObject("HpBar");
		go.transform.SetParent(Root.transform, false);
		go.transform.localPosition = new Vector3(0f, 1.55f, 0f);
		_hpBar = go.AddComponent<LineRenderer>();
		_hpBar.positionCount = 2;
		_hpBar.startWidth = 0.08f;
		_hpBar.endWidth = 0.08f;
		_hpBar.useWorldSpace = true;
		_hpBar.material = new Material(Shader.Find("Sprites/Default") ?? Shader.Find("Legacy Shaders/Particles/Alpha Blended"));
		UpdateHpBar();
	}

	private void UpdateHpBar()
	{
		if (_hpBar == null || Root == null) return;
		float pct = Mathf.Clamp01(Hp / 100f);
		var mid = Root.transform.position + Vector3.up * 1.55f;
		var right = Root.transform.right;
		float half = 0.9f;
		_hpBar.SetPosition(0, mid - right * half);
		_hpBar.SetPosition(1, mid - right * half + right * (half * 2f * pct));
		var c = Color.Lerp(Color.red, Color.green, pct);
		_hpBar.startColor = c;
		_hpBar.endColor = c;
		if (_labelMesh != null && Time.unscaledTime >= _spinUntil)
			_labelMesh.color = Color.Lerp(_labelMesh.color, Color.white, 0.15f);
	}

	private void ApplySteerVisual()
	{
		if (BoundEnemy?.carController == null) return;
		var cc = BoundEnemy.carController;
		try
		{
			cc.steeringAngleDeg = _steerDeg;
			if (cc.frontWheels == null) return;
			foreach (var w in cc.frontWheels)
			{
				if (w == null) continue;
				var e = w.localEulerAngles;
				w.localRotation = Quaternion.Euler(e.x, _steerDeg, e.z);
			}
		}
		catch { /* visual */ }
	}

	private void SpinWheels(float dt)
	{
		if (BoundEnemy?.carController == null) return;
		float deg = _speed * 45f * dt;
		if (Mathf.Abs(deg) < 0.01f) return;
		try
		{
			var cc = BoundEnemy.carController;
			void Spin(System.Collections.Generic.List<Transform> list)
			{
				if (list == null) return;
				foreach (var w in list)
				{
					if (w == null) continue;
					w.Rotate(deg, 0f, 0f, Space.Self);
				}
			}
			Spin(cc.frontWheels);
			Spin(cc.backWheels);
		}
		catch { /* visual */ }
	}

	private void ApplyBrakeLights()
	{
		if (BoundEnemy?.carController == null) return;
		bool lit = _braking || _reverse;
		if (lit == _brakeLit) return;
		_brakeLit = lit;
		try
		{
			var lights = BoundEnemy.carController.becSpate;
			if (lights == null) return;
			foreach (var renderer in lights)
			{
				if (renderer == null) continue;
				if (lit) renderer.material.EnableKeyword("_EMISSION");
				else renderer.material.DisableKeyword("_EMISSION");
			}
		}
		catch { /* visual */ }
	}

	private void ApplyExhaust()
	{
		if (BoundEnemy == null) return;
		if (_exhaust == _exhaustOn) return;
		_exhaustOn = _exhaust;
		try
		{
			foreach (var pipe in BoundEnemy.GetComponentsInChildren<ExaustPipeEffect>(true))
			{
				if (pipe == null) continue;
				if (_exhaustOn) pipe.StartEmission();
				else pipe.StopEmission();
			}
		}
		catch { /* visual */ }
	}

	private void ApplyTrails()
	{
		if (_trails == _trailsOn) return;
		_trailsOn = _trails;
		try
		{
			var root = BoundEnemy != null ? BoundEnemy.gameObject : Root;
			if (root == null) return;
			foreach (var trail in root.GetComponentsInChildren<TireTrails>(true))
			{
				if (trail == null) continue;
				if (_trailsOn)
				{
					trail.StartTrails();
					trail.StartParticles();
				}
				else
				{
					trail.StopTrails();
					trail.StopParticles();
				}
			}
		}
		catch { /* visual */ }
	}

	private void ApplyMagnetVisual()
	{
		if (Root == null) return;
		if (_magnet)
		{
			if (_magnetBeam == null)
			{
				var go = new GameObject("HGMP_MagnetBeam");
				go.transform.SetParent(Root.transform, false);
				go.transform.localPosition = new Vector3(0f, 0.6f, 1.2f);
				_magnetBeam = go.AddComponent<LineRenderer>();
				_magnetBeam.positionCount = 2;
				_magnetBeam.startWidth = 0.12f;
				_magnetBeam.endWidth = 0.35f;
				_magnetBeam.useWorldSpace = true;
				_magnetBeam.material = new Material(Shader.Find("Sprites/Default") ?? Shader.Find("Legacy Shaders/Particles/Alpha Blended"));
				_magnetBeam.startColor = new Color(0.35f, 0.75f, 1f, 0.85f);
				_magnetBeam.endColor = new Color(0.35f, 0.75f, 1f, 0.15f);
			}
			var origin = Root.transform.position + Vector3.up * 0.6f + Root.transform.forward * 1.2f;
			_magnetBeam.SetPosition(0, origin);
			_magnetBeam.SetPosition(1, origin + Root.transform.forward * 6f);
			_magnetBeam.enabled = true;
		}
		else if (_magnetBeam != null)
		{
			_magnetBeam.enabled = false;
		}
	}

	private void ApplyGlueVisual()
	{
		if (Root == null) return;
		if (_glue)
		{
			if (_glueRing == null)
			{
				var go = new GameObject("HGMP_GlueRing");
				go.transform.SetParent(Root.transform, false);
				_glueRing = go.AddComponent<LineRenderer>();
				_glueRing.positionCount = 5;
				_glueRing.startWidth = 0.1f;
				_glueRing.endWidth = 0.1f;
				_glueRing.useWorldSpace = true;
				_glueRing.loop = true;
				_glueRing.material = new Material(Shader.Find("Sprites/Default") ?? Shader.Find("Legacy Shaders/Particles/Alpha Blended"));
				_glueRing.startColor = new Color(0.85f, 0.95f, 0.35f, 0.75f);
				_glueRing.endColor = new Color(0.85f, 0.95f, 0.35f, 0.75f);
			}
			var c = Root.transform.position + Vector3.up * 0.12f;
			var r = Root.transform.right;
			var f = Root.transform.forward;
			_glueRing.SetPosition(0, c - r * 1.1f - f * 2f);
			_glueRing.SetPosition(1, c + r * 1.1f - f * 2f);
			_glueRing.SetPosition(2, c + r * 1.1f + f * 2f);
			_glueRing.SetPosition(3, c - r * 1.1f + f * 2f);
			_glueRing.SetPosition(4, c - r * 1.1f - f * 2f);
			_glueRing.enabled = true;
		}
		else if (_glueRing != null)
		{
			_glueRing.enabled = false;
		}
	}

	private void ApplyHeadlights()
	{
		bool want = false;
		try
		{
			var weather = RunState.ActiveWeather;
			if (weather != null)
				want = weather.headlightsActive;
			else
			{
				var wc = Object.FindAnyObjectByType<WeatherController>();
				if (wc?.activeProfile != null)
					want = wc.activeProfile.headlightsActive;
			}
		}
		catch { /* */ }
		if (want == _lightsOn) return;
		_lightsOn = want;
		try
		{
			var root = BoundEnemy != null ? BoundEnemy.gameObject : Root;
			if (root == null) return;
			foreach (var hl in root.GetComponentsInChildren<HeadLightsController>(true))
			{
				if (hl?.spotlight == null) continue;
				hl.spotlight.SetActive(_lightsOn);
			}
		}
		catch { /* visual */ }
	}

	private EnemyCarEffectController? GetEnemyFx()
	{
		if (BoundEnemy == null) { _enemyFx = null; return null; }
		if (_enemyFx == null)
			_enemyFx = BoundEnemy.GetComponentInChildren<EnemyCarEffectController>(true);
		return _enemyFx;
	}

	private void ApplyEngineHum()
	{
		if (Root == null) return;
		float speed = Mathf.Abs(_speed);
		if (_engineHum == null)
		{
			_engineHum = Root.GetComponent<AudioSource>() ?? Root.AddComponent<AudioSource>();
			_engineHum.loop = true;
			_engineHum.playOnAwake = false;
			_engineHum.spatialBlend = 1f;
			_engineHum.minDistance = 4f;
			_engineHum.maxDistance = 90f;
			_engineHum.dopplerLevel = 0.4f;
			if (_engineClip == null)
			{
				try
				{
					foreach (var bank in Resources.FindObjectsOfTypeAll<EngineAudioBankPlayer>())
					{
						var clipsField = AccessTools.Field(typeof(EngineAudioBankPlayer), "clips");
						var clips = clipsField?.GetValue(bank) as AudioClip[][];
						if (clips == null) continue;
						foreach (var row in clips)
						{
							if (row == null) continue;
							foreach (var c in row)
							{
								if (c != null) { _engineClip = c; break; }
							}
							if (_engineClip != null) break;
						}
						if (_engineClip != null) break;
					}
				}
				catch { /* audio */ }
			}
			_engineHum.clip = _engineClip;
		}
		if (_engineHum.clip == null) return;
		if (speed > 1.5f || _engineIndicator > 0.08f)
		{
			if (!_engineHum.isPlaying) _engineHum.Play();
			float gearMul = _inGear && _gear != 0
				? Mathf.Lerp(0.85f, 1.15f, Mathf.Clamp01(Mathf.Abs(_gear) / 6f))
				: 0.9f;
			float nosMul = _nitroLit ? 1.12f : 1f;
			float rev = _engineIndicator >= 0f
				? Mathf.Clamp01(_engineIndicator)
				: Mathf.Clamp01(speed / 85f);
			_engineHum.pitch = Mathf.Lerp(0.55f, 1.85f, rev) * gearMul * nosMul;
			_engineHum.volume = Mathf.Clamp01(0.12f + rev * 0.55f + Mathf.Clamp01(speed / 40f) * 0.2f)
			                   * (_nitroLit ? 0.42f : 0.32f);
		}
		else if (_engineHum.isPlaying)
		{
			_engineHum.volume = Mathf.MoveTowards(_engineHum.volume, 0f, Time.deltaTime * 1.5f);
			if (_engineHum.volume <= 0.01f) _engineHum.Stop();
		}

		if (_blinkLeft > 0 && Time.unscaledTime < _blinkUntil && _labelMesh != null)
		{
			bool on = Mathf.Repeat(Time.unscaledTime * 6f, 1f) < 0.5f;
			_labelMesh.color = on ? new Color(1f, 0.9f, 0.2f, 1f) : Color.white;
		}
	}

	private void ApplySmoke()
	{
		bool want = _forcedSmoke || _burning || Hp < 35f;
		if (Root == null) return;

		// Prefer the game's own smoke/fire particle stacks on claimed bots.
		var fx = GetEnemyFx();
		if (fx != null)
		{
			if (want != _fxSmokeLit)
			{
				_fxSmokeLit = want;
				try
				{
					if (want)
					{
						fx.StartSmokeEmission();
						if (Hp < 20f) fx.StartFlamesEffect();
					}
					else
					{
						fx.StopFireEmission();
						try
						{
							AccessTools.Method(typeof(EnemyCarEffectController), "StopSmokeEmission")
								?.Invoke(fx, null);
						}
						catch { /* optional */ }
					}
				}
				catch { /* fx */ }
			}
			if (_smokePlume != null) _smokePlume.enabled = false;
			return;
		}

		if (want)
		{
			if (_smokePlume == null)
			{
				var go = new GameObject("HGMP_Smoke");
				go.transform.SetParent(Root.transform, false);
				_smokePlume = go.AddComponent<LineRenderer>();
				_smokePlume.positionCount = 2;
				_smokePlume.startWidth = 0.35f;
				_smokePlume.endWidth = 0.9f;
				_smokePlume.useWorldSpace = true;
				_smokePlume.material = new Material(Shader.Find("Sprites/Default") ?? Shader.Find("Legacy Shaders/Particles/Alpha Blended"));
			}
			var origin = Root.transform.position + Vector3.up * 1.1f;
			float wobble = Mathf.Sin(Time.unscaledTime * 9f) * 0.15f;
			_smokePlume.SetPosition(0, origin);
			_smokePlume.SetPosition(1, origin + Vector3.up * (1.6f + Hp * 0.01f) + Root.transform.right * wobble);
			float a = Mathf.Lerp(0.85f, 0.35f, Mathf.Clamp01(Hp / 35f));
			_smokePlume.startColor = new Color(0.15f, 0.15f, 0.15f, a);
			_smokePlume.endColor = new Color(0.25f, 0.25f, 0.25f, 0.05f);
			_smokePlume.enabled = true;
		}
		else if (_smokePlume != null)
		{
			_smokePlume.enabled = false;
		}
	}

	private bool TryCloneCarMesh(bool transparent)
	{
		try
		{
			MeshFilter? src = null;
			foreach (var csc in Object.FindObjectsByType<CarSkinController>(FindObjectsSortMode.None))
			{
				if (csc == null) continue;
				src = csc.GetComponentInChildren<MeshFilter>();
				if (src?.sharedMesh != null) break;
			}
			if (src?.sharedMesh == null) return false;
			_fallbackBody = new GameObject("FallbackMesh");
			_fallbackBody.transform.SetParent(Root.transform, false);
			var mf = _fallbackBody.AddComponent<MeshFilter>();
			mf.sharedMesh = src.sharedMesh;
			var mr = _fallbackBody.AddComponent<MeshRenderer>();
			var srcMr = src.GetComponent<MeshRenderer>();
			if (srcMr?.sharedMaterial != null)
				mr.sharedMaterial = srcMr.sharedMaterial;
			else
				mr.material.color = transparent
					? new Color(1f, 0.35f, 0.25f, 0.55f)
					: new Color(0.9f, 0.25f, 0.2f, 1f);
			return true;
		}
		catch { return false; }
	}
}

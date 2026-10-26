using System.Collections.Generic;
using UnityEngine;

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

	private Vector3 _targetPos;
	private Quaternion _targetRot = Quaternion.identity;
	private TextMesh? _labelMesh;
	private GameObject? _fallbackBody;
	private Rigidbody? _enemyRb;
	private bool _enemyWasCanMove;
	private bool _enemyWasInvincible;

	private GhostCar(string label) => Label = label;

	public static GhostCar Spawn(ulong peerId, string label, bool transparent, int preferredSlot = -1)
	{
		var g = new GhostCar(label) { PreferredSlot = preferredSlot };
		g.Root = new GameObject($"HGMP_Ghost_{label}");
		Object.DontDestroyOnLoad(g.Root);

		if (!g.TryBindEnemy(peerId))
			g.BuildFallbackVisual(peerId, transparent);

		var labelGo = new GameObject("Name");
		labelGo.transform.SetParent(g.Root.transform, false);
		labelGo.transform.localPosition = new Vector3(0f, 2.0f, 0f);
		g._labelMesh = labelGo.AddComponent<TextMesh>();
		g._labelMesh.text = label;
		g._labelMesh.characterSize = 0.12f;
		g._labelMesh.anchor = TextAnchor.MiddleCenter;
		g._labelMesh.color = Color.white;

		Plugin.Log.LogInfo($"PvP ghost spawned: {label} ({peerId}) slot={preferredSlot}" +
		                  (g.BoundEnemy != null ? " [bot slot]" : " [primitive]"));
		return g;
	}

	public void PushTarget(Vector3 pos, Quaternion rot, float speed, float hp, float remoteTime)
	{
		_targetPos = pos;
		_targetRot = rot;
		Hp = hp;
		_ = speed;
		_ = remoteTime;
		if (_labelMesh != null)
			_labelMesh.text = $"{Label}  {Mathf.CeilToInt(hp)}hp";

		// Late bind: guest/host may receive poses before enemies exist on the track.
		if (BoundEnemy == null && Hitbox != null)
			TryBindEnemy(Hitbox.PeerId);
	}

	public void Tick(float dt)
	{
		if (Root == null) return;
		float t = 1f - Mathf.Exp(-14f * dt);
		var pos = Vector3.Lerp(Root.transform.position, _targetPos, t);
		var rot = Quaternion.Slerp(Root.transform.rotation, _targetRot, t);

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
			_labelMesh.transform.rotation = Quaternion.LookRotation(
				_labelMesh.transform.position - Camera.main.transform.position);
	}

	public void Destroy()
	{
		ReleaseEnemy();
		if (Root != null) Object.Destroy(Root);
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

		Hitbox = enemy.gameObject.GetComponent<RemotePlayerHitbox>()
		         ?? enemy.gameObject.AddComponent<RemotePlayerHitbox>();
		Hitbox.PeerId = peerId;
		Hitbox.DamageMultiplier = 1f;

		// Bot CameraSwitch fights the local view and NREs on destroyed cameras — kill immediately.
		foreach (var cs in enemy.GetComponentsInChildren<CameraSwitch>(true))
		{
			cs.enabled = false;
			Object.Destroy(cs);
		}
		if (enemy.carController != null)
			enemy.carController.enabled = false;

		Plugin.Log.LogInfo($"Remote peer {peerId} claimed bot '{enemy.name}' for grid slot {PreferredSlot}");
		return true;
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

		var hitGo = new GameObject("PvPHitbox");
		hitGo.transform.SetParent(Root.transform, false);
		hitGo.transform.localPosition = new Vector3(0f, 0.5f, 0f);
		hitGo.transform.localScale = new Vector3(2.2f, 1.4f, 4.0f);
		hitGo.AddComponent<BoxCollider>();
		Hitbox = hitGo.AddComponent<RemotePlayerHitbox>();
		Hitbox.PeerId = peerId;
	}
}

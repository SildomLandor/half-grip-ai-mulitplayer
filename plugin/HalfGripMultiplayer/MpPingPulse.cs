using UnityEngine;

namespace HalfGripMultiplayer;

/// <summary>Bob + face camera for WorldPing markers.</summary>
internal sealed class MpPingPulse : MonoBehaviour
{
	public float Life = 4f;
	private float _born;
	private Vector3 _base;

	private void Awake()
	{
		_born = Time.unscaledTime;
		_base = transform.position;
	}

	private void Update()
	{
		float age = Time.unscaledTime - _born;
		float t = Mathf.Clamp01(age / Life);
		float bob = Mathf.Sin(age * 6f) * 0.35f;
		transform.position = _base + Vector3.up * bob;
		var cam = Camera.main;
		if (cam != null)
			transform.rotation = Quaternion.LookRotation(transform.position - cam.transform.position);
		var tm = GetComponent<TextMesh>();
		if (tm != null)
		{
			var c = tm.color;
			c.a = 1f - t * t;
			tm.color = c;
		}
	}
}

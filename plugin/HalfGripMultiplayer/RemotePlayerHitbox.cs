using UnityEngine;

namespace HalfGripMultiplayer;

/// <summary>Collider on a remote ghost so local gun rays can score PvP hits.</summary>
public sealed class RemotePlayerHitbox : MonoBehaviour
{
	public ulong PeerId;
	public float DamageMultiplier = 1f;
}

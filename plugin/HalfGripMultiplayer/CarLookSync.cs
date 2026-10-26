using System;
using System.Collections.Generic;
using UnityEngine;

namespace HalfGripMultiplayer;

/// <summary>
/// Syncs paint / body model index / chassis CarSkin asset name between peers.
/// Applied onto the remote ghost's claimed EnemyController CarSkinController.
/// </summary>
internal struct CarLookData : IEquatable<CarLookData>
{
	public byte SkinIndex;
	public Color Color1;
	public Color Color2;
	public Color Color3;
	public string AssetName;
	public string ChassisName;

	public static CarLookData Empty => new()
	{
		SkinIndex = 0,
		Color1 = Color.red,
		Color2 = Color.white,
		Color3 = Color.black,
		AssetName = "",
		ChassisName = "",
	};

	public bool Equals(CarLookData other) =>
		SkinIndex == other.SkinIndex &&
		Approx(Color1, other.Color1) &&
		Approx(Color2, other.Color2) &&
		Approx(Color3, other.Color3) &&
		string.Equals(AssetName, other.AssetName, StringComparison.Ordinal) &&
		string.Equals(ChassisName, other.ChassisName, StringComparison.Ordinal);

	public override bool Equals(object? obj) => obj is CarLookData o && Equals(o);
	public override int GetHashCode() => HashCode.Combine(SkinIndex, AssetName, ChassisName);

	private static bool Approx(Color a, Color b) =>
		Mathf.Abs(a.r - b.r) < 0.002f &&
		Mathf.Abs(a.g - b.g) < 0.002f &&
		Mathf.Abs(a.b - b.b) < 0.002f;
}

internal static class CarLookSync
{
	private static readonly Dictionary<ulong, CarLookData> Pending = new();
	private static CarLookData _lastSent = CarLookData.Empty;
	private static bool _dirty = true;

	public static void MarkDirty() => _dirty = true;

	public static bool TryCaptureLocal(out CarLookData look)
	{
		look = CarLookData.Empty;
		var car = FindLocalPlayerCar();
		if (car == null) return false;
		var csc = car.GetComponent<CarSkinController>();
		if (csc == null) return false;

		var skin = csc.carSkin;
		byte idx = 0;
		Color c1 = Color.red, c2 = Color.white, c3 = Color.black;
		string asset = "";
		if (skin != null)
		{
			idx = (byte)Mathf.Clamp(skin.skinIndex, 0, 255);
			c1 = Opaque(skin.color1);
			c2 = Opaque(skin.color2);
			c3 = Opaque(skin.color3);
			if (!string.IsNullOrEmpty(skin.name) && !skin.name.StartsWith("CarSkin", StringComparison.Ordinal))
				asset = skin.name.Replace("(Clone)", "").Trim();
			else if (!string.IsNullOrEmpty(skin.name))
				asset = skin.name.Replace("(Clone)", "").Trim();
		}
		else
		{
			// Fall back to painted colours from save even if controller not dressed yet.
			var colours = PaintSchemes.Decode(PlayerCarSkin.Line);
			if (colours != null && colours.Length >= 3)
			{
				c1 = Opaque(colours[0]);
				c2 = Opaque(colours[1]);
				c3 = Opaque(colours[2]);
			}
		}

		string chassis = "";
		try
		{
			if (PlayerLoadout.Known)
			{
				var up = PlayerLoadout.First<ChassisUpgrade>();
				if (up != null)
				{
					chassis = up.name ?? "";
					if (up.carSkin != null && string.IsNullOrEmpty(asset))
						asset = up.carSkin.name.Replace("(Clone)", "").Trim();
				}
			}
		}
		catch { /* loadout not ready */ }

		look = new CarLookData
		{
			SkinIndex = idx,
			Color1 = c1,
			Color2 = c2,
			Color3 = c3,
			AssetName = asset ?? "",
			ChassisName = chassis ?? "",
		};
		return true;
	}

	public static bool NeedsSend(out CarLookData look)
	{
		if (!TryCaptureLocal(out look)) return false;
		if (_dirty || !look.Equals(_lastSent))
		{
			_lastSent = look;
			_dirty = false;
			return true;
		}
		return false;
	}

	public static void RememberSent(CarLookData look)
	{
		_lastSent = look;
		_dirty = false;
	}

	public static void StorePending(ulong peerId, CarLookData look) => Pending[peerId] = look;

	public static bool TryGetPending(ulong peerId, out CarLookData look) => Pending.TryGetValue(peerId, out look);

	public static void ApplyToController(CarSkinController? csc, CarLookData look)
	{
		if (csc == null) return;
		try
		{
			CarSkin? baseSkin = ResolveAsset(look.AssetName);
			if (baseSkin == null)
				baseSkin = ResolveChassisSkin(look.ChassisName);

			CarSkin dressed;
			if (baseSkin != null)
			{
				dressed = UnityEngine.Object.Instantiate(baseSkin);
			}
			else
			{
				dressed = ScriptableObject.CreateInstance<CarSkin>();
			}

			int modelCount = csc.carModels != null ? csc.carModels.Length : 1;
			if (modelCount <= 0) modelCount = 1;
			dressed.skinIndex = Mathf.Clamp(look.SkinIndex, 0, modelCount - 1);
			dressed.color1 = Opaque(look.Color1);
			dressed.color2 = Opaque(look.Color2);
			dressed.color3 = Opaque(look.Color3);
			csc.ApplySkin(dressed);
			Plugin.Log.LogInfo(
				$"CarLook apply on {csc.name}: idx={dressed.skinIndex} asset={look.AssetName} chassis={look.ChassisName}");
		}
		catch (Exception ex)
		{
			Plugin.Log.LogWarning($"CarLook apply failed: {ex.Message}");
		}
	}

	private static CarSkin? ResolveAsset(string name)
	{
		if (string.IsNullOrWhiteSpace(name)) return null;
		foreach (var skin in Resources.FindObjectsOfTypeAll<CarSkin>())
		{
			if (skin == null) continue;
			var n = skin.name.Replace("(Clone)", "").Trim();
			if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase))
				return skin;
		}
		return null;
	}

	private static CarSkin? ResolveChassisSkin(string chassisName)
	{
		if (string.IsNullOrWhiteSpace(chassisName)) return null;
		foreach (var up in Resources.FindObjectsOfTypeAll<ChassisUpgrade>())
		{
			if (up == null) continue;
			if (string.Equals(up.name, chassisName, StringComparison.OrdinalIgnoreCase) && up.carSkin != null)
				return up.carSkin;
		}
		return null;
	}

	private static CarControllerNew? FindLocalPlayerCar()
	{
		foreach (var c in UnityEngine.Object.FindObjectsByType<CarControllerNew>(FindObjectsSortMode.None))
			if (c != null && c.isPlayer && c.isActiveAndEnabled) return c;
		var tm = UnityEngine.Object.FindFirstObjectByType<TrackManager>();
		return tm != null ? tm.playerCarController : null;
	}

	private static Color Opaque(Color c)
	{
		c.a = 1f;
		return c;
	}
}

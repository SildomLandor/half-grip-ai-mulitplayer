using UnityEngine;

namespace HalfGripMultiplayer;

internal enum RaceMode : byte
{
	Standard = 0,
	TimeAttack = 1,
	LastMan = 2,
	AssassinateCoop = 3,
}

/// <summary>Host-authored lobby rules mirrored to guests via RaceSetup trailing bytes.</summary>
internal static class LobbyRules
{
	public static RaceMode Mode { get; set; } = RaceMode.Standard;
	public static bool NoGuns { get; set; }
	public static bool SharedShop { get; set; }
	/// <summary>0 = use map default ceasefire.</summary>
	public static float CeasefireOverride { get; set; }
	public static bool ShortRaceToggle { get; set; }

	public static float SharedPool { get; set; }

	public static string ModeLabel => Mode switch
	{
		RaceMode.TimeAttack => MpLoc.T("TIME ATTACK"),
		RaceMode.LastMan => MpLoc.T("LAST MAN"),
		RaceMode.AssassinateCoop => MpLoc.T("ASSASSINATE CO-OP"),
		_ => MpLoc.T("STANDARD"),
	};

	public static void ApplyToRaceSync()
	{
		if (CeasefireOverride > 0.05f)
			RaceSync.ForceCeasefire(CeasefireOverride);
		if (ShortRaceToggle)
			RaceSync.ForceShortRace(true);
		if (Mode == RaceMode.TimeAttack)
		{
			RaceSync.FreePlay = true; // no cutoff pressure; personal finish still counts
		}
		if (Mode == RaceMode.AssassinateCoop)
		{
			RaceSync.ForceObjectiveAssassinate();
		}
	}

	public static void ResetSession()
	{
		Mode = RaceMode.Standard;
		NoGuns = false;
		SharedShop = false;
		CeasefireOverride = 0f;
		ShortRaceToggle = false;
		SharedPool = 0f;
	}
}

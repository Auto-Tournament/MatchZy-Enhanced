using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;

namespace AutoTournamentCS2;

public static class PawnExtensions
{
    public static void ResetNoclipToWalk(this CBasePlayerPawn? pawn)
    {
        if (pawn == null) return;
        if (pawn.MoveType != MoveType_t.MOVETYPE_NOCLIP) return;

        pawn.MoveType = MoveType_t.MOVETYPE_WALK;
        pawn.ActualMoveType = MoveType_t.MOVETYPE_WALK;
        Utilities.SetStateChanged(pawn, "CBaseEntity", "m_MoveType");
    }

    public static void TeleportKeepingModelUpright(this CBasePlayerPawn? pawn, Vector? position, QAngle? angle, Vector? velocity)
    {
        if (pawn == null) return;

        pawn.Teleport(position, angle, velocity);
        if (angle == null) return;

        var sceneNode = pawn.CBodyComponent?.SceneNode;
        if (sceneNode == null) return;

        sceneNode.Rotation.X = 0f;
        sceneNode.Rotation.Y = angle.Y;
        sceneNode.Rotation.Z = 0f;
        sceneNode.AbsRotation.X = 0f;
        sceneNode.AbsRotation.Y = angle.Y;
        sceneNode.AbsRotation.Z = 0f;
        Utilities.SetStateChanged(pawn, "CBaseEntity", "m_CBodyComponent");
    }
}

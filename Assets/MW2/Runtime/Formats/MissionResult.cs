using System;
using System.Collections.Generic;
using System.Text;

namespace MW2.Formats
{
    /// <summary>
    /// One objective record of the player's team as FUN_100424dd 0x100424dd copies it: the objective state record
    /// (DAT_102007ba + team * 0x3c0a + index * 0x13f) gives the state (5 = complete), the screen group +1 (MTBL +0x2e),
    /// the start / end seconds +0x67 / +0x6b (-1 until set by Objective_Evaluate 0x1004154a), the 'M' flag +0x75 and the
    /// text +0xad (MTBL +0x57). Only records with the 'V' flag +0x74 (MTBL +4) are written.
    /// </summary>
    public sealed class MissionObjectiveResult
    {
        public bool Visible, Complete, Mandatory;
        public int Group, Started = -1, Finished = -1;
        public string Text = "";
    }

    /// <summary>
    /// MW2MSN.CFG, the engine's mission report (FUN_100424dd 0x100424dd, called by SimMain after "Calling EndTheMission"):
    /// 0x9d4 zeroed bytes, +0 "MW2M", +4 number of records, +8 / +0xc the team's start and end seconds (team +4 / +8,
    /// DAT_10200784 / DAT_10200788), +0x10 the team state byte +0x38 (0 running, 2 success, 3 failure, 4 time exceeded),
    /// then 0x34-byte records from +0x14: u32 complete, u32 group, u32 start, u32 end, u32 mandatory, text from +0x14 by
    /// strcpy (a text longer than 0x1f bytes runs into the next record, which then overwrites it).
    /// </summary>
    public static class MissionReportFile
    {
        public const int Size = 0x9d4;
        public const int MaxRecords = (Size - 0x14) / 0x34;

        public static byte[] Write(int teamStart, int teamEnd, int teamState, IEnumerable<MissionObjectiveResult> objectives)
        {
            if (objectives == null) throw new ArgumentNullException(nameof(objectives));
            var d = new byte[Size];
            Encoding.ASCII.GetBytes("MW2M", 0, 4, d, 0);
            Put(d, 8, teamStart);
            Put(d, 0xc, teamEnd);
            Put(d, 0x10, teamState & 0xff);
            int n = 0;
            foreach (var o in objectives)
            {
                if (!o.Visible) continue;
                if (n >= MaxRecords) break;       // the original's stack buffer holds 48 records
                int p = 0x14 + n * 0x34;
                Put(d, p, o.Complete ? 1 : 0);
                Put(d, p + 4, o.Group & 0xff);
                Put(d, p + 8, o.Started);
                Put(d, p + 0xc, o.Finished);
                Put(d, p + 0x10, o.Mandatory ? 1 : 0);
                var text = Encoding.ASCII.GetBytes(o.Text ?? "");
                int len = Math.Min(text.Length, Size - (p + 0x14) - 1);
                Buffer.BlockCopy(text, 0, d, p + 0x14, len);
                d[p + 0x14 + len] = 0;
                n++;
            }
            Put(d, 4, n);
            return d;
        }

        static void Put(byte[] d, int o, int v)
        {
            d[o] = (byte)v; d[o + 1] = (byte)(v >> 8); d[o + 2] = (byte)(v >> 16); d[o + 3] = (byte)(v >> 24);
        }
    }

    /// <summary>
    /// MW2CAR.CFG, the mission statistics block DAT_101fd6e0 (0xd6 bytes, zeroed by FirstShots 0x10010c60, written by FUN_10024c87 0x10024c87; the shell reads the first 0x50 bytes into career +0x2e0f).
    /// The u16 counters keep the names of the shell's debug listing FUN_1001deaf 0x1001deaf; the conditions are those of
    /// the engine code that increments them (the "star" counters of units are counted for every destroyed unit whose
    /// GP flags carry 0x1400, whoever destroyed it, Mech_Destroy 0x10035ef6 at 0x1003625b).
    /// Shots and hits (+0x13..+0x1b, +0x2a..+0x32: Ams_Fire 0x1000ce3f, Shot_Move 0x10010f40, Weapon_FireProjectile
    /// 0x1006c554) are not counted yet: only that debug listing reads them.
    /// </summary>
    public sealed class CombatCareer
    {
        public const int Size = 0xd6;
        public const int ShellSize = 0x50;

        // Destroyed by the player (Mech_Destroy, killer == player): mechs (GP +0 == 1) / other gamepieces by side.
        public const int EnemyMechKills = 0x07, NeutralMechKills = 0x09, FriendlyMechKills = 0x0b;
        // Structures destroyed by the player (FUN_10012e9d 0x10012e9d) by Gt_Side.
        public const int EnemyBldgKills = 0x0d, NeutralBldgKills = 0x0f, FriendlyBldgKills = 0x11;
        public const int ShotsFired = 0x13, ShotsHitEnemy = 0x15, ShotsHitNeutral = 0x17, ShotsHitFriendly = 0x19, HitsTaken = 0x1b;
        /// <summary>Byte +0x1d: 1 at the start (SimMain 0x100465ae), 2 the player ejected, 4 the player was destroyed.</summary>
        public const int Outcome = 0x1d;
        public const int StarEnemyMechKills = 0x1e, StarNeutralMechKills = 0x20, StarFriendlyMechKills = 0x22;
        // Structures destroyed by any member of the player's team.
        public const int StarEnemyBldgKills = 0x24, StarNeutralBldgKills = 0x26, StarFriendlyBldgKills = 0x28;
        public const int StarShotsFired = 0x2a;
        /// <summary>Units of the player's team destroyed (+0x34) or ejected (state 5, +0x36).</summary>
        public const int StarMembersKilled = 0x34, StarMembersEjected = 0x36;
        // AfterWorldLoader 0x100739f9: every gamepiece and every structure by side.
        public const int TotalFriendlyMechs = 0x38, TotalNeutralMechs = 0x3a, TotalEnemyMechs = 0x3c;
        public const int TotalFriendlyBldgs = 0x3e, TotalNeutralBldgs = 0x40, TotalEnemyBldgs = 0x42;
        public const int EnemyOtherKills = 0x44, NeutralOtherKills = 0x46, FriendlyOtherKills = 0x48;
        public const int StarEnemyOtherKills = 0x4a, StarNeutralOtherKills = 0x4c, StarFriendlyOtherKills = 0x4e;
        /// <summary>u16 +0x50: number of gamepieces, set only by FirstNetwork 0x10020376 (DAT_100a6634); 0 in single play.</summary>
        public const int GamepieceCount = 0x50;

        public readonly byte[] Data = new byte[Size];

        public CombatCareer() { Data[Outcome] = 1; }

        public int this[int offset] => offset == Outcome ? Data[offset] : BitConverter.ToUInt16(Data, offset);

        public void Add(int offset)
        {
            int v = BitConverter.ToUInt16(Data, offset) + 1;
            Data[offset] = (byte)v; Data[offset + 1] = (byte)(v >> 8);
        }

        public void Set16(int offset, int value)
        {
            Data[offset] = (byte)value; Data[offset + 1] = (byte)(value >> 8);
        }

        /// <summary>Pick the counter of a side: 0 player's side, 1 enemy, 2 neutral (Gp_Side 0x1002a7c1 / Gt_Side 0x1002a7dc);
        /// another side counts nothing (-1).</summary>
        public static int BySide(int side, int friendly, int enemy, int neutral) =>
            side == 0 ? friendly : side == 1 ? enemy : side == 2 ? neutral : -1;

        public void AddBySide(int side, int friendly, int enemy, int neutral)
        {
            int o = BySide(side, friendly, enemy, neutral);
            if (o >= 0) Add(o);
        }
    }
}

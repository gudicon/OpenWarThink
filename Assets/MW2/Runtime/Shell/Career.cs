using System;
using System.Collections.Generic;
using System.IO;
using MW2.Formats;

namespace MW2.Shell
{
    /// <summary>
    /// The shell's career state DAT_100d3d58 (0x2e63 bytes, kept byte for byte) and the record lists it owns. Saved and loaded as
    /// SHELLMER.CFG / mersave%d.sav (FUN_1006d093 0x1006d093 / FUN_1006ddf6 0x1006ddf6): the state, the salvage file when +0x156
    /// is set, market mechs and their copies, hire pilots, hire techs, dealer weapons + equipment, dealer ammunition, unit mechs
    /// and their copies, unit pilots, unit techs, unit weapons + equipment, unit ammunition, then a dword 0xff.
    /// </summary>
    public sealed class Career : ShellRecord
    {
        public sealed class LaunchEntry
        {
            public int Slot;
            public string MekName, PilotName, Model;
            public byte[] MekData;
            public bool Technician;
        }
        public const int Size = 0x2e63;

        // Saved lists (counts in the state at +0x142, +0x146, +0x14a, +0x14e, +0x152, +0x12e, +0x132, +0x136, +0x13a, +0x13e).
        public readonly List<MechRecord> MarketMechs = new List<MechRecord>(), MarketCopies = new List<MechRecord>();   // 0x100cd11c / 120
        public readonly List<PilotRecord> HirePilots = new List<PilotRecord>();     // 0x100cd164
        public readonly List<TechRecord> HireTechs = new List<TechRecord>();        // 0x100cd168
        public readonly List<ItemRecord> DealerWeapons = new List<ItemRecord>(), DealerAmmo = new List<ItemRecord>();   // 0x100cd14c / 150
        public readonly List<MechRecord> UnitMechs = new List<MechRecord>(), UnitCopies = new List<MechRecord>();       // 0x100cd118 / 10c
        public readonly List<PilotRecord> UnitPilots = new List<PilotRecord>();     // 0x100cd124
        public readonly List<TechRecord> UnitTechs = new List<TechRecord>();        // 0x100cd128
        public readonly List<ItemRecord> UnitWeapons = new List<ItemRecord>(), UnitAmmo = new List<ItemRecord>();       // 0x100cd134 / 138
        // Not saved: items and mechs a step adds (SWNL / SMHL, sorted into the unit by Career_New or the salvage screen).
        public readonly List<ItemRecord> StepItems = new List<ItemRecord>();        // 0x100cd12c
        public readonly List<MechRecord> StepMechs = new List<MechRecord>();        // 0x100cd130
        /// <summary>salvage.mw2 bytes carried by a save (+0x156).</summary>
        public byte[] SalvageFile;

        /// <summary>MW2SHELL.DLL FUN_1001c67e 0x1001c67e: copy the engine's 0x9d4-byte
        /// MW2MSN.CFG report into state +0x243b before saving the career.</summary>
        public void LoadMissionReport(ShellMissionReport report)
        {
            if (report == null) throw new ArgumentNullException(nameof(report));
            Buffer.BlockCopy(report.Data, 0, Data, 0x243b, ShellMissionReport.Size);
        }

        /// <summary>
        /// The shell's restart after a mission ("sim" on the command line, ShellMain 0x100428d3): Save_Read 0x1006ddf6 of
        /// SHELLMER.CFG with param 0 gives every unit mech whose MEK was written for the launch (+0x9cb) the mission's
        /// damage from that MEK (Mech_FromMek 0x10047700, then FUN_10048fa8) and clears the flag; then state +0 = 1 and
        /// +0x239a / +0x239b = 0. <paramref name="writtenMek"/> returns the MEK the engine wrote back, or null when there
        /// is none (the original then only clears the flag). Returns the message ShellMain posts: 0xc029 in mode 2,
        /// otherwise 0xc00a, also stored in +0x23bf / +0x23c3 / +0x23c7 / +0x23cb.
        /// </summary>
        public uint ReturnFromMission(Func<string, MechRecord> writtenMek, ShellTables tables)
        {
            foreach (var mech in UnitMechs)
            {
                if (!mech.Custom) continue;
                var damaged = writtenMek?.Invoke(mech.MekName);
                if (damaged != null) mech.MergeMissionDamage(damaged, tables);
                mech.Custom = false;
            }
            Started = true;
            Data[0x239a] = 0;
            Data[0x239b] = 0;
            uint msg = Mode == 2 ? 0xc029u : 0xc00au;
            Msg23BF = Msg23C3 = Msg23C7 = Msg23CB = msg;
            return msg;
        }

        /// <summary>MW2SHELL.DLL FUN_1001ede1 0x1001ede1: after the engine returns,
        /// copy the first 0x50 bytes of MW2CAR.CFG into state +0x2e0f.</summary>
        public void LoadCombatCareer(byte[] combatCareer)
        {
            if (combatCareer == null || combatCareer.Length < 0x50)
                throw new ArgumentException("MW2CAR.CFG is shorter than the shell's 0x50-byte block", nameof(combatCareer));
            Buffer.BlockCopy(combatCareer, 0, Data, 0x2e0f, 0x50);
        }

        public Career() : base(Size) { }

        // State fields.
        public bool Started { get => Data[0] != 0; set => Data[0] = (byte)(value ? 1 : 0); }
        public bool Registered { get => Data[1] != 0; set => Data[1] = (byte)(value ? 1 : 0); }
        public string UnitName { get => Str(0x102, 0x20); set => SetStr(0x102, value); }
        public int Year { get => I32(0x122); set => SetI32(0x122, value); }
        public int Month { get => I32(0x126); set => SetI32(0x126, value); }
        public int Funds { get => I32(0x12a); set => SetI32(0x12a, value); }
        public int NextIdCounter { get => I32(0x15e); set => SetI32(0x15e, value); }
        public int DropShipSlots { get => I32(0x2e5f); set => SetI32(0x2e5f, value); }
        public int RandomSeed { get => I32(0x162); set => SetI32(0x162, value); }
        public string Mission { get => Str(0x166, 0x10); set => SetStr(0x166, value); }
        public string NewsFlic => Str(0x176, 0x10);
        public string NewsBmp => Str(0x186, 0x10);
        public string Starmap => Str(0x196, 0x10);
        public string Employer => Str(0x1c6, 0x10);
        public string AbortCampaign => Str(0x1d6, 0x10);
        public int SuccessPay => I32(0x1e6);
        public int FailPay => I32(0x1ea);
        public int TargetPay => I32(0x1ee);
        public int BonusPay => I32(0x1f2);
        public int BountyPay => I32(0x1f6);
        public int ExpenseMultiplier => I32(0x1fa);
        public int StepType { get => I32(0x1fe); set => SetI32(0x1fe, value); }
        public int SuccessCount => I32(0x2392);
        public int FailCount => I32(0x2396);
        public string SuccessStep(int i) => Str(0x202 + i * 0x10, 0x10);
        public string FailStep(int i) => Str(0x2a2 + i * 0x10, 0x10);
        public string News => Str(0x342, 0x1000);
        public string Contract => Str(0x1342, 0x1000);
        public bool Outcome239C { get => Data[0x239c] != 0; set => Data[0x239c] = (byte)(value ? 1 : 0); }
        public bool Finished239D { get => Data[0x239d] != 0; set => Data[0x239d] = (byte)(value ? 1 : 0); }
        public int NextChoice { get => I32(0x239e); set => SetI32(0x239e, value); }
        /// <summary>
        /// The accept spot of FUN_1000b5e5 0x1000b5e5: +0x239e the offer, +0x2433 whether it was generated for the
        /// story's end (+0x242f), and for an offer with a seed the seed into +0x162 and, when its step is a RAN step, the
        /// fourth letter's number into +0x23cf (the mission number a loaded game regenerates).
        /// </summary>
        public bool AcceptContract(int selected, List<ContractOffers.Offer> offers)
        {
            if (!Finished239D || selected < 0 || selected >= offers.Count) return false;
            Finished239D = false;
            NextChoice = selected;
            SetI32(0x2433, offers[selected].Generated ? 1 : 0);
            if (offers[selected].Seed >= 0)
            {
                SetI32(0x162, offers[selected].Seed);
                string step = (Outcome239C ? SuccessStep(selected) : FailStep(selected)).ToUpperInvariant();
                if (step.Contains("RAN")) RanNumber = Atoi(step.Length > 3 ? step.Substring(3, 1) : "");
            }
            Started = true;
            return true;
        }

        /// <summary>+0x23cf: the mission number of the accepted RAN contract.</summary>
        public int RanNumber { get => I32(0x23cf); set => SetI32(0x23cf, value); }
        /// <summary>+0x2342: the seed of each offer of the step (-1 none).</summary>
        public int OfferSeed(int i) => I32(0x2342 + i * 4);
        public void SetOfferSeed(int i, int v) => SetI32(0x2342 + i * 4, v);
        /// <summary>+0x242f: the story is over and the contracts are generated (0xc03a "continue").</summary>
        public bool Endless => I32(0x242f) != 0;

        /// <summary>
        /// FUN_1006adab 0x1006adab: the generator's parameters (mission <paramref name="number"/>, or the 4th letter of
        /// the mission +0x169 when negative; the career's year; no planet) and its seed: a new one from the tick counter
        /// DAT_100cd478 % 256000000 (into +0x162), else a rand() one, else none (0, the rand() state as it is); the files
        /// go beside the game. Returns the seed; <paramref name="result"/> is FUN_1004f490's.
        /// </summary>
        public int RunRmg(ShellData data, int number, int success, int fail, bool tickSeed, bool newSeed, int tick, out int result)
        {
            var p = new RandomMissionParams();
            if (number < 0) number = Atoi(Mission.Length > 3 ? Mission.Substring(3, 1) : "");
            p.Number = number;
            p.SuccessPay = success;
            p.FailPay = fail;
            p.Date = Year;
            uint seed = 0;
            if (tickSeed)
            {
                SetI32(0x162, tick % 256000000);
                seed = (uint)I32(0x162);
                data.Random.Srand(seed);
            }
            else if (newSeed)
            {
                seed = (uint)data.Random.Rand();
                data.Random.Srand(seed);
            }
            p.Seed = seed;
            var gen = new RandomMissionGenerator(data.Tables, data.Random.Rand, data.ReadRmg);
            result = gen.Generate(p);
            foreach (var kv in gen.Files) data.Files[kv.Key] = kv.Value;
            data.RmgLog = gen.Log.ToString();
            return (int)seed;
        }

        /// <summary>
        /// Campaign_LoadRnd 0x1006b689: a RAN step's generated contract RND&lt;n&gt;CAM1 (beside the game): its CPGN
        /// scene (+0xc) becomes the mission +0x166, its CTNT the contract +0x1342.
        /// </summary>
        public void LoadRnd(string name, ShellData data)
        {
            byte[] d = data.ReadLoose("RND" + (name.Length > 3 ? name.Substring(3) : ""));
            if (d == null) return;
            int p = 0xc, end = BitConverter.ToInt32(d, 4);
            while (p + 8 <= end && p + 8 <= d.Length)
            {
                string tag = System.Text.Encoding.ASCII.GetString(d, p, 4);
                int length = BitConverter.ToInt32(d, p + 4);
                if (tag == "CPGN")
                {
                    int e = p + 0x14;
                    while (e < d.Length && d[e] != 0) e++;
                    SetStr(0x166, System.Text.Encoding.ASCII.GetString(d, p + 0x14, e - p - 0x14));
                    p += 0xa0;
                    continue;
                }
                if (tag == "CTNT")
                {
                    int e = p + 8;
                    while (e < p + length && e < d.Length && d[e] != 0) e++;
                    SetStr(0x1342, System.Text.Encoding.ASCII.GetString(d, p + 8, e - p - 8));
                }
                if (length < 8) break;
                p += length;
            }
        }

        /// <summary>FUN_1006ad2f 0x1006ad2f: for a RAN step ran&lt;n&gt;wld1.bwd becomes randwld1.bwd (the scene includes that).</summary>
        public static void RenameWorld(string name, ShellData data)
        {
            if (!name.ToUpperInvariant().Contains("RAN")) return;
            data.Files.Remove("randwld1.bwd");
            string from = $"ran{Atoi(name.Length > 3 ? name.Substring(3, 1) : "")}wld1.bwd";
            if (data.Files.TryGetValue(from, out var d))
            {
                data.Files.Remove(from);
                data.Files["randwld1.bwd"] = d;
            }
        }
        public bool Byte23A2 { get => Data[0x23a2] != 0; set => Data[0x23a2] = (byte)(value ? 1 : 0); }
        public bool Byte23B7 { get => Data[0x23b7] != 0; set => Data[0x23b7] = (byte)(value ? 1 : 0); }
        public bool Byte23B8 { get => Data[0x23b8] != 0; set => Data[0x23b8] = (byte)(value ? 1 : 0); }
        public bool Byte23B9 { get => Data[0x23b9] != 0; set => Data[0x23b9] = (byte)(value ? 1 : 0); }
        public bool Byte23BA { get => Data[0x23ba] != 0; set => Data[0x23ba] = (byte)(value ? 1 : 0); }
        /// <summary>+0x23bb: 0 novice (NOVICE), 1 regular (CAMP), 2 instant action (CAMP, ran0scn1).</summary>
        public int Mode { get => I32(0x23bb); set => SetI32(0x23bb, value); }
        public uint Msg23BF { get => (uint)I32(0x23bf); set => SetI32(0x23bf, (int)value); }
        public uint Msg23C3 { get => (uint)I32(0x23c3); set => SetI32(0x23c3, (int)value); }
        public uint Msg23C7 { get => (uint)I32(0x23c7); set => SetI32(0x23c7, (int)value); }
        public uint Msg23CB { get => (uint)I32(0x23cb); set => SetI32(0x23cb, (int)value); }

        /// <summary>FUN_10048d73 0x10048d73: the next mech / pilot id.</summary>
        public int NextId()
        {
            int v = NextIdCounter;
            NextIdCounter = v + 1;
            return v;
        }

        /// <summary>The shell's start-up state (ShellMain): +0x239c = 1, the screen messages 0xc02b.</summary>
        public static Career Initial()
        {
            var c = new Career();
            c.Outcome239C = true;
            c.Msg23C3 = c.Msg23C7 = c.Msg23CB = 0xc02b;
            return c;
        }

        /// <summary>
        /// Career_New 0x1006cbb4 (SHELLMER.CFG is deleted by the shell here): state +0x239c = 1, +0x23bb = mode, +0x2e5f = 3, the
        /// twenty dwords at +0x2342 = -1, then the mode's first step: NOVICE; CAMP with funds = success pay, monthly costs
        /// 10000 / 2000 / 1000 / 500 / 720 and the step's items and mechs moved into the unit; CAMP with mission ran0scn1.
        /// </summary>
        public static Career New(int mode, ShellData data)
        {
            var c = new Career();
            c.Outcome239C = true;
            c.Mode = mode;
            c.SetI32(0x2e5f, 3);
            for (int i = 0; i < 0x14; i++) c.SetI32(0x2342 + i * 4, -1);
            if (mode == 0)
            {
                c.LoadStep("NOVICE", data);
                c.Funds = 0;
            }
            else if (mode == 1)
            {
                c.LoadStep("CAMP", data);
                c.Funds = c.SuccessPay;
                c.SetI32(0x23a3, 10000); c.SetI32(0x23a7, 2000); c.SetI32(0x23ab, 1000); c.SetI32(0x23af, 500); c.SetI32(0x23b3, 0x2d0);
                foreach (var item in c.StepItems) (item.Kind == 1 ? c.UnitAmmo : c.UnitWeapons).Add(item);
                c.StepItems.Clear();
                c.MoveStepMechsToUnit();
                c.Finished239D = true;
                c.Byte23A2 = false;
            }
            else if (mode == 2)
            {
                c.LoadStep("CAMP", data);
                c.Mission = "ran0scn1";
                c.MoveStepMechsToUnit();
            }
            return c;
        }

        /// <summary>
        /// Register.cpp FUN_1005b744 0x1005b744: create the player pilot, assign its id, register the unit and move the
        /// novice's available people, mechs and items into the unit. The registration screen uppercases both entered names
        /// with FUN_1000739d before calling this function; this method receives those already edited strings.
        /// Returns the next screen message: terminal 0xc026 for novice/regular, instant action 0xc029 for mode 2.
        /// </summary>
        public uint Register(string pilotName, string unitName)
        {
            Registered = true;
            Started = true;
            var player = new PilotRecord();
            player.SetStr(0, (pilotName ?? "").ToUpperInvariant());
            player.MechId = -1;
            player.Id = NextId();
            for (int k = 0; k < 8; k++) player.SetI16(0x28 + k * 2, -1);
            player.Data[0x2064] = 1;
            UnitPilots.Add(player);
            UnitName = (unitName ?? "").ToUpperInvariant();

            uint next = 0xc026;
            if (Mode == 0)
            {
                TransferNoviceInventory();
            }
            else if (Mode == 1 && UnitMechs.Count > 0)
            {
                // The regular career puts the player in the first mech of the unit.
                var mech = UnitMechs[0];
                mech.PilotId = player.Id;
                player.MechId = mech.MechId;
            }
            else if (Mode == 2) next = 0xc029;

            foreach (var item in UnitWeapons) item.Field16 = 0;
            foreach (var item in UnitAmmo) item.Field16 = 0;
            return next;
        }

        /// <summary>ShellWindowProc 0x100408c7, message 0xc016: when entering mission command from the hangar,
        /// load the chosen success or failure campaign step. Novice mode transfers the newly offered people,
        /// mechs and items into the unit. Returning from 0xc036/0xc02e skips this advance.</summary>
        public bool AdvanceAtMissionCommand(ShellData data, uint previous)
        {
            if (previous == 0xc02d || previous == 0xc036 || previous == 0xc02e) return false;
            if (!((Mode == 1 && !Byte23A2) || Mode == 0)) return false;
            int choice = NextChoice;
            if (choice < 0 || choice >= 10) return false;
            string step = Outcome239C ? SuccessStep(choice) : FailStep(choice);
            if (string.IsNullOrEmpty(step)) return false;
            RenameWorld(step, data);        // FUN_1006ad2f before Campaign_Load
            if (!LoadStep(step, data)) return false;
            if (Mode == 0)
            {
                TransferNoviceInventory();
            }
            return true;
        }

        /// <summary>FUN_10049399 0x10049399, FUN_10049411 0x10049411, FUN_100495af 0x100495af:
        /// novice careers receive all people, mechs and items from the market; spent unit mechs are discarded.</summary>
        void TransferNoviceInventory()
        {
            foreach (var pilot in HirePilots) { pilot.Id = NextId(); UnitPilots.Add(pilot); }
            HirePilots.Clear();
            var keptMechs = new List<MechRecord>();
            var keptCopies = new List<MechRecord>();
            foreach (var mech in UnitMechs)
                if (mech.I32(0x994) > 0)
                {
                    keptMechs.Add(mech);
                    foreach (var copy in UnitCopies)
                        if (copy.MechId == mech.MechId) { keptCopies.Add(copy); break; }
                }
            UnitMechs.Clear(); UnitCopies.Clear();
            UnitMechs.AddRange(keptMechs); UnitCopies.AddRange(keptCopies);
            foreach (var mech in MarketMechs) { UnitMechs.Add(mech); UnitCopies.Add(mech.Clone()); }
            MarketMechs.Clear(); MarketCopies.Clear();
            foreach (var item in DealerWeapons) { item.Field16 = 0; UnitWeapons.Add(item); }
            foreach (var item in DealerAmmo) { item.Field16 = 0; UnitAmmo.Add(item); }
            DealerWeapons.Clear(); DealerAmmo.Clear();
        }

        /// <summary>FUN_10023f12 0x10023f12: both abort-step and first failure-step must permit aborting.</summary>
        public bool CanAbortCampaign() =>
            !string.Equals(AbortCampaign, "DEATH", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(FailStep(0), "DEATH", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// FUN_10023f12 0x10023f12 after confirmation: load the abort step, discard outstanding step awards and
        /// reset the outcome flags. Returns landing flic 0xc034 when movies are on, otherwise the hangar 0xc01c.
        /// </summary>
        public uint Abort(ShellData data, bool moviesOn)
        {
            if (!CanAbortCampaign() || !LoadStep(AbortCampaign, data)) return 0xc004;
            StepMechs.Clear();
            StepItems.Clear();
            Finished239D = true;
            Outcome239C = false;
            Byte23A2 = false;
            Byte23B8 = false;
            return moviesOn ? 0xc034u : 0xc01cu;
        }

        /// <summary>
        /// FUN_100202e9 0x100202e9 after a mission: a regular career with no remaining mech and outcome other than 4
        /// cannot continue. DEATH returns to title; otherwise it loads the abort step and returns to the hangar.
        /// Zero means that the career may proceed. The Win32 warning dialog is handled by the calling screen.
        /// </summary>
        public uint ResolveDestroyedUnit(int missionOutcome, ShellData data)
        {
            if (Mode != 1 || missionOutcome == 4 || StepType == 4 || UnitMechs.Count != 0) return 0;
            if (string.Equals(AbortCampaign, "DEATH", StringComparison.OrdinalIgnoreCase)) return 0xc02b;
            if (!LoadStep(AbortCampaign, data)) return 0;
            Finished239D = true;
            Outcome239C = false;
            Byte23A2 = false;
            return 0xc01c;
        }

        /// <summary>
        /// Career_End FUN_1001ec9a 0x1001ec9a: +0x1fe becomes 4; novice success plays mend, regular careers choose
        /// mpoorw below 15,000,000 C-bills of cash plus unit assets (FUN_1004a8f8), otherwise mrich.
        /// </summary>
        public string EndingFlic(ShellTables tables)
        {
            StepType = 4;
            if (Mode == 0) return Outcome239C ? "mend" : null;
            if (Mode == 1) return Funds + ShellEconomy.UnitAssets(this, tables) < 15000000 ? "mpoorw" : "mrich";
            return null;
        }

        /// <summary>
        /// FUN_10024da9 0x10024da9 (step +1) / FUN_10024efb 0x10024efb (step -1): the pilot takes the next / previous
        /// free unit mech after his own; the placeholder "no mech" record DAT_100cb510, kept after the unit's mechs while
        /// the duty roster is open, leaves him without one. False (nothing changes) when he has no mech and none is free.
        /// <paramref name="chosen"/> is the new mech, null for the placeholder.
        /// </summary>
        public bool CycleMech(PilotRecord pilot, int step, out MechRecord chosen)
        {
            chosen = null;
            int free = 0;
            foreach (var m in UnitMechs) if (m.PilotId == -1) free++;
            if (pilot.MechId == -1 && free == 0) return false;
            Started = true;
            var current = UnitMechs.Find(m => m.PilotId == pilot.Id);
            int n = UnitMechs.Count + 1;
            int index = current != null ? UnitMechs.IndexOf(current) : UnitMechs.Count;
            while (true)
            {
                index = ((index + step) % n + n) % n;
                if (index == UnitMechs.Count)
                {
                    pilot.MechId = -1;
                    if (current != null) current.PilotId = -1;
                    return true;
                }
                var m = UnitMechs[index];
                if (m.PilotId != -1) continue;
                m.PilotId = pilot.Id;
                pilot.MechId = m.MechId;
                if (current != null) current.PilotId = -1;
                chosen = m;
                return true;
            }
        }

        /// <summary>FUN_1002ce0e 0x1002ce0e, hiring a MechWarrior: only below 8 pilots; a new id (Career_NextId), no mech.</summary>
        public bool HirePilot(int index)
        {
            if (UnitPilots.Count >= 8) return false;
            var p = HirePilots[index];
            p.Id = NextId();
            p.MechId = -1;
            HirePilots.Remove(p);
            UnitPilots.Add(p);
            return true;
        }

        /// <summary>FUN_1002ce0e: firing a MechWarrior frees his mech (+0x98c) and returns him to the hire list.</summary>
        public void FirePilot(int index)
        {
            var p = UnitPilots[index];
            if (p.MechId != -1)
            {
                var mech = UnitMechs.Find(m => m.PilotId == p.Id);
                if (mech != null) mech.PilotId = -1;
                p.MechId = -1;
            }
            UnitPilots.Remove(p);
            HirePilots.Add(p);
        }

        /// <summary>FUN_1002ce0e, hiring an AeroTech pilot: the drop ship carries only one; a new id at +0x20.</summary>
        public bool HireTech(int index)
        {
            if (UnitTechs.Count >= 1) return false;
            var t = HireTechs[index];
            t.SetI32(0x20, NextId());
            HireTechs.Remove(t);
            UnitTechs.Add(t);
            return true;
        }

        public void FireTech(int index)
        {
            var t = UnitTechs[index];
            UnitTechs.Remove(t);
            HireTechs.Add(t);
        }

        /// <summary>
        /// Unit_Expenses 0x1001b6ff: (maintenance +0x23a3 x unit mechs + 1 + technicians +0x23a7 x unit mechs + travel
        /// +0x23ab + broker +0x23af + guild +0x23b3 + wingmen's salaries (+0x3c, FUN_1001b632) + technicians' salaries
        /// (+0x24, FUN_1001b6a0)) x the step's multiplier +0x1fa; paid from the funds when <paramref name="deduct"/>.
        /// </summary>
        public int UnitExpenses(bool deduct)
        {
            int pilots = 0, techs = 0;
            foreach (var p in UnitPilots) if (!p.Player) pilots += p.I32(0x3c);
            foreach (var t in UnitTechs) techs += t.I32(0x24);
            int total = (I32(0x23a3) * UnitMechs.Count + 1 + I32(0x23a7) * UnitMechs.Count + I32(0x23ab) + I32(0x23af) +
                         I32(0x23b3) + pilots + techs) * ExpenseMultiplier;
            if (deduct) Funds -= total;
            return total;
        }

        /// <summary>FUN_1004a32f 0x1004a32f: the player pilot must have a mech before the briefing or customization.</summary>
        public bool PlayerHasMech()
        {
            foreach (var pilot in UnitPilots) if (pilot.Player && pilot.MechId != -1) return true;
            return false;
        }

        /// <summary>
        /// FUN_1002504d 0x1002504d: count assigned non-player pilots, then technicians with +0x2c = 1, but when
        /// three or more starmates have mechs the function returns capacity + 1 to reject launch outright.
        /// </summary>
        public int CrewCountForLaunch()
        {
            int count = 0;
            foreach (var pilot in UnitPilots) if (!pilot.Player && pilot.MechId != -1) count++;
            if (count >= 3) return DropShipSlots + 1;
            foreach (var tech in UnitTechs) if (tech.Data[0x2c] == 1) count++;
            return count;
        }

        /// <summary>
        /// Drop-ship setup FUN_100229fa 0x100229fa: copy the mission name, put zero at byte 4, append "brf2"
        /// (e.g. mcc1scn1 -> MCC1BRF2). An SDSC chunk sets the
        /// available slots from its fourth body dword (chunk +0x14). A BWD without SDSC sets four; no BWD keeps the
        /// current capacity (three after Career_New).
        /// </summary>
        public void LoadBriefingCapacity(ShellData data)
        {
            string prefix = Mission.Length > 4 ? Mission.Substring(0, 4) : Mission;
            byte[] bwd = data.ReadBwd(prefix + "brf2");
            if (bwd == null) return;
            foreach (var (tag, body, length) in BwdChunks.Enumerate(bwd))
                if (tag == "SDSC" && length >= 16)
                {
                    DropShipSlots = BitConverter.ToInt32(bwd, body + 12);
                    return;
                }
            DropShipSlots = 4;
        }

        /// <summary>FUN_100229fa: when the briefing has fewer slots than the current crew, drop the first tech
        /// assignment and then unassign non-player pilots and their mechs in list order until the crew fits.</summary>
        public void TrimDropShipCrew()
        {
            if (CrewCountForLaunch() <= DropShipSlots) return;
            if (UnitTechs.Count > 0) UnitTechs[0].Data[0x2c] = 0;
            while (CrewCountForLaunch() > DropShipSlots)
            {
                bool removed = false;
                foreach (var pilot in UnitPilots)
                {
                    if (pilot.Player || pilot.MechId == -1) continue;
                    foreach (var mech in UnitMechs)
                        if (mech.MechId == pilot.MechId) { mech.PilotId = -1; break; }
                    pilot.MechId = -1;
                    removed = true;
                }
                if (!removed) break;
            }
        }

        /// <summary>FUN_10024a97 0x10024a97: player in slot 0, at most two other assigned pilots,
        /// then on-duty technicians up to slot 3. This is the roster prepared before 0xc011.</summary>
        public List<LaunchEntry> BuildLaunchRoster(ShellData data)
        {
            var roster = new List<LaunchEntry>();
            int nextSlot = 1;
            foreach (var pilot in UnitPilots)
            {
                if (pilot.MechId == -1) continue;
                MechRecord mech = null;
                foreach (var candidate in UnitMechs)
                    if (candidate.MechId == pilot.MechId) { mech = candidate; break; }
                if (mech == null) continue;
                // FUN_10024a97 writes the assigned mech through FUN_10048514 before FUN_10074511 fills the launch slot.
                byte[] mekData = mech.ToMek();
                mech.Custom = true;
                int slot;
                if (pilot.Player) slot = 0;
                else
                {
                    if (nextSlot >= 3) continue;
                    slot = nextSlot++;
                }
                string mek = mech.MekName;
                int index = data.Tables.FindMech(mek.Length >= 3 ? mek.Substring(0, 3) : mek);
                roster.Add(new LaunchEntry
                {
                    Slot = slot, MekName = mek, PilotName = pilot.Name.Length > 21 ? pilot.Name.Substring(0, 21) : pilot.Name,
                    Model = index >= 0 ? data.Tables.MechModel(index) : "", MekData = mekData
                });
            }
            foreach (var tech in UnitTechs)
                if (tech.Data[0x2c] == 1 && nextSlot < 4)
                {
                    string source = tech.Str(0x4d, 0x10);
                    var mech = data.BuildMech(source, this);
                    if (mech == null) continue;
                    string mek = mech.MekName;
                    if (mek.Length >= 8) mek = mek.Substring(0, 5) + "mer";
                    byte[] mekData = mech.ToMek();
                    int index = data.Tables.FindMech(mek.Length >= 3 ? mek.Substring(0, 3) : mek);
                    roster.Add(new LaunchEntry
                    {
                        Slot = nextSlot++, MekName = mek,
                        PilotName = tech.Name.Length > 21 ? tech.Name.Substring(0, 21) : tech.Name,
                        Model = index >= 0 ? data.Tables.MechModel(index) : "", MekData = mekData, Technician = true
                    });
                }
            return roster;
        }

        /// <summary>Career_New: each step mech gets the first free unit number (FUN_10048df5 0x10048df5) and joins the unit with a copy.</summary>
        void MoveStepMechsToUnit()
        {
            while (StepMechs.Count > 0)
            {
                var m = StepMechs[0];
                StepMechs.RemoveAt(0);
                Renumber(m, UnitMechs);
                UnitMechs.Add(m);
                UnitCopies.Add(m.Clone());
            }
        }

        /// <summary>FUN_10048df5: the lowest number n not used by the list ("%s%02d%s": code, n, the name from +0x9a8).</summary>
        public static void Renumber(MechRecord m, List<MechRecord> list)
        {
            int n = 0;
            while (true)
            {
                bool used = false;
                foreach (var o in list)
                {
                    string two = o.Str(0x9a6, 2);
                    if (Atoi(two) == n) { used = true; n++; break; }
                }
                if (!used) break;
            }
            string code = m.Str(0x9a3, 3), rest = m.Str(0x9a8, 0x1b);
            m.MekName = $"{code}{n:00}{rest}";
        }

        static int Atoi(string s)
        {
            int i = 0, v = 0, sign = 1;
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
            if (i < s.Length && (s[i] == '-' || s[i] == '+')) { if (s[i] == '-') sign = -1; i++; }
            for (; i < s.Length && char.IsDigit(s[i]); i++) v = v * 10 + (s[i] - '0');
            return v * sign;
        }

        /// <summary>
        /// Campaign_Load 0x1006b879: the step header into the state and its lists into new market / dealer / hire lists (the
        /// step items and mechs accumulate). Records are built like the shell builds them: MHLT / SMHL mechs by FUN_10047700 with
        /// "MER" at +0x9a8 and ".MEK" appended, WNLT / AMMO / EQUP / SWNL items with the table prices (FUN_100268b0 /
        /// FUN_10026902), PTLT / THLT from their BWDs (FUN_1006aecc / FUN_1006b0db).
        /// </summary>
        public bool LoadStep(string name, ShellData data)
        {
            byte[] d = data.ReadBwd(name);
            if (d == null) return false;
            var step = Campaign.Decode(name, d);
            if (step == null) return false;
            MarketMechs.Clear(); MarketCopies.Clear(); DealerWeapons.Clear(); DealerAmmo.Clear(); HirePilots.Clear(); HireTechs.Clear();
            Finished239D = false;
            Year = step.Year;
            Month = step.Month;
            SetI32(0x1e6, step.SuccessPay);
            SetI32(0x1ea, step.FailPay);
            SetI32(0x1ee, step.TargetPay);
            SetI32(0x1f2, step.BonusPay);
            SetI32(0x1f6, step.BountyPay);
            SetI32(0x1fa, step.ExpenseMultiplier);
            StepType = step.Type;
            // strstr(upper(name), "RAN") -> sprintf("ran%dscn1", atoi(name[3])); atoi of a letter is 0 (RANVCAM1 -> ran0scn1).
            if (name.ToUpperInvariant().Contains("RAN")) Mission = $"ran{Atoi(name.Length > 3 ? name.Substring(3, 1) : "")}scn1";
            else Mission = step.Mission;
            SetStr(0x176, step.NewsFlic);
            SetStr(0x186, step.NewsBmp);
            SetStr(0x196, step.Starmap);
            SetStr(0x1c6, step.Employer);
            SetStr(0x1d6, step.AbortCampaign);
            SetStr(0x1b6, "");
            foreach (var tag in step.ChunkOrder)
                switch (tag)
                {
                    case "SWNL":
                        foreach (var e in step.UnitItems) StepItems.Add(StepItem(e, data.Tables));
                        break;
                    case "SMHL":
                        foreach (var e in step.UnitMechs)
                        {
                            var m = data.BuildMech(e.Mek, this);
                            if (m == null) continue;
                            m.Data[0x9a8] = (byte)'M'; m.Data[0x9a9] = (byte)'E'; m.Data[0x9aa] = (byte)'R';
                            m.MekName = m.Str(0x9a3, 0x20) + ".MEK";
                            m.Field998 = e.Value;
                            m.TechYear = e.TechYear;
                            m.Custom = false;
                            m.SalvageChance = e.SalvageChance;
                            StepMechs.Add(m);
                        }
                        break;
                    case "AMMO":
                        foreach (var e in step.Ammo)
                            for (int k = 0; k < e[1]; k++)
                            {
                                var it = new ItemRecord { Index = e[0], FieldE = 1, Price = e[2] };
                                InitAmmo(it, data.Tables);
                                it.Field12 = e[3];
                                it.Field16 = 100;
                                it.Kind = 1;
                                DealerAmmo.Add(it);
                            }
                        break;
                    case "NEWS":
                        if (step.News != null) SetStr(0x342, step.News);
                        break;
                    case "EQUP":
                        foreach (var e in step.Equipment)
                            DealerWeapons.Add(new ItemRecord { Kind = 2, Price = e[1], Index = e[0], FieldA = e[2], FieldE = e[3], Field12 = e[4] });
                        break;
                    case "THLT":
                        foreach (var techName in step.Techs)
                        {
                            var b2 = data.ReadBwd(techName);
                            var tech = b2 == null ? null : ReadTech(b2);
                            if (tech != null) HireTechs.Add(tech);
                        }
                        break;
                    case "MHLT":
                        foreach (var e in step.Mechs)
                        {
                            var m = data.BuildMech(e.Mek, this);
                            if (m == null) continue;
                            m.Data[0x9a8] = (byte)'M'; m.Data[0x9a9] = (byte)'E'; m.Data[0x9aa] = (byte)'R';
                            m.MekName = m.Str(0x9a3, 0x20) + ".MEK";
                            m.Field998 = e.Value;
                            m.TechYear = e.TechYear;
                            m.Custom = false;
                            MarketMechs.Add(m);
                            MarketCopies.Add(m.Clone());
                        }
                        break;
                    case "PTLT":
                        foreach (var pilotName in step.Pilots)
                        {
                            var b2 = data.ReadBwd(pilotName);
                            var pilot = b2 == null ? null : ReadPilot(b2);
                            if (pilot != null) HirePilots.Add(pilot);
                        }
                        break;
                    case "WNLT":
                        foreach (var e in step.Weapons)
                        {
                            var it = new ItemRecord { Kind = 0, Index = e[0], Price = e[1] };
                            InitWeapon(it, data.Tables);
                            it.FieldE = e[2];
                            it.FieldA = -1;
                            DealerWeapons.Add(it);
                        }
                        break;
                    case "CTNT":
                        if (step.Contract != null) SetStr(0x1342, step.Contract);
                        break;
                }
            SetI32(0x2396, step.FailList.Count);
            for (int i = 0; i < step.FailList.Count; i++) SetStr(0x2a2 + i * 0x10, step.FailList[i]);
            SetI32(0x2392, step.SuccessList.Count);
            for (int i = 0; i < step.SuccessList.Count; i++) SetStr(0x202 + i * 0x10, step.SuccessList[i]);
            // Campaign_LoadRnd for a RAN step: the generated contract's scene and text.
            if (name.StartsWith("RAN", StringComparison.OrdinalIgnoreCase)) LoadRnd(name.ToUpperInvariant(), data);
            return true;
        }

        /// <summary>SWNL entry (code, chance, price, year): weapons and equipment through FUN_100268b0, ammunition through
        /// FUN_10026902 (both overwrite the step's price with the table price).</summary>
        static ItemRecord StepItem(int[] e, ShellTables t)
        {
            int kind = Campaign.ItemKind(e[0]);
            int idx = ItemIndex(kind, e[0], t);
            var it = new ItemRecord();
            if (kind == 1)
            {
                it.Index = idx;
                it.Kind = 1;
                it.Price = e[2];
                it.Field16 = e[1];
                it.Field12 = e[3];
                it.FieldE = 1;
                it.FieldA = -1;
                InitAmmo(it, t);
            }
            else
            {
                it.Index = kind == 2 ? idx : e[0];
                it.Kind = kind;
                it.Price = e[2];
                it.FieldE = e[3];
                it.Field12 = e[1];
                it.FieldA = -1;
                InitWeapon(it, t);
            }
            return it;
        }

        /// <summary>FUN_1004a6fe 0x1004a6fe: weapon code / 100, ammunition (code - 10000) / 10000, equipment by its table entry
        /// (0x100a8198, 0xc each, matching code / 50).</summary>
        /// <summary>
        /// FUN_10009724 0x10009724 on an item list made with the comparator FUN_10048d27 0x10048d27 (the unit's and the
        /// dealer's lists and their category lists): FUN_100097dc finds the entry to drop with the comparator, which
        /// compares only the first int (the table index), so the first record with the same index goes, whichever it
        /// is (another kind or a damaged copy too).
        /// </summary>
        public static void RemoveItem(List<ItemRecord> list, ItemRecord item)
        {
            int k = list.FindIndex(x => x.Index == item.Index);
            if (k >= 0) list.RemoveAt(k);
        }

        public static int ItemIndex(int kind, int code, ShellTables t)
        {
            if (kind == 0) return code / 100;
            if (kind == 1) return (code - 10000) / 10000;
            if (kind == 2)
                for (int k = 0x17; k >= 0; k--)
                    if (code / 0x32 == t.Item(k).Code / 0x32) return k;
            return -1;
        }

        /// <summary>FUN_100268b0 0x100268b0: +0x12 = 100, price from the weapon table (+0x1c, at least 10000 when not set),
        /// +0xa = -1, +0xe = 0.</summary>
        public static void InitWeapon(ItemRecord it, ShellTables t)
        {
            it.Field12 = 100;
            it.Price = t.Weapon(it.Index).Price;
            if (it.Price < 1) it.Price = 10000;
            it.FieldA = -1;
            it.FieldE = 0;
        }

        /// <summary>FUN_10026902 0x10026902: +0x16 = 100, ammunition price from the weapon table (+0x20, at least 10000), +0x12 = 0.</summary>
        public static void InitAmmo(ItemRecord it, ShellTables t)
        {
            it.Field16 = 100;
            it.Price = t.Weapon(it.Index).AmmoPrice;
            if (it.Price < 1) it.Price = 10000;
            it.Field12 = 0;
        }

        /// <summary>FUN_1006aecc 0x1006aecc: PILT (name, 8 skill shorts at +0x20, salary +0x30, strings +0x34 / +0x44) and DSSR text.</summary>
        public static PilotRecord ReadPilot(byte[] bwd)
        {
            PilotRecord p = null;
            int total = BitConverter.ToInt32(bwd, 4), pos = 0xc;
            while (pos < total && pos + 8 <= bwd.Length)
            {
                string tag = System.Text.Encoding.ASCII.GetString(bwd, pos, 4);
                int len = BitConverter.ToInt32(bwd, pos + 4);
                if (tag == "DSSR" && p != null)
                {
                    int n = Math.Min(len - 1, PilotRecord.Size - 100 - 1);
                    Buffer.BlockCopy(bwd, pos + 8, p.Data, 100, Math.Min(n, bwd.Length - pos - 8));
                    p.Data[Math.Min(len + 100, PilotRecord.Size - 1)] = 0;
                    pos += len;
                }
                else if (tag == "PILT")
                {
                    p = new PilotRecord();
                    Buffer.BlockCopy(bwd, pos + 8, p.Data, 0, 0x1f);
                    p.Data[0x20] = 0;
                    p.Data[100] = 0;
                    p.SetI32(0x20, -1);
                    p.SetI32(0x24, -1);
                    p.SetStr(0x44, ReadZ(bwd, pos + 0x3c));
                    p.SetStr(0x54, ReadZ(bwd, pos + 0x4c));
                    p.SetI32(0x38, 0);
                    p.SetI32(0x3c, BitConverter.ToInt32(bwd, pos + 0x38));
                    p.SetI32(0x40, 0);
                    p.Data[0x2064] = 0;
                    for (int k = 0; k < 8; k++) p.SetI16(0x28 + k * 2, BitConverter.ToInt16(bwd, pos + 0x28 + k * 2));
                    pos += 0x5c;
                }
                else
                {
                    if (len < 8) break;
                    pos += len;
                }
            }
            return p;
        }

        /// <summary>FUN_1006b0db 0x1006b0db: TECH (or PILT) chunk and DSSR text.</summary>
        public static TechRecord ReadTech(byte[] bwd)
        {
            TechRecord t = null;
            int total = BitConverter.ToInt32(bwd, 4), pos = 0xc;
            while (pos < total && pos + 8 <= bwd.Length)
            {
                string tag = System.Text.Encoding.ASCII.GetString(bwd, pos, 4);
                int len = BitConverter.ToInt32(bwd, pos + 4);
                if (tag == "TECH" || tag == "PILT")
                {
                    t = new TechRecord();
                    Buffer.BlockCopy(bwd, pos + 8, t.Data, 0, 0x1f);
                    t.Data[0x20] = 0;
                    t.Data[0x2c] = 0;
                    t.SetI32(0x20, -1);
                    for (int k = 0; k < 8; k++) t.SetI16(0x2d + k * 2, BitConverter.ToInt16(bwd, pos + 0x28 + k * 2));
                    t.SetStr(0x3d, ReadZ(bwd, pos + 0x3c));
                    t.SetStr(0x4d, ReadZ(bwd, pos + 0x4c));
                    t.SetI32(0x24, BitConverter.ToInt32(bwd, pos + 0x38));
                    t.SetI32(0x28, 0);
                    t.Data[0x5d] = 0;
                    t.Data[0x5e] = 0;
                    pos += 0x5c;
                }
                else if (tag == "DSSR" && t != null)
                {
                    int n = Math.Min(len - 1, TechRecord.Size - 0x5e - 1);
                    Buffer.BlockCopy(bwd, pos + 8, t.Data, 0x5e, Math.Min(n, bwd.Length - pos - 8));
                    t.Data[Math.Min(len + 0x5e, TechRecord.Size - 1)] = 0;
                    pos += len;
                }
                else
                {
                    if (len < 8) break;
                    pos += len;
                }
            }
            return t;
        }

        static string ReadZ(byte[] d, int o)
        {
            int e = o;
            while (e < d.Length && d[e] != 0) e++;
            return System.Text.Encoding.ASCII.GetString(d, o, e - o);
        }

        /// <summary>The name the save screen gives a save (state +2, strcpy of the slot's name by FUN_1006f614).</summary>
        public string SaveName { get => Str(2, 0x1d); set => SetStr(2, value); }

        /// <summary>
        /// Save_Write 0x1006d093 as the save screen calls it: +0x23c7 takes +0x23c3 (the screen the save screen came from,
        /// so loading returns there) while the state is written and gets its value back after; the live state's +0 is
        /// cleared; salvage.mw2 (<paramref name="salvage"/>) goes with it only when that screen is the debriefing 0xc00a or
        /// the salvage screen 0xc039 and its first dword is above 0.
        /// </summary>
        public byte[] SaveGame(byte[] salvage)
        {
            uint current = Msg23C7;
            Msg23C7 = Msg23C3;
            Started = false;
            var kept = SalvageFile;
            SalvageFile = Msg23C7 == 0xc00a || Msg23C7 == 0xc039 ? salvage : null;
            byte[] bytes = Save();
            SalvageFile = kept;
            Msg23C7 = current;
            return bytes;
        }

        /// <summary>FUN_1006d093: write the save (the counts are refreshed into the state first, +0 cleared, +0x156 set when a
        /// salvage file goes with it).</summary>
        public byte[] Save()
        {
            var o = new MemoryStream();
            SetI32(0x12e, UnitMechs.Count); SetI32(0x132, UnitPilots.Count); SetI32(0x136, UnitTechs.Count);
            SetI32(0x13a, UnitWeapons.Count); SetI32(0x13e, UnitAmmo.Count); SetI32(0x142, MarketMechs.Count);
            SetI32(0x146, HirePilots.Count); SetI32(0x14a, HireTechs.Count); SetI32(0x14e, DealerWeapons.Count);
            SetI32(0x152, DealerAmmo.Count);
            SetI32(0x15a, 0);
            SetI32(0x156, SalvageFile != null && SalvageFile.Length >= 4 && BitConverter.ToInt32(SalvageFile, 0) > 0 ? 1 : 0);
            var state = (byte[])Data.Clone();
            state[0] = 0;
            o.Write(state, 0, Size);
            if (I32(0x156) != 0)
            {
                o.Write(BitConverter.GetBytes(SalvageFile.Length), 0, 4);
                o.Write(SalvageFile, 0, SalvageFile.Length);
            }
            void W(IEnumerable<ShellRecord> list) { foreach (var r in list) o.Write(r.Data, 0, r.Data.Length); }
            // Save_Write 1006d52f / 1006d717 bounds copy lists by the corresponding live mech count.
            // Buying leaves a pristine record in MarketCopies; writing that extra record shifts the pilot data.
            void Copies(List<MechRecord> list, int count)
            {
                for (int i = 0; i < count; i++) o.Write(list[i].Data, 0, list[i].Data.Length);
            }
            W(MarketMechs); Copies(MarketCopies, MarketMechs.Count); W(HirePilots); W(HireTechs); W(DealerWeapons); W(DealerAmmo);
            W(UnitMechs); Copies(UnitCopies, UnitMechs.Count); W(UnitPilots); W(UnitTechs); W(UnitWeapons); W(UnitAmmo);
            o.Write(BitConverter.GetBytes(0xff), 0, 4);
            return o.ToArray();
        }

        /// <summary>FUN_1006ddf6: read a save (state +0 cleared, +0x156 read and cleared; the old 0x9d0-byte mech records of
        /// earlier versions are not handled).</summary>
        public static Career Load(byte[] d)
        {
            var c = new Career();
            Buffer.BlockCopy(d, 0, c.Data, 0, Size);
            c.Data[0] = 0;
            bool salvage = c.I32(0x156) == 1;
            c.SetI32(0x156, 0);
            int p = Size;
            if (salvage)
            {
                int n = BitConverter.ToInt32(d, p);
                p += 4;
                if (n != 0)
                {
                    c.SalvageFile = new byte[n];
                    Buffer.BlockCopy(d, p, c.SalvageFile, 0, n);
                    p += n;
                }
            }
            c.SetI32(0x15a, 0);
            byte[] Take(int size) { var r = new byte[size]; Buffer.BlockCopy(d, p, r, 0, size); p += size; return r; }
            for (int i = 0; i < c.I32(0x142); i++) c.MarketMechs.Add(new MechRecord(Take(MechRecord.Size)));
            for (int i = 0; i < c.I32(0x142); i++) c.MarketCopies.Add(new MechRecord(Take(MechRecord.Size)));
            for (int i = 0; i < c.I32(0x146); i++) c.HirePilots.Add(new PilotRecord(Take(PilotRecord.Size)));
            for (int i = 0; i < c.I32(0x14a); i++) c.HireTechs.Add(new TechRecord(Take(TechRecord.Size)));
            for (int i = 0; i < c.I32(0x14e); i++) c.DealerWeapons.Add(new ItemRecord(Take(ItemRecord.Size)));
            for (int i = 0; i < c.I32(0x152); i++) c.DealerAmmo.Add(new ItemRecord(Take(ItemRecord.Size)));
            for (int i = 0; i < c.I32(0x12e); i++) c.UnitMechs.Add(new MechRecord(Take(MechRecord.Size)));
            for (int i = 0; i < c.I32(0x12e); i++) c.UnitCopies.Add(new MechRecord(Take(MechRecord.Size)));
            for (int i = 0; i < c.I32(0x132); i++) c.UnitPilots.Add(new PilotRecord(Take(PilotRecord.Size)));
            for (int i = 0; i < c.I32(0x136); i++) c.UnitTechs.Add(new TechRecord(Take(TechRecord.Size)));
            for (int i = 0; i < c.I32(0x13a); i++) c.UnitWeapons.Add(new ItemRecord(Take(ItemRecord.Size)));
            for (int i = 0; i < c.I32(0x13e); i++) c.UnitAmmo.Add(new ItemRecord(Take(ItemRecord.Size)));
            return c;
        }
    }

    /// <summary>Game data the shell's records are built from: MW2.PRJ (BWD, MEK), the game folder and MW2SHELL.DLL's tables.</summary>
    public sealed class ShellData
    {
        public readonly PrjArchive Prj;
        public readonly ShellTables Tables;
        public readonly string GameFolder;

        public ShellData(PrjArchive prj, ShellTables tables, string gameFolder)
        {
            Prj = prj;
            Tables = tables;
            GameFolder = gameFolder;
        }

        public byte[] ReadBwd(string name) => Prj.TryFind("BWD", name, out var e) ? Prj.Read(e) : null;
        public byte[] ReadMek(string name) => Prj.TryFind("MEK", name, out var e) ? Prj.Read(e) : null;

        /// <summary>The files the shell writes beside the game (the random missions of RandomMissionGenerator), kept in
        /// memory by file name; the game folder is never changed.</summary>
        public readonly Dictionary<string, byte[]> Files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        /// <summary>The shell's MSVCRT rand() / srand().</summary>
        public readonly ShellRandom Random = new ShellRandom();
        /// <summary>rmglog.txt of the last run of the generator.</summary>
        public string RmgLog;

        /// <summary>FUN_10007d89 0x10007d89 with -2: "&lt;name&gt;.BWD" beside the game.</summary>
        public byte[] ReadLoose(string name) => Files.TryGetValue(name + ".bwd", out var d) ? d : null;

        /// <summary>A file of the game's RMG folder (fopen of "rmg\&lt;name&gt;.bwd").</summary>
        public byte[] ReadRmg(string path)
        {
            string file = Path.Combine(GameFolder ?? "", path.Replace('\\', Path.DirectorySeparatorChar));
            return File.Exists(file) ? File.ReadAllBytes(file) : null;
        }

        /// <summary>FUN_10047700 with the MEK from the PRJ or the game's mek folder.</summary>
        public MechRecord BuildMech(string name, Career c) =>
            MechRecord.FromMek(name, MechRecord.LoadMek(name, ReadMek, GameFolder), Tables, c.NextId);
    }
}

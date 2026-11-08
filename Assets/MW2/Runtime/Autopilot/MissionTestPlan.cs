using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using MW2.Formats;

namespace MW2.Autopilot
{
    /// <summary>Test configuration only. Does not rewrite MW2.PRJ, difficulty, MEKs or campaign saves.</summary>
    [Serializable]
    public sealed class MissionTestPlan
    {
        public string gameFolder = Mw2GameFolder.Default;
        public string missions = "BET3", campaign = "", mech = "COMMANDO", mek = "CMD00STD";
        public string output = "", mode = "Fair", tactic = "Balanced", mekFile = "";
        public int attempts = 1, seed = 12345, attempt = 1;
        public float timeout = 600f, speed = 1f;
        public bool child, invulnerable, quit, headless;

        public static MissionTestPlan Parse(string[] args)
        {
            var p = new MissionTestPlan();
            for (int i = 0; i < args.Length; i++)
            {
                string Next() { if (++i >= args.Length) throw new ArgumentException("Missing value after " + args[i - 1]); return args[i]; }
                switch (args[i])
                {
                    case "--mw2-child": p.child = true; break;
                    case "--game-folder": p.gameFolder = Next(); break;
                    case "--mission": case "--missions": p.missions = Next(); break;
                    case "--campaign": p.campaign = Next(); break;
                    case "--mech": p.mech = Next(); break;
                    case "--mek": p.mek = Next(); break;
                    case "--mek-file": p.mekFile = Next(); break;
                    case "--output": p.output = Next(); break;
                    case "--attempts": p.attempts = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--attempt": p.attempt = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--seed": p.seed = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--timeout": p.timeout = float.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--speed": p.speed = float.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--mode": p.mode = Next(); break;
                    case "--tactic": p.tactic = Next(); break;
                    case "--invulnerable": p.invulnerable = true; break;
                    case "--quit": p.quit = true; break;
                    case "--headless": p.headless = true; break;
                }
            }
            p.Validate(); return p;
        }

        public void Validate()
        {
            if (attempts < 1 || attempts > 100000 || attempt < 1) throw new ArgumentException("Attempts must be 1..100000");
            if (float.IsNaN(timeout) || float.IsInfinity(timeout) || timeout < 1 || timeout > 86400) throw new ArgumentException("Timeout must be 1..86400 seconds");
            if (float.IsNaN(speed) || speed < 1 || speed > 16) throw new ArgumentException("Speed must be 1..16");
            if (mode != "Fair" && mode != "Validation") throw new ArgumentException("Mode must be Fair or Validation");
            if (invulnerable && mode != "Validation") throw new ArgumentException("Invulnerability is allowed only in Validation");
            if (!Enum.TryParse<PilotTactic>(tactic, out _)) throw new ArgumentException("Unknown tactic " + tactic);
            if (!string.IsNullOrEmpty(campaign) && !string.Equals(campaign, "mercenaries", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Unsupported campaign: " + campaign + ". This archive is Mercenaries, not Wolf/Jade Falcon 31st Century Combat.");
            if (string.IsNullOrWhiteSpace(mech) || string.IsNullOrWhiteSpace(mek)) throw new ArgumentException("Explicit mech and MEK required");
            if (string.IsNullOrWhiteSpace(output)) output = Path.Combine("MissionTests", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
            output = Path.GetFullPath(output);
        }

        public List<string> MissionList(PrjArchive prj)
        {
            if (!string.IsNullOrEmpty(campaign)) return MissionCatalog.StoryMissions(prj);
            return missions.Split(',').Select(MissionCatalog.Prefix).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    public static class MissionCatalog
    {
        public static string Prefix(string name)
        {
            string s = name.Trim().ToUpperInvariant();
            // Campaign_LoadRnd1006b689 stores generated names including ".bwd";
            // normal shell launch takes their first four characters. This test
            // adapter accepts the same scene-file form without allowing paths.
            if (s.EndsWith(".BWD")) s = s.Substring(0, s.Length - 4);
            if (s.EndsWith("SCN1")) s = s.Substring(0, s.Length - 4);
            if (s.Length != 4 || s.Any(c => !char.IsLetterOrDigit(c))) throw new ArgumentException("Invalid mission prefix: " + name);
            return s;
        }
        public static List<string> StoryMissions(PrjArchive prj)
        {
            var steps = CampaignSteps(prj);
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // Career_New 0x1006cbb4 starts only at CAMP/NOVICE. SLST/FLST are the
            // actual contract graph; unrelated compiled prototypes are not careers.
            var pending = new Stack<string>(new[] { "CAMP", "NOVICE" });
            var reachable = new List<Campaign>();
            while (pending.Count > 0)
            {
                string step = pending.Pop();
                if (!visited.Add(step) || !steps.TryGetValue(step, out var campaign)) continue; // DEATH is a source sentinel
                reachable.Add(campaign);
                foreach (string next in campaign.SuccessList.Concat(campaign.FailList)) pending.Push(next);
            }
            return StoryNames(reachable);
        }

        /// <summary>All CPGN story references, including unreachable prototypes, retained for audit.</summary>
        public static List<string> ReferencedStoryMissions(PrjArchive prj) => StoryNames(CampaignSteps(prj).Values);

        static Dictionary<string, Campaign> CampaignSteps(PrjArchive prj)
        {
            var steps = new Dictionary<string, Campaign>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in prj.Entries("BWD"))
            {
                byte[] bytes = prj.Read(e);
                if (Campaign.Has(bytes)) steps[e.Name] = Campaign.Decode(e.Name, bytes);
            }
            return steps;
        }

        static List<string> StoryNames(IEnumerable<Campaign> steps)
        {
            var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in steps)
            {
                if ((c.Type == 1 || c.Type == 2) && !string.IsNullOrEmpty(c.Mission) && c.Mission.EndsWith("scn1", StringComparison.OrdinalIgnoreCase))
                {
                    string p = Prefix(c.Mission);
                    if (!p.StartsWith("RAN") && !p.StartsWith("TRA")) names.Add(p);
                }
            }
            return names.ToList();
        }
    }
}

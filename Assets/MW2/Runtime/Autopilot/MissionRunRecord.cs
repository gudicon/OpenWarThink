using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace MW2.Autopilot
{
    [Serializable] public sealed class ValidationCoverage
    {
        public int navTriggers, objectiveActivations, objectiveCompletions, unitRegistrations, unitReveals, unitDeaths;
        public bool originalFinishObserved, invulnerabilityRequested, effectiveInvulnerability;
        public string[] reviewNotes;
    }
    public sealed class RunOutcome
    {
        public string result, classification, reason;
        public static RunOutcome Evaluate(bool errors, bool dead, string director, float seconds, float timeout,
                                          bool target, int recoveries, string killer)
        {
            if (errors) return New("Error", "UnityError", "See errors and Player.log");
            if (dead) return New("Defeat", "CombatDefeat", "Player destroyed by " + killer + "; root cause requires trace analysis");
            if (director == "Success") return New("Victory", "DirectorSuccess", "Original mission evaluator returned Success");
            if (director != "Running") return New("Defeat", "MissionConditionFailure", director);
            if (seconds >= timeout) return New("Timeout", recoveries > 2 ? "NavigationTimeout" : target ? "CombatTimeout" : "ObjectiveTimeout",
                "Test deadline reached; this is not evidence of an original-game bug");
            return null;
        }
        static RunOutcome New(string result, string classification, string reason) =>
            new RunOutcome { result = result, classification = classification, reason = reason };
    }
    [Serializable] public sealed class ObjectiveRecord
    {
        public int index; public string text, state, mask; public bool visible, mandatory;
    }
    /// <summary>Read-only scene observer, never supplied to pilot decisions. Sampled states are not every evaluator transition.</summary>
    [Serializable] public sealed class TeamObjectiveRecord
    {
        public int team, index, boundTargets;
        public string target, mask, state;
        public bool visible, mandatory;
        public string[] observedStates;
    }
    [Serializable] public sealed class WeaponRecord
    {
        public string name; public int group, shots, ammo; public float damagePotential;
    }
    [Serializable] public sealed class TimelineEvent
    {
        public float seconds; public string kind, detail;
    }
    [Serializable] public sealed class MissionRunRecord
    {
        public int schema = 1;
        public string mission, campaign = "Mercenaries", faction = "Mercenary", mode, tactic, mech, mek;
        public int attempt, seed, difficulty;
        public int processId;
        public string runtimeAssemblySha256, originalDllSha256, directorResult, deathMechanism;
        public bool invulnerable, unlimitedAmmo, playerHeat, fairEligible;
        public float durationSeconds, wallSeconds, timeScale = 1f, simulationHz = 60f;
        public string result, classification, reason, lastNav, finalPilotState, lastDecision, deathSource;
        public int enemiesDestroyed, enemiesDestroyedByPlayer, enemiesRemaining;
        public float damageTaken, rawDamageTaken, peakHeat;
        public bool timelineTruncated;
        public int[] armor, rearArmor, structure;
        public ObjectiveRecord[] objectives;
        public TeamObjectiveRecord[] allTeamObjectives;
        public WeaponRecord[] weapons;
        public string[] errors;
        public string[] missingResources;
        public TimelineEvent[] timeline;
        public ValidationCoverage validation;
        public string ToJson() => ReportJson.Write(this);
    }

    [Serializable] public sealed class SeriesSummary
    {
        public string mode, campaign, note = "Independent fresh-process scenarios; not a persistent career simulation";
        public int total, victory, defeat, timeout, error;
        public float successPercent;
        public string[] frequentReasons;
        public static SeriesSummary Build(IEnumerable<MissionRunRecord> records, string mode)
        {
            var s = new SeriesSummary { mode = mode, campaign = "Mercenaries" };
            var counts = new Dictionary<string, int>();
            foreach (var r in records)
            {
                if (r.mode != mode) throw new ArgumentException("Validation and fair runs must not be mixed");
                s.total++;
                switch (r.result) { case "Victory": s.victory++; break; case "Defeat": s.defeat++; break;
                    case "Timeout": s.timeout++; break; default: s.error++; break; }
                if (r.result != "Victory") { string k = r.classification ?? "Unknown"; counts[k] = counts.TryGetValue(k, out int n) ? n + 1 : 1; }
            }
            s.successPercent = s.total > 0 ? 100f * s.victory / s.total : 0;
            var sorted = new List<KeyValuePair<string, int>>(counts);
            sorted.Sort((a, b) => { int c = b.Value.CompareTo(a.Value); return c != 0 ? c : string.CompareOrdinal(a.Key, b.Key); });
            s.frequentReasons = sorted.ConvertAll(p => p.Key + ": " + p.Value).ToArray();
            return s;
        }
    }

    /// <summary>Small invariant JSON encoder for explicit report DTOs; also executable in decoder tests without Unity.</summary>
    public static class ReportJson
    {
        public static string Write(object value) { var b = new StringBuilder(); Append(b, value); return b.ToString(); }
        static void Append(StringBuilder b, object v)
        {
            if (v == null) { b.Append("null"); return; }
            if (v is string s)
            {
                b.Append('"');
                foreach (char c in s)
                    switch (c) { case '"': b.Append("\\\""); break; case '\\': b.Append("\\\\"); break;
                        case '\n': b.Append("\\n"); break; case '\r': b.Append("\\r"); break; case '\t': b.Append("\\t"); break;
                        default: if (c < 32) b.Append("\\u").Append(((int)c).ToString("x4")); else b.Append(c); break; }
                b.Append('"'); return;
            }
            if (v is bool flag) { b.Append(flag ? "true" : "false"); return; }
            if (v.GetType().IsPrimitive || v is decimal)
            {
                string number = Convert.ToString(v, CultureInfo.InvariantCulture);
                b.Append(number == "NaN" || number.Contains("Infinity") ? "null" : number); return;
            }
            if (v is IEnumerable list)
            {
                b.Append('['); bool first = true;
                foreach (object e in list) { if (!first) b.Append(','); first = false; Append(b, e); }
                b.Append(']'); return;
            }
            b.Append('{'); bool start = true;
            foreach (var f in v.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
            { if (!start) b.Append(','); start = false; Append(b, f.Name); b.Append(':'); Append(b, f.GetValue(v)); }
            b.Append('}');
        }
    }
}
